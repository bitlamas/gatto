using Gatto.Repl.Input;
using Gatto.Terminal;

namespace Gatto.Tests;

public class LineEditorPasteTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gatto-lp").FullName;
    private History H() => new(Path.Combine(_dir, "history.txt"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    //each item says whether more input is pending after it, so a paste is true on every key but its last
    private sealed class Burst(IEnumerable<(ComposerInput Item, bool AvailAfter)> items) : IComposerSource
    {
        private readonly Queue<(ComposerInput Item, bool AvailAfter)> _q = new(items);
        private bool _avail;
        public bool KeyAvailable => _avail;
        public ComposerInput Read() { var (i, a) = _q.Dequeue(); _avail = a; return i; }
    }

    private sealed class ThrowingPeek(IEnumerable<ConsoleKeyInfo> keys) : IComposerSource
    {
        private readonly Queue<ConsoleKeyInfo> _q = new(keys);
        public bool KeyAvailable => throw new InvalidOperationException("peek failed");
        public ComposerInput Read() => new ComposerInput.Key(_q.Dequeue());
    }

    private static ConsoleKeyInfo Ch(char c) => c switch
    {
        '\n' => new('\r', ConsoleKey.Enter, false, false, false),
        '\t' => new('\t', ConsoleKey.Tab, false, false, false),
        _ => new(c, ConsoleKey.A, false, false, false),
    };

    private static ConsoleKeyInfo Enter() => Ch('\n');
    private static ConsoleKeyInfo Key(ConsoleKey k) => new('\0', k, false, false, false);

    //the keys of a paste, each with more pending behind it but the last
    private static IEnumerable<(ComposerInput, bool)> Paste(string text) =>
        text.Select((c, i) => ((ComposerInput)new ComposerInput.Key(Ch(c)), i < text.Length - 1));

    private static IEnumerable<(ComposerInput, bool)> Typed(params ConsoleKeyInfo[] keys) =>
        keys.Select(k => ((ComposerInput)new ComposerInput.Key(k), false));

    private static (ComposerInput, bool) Pending(ComposerInput i) => (i, true);
    private static (ComposerInput, bool) Last(ComposerInput i) => (i, false);
    private static ComposerInput In(ConsoleKeyInfo k) => new ComposerInput.Key(k);

    private string Read(IEnumerable<(ComposerInput, bool)> items, out int paints, string? initial = null,
        Action<LineEditor>? wire = null)
    {
        var editor = new LineEditor(new Burst(items), H());
        wire?.Invoke(editor);
        var n = 0;
        var text = editor.Read(_ => n++, initial);
        paints = n;
        return text;
    }

    [Fact]
    public void A_burst_of_2000_keys_paints_once()
    {
        var text = Read(Paste(new string('x', 2000)).Concat(Typed(Enter())), out var paints);
        Assert.Equal(2000, text.Length);
        Assert.Equal(2, paints);   //the paint when the read starts, then one for the whole burst
    }

    [Fact]
    public void A_burst_ending_in_Enter_does_not_submit_and_keeps_its_inner_lines()
    {
        var text = Read(Paste("ab\ncd\n").Concat(Typed(Enter())), out _);
        Assert.Equal("ab\ncd", text);
    }

    [Fact]
    public void A_tab_inside_a_burst_is_kept()
    {
        Assert.Equal("a\tb", Read(Paste("a\tb").Concat(Typed(Enter())), out _));
    }

    [Fact]
    public void An_arrow_ends_a_burst_and_then_moves_the_caret()
    {
        var items = Paste("ab").Select((x, _) => (x.Item1, true))
            .Append(Last(In(Key(ConsoleKey.LeftArrow))))
            .Concat(Typed(Ch('X'), Enter()));
        Assert.Equal("aXb", Read(items, out _));
    }

    [Fact]
    public void AltV_ends_a_burst_and_pastes_the_image()
    {
        var pasted = 0;
        var items = Paste("ab").Select((x, _) => (x.Item1, true))
            .Append(Last(In(new ConsoleKeyInfo('v', ConsoleKey.V, false, true, false))))
            .Concat(Typed(Enter()));
        var text = Read(items, out _, wire: e => e.OnPasteImage = () => pasted++);
        Assert.Equal("ab", text);
        Assert.Equal(1, pasted);
    }

    [Fact]
    public void A_burst_over_a_live_selection_replaces_it()
    {
        var items = new[]
        {
            Last(new ComposerInput.Select(0, 0, ComposerGesture.Begin)),
            Last(new ComposerInput.Select(0, 5, ComposerGesture.Extend)),
        }.Concat(Paste("HEY")).Concat(Typed(Enter()));
        Assert.Equal("HEY world", Read(items, out _, initial: "hello world"));
    }

    [Fact]
    public void A_lone_Enter_with_a_gesture_behind_it_submits()
    {
        var items = new[] { Pending(In(Enter())), Last(new ComposerInput.Select(0, 0, ComposerGesture.Clear)) };
        Assert.Equal("hi", Read(items, out _, initial: "hi"));
    }

    [Fact]
    public void A_lone_Tab_with_an_arrow_behind_it_completes()
    {
        var items = new[] { Pending(In(Ch('\t'))), Last(In(Key(ConsoleKey.RightArrow))) }.Concat(Typed(Enter()));
        Assert.Equal("/model", Read(items, out _, initial: "/m"));
    }

    [Fact]
    public void A_key_behind_a_submitting_Enter_runs_in_the_next_read()
    {
        var altQ = new ConsoleKeyInfo('q', ConsoleKey.Q, false, true, false);
        var editor = new LineEditor(new Burst(new[] { Pending(In(Enter())), Last(In(altQ)), Last(In(Enter())) }), H());
        Assert.Equal("hi", editor.Read(_ => { }, "hi"));
        Assert.Equal("q", editor.Read(_ => { }));
    }

    [Fact]
    public void A_ShiftAltGr_character_stays_inside_the_burst()
    {
        var upperAccent = new ConsoleKeyInfo((char)0xC9, ConsoleKey.E, true, true, true);
        var items = new[] { Pending(In(Ch('a'))), Pending(In(upperAccent)), Last(In(Ch('b'))) }.Concat(Typed(Enter()));
        Assert.Equal("a" + (char)0xC9 + "b", Read(items, out var paints));
        Assert.Equal(2, paints);
    }

    [Fact]
    public void A_pasted_backslash_before_a_newline_is_kept()
    {
        Assert.Equal("a\\\nb", Read(Paste("a\\\nb").Concat(Typed(Enter())), out _));
    }

    [Fact]
    public void A_typed_backslash_before_Enter_still_continues_the_line()
    {
        Assert.Equal("a\nb", Read(Typed(Ch('a'), Ch('\\'), Enter(), Ch('b'), Enter()), out _));
    }

    private static IEnumerable<(ComposerInput, bool)> TypedText(string text) => Typed([.. text.Select(Ch)]);

    private static readonly string Five = "one\ntwo\nthree\nfour\nfive";

    //the editor after the items, and the view of its last paint before the submit
    private (string Sent, EditorView Last, History History) ReadViews(IEnumerable<(ComposerInput, bool)> items)
    {
        var h = H();
        var editor = new LineEditor(new Burst(items), h);
        EditorView? last = null;
        var sent = editor.Read(v => last = v);
        return (sent, last!, h);
    }

    [Fact]
    public void Five_pasted_lines_stand_as_one_token_on_a_one_row_composer()
    {
        var (_, last, _) = ReadViews(Paste(Five).Concat(Typed(Enter())));
        Assert.Equal(new[] { "[Pasted text #1 +5 lines]" }, last.Lines);

        var surface = new Gatto.Terminal.Tests.Fakes.RecordingSurface { Width = 120, Height = 40 };
        var theme = new Theme(new TermCaps(true, true));
        var status = new StatusInfo(@"C:\proj", "qwen", "coder", new CtxState(), @"C:\Users\x");
        var painter = new Gatto.Repl.Render.Chrome.ChromePainter(surface, theme, new object())
        {
            Frame = new InputFrame(surface, theme, "coder", status, glyphs: GlyphSet.Unicode),
            RoleForTint = "coder",
        };
        painter.State.Composer = last;
        var block = painter.ComposeChromeBlock(120, 40);
        Assert.Single(block.Rows, r => r.Region == Gatto.Repl.Render.ChromeRegion.Composer);
    }

    [Fact]
    public void Four_pasted_lines_stand_as_text()
    {
        var (_, last, _) = ReadViews(Paste("one\ntwo\nthree\nfour").Concat(Typed(Enter())));
        Assert.Equal(new[] { "one", "two", "three", "four" }, last.Lines);
    }

    [Fact]
    public void One_pasted_line_over_1000_characters_is_one_line_token()
    {
        var (sent, last, _) = ReadViews(Paste(new string('x', 1001)).Concat(Typed(Enter())));
        Assert.Equal(new[] { "[Pasted text #1 +1 line]" }, last.Lines);
        Assert.Equal(1001, sent.Length);
    }

    [Fact]
    public void Two_pastes_take_two_numbers()
    {
        var (_, last, _) = ReadViews(Paste(Five).Concat(TypedText(" ")).Concat(Paste(Five + "\nsix")).Concat(Typed(Enter())));
        Assert.Equal(new[] { "[Pasted text #1 +5 lines] [Pasted text #2 +6 lines]" }, last.Lines);
    }

    [Fact]
    public void The_message_and_the_history_hold_the_pasted_text_where_the_token_stood()
    {
        var (sent, _, h) = ReadViews(TypedText("x ").Concat(Paste(Five)).Concat(TypedText(" y")).Concat(Typed(Enter())));
        Assert.Equal("x " + Five + " y", sent);
        Assert.Equal(new[] { "x " + Five + " y" }, h.Entries);
    }

    [Fact]
    public void A_typed_token_with_nothing_stored_is_sent_as_typed()
    {
        var (sent, _, _) = ReadViews(TypedText("[Pasted text #7 +3 lines]").Concat(Typed(Enter())));
        Assert.Equal("[Pasted text #7 +3 lines]", sent);
    }

    [Fact]
    public void A_paste_holding_a_token_string_is_sent_as_it_was_pasted()
    {
        var first = "see [Pasted text #2 +5 lines]\n2\n3\n4\n5\n6";
        var (sent, _, _) = ReadViews(Paste(first).Concat(TypedText(" ")).Concat(Paste(Five)).Concat(Typed(Enter())));
        Assert.Equal(first + " " + Five, sent);
    }

    [Fact]
    public void A_cleared_token_typed_again_is_sent_as_typed()
    {
        var ctrlU = new ConsoleKeyInfo('\u0015', ConsoleKey.U, false, false, true);
        var items = Paste(Five).Concat(Typed(ctrlU)).Concat(TypedText("[Pasted text #1 +5 lines]")).Concat(Typed(Enter()));
        Assert.Equal("[Pasted text #1 +5 lines]", ReadViews(items).Sent);
    }

    private static ConsoleKeyInfo Mod(ConsoleKey k, bool shift = false, bool ctrl = false, char c = '\0') => new(c, k, shift, false, ctrl);
    private static readonly ConsoleKeyInfo Left = Mod(ConsoleKey.LeftArrow);
    private static readonly ConsoleKeyInfo Right = Mod(ConsoleKey.RightArrow);
    private static readonly ConsoleKeyInfo Up = Mod(ConsoleKey.UpArrow);
    private static readonly ConsoleKeyInfo Down = Mod(ConsoleKey.DownArrow);
    private static readonly ConsoleKeyInfo Home = Mod(ConsoleKey.Home);
    private static readonly ConsoleKeyInfo End = Mod(ConsoleKey.End);
    private static readonly ConsoleKeyInfo Backspace = Mod(ConsoleKey.Backspace, c: (char)8);
    private static readonly ConsoleKeyInfo Delete = Mod(ConsoleKey.Delete);
    private static readonly ConsoleKeyInfo ShiftLeft = Mod(ConsoleKey.LeftArrow, shift: true);
    private static readonly ConsoleKeyInfo ShiftRight = Mod(ConsoleKey.RightArrow, shift: true);
    private static readonly ConsoleKeyInfo CtrlLeft = Mod(ConsoleKey.LeftArrow, ctrl: true);
    private static readonly ConsoleKeyInfo CtrlRight = Mod(ConsoleKey.RightArrow, ctrl: true);
    private static readonly ConsoleKeyInfo CtrlW = Mod(ConsoleKey.W, ctrl: true, c: (char)23);
    private static readonly ConsoleKeyInfo CtrlA = Mod(ConsoleKey.A, ctrl: true, c: (char)1);
    private static readonly ConsoleKeyInfo CtrlC = Mod(ConsoleKey.C, ctrl: true, c: (char)3);
    private static readonly ConsoleKeyInfo ShiftEnter = new('\r', ConsoleKey.Enter, true, false, false);
    private const string Token1 = "[Pasted text #1 +5 lines]";

    private static (ComposerInput, bool) Gesture(int line, int col, ComposerGesture kind) =>
        (new ComposerInput.Select(line, col, kind), false);

    private string Sent(IEnumerable<(ComposerInput, bool)> items) => ReadViews(items).Sent;

    [Fact]
    public void Left_at_a_tokens_end_goes_to_its_start()
    {
        Assert.Equal("X" + Five, Sent(Paste(Five).Concat(Typed(Left, Ch('X'), Enter()))));
    }

    [Fact]
    public void Right_at_a_tokens_start_goes_to_its_end()
    {
        Assert.Equal(Five + "Y", Sent(Paste(Five).Concat(Typed(Home, Right, Ch('Y'), Enter()))));
    }

    [Fact]
    public void Shift_Left_over_a_token_selects_all_of_it()
    {
        Assert.Equal("a ", Sent(TypedText("a ").Concat(Paste(Five)).Concat(Typed(ShiftLeft, Backspace, Enter()))));
    }

    [Fact]
    public void Ctrl_Left_stops_at_a_tokens_start()
    {
        Assert.Equal("a X" + Five, Sent(TypedText("a ").Concat(Paste(Five)).Concat(Typed(CtrlLeft, Ch('X'), Enter()))));
    }

    [Fact]
    public void Ctrl_Right_stops_at_a_tokens_end()
    {
        Assert.Equal(Five + "X b", Sent(Paste(Five).Concat(TypedText(" b")).Concat(Typed(Home, CtrlRight, Ch('X'), Enter()))));
    }

    [Fact]
    public void Up_into_a_token_stops_at_its_start_even_when_the_end_is_nearer()
    {
        var digits = "0123456789012345678901234";
        var items = TypedText("ab").Concat(Paste(Five)).Concat(Typed(ShiftEnter)).Concat(TypedText(digits)).Concat(Typed(Up, Ch('X'), Enter()));
        Assert.Equal("abX" + Five + "\n" + digits, Sent(items));
    }

    [Fact]
    public void Down_into_a_token_stops_at_its_end_even_when_the_start_is_nearer()
    {
        var items = TypedText("0123456789").Concat(Typed(ShiftEnter)).Concat(TypedText("ab")).Concat(Paste(Five))
            .Concat(Typed(Up, Down, Ch('X'), Enter()));
        Assert.Equal("0123456789\nab" + Five + "X", Sent(items));
    }

    [Fact]
    public void Up_and_Down_over_a_wrapped_token_always_change_rows()
    {
        for (var p = 0; p <= 16; p++)
        {
            var bs = new string('b', 16);
            var items = TypedText(new string('a', p)).Concat(Paste(Five)).Concat(Typed(Home, Down))
                .Concat(Typed(End, ShiftEnter)).Concat(TypedText(bs)).Concat(Typed(Up, Enter()));
            var views = new List<EditorView>();
            new LineEditor(new Burst(items), H(), width: () => 20).Read(v => views.Add(v));
            var afterDown = views[p + 3];
            var layout = new ComposerLayout(afterDown.Lines, 20);
            if (layout.RowCount >= 2)
                Assert.True(layout.PositionToCell(afterDown.CursorLine, afterDown.CursorCol).RegionRow > 0, $"Down stayed on the first row, prefix {p}");
            var afterUp = views[^1];
            var final = new ComposerLayout(afterUp.Lines, 20);
            var from = final.PositionToCell(1, bs.Length).RegionRow;
            Assert.True(final.PositionToCell(afterUp.CursorLine, afterUp.CursorCol).RegionRow < from, $"Up stayed on its row, prefix {p}");
        }
    }

    [Fact]
    public void A_click_inside_a_token_lands_on_its_nearer_edge()
    {
        Assert.Equal(Five + "X", Sent(Paste(Five).Append(Gesture(0, 20, ComposerGesture.Begin)).Concat(Typed(Ch('X'), Enter()))));
        Assert.Equal("X" + Five, Sent(Paste(Five).Append(Gesture(0, 3, ComposerGesture.Begin)).Concat(Typed(Ch('X'), Enter()))));
    }

    [Fact]
    public void A_double_click_inside_a_token_selects_all_of_it()
    {
        var items = TypedText("hi ").Concat(Paste(Five)).Concat(TypedText(" ok"))
            .Append(Gesture(0, 11, ComposerGesture.Begin)).Append(Gesture(0, 15, ComposerGesture.Extend))
            .Concat(Typed(Backspace, Enter()));
        Assert.Equal("hi  ok", Sent(items));
    }

    [Fact]
    public void A_drag_ending_inside_a_token_covers_all_of_it()
    {
        var items = TypedText("ab ").Concat(Paste(Five))
            .Append(Gesture(0, 1, ComposerGesture.Begin)).Append(Gesture(0, 10, ComposerGesture.Extend))
            .Concat(Typed(Backspace, Enter()));
        Assert.Equal("a", Sent(items));
    }

    [Fact]
    public void Backspace_at_a_tokens_end_removes_it_whole()
    {
        Assert.Equal("a", Sent(TypedText("a").Concat(Paste(Five)).Concat(Typed(Backspace, Enter()))));
    }

    [Fact]
    public void Delete_at_a_tokens_start_removes_it_whole()
    {
        Assert.Equal("ab", Sent(TypedText("a").Concat(Paste(Five)).Concat(TypedText("b")).Concat(Typed(Left, Left, Delete, Enter()))));
    }

    [Fact]
    public void Ctrl_W_at_a_tokens_end_removes_it_whole()
    {
        Assert.Equal("a ", Sent(TypedText("a ").Concat(Paste(Five)).Concat(Typed(CtrlW, Enter()))));
    }

    [Fact]
    public void A_removed_token_typed_again_is_sent_as_typed()
    {
        Assert.Equal(Token1, Sent(Paste(Five).Concat(Typed(Backspace)).Concat(TypedText(Token1)).Concat(Typed(Enter()))));
    }

    [Fact]
    public void Ctrl_C_over_a_token_copies_the_text_that_would_be_sent()
    {
        var copied = new List<string>();
        var editor = new LineEditor(new Burst(TypedText("x ").Concat(Paste(Five)).Concat(Typed(CtrlA, CtrlC, Enter()))), H());
        editor.Read(_ => { }, onCopyText: copied.Add);
        Assert.Equal(new[] { "x " + Five }, copied);
    }

    private static readonly ConsoleKeyInfo CtrlV = Mod(ConsoleKey.V, ctrl: true, c: (char)22);

    [Fact]
    public void The_same_text_pasted_again_expands_in_place_with_the_caret_at_its_end()
    {
        var (sent, last, _) = ReadViews(Paste(Five).Concat(Paste(Five)).Concat(Typed(Enter())));
        Assert.Equal(Five.Split('\n'), last.Lines);
        Assert.Equal((4, 4), (last.CursorLine, last.CursorCol));
        Assert.Equal(Five, sent);
    }

    [Fact]
    public void A_different_text_pasted_next_is_a_second_token()
    {
        var (_, last, _) = ReadViews(Paste(Five).Concat(Paste(Five + "\nsix")).Concat(Typed(Enter())));
        Assert.Equal(new[] { Token1 + "[Pasted text #2 +6 lines]" }, last.Lines);
    }

    [Fact]
    public void A_key_between_the_two_pastes_ends_the_hint()
    {
        var (_, last, _) = ReadViews(Paste(Five).Concat(Typed(CtrlC)).Concat(Paste(Five)).Concat(Typed(Enter())));
        Assert.Equal(new[] { Token1 + "[Pasted text #2 +5 lines]" }, last.Lines);
    }

    [Fact]
    public void A_caret_moved_off_the_token_ends_the_hint()
    {
        var (_, last, _) = ReadViews(Paste(Five).Concat(Typed(Left, Right)).Concat(Paste(Five)).Concat(Typed(Enter())));
        Assert.Equal(new[] { Token1 + "[Pasted text #2 +5 lines]" }, last.Lines);
    }

    [Fact]
    public void A_Ctrl_V_key_expands_the_hinted_paste()
    {
        var (sent, last, _) = ReadViews(Paste(Five).Concat(Typed(CtrlV, Enter())));
        Assert.Equal(Five.Split('\n'), last.Lines);
        Assert.Equal(Five, sent);
    }

    [Fact]
    public void The_hint_stands_after_the_token_and_ends_at_the_next_key()
    {
        var hints = new List<string?>();
        var editor = new LineEditor(new Burst(Paste(Five).Concat(Typed(Ch('a'), Enter()))), H());
        editor.Read(_ => { }, onHintChanged: () => hints.Add(editor.PasteAgainHint));
        Assert.Equal(new[] { "paste again to expand", null }, hints);
    }

    [Fact]
    public void The_composer_paints_the_token_before_the_hint_is_set()
    {
        var events = new List<string>();
        var editor = new LineEditor(new Burst(Paste(Five).Concat(Typed(Enter()))), H());
        editor.Read(v => events.Add("paint " + string.Join("|", v.Lines)), onHintChanged: () => events.Add("hint"));
        Assert.Equal(new[] { "paint ", "paint " + Token1, "hint" }, events.Take(3));   //the Enter that submits clears the hint after these
    }

    [Fact]
    public void A_frame_composed_while_the_hint_stands_shows_it_in_the_top_rule()
    {
        string? hint = null;
        var editor = new LineEditor(new Burst(Paste(Five).Concat(Typed(Enter()))), H());
        EditorView? token = null;
        editor.Read(v => token ??= v.Lines[0].Length > 0 ? v : null, onHintChanged: () => hint ??= editor.PasteAgainHint);

        var surface = new Gatto.Terminal.Tests.Fakes.RecordingSurface { Width = 120, Height = 40 };
        var theme = new Theme(new TermCaps(true, true));
        var status = new StatusInfo(@"C:\proj", "qwen", "coder", new CtxState(), @"C:\Users\x") with { ChordHint = hint };
        var painter = new Gatto.Repl.Render.Chrome.ChromePainter(surface, theme, new object())
        {
            Frame = new InputFrame(surface, theme, "coder", status, glyphs: GlyphSet.Unicode),
            RoleForTint = "coder",
        };
        painter.State.Composer = token!;
        var block = painter.ComposeChromeBlock(120, 40);
        Assert.Contains(block.Rows, r => r.Region == Gatto.Repl.Render.ChromeRegion.Rule && r.Visible.Contains("paste again to expand", StringComparison.Ordinal));
    }

    private static readonly string Six = "m1\nm2\nm3\nm4\nm5\nm6";

    //an editor over a history that already holds the entries, and every paint it makes
    private (string Sent, List<EditorView> Views) WithHistory(IEnumerable<(ComposerInput, bool)> items, string[] entries,
        Func<IReadOnlyList<string>>? pullQueue = null, string? initial = null)
    {
        var h = H();
        foreach (var e in entries) h.Record(e);
        var views = new List<EditorView>();
        var sent = new LineEditor(new Burst(items), h).Read(v => views.Add(v), initial, pullQueue: pullQueue);
        return (sent, views);
    }

    [Fact]
    public void Up_over_a_long_entry_shows_one_token_and_sends_the_entry()
    {
        var (sent, views) = WithHistory(Typed(Up, Enter()), [Six]);
        Assert.Equal(new[] { "[Pasted text #1 +6 lines]" }, views[^1].Lines);
        Assert.Equal(Six, sent);
    }

    [Fact]
    public void Up_over_a_short_entry_shows_its_text()
    {
        var (_, views) = WithHistory(Typed(Up, Enter()), ["a\nb"]);
        Assert.Equal(new[] { "a", "b" }, views[^1].Lines);
    }

    [Fact]
    public void Up_Down_Up_shows_one_number_for_one_entry()
    {
        var (_, views) = WithHistory(Typed(Up, Down, Up, Enter()), [Six]);
        Assert.Equal(new[] { "[Pasted text #1 +6 lines]" }, views[1].Lines);
        Assert.Equal(new[] { "" }, views[2].Lines);
        Assert.Equal(new[] { "[Pasted text #1 +6 lines]" }, views[3].Lines);
    }

    [Fact]
    public void Down_returns_the_draft_as_it_was_with_its_token_working()
    {
        var items = TypedText("a").Concat(Typed(ShiftEnter)).Concat(TypedText("b")).Concat(Typed(ShiftEnter))
            .Concat(TypedText("c")).Concat(Typed(ShiftEnter)).Concat(TypedText("d")).Concat(Typed(ShiftEnter))
            .Concat(Paste(Five)).Concat(Typed(Up, Up, Up, Up, Up, Down, Enter()));
        var (sent, views) = WithHistory(items, ["old"]);
        Assert.Equal(new[] { "old" }, views[^2].Lines);
        Assert.Equal(new[] { "a", "b", "c", "d", Token1 }, views[^1].Lines);
        Assert.Equal("a\nb\nc\nd\n" + Five, sent);
    }

    [Fact]
    public void A_clear_during_a_walk_leaves_the_drafts_token_working()
    {
        var ctrlU = Mod(ConsoleKey.U, ctrl: true, c: (char)21);
        var (sent, views) = WithHistory(Paste(Five).Concat(Typed(Up, ctrlU, Down, Enter())), ["old"]);
        Assert.Equal(new[] { Token1 }, views[^1].Lines);
        Assert.Equal(Five, sent);
    }

    [Fact]
    public void A_long_queued_message_pulled_over_typed_text_stands_as_its_own_token()
    {
        var pulled = false;
        IReadOnlyList<string> Pull() { if (pulled) return []; pulled = true; return [Six]; }
        var (sent, views) = WithHistory(Paste(Five).Concat(Typed(Up, Enter())), [], Pull);
        Assert.Equal(new[] { "[Pasted text #2 +6 lines]", "", Token1 }, views[^1].Lines);
        Assert.Equal(Six + "\n\n" + Five, sent);
    }

    [Fact]
    public void A_long_seed_stands_as_one_token()
    {
        var (sent, views) = WithHistory(Typed(Enter()), [], initial: Six);
        Assert.Equal(new[] { "[Pasted text #1 +6 lines]" }, views[0].Lines);
        Assert.Equal(Six, sent);
    }

    [Fact]
    public void A_handed_back_text_of_two_lines_stands_as_two_lines_and_is_painted()
    {
        var (sent, views) = WithHistory(new[] { Last(new ComposerInput.Insert("line one\nline two")) }.Concat(Typed(Enter())), []);
        Assert.Equal(new[] { "line one", "line two" }, views[^1].Lines);
        Assert.Equal((1, 8), (views[^1].CursorLine, views[^1].CursorCol));
        Assert.Equal("line one\nline two", sent);
    }

    [Fact]
    public void A_long_handed_back_text_stands_as_one_token()
    {
        var (sent, views) = WithHistory(new[] { Last(new ComposerInput.Insert(Six)) }.Concat(Typed(Enter())), []);
        Assert.Equal(new[] { "[Pasted text #1 +6 lines]" }, views[^1].Lines);
        Assert.Equal(Six, sent);
    }

    [Fact]
    public void A_handed_back_text_behind_a_burst_lands_after_it()
    {
        var items = Paste("ab").Select((x, _) => (x.Item1, true)).Append(Last(new ComposerInput.Insert("cd"))).Concat(Typed(Enter()));
        Assert.Equal("abcd", WithHistory(items, []).Sent);
    }

    private static readonly System.Text.RegularExpressions.Regex TokenShape = new(@"\[Pasted text #\d+ \+\d+ lines?\]");

    [Fact]
    public void After_every_key_of_a_random_run_the_caret_is_never_inside_a_token()
    {
        var rng = new Random(826);
        var keys = new[] { Left, Right, ShiftLeft, ShiftRight, CtrlLeft, CtrlRight, Up, Down, Home, End, Backspace, Delete, CtrlW, Ch('a'), Ch(' '), ShiftEnter };
        var kinds = new[] { ComposerGesture.Begin, ComposerGesture.Extend };
        var random = Enumerable.Range(0, 200).Select(_ => rng.Next(5) == 0
            ? Gesture(rng.Next(3), rng.Next(40), kinds[rng.Next(2)])
            : (In(keys[rng.Next(keys.Length)]), false)).ToList();
        var items = TypedText("ab ").Concat(Paste(Five)).Concat(TypedText(" cd ")).Concat(Paste(Five + "\nsix"))
            .Concat(TypedText(" ef")).Concat(random).Concat(Typed(Enter()));
        var views = new List<EditorView>();
        new LineEditor(new Burst(items), H(), width: () => 20).Read(v => views.Add(v));
        for (var i = 0; i < views.Count; i++)
        {
            var v = views[i];
            foreach (System.Text.RegularExpressions.Match m in TokenShape.Matches(v.Lines[v.CursorLine]))
                Assert.False(m.Index < v.CursorCol && v.CursorCol < m.Index + m.Length,
                    $"paint {i}: caret at {v.CursorLine}:{v.CursorCol} inside {m.Value}");
        }
    }

    [Fact]
    public void A_source_whose_peek_throws_types_each_key()
    {
        var editor = new LineEditor(new ThrowingPeek(new[] { Ch('a'), Ch('b'), Enter() }), H());
        var paints = 0;
        Assert.Equal("ab", editor.Read(_ => paints++));
        Assert.Equal(3, paints);
    }
}
