using Gatto.Repl.Prompts;
using Gatto.Terminal;
using Gatto.Terminal.Tests.Fakes;

namespace Gatto.Tests;

//the panel's keys, filter, setting and gestures, driven with no key loop, and the widget driven end to end once
public class PanelStateTests
{
    private static readonly Theme T = new(new TermCaps(true, true));

    private static readonly FooterKey[] ModelKeys = [new("Enter", "switch"), new("d", "default"), new("Esc", "close")];

    private static readonly string[] Names =
        ["bonsai-27b", "qwen3.5-9b", "qwen3.6-35b", "qwen3.8-flash", "mistral-7b", "llama-8b", "gemma-9b", "phi-4",
         "granite-8b", "olmo-7b", "smol-2b", "yi-6b"];

    private static List<SelectOption> Models() => [.. Names.Select(n => new SelectOption(n, Columns: ["64k", n + ".gguf"]))];

    private static PanelFields Panel(PanelUnder? under = null) =>
        new("model", ModelKeys, Header: ["ctx", "file"], Legend: "loaded", Under: under);

    private static PanelState State(PanelUnder? under = null, int cursor = 0, char[]? controls = null) =>
        new(Panel(under), Models(), numbered: true, cursor, controls);

    private static ConsoleKeyInfo K(ConsoleKey k) => new('\0', k, false, false, false);
    private static ConsoleKeyInfo C(char c) => new(c, char.IsAsciiLetter(c) ? ConsoleKey.A + (char.ToLowerInvariant(c) - 'a') : 0, false, false, false);
    private static readonly ConsoleKeyInfo EnterKey = new('\r', ConsoleKey.Enter, false, false, false);
    private static readonly ConsoleKeyInfo EscKey = new('\u001b', ConsoleKey.Escape, false, false, false);

    private static void Type(PanelState s, string text) { foreach (var c in text) s.Key(C(c)); }

    //composes the frame the user would see and records it, as the factory does
    private static PanelFrame Paint(PanelState s, int w = 80, int room = 10)
    {
        var view = s.View();
        var frame = PanelComposer.Compose(Panel(), s.OptionsOf(view.Tab), true, view, w, room, T, GlyphSet.Unicode);
        s.Record(frame.Layout, view.Tab);
        return frame;
    }

    [Fact]
    public void PgDn_moves_the_cursor_a_bodys_worth_and_the_window_follows()
    {
        var s = State();
        var before = Paint(s);
        s.Key(K(ConsoleKey.PageDown));
        var after = Paint(s);

        Assert.True(s.CursorIndex >= before.Layout.BodyRows - 2);
        Assert.True(after.Layout.Offset > 0 || s.CursorIndex < after.Layout.BodyRows);
        Assert.Contains(after.Rows.Select(TermText.StripAnsiForWidth), r => r.StartsWith("❯", StringComparison.Ordinal));
    }

    //a list that fits its body draws no box, so ? and Tab open none
    [Fact]
    public void On_a_list_that_fits_the_question_mark_and_Tab_change_nothing()
    {
        var s = new PanelState(Panel(), [.. Models().Take(5)], numbered: true, 0, null);
        Paint(s, room: 20);

        Assert.Equal(PanelStep.None, s.Key(C('?')));
        Assert.Equal(PanelStep.None, s.Key(new ConsoleKeyInfo('\t', ConsoleKey.Tab, false, false, false)));
        Assert.False(s.View().InBox);
    }

    [Fact]
    public void Typing_into_the_box_filters_and_the_hint_counts_the_matches()
    {
        var s = State();
        Paint(s);
        s.Key(C('?'));
        Type(s, "qwen3.");

        Assert.Equal([1, 2, 3], s.View().Shown);
        Assert.Contains("3 of 12", TermText.StripAnsiForWidth(Paint(s).Rows[1]));
    }

    [Fact]
    public void An_empty_match_shows_nothing_matches_and_zero()
    {
        var s = State();
        Paint(s);
        s.Key(C('?'));
        Type(s, "zz");

        var rows = Paint(s).Rows.Select(TermText.StripAnsiForWidth).ToList();
        Assert.Contains("  nothing matches", rows);
        Assert.Contains("0 of 12", rows[1]);
    }

    [Fact]
    public void Esc_in_the_box_clears_then_returns_the_keys_and_Enter_keeps_the_filter()
    {
        var s = State();
        Paint(s);
        s.Key(C('?'));
        Type(s, "qwen");
        s.Key(EscKey);
        Assert.Equal("", s.View().Draft);
        Assert.True(s.View().InBox);
        s.Key(EscKey);
        Assert.False(s.View().InBox);

        Paint(s);
        s.Key(C('?'));
        Type(s, "qwen");
        s.Key(EnterKey);
        Assert.False(s.View().InBox);
        Assert.Equal("qwen", s.View().Draft);
        Assert.Equal(0, s.View().Cursor);
    }

    //every outcome names the option's original index, never its place in the filtered list
    [Fact]
    public void Chosen_and_Control_after_a_filter_name_the_original_index()
    {
        var s = State(controls: ['d']);
        Paint(s);
        s.Key(C('?'));
        Type(s, "flash");
        s.Key(EnterKey);
        Assert.Equal(new SelectOutcome.Control('d', 3), s.Key(C('d')).Outcome);
        Assert.Equal(new SelectOutcome.Chosen(3), s.Key(EnterKey).Outcome);
    }

    [Fact]
    public void A_digit_over_a_filtered_list_picks_the_row_it_is_drawn_beside()
    {
        var s = State();
        Paint(s);
        s.Key(C('?'));
        Type(s, "qwen");
        s.Key(EnterKey);

        Assert.Equal(new SelectOutcome.Chosen(2), s.Key(new ConsoleKeyInfo('2', ConsoleKey.D2, false, false, false)).Outcome);
    }

    [Fact]
    public void A_typed_control_letter_in_the_box_is_a_character_of_the_draft()
    {
        var s = State(controls: ['d']);
        Paint(s);
        s.Key(C('?'));

        Assert.Null(s.Key(C('d')).Outcome);
        Assert.Equal("d", s.View().Draft);
    }

    [Fact]
    public void The_setting_steps_clamps_at_the_ends_and_resets_when_the_cursor_leaves()
    {
        var setting = new PanelUnder.Setting("default effort", _ => ["low", "medium", "high"], _ => "medium");
        var s = State(setting);

        s.Key(K(ConsoleKey.LeftArrow));
        Assert.Equal("low", s.SettingValue);
        Assert.True(s.SettingMoved);
        s.Key(K(ConsoleKey.LeftArrow));
        Assert.Equal("low", s.SettingValue);
        s.Key(K(ConsoleKey.RightArrow));
        s.Key(K(ConsoleKey.RightArrow));
        s.Key(K(ConsoleKey.RightArrow));
        Assert.Equal("high", s.SettingValue);

        s.Key(K(ConsoleKey.DownArrow));
        Assert.Equal("medium", s.SettingValue);
        Assert.False(s.SettingMoved);
    }

    [Fact]
    public void The_arrows_in_the_box_move_no_setting_and_down_lands_on_the_first_row()
    {
        var setting = new PanelUnder.Setting("default effort", _ => ["low", "medium"], _ => "medium");
        var s = State(setting, cursor: 5);
        Paint(s);
        s.Key(C('?'));
        s.Key(K(ConsoleKey.RightArrow));
        s.Key(K(ConsoleKey.LeftArrow));
        Assert.Equal("medium", s.SettingValue);

        s.Key(K(ConsoleKey.DownArrow));
        Assert.False(s.View().InBox);
        Assert.Equal(0, s.CursorIndex);
    }

    private static (int Row, int Col) KeyCell(PanelFrame f, string word, int fromEnd = 0)
    {
        var row = f.Rows.Count - 1;
        var span = f.Layout.Hits.Where(h => h.Row == row && h.Hit == new PanelHit.Key(word)).ToList();
        return (row, span.Max(h => h.To) - 1 + fromEnd);
    }

    //a press at the last cell of a key word's range acts as that key, and one cell past it acts as nothing
    [Fact]
    public void A_press_on_a_key_words_last_cell_acts_as_the_key_and_one_past_it_does_nothing()
    {
        var s = State();
        var f = Paint(s);
        var wakes = 0;
        var (row, last) = KeyCell(f, "Esc");

        Assert.True(s.Mouse(new MouseEvent(last + 1, 0, MouseKind.Press, MouseButton.Left, 0, 0), row, 80, () => wakes++));
        Assert.Equal(0, wakes);

        Assert.True(s.Mouse(new MouseEvent(last, 0, MouseKind.Press, MouseButton.Left, 0, 0), row, 80, () => wakes++));
        Assert.Equal(1, wakes);
        Assert.IsType<SelectOutcome.Cancelled>(s.Key(PanelState.Wake).Outcome);
    }

    [Fact]
    public void An_event_at_another_width_is_dropped()
    {
        var s = State();
        Paint(s);

        Assert.False(s.Mouse(new MouseEvent(4, 0, MouseKind.Press, MouseButton.Left, 0, 0), 4, 100, () => { }));
    }

    //two presses that arrive before the loop wakes resolve in order, each against the cursor the one before it left
    [Fact]
    public void Two_queued_presses_resolve_in_order()
    {
        var s = State();
        var f = Paint(s);
        var target = f.Layout.Hits.First(h => h.Hit == new PanelHit.Option(2));
        var press = new MouseEvent(target.From, 0, MouseKind.Press, MouseButton.Left, 0, 0);

        s.Mouse(press, target.Row, 80, () => { });
        s.Mouse(press, target.Row, 80, () => { });

        Assert.Null(s.Key(PanelState.Wake).Outcome);
        Assert.Equal(2, s.CursorIndex);
        Assert.Equal(new SelectOutcome.Chosen(2), s.Key(PanelState.Wake).Outcome);
        Assert.Equal(PanelStep.None, s.Key(PanelState.Wake));   //an empty wake does nothing
    }

    [Fact]
    public void A_hover_bands_a_key_word_another_hover_unbands_it_and_the_leave_unbands_it()
    {
        var s = State();
        var f = Paint(s);
        var (row, col) = KeyCell(f, "Enter");

        s.Mouse(new MouseEvent(col, 0, MouseKind.Move, MouseButton.None, 0, 0), row, 80, () => { });
        s.Key(PanelState.Wake);
        Assert.Equal("Enter", s.View().HoverKey);
        Assert.Contains(Ansi.Bg(T.Map(T.BandOf(Theme.Bright)), T.TrueColor), Paint(s).Rows[^1]);

        s.Mouse(new MouseEvent(0, 0, MouseKind.Move, MouseButton.None, 0, 0), 0, 80, () => { });
        s.Key(PanelState.Wake);
        Assert.Null(s.View().HoverKey);

        s.Mouse(new MouseEvent(col, 0, MouseKind.Move, MouseButton.None, 0, 0), row, 80, () => { });
        s.Key(PanelState.Wake);
        s.Mouse(new MouseEvent(0, 0, MouseKind.Move, MouseButton.None, 0, 0), -1, 80, () => { });
        s.Key(PanelState.Wake);
        Assert.Null(s.View().HoverKey);
    }

    private sealed class Keys(params ConsoleKeyInfo[] keys) : IKeySource
    {
        private readonly Queue<ConsoleKeyInfo> _q = new(keys);
        public ConsoleKeyInfo ReadKey() => _q.Dequeue();
        public bool KeyAvailable => _q.Count > 0;
    }

    //the widget routes a panel spec to its own loop, draws the title and the key row, and answers with the original index
    [Fact]
    public void Show_draws_a_panel_and_answers_with_the_original_index()
    {
        var surface = new RecordingSurface { Width = 80, Height = 24 };
        var spec = new SelectSpec([], null, Models(), Panel: Panel());
        var widget = new SelectPrompt(surface, T, new Keys(K(ConsoleKey.DownArrow), EnterKey));

        Assert.Equal(new SelectOutcome.Chosen(1), widget.Show(spec));
        var text = TermText.StripAnsiForWidth(surface.Text);
        Assert.Contains("  model", text);
        Assert.Contains("Enter switch   d default   Esc close", text);
        Assert.DoesNotContain("the rest have no number", text);
    }

    //each wait advances the clock by its budget and never sees a key, and a hook runs at each wait
    private sealed class PollClock(Action<int>? onWait = null) : IPollClock
    {
        public int Waits { get; private set; }
        public long ElapsedMs { get; private set; }

        public bool WaitForKey(TimeSpan budget)
        {
            Waits++;
            ElapsedMs += (long)budget.TotalMilliseconds;
            onWait?.Invoke(Waits);
            return false;
        }
    }

    private static int Renders(RecordingSurface s) =>
        TermText.StripAnsiForWidth(s.Text).Split('\n').Count(l => l.TrimStart('\r').StartsWith("  model", StringComparison.Ordinal));

    [Fact]
    public void A_panel_poll_that_arrives_answers_the_cursors_original_index_with_no_key_read()
    {
        var checks = 0;
        var spec = new SelectSpec([], null, Models(), Panel: Panel(), InitialCursor: 2, Poll: new PollSpec(
            Arrived: () => ++checks >= 2, Row: _ => "", Clock: new PollClock(), Interval: TimeSpan.FromSeconds(1), RenderRow: false));

        var outcome = new SelectPrompt(new RecordingSurface { Width = 80, Height = 24 }, T, new Keys()).Show(spec);

        Assert.Equal(2, Assert.IsType<SelectOutcome.Arrived>(outcome).Cursor);
        Assert.Equal(2, checks);
    }

    [Fact]
    public void A_panel_poll_repaints_once_per_change_of_its_row()
    {
        var checks = 0;
        var clock = new PollClock();
        var surface = new RecordingSurface { Width = 80, Height = 24 };
        var spec = new SelectSpec([], null, Models(), Panel: Panel(), Poll: new PollSpec(
            Arrived: () => ++checks >= 2, Row: ms => ms < 500 ? "a" : "b", Clock: clock, Interval: TimeSpan.FromSeconds(1), RenderRow: false));

        new SelectPrompt(surface, T, new Keys()).Show(spec);

        Assert.True(clock.Waits > 4, "the wait ended before the row changed");
        Assert.Equal(3, Renders(surface));   //the first paint, then null to a and a to b, each once
    }

    [Fact]
    public void A_close_signalled_during_a_panel_poll_answers_cancelled()
    {
        using var close = new CancellationTokenSource();
        var spec = new SelectSpec([], null, Models(), Panel: Panel(), Close: close.Token, Poll: new PollSpec(
            Arrived: () => false, Row: _ => "", Clock: new PollClock(n => { if (n == 3) close.Cancel(); }),
            Interval: TimeSpan.FromSeconds(1), RenderRow: false));

        var outcome = new SelectPrompt(new RecordingSurface { Width = 80, Height = 24 }, T, new Keys()).Show(spec);

        Assert.IsType<SelectOutcome.Cancelled>(outcome);
    }

    [Fact]
    public void A_panel_with_a_poll_that_renders_its_row_throws_at_entry()
    {
        var spec = new SelectSpec([], null, Models(), Panel: Panel(), Poll: new PollSpec(
            Arrived: () => false, Row: _ => "", Clock: new PollClock(), Interval: TimeSpan.FromSeconds(1)));

        Assert.Throws<ArgumentException>(() => new SelectPrompt(new RecordingSurface(), T, new Keys()).Show(spec));
    }

    private static readonly FooterKey[] TaskKeys = [new("Enter", "detail"), new("x", "stop"), new("Esc", "close")];
    private static readonly FooterKey[] WatchKeys = [new("Enter", "detail"), new("Esc", "close")];

    //two shells and the watch, each with a detail, the watch's naming no x
    private static PanelFields Tasks(Func<int, int>? lines = null) => new("tasks", TaskKeys, Count: "2 shells · watch", SearchWhenLong: false,
        LabelWhole: true, KeysFor: i => i == 2 ? WatchKeys : TaskKeys,
        DetailOf: i => [new($"command {i}"), .. Enumerable.Range(1, lines?.Invoke(i) ?? 30).Select(n => new DetailRow($"line {n}", Dim: true))],
        DetailKeysFor: i => i == 2 ? [new("Esc", "back")] : [new("x", "stop"), new("Esc", "back")]);

    private static List<SelectOption> TaskRows(params string[] keys) =>
        [.. (keys.Length == 0 ? ["1", "2", "watch"] : keys).Select(k => new SelectOption(k == "watch" ? "watch" : $"{k}  running 3s", Key: k))];

    private static PanelState TaskState(Func<int, int>? lines = null, int cursor = 0, string? detail = null, int offset = PanelState.End) =>
        new(Tasks(lines), TaskRows(), numbered: false, cursor, ['x'], detail, offset);

    private static PanelFrame PaintTasks(PanelState s, int room = 12, Func<int, int>? lines = null)
    {
        var view = s.View();
        var frame = PanelComposer.Compose(Tasks(lines), s.OptionsOf(view.Tab), false, view, 80, room, T, GlyphSet.Unicode);
        s.Record(frame.Layout, view.Tab);
        return frame;
    }

    [Fact]
    public void Enter_on_a_row_with_a_detail_opens_it_end_first_and_answers_nothing()
    {
        var s = TaskState();
        PaintTasks(s);

        Assert.Equal(PanelStep.Changed, s.Key(EnterKey));
        Assert.Equal("1", s.View().Detail);
        Assert.Equal(PanelState.End, s.DetailOffset);
        Assert.Equal("command 0", s.View().DetailRows![0].Text);
        Assert.Equal("  line 30", TermText.StripAnsiForWidth(PaintTasks(s).Rows[^3]));
    }

    [Fact]
    public void The_end_sentinel_survives_a_key_that_moves_nothing_and_a_re_entry()
    {
        var s = TaskState();
        PaintTasks(s);
        s.Key(EnterKey);
        PaintTasks(s);

        Assert.Equal(PanelStep.None, s.Key(K(ConsoleKey.DownArrow)));
        PaintTasks(s);
        s.Key(C('q'));
        Assert.Equal(PanelState.End, s.DetailOffset);

        var again = TaskState(lines: _ => 40, detail: s.Detail, offset: s.DetailOffset);
        Assert.Equal("  line 40", TermText.StripAnsiForWidth(PaintTasks(again, lines: _ => 40).Rows[^3]));
    }

    [Fact]
    public void PgUp_in_a_detail_moves_by_a_page_clamps_at_the_top_and_End_follows_the_tail_again()
    {
        var s = TaskState();
        PaintTasks(s);
        s.Key(EnterKey);
        var detail = PaintTasks(s).Layout;

        s.Key(K(ConsoleKey.PageUp));
        Assert.Equal(detail.Offset - (detail.BodyRows - 2), s.DetailOffset);
        for (var i = 0; i < 12; i++) { PaintTasks(s); s.Key(K(ConsoleKey.PageUp)); }
        Assert.Equal(0, s.DetailOffset);

        PaintTasks(s);
        s.Key(K(ConsoleKey.End));
        Assert.Equal(PanelState.End, s.DetailOffset);
        Assert.Equal(0, s.CursorIndex);   //no detail move moves the cursor
    }

    [Fact]
    public void Esc_closes_the_detail_at_the_same_cursor_and_the_detail_frame_never_moves_the_list()
    {
        var s = TaskState(cursor: 1);
        PaintTasks(s);
        s.Key(EnterKey);
        PaintTasks(s);
        s.Key(K(ConsoleKey.PageUp));
        PaintTasks(s);

        s.Key(EscKey);
        Assert.Null(s.View().Detail);
        s.Key(C('q'));   //the last frame is the detail's, so this key must not copy its offset into the list
        Assert.Equal(0, s.View().Offset);
        Assert.Equal(1, s.CursorIndex);
    }

    [Fact]
    public void A_detail_whose_row_is_gone_at_construction_is_dropped_and_the_cursor_clamps()
    {
        var s = new PanelState(Tasks(), TaskRows("1", "watch"), numbered: false, 2, ['x'], detail: "2");

        Assert.Null(s.Detail);
        Assert.Equal(1, s.CursorIndex);
        Assert.Equal("watch", new PanelState(Tasks(), TaskRows("1", "watch"), false, 0, ['x'], detail: "watch").Detail);
    }

    [Fact]
    public void A_control_key_is_taken_only_where_the_rows_key_row_names_it()
    {
        var shell = TaskState();
        PaintTasks(shell);
        shell.Key(EnterKey);
        Assert.Equal(new SelectOutcome.Control('x', 0), shell.Key(C('x')).Outcome);

        var watch = TaskState(cursor: 2);
        PaintTasks(watch);
        Assert.Equal(PanelStep.None, watch.Key(C('x')));
        watch.Key(EnterKey);
        Assert.Equal("watch", watch.Detail);
        Assert.Equal(PanelStep.None, watch.Key(C('x')));
    }

    [Fact]
    public void The_digits_the_question_mark_and_Tab_do_nothing_in_a_detail()
    {
        var s = TaskState();
        PaintTasks(s);
        s.Key(EnterKey);
        PaintTasks(s);

        Assert.Equal(PanelStep.None, s.Key(new ConsoleKeyInfo('1', ConsoleKey.D1, false, false, false)));
        Assert.Equal(PanelStep.None, s.Key(C('?')));
        Assert.Equal(PanelStep.None, s.Key(new ConsoleKeyInfo('\t', ConsoleKey.Tab, false, false, false)));
        Assert.Equal("1", s.Detail);
    }

    [Fact]
    public void The_wheel_in_a_detail_moves_by_the_lines_the_controller_resolved()
    {
        var s = TaskState();
        PaintTasks(s);
        s.Key(EnterKey);
        var end = PaintTasks(s).Layout.Offset;

        s.Mouse(new MouseEvent(4, 0, MouseKind.Wheel, MouseButton.None, 3, 0), 5, 80, () => { });
        s.Key(PanelState.Wake);

        Assert.Equal(end - 3, s.DetailOffset);
    }

    //a detail's text belongs to the chrome's copy drag, every other row stays the panel's
    [Fact]
    public void A_press_on_a_detail_body_row_is_declined_and_a_move_there_only_takes_the_band_off()
    {
        var s = TaskState();
        PaintTasks(s);
        s.Key(EnterKey);
        var f = PaintTasks(s);
        var wakes = 0;
        var body = f.Layout.BodyStart + 1;

        Assert.False(s.Mouse(new MouseEvent(6, 0, MouseKind.Press, MouseButton.Left, 0, 0), body, 80, () => wakes++));
        Assert.False(s.Mouse(new MouseEvent(6, 0, MouseKind.Move, MouseButton.None, 0, 0), body, 80, () => wakes++));
        Assert.Equal(0, wakes);
        Assert.True(s.Mouse(new MouseEvent(2, 0, MouseKind.Press, MouseButton.Left, 0, 0), 0, 80, () => wakes++));

        var (row, col) = KeyCell(f, "Esc");
        s.Mouse(new MouseEvent(col, 0, MouseKind.Move, MouseButton.None, 0, 0), row, 80, () => wakes++);
        s.Key(PanelState.Wake);
        Assert.Equal("Esc", s.View().HoverKey);
        Assert.True(s.Mouse(new MouseEvent(6, 0, MouseKind.Move, MouseButton.None, 0, 0), body, 80, () => wakes++));
        s.Key(PanelState.Wake);
        Assert.Null(s.View().HoverKey);
    }

    [Fact]
    public void A_detail_and_its_offset_handed_to_a_new_state_reopen_where_they_were()
    {
        var s = TaskState(detail: "2", offset: 7);
        var f = PaintTasks(s);

        Assert.Equal("2", s.View().Detail);
        Assert.Equal(7, f.Layout.Offset);
        Assert.Equal("  2  running 3s", TermText.StripAnsiForWidth(f.Rows[1]));
    }

    [Fact]
    public void Live_rows_are_asked_at_each_view_and_a_new_count_marks_the_state_stale()
    {
        var ticks = 0;
        List<SelectOption> Now() => [new($"1  running {++ticks}s", Key: "1"), new("watch", Key: "watch")];
        var s = new PanelState(Tasks() with { OptionsNow = Now }, Now(), false, 0, ['x']);

        s.View();
        Assert.Equal("1  running 2s", s.OptionsOf(0)[0].Label);
        s.View();
        Assert.Equal("1  running 3s", s.OptionsOf(0)[0].Label);
        Assert.False(s.Stale);

        var shrunk = new PanelState(Tasks() with { OptionsNow = () => [new("watch", Key: "watch")] }, Now(), false, 0, ['x']);
        shrunk.View();
        Assert.True(shrunk.Stale);
    }

    //the window's offset is over wrapped rows, so a move from the end starts at the end the user saw
    [Fact]
    public void A_parked_offset_over_a_wrapped_detail_is_a_physical_row()
    {
        var command = string.Join(" ", Enumerable.Range(1, 40).Select(i => $"word{i:00}"));
        var fields = Tasks() with { DetailOf = _ => [new(command, Wrap: true), .. Enumerable.Range(1, 20).Select(n => new DetailRow($"line {n}"))] };
        var s = new PanelState(fields, TaskRows(), numbered: false, 0, ['x']);
        PanelFrame Draw()
        {
            var view = s.View();
            var frame = PanelComposer.Compose(fields, s.OptionsOf(view.Tab), false, view, 80, 12, T, GlyphSet.Unicode);
            s.Record(frame.Layout, view.Tab);
            return frame;
        }
        Draw();
        s.Key(EnterKey);
        var end = Draw().Layout;
        Assert.True(end.BodyTotal > 21, $"{end.BodyTotal} physical rows");

        s.Key(K(ConsoleKey.UpArrow));
        Assert.Equal(end.Offset - 1, s.DetailOffset);
        Assert.Equal(end.Offset - 1, Draw().Layout.Offset);
        s.Key(K(ConsoleKey.Home));
        Assert.Equal("  word01 word02", string.Join(" ", TermText.StripAnsiForWidth(Draw().Rows[3]).Split(' ').Take(4)));
    }

    //an extension's file is a painted word, so a draft that names it alone keeps the row
    [Fact]
    public void A_draft_matching_a_trail_word_alone_keeps_the_row()
    {
        var fields = new PanelFields("tools", [new("Esc", "close")], CountUnit: "armed");
        List<SelectOption> options =
        [
            .. Enumerable.Range(0, 20).Select(i => new SelectOption($"tool_{i}", Description: "a gloss")),
            new("web_fetch", Description: "Fetch a URL.", Trail: [new SelectRun("web_search.csx", Theme.Dim)]),
        ];
        var s = new PanelState(fields, options, numbered: false, 0, null);
        var view = s.View();
        s.Record(PanelComposer.Compose(fields, options, false, view, 80, 10, T, GlyphSet.Unicode).Layout, 0);
        s.Key(C('?'));
        Type(s, "search.csx");

        Assert.Equal([20], s.View().Shown);
    }

    [Fact]
    public void A_detail_that_does_not_open_end_first_opens_at_its_top()
    {
        var fields = Tasks() with { DetailEndFirst = false };
        var s = new PanelState(fields, TaskRows(), numbered: false, 0, ['x']);
        PaintTasks(s);

        Assert.Equal(PanelStep.Changed, s.Key(EnterKey));
        Assert.Equal(0, s.DetailOffset);
        Assert.Equal("  command 0", TermText.StripAnsiForWidth(PaintTasks(s).Rows[3]));
    }

    //three tabs the shape of help: a list that names Enter, a text with no cursor, and a toggling list that names Space
    private sealed class Tabbed
    {
        public List<SelectOption> Commands = [.. Enumerable.Range(0, 12).Select(i => new SelectOption($"/cmd{i:00}", Description: "a gloss", Key: $"/cmd{i:00}"))];
        public List<SelectOption> Extensions = [.. new[] { "ask_user", "cards", "undo", "web_search" }.Select(n => new SelectOption(n, Key: n))];
        public bool SpaceNamed = true;
        public List<(char, int)> Acts = [];
        public PanelFields Fields;

        public Tabbed(int shortcutRows = 8, Func<char, int, bool>? act = null)
        {
            var esc = new FooterKey("Esc", "close");
            Fields = new PanelFields("help", [new("Enter", "compose"), esc], Tabs:
            [
                //each read is a copy as a live source hands one, so a change shows as a new count
                new PanelTab("commands", [.. Commands], OptionsNow: () => [.. Commands]),
                new PanelTab("shortcuts", [], Keys: [esc], Rows: [.. Enumerable.Range(1, shortcutRows).Select(n => new DetailRow($"row {n}"))]),
                new PanelTab("extensions", [.. Extensions], Keys: [esc],
                    KeysFor: _ => SpaceNamed ? [new("Space", "on or off"), esc] : [esc],
                    Under: new PanelUnder.Sentence(i => $"sentence {i}"),
                    OptionsNow: () => [.. Extensions], Rows: [new DetailRow("no extensions in the home", Dim: true)]),
            ], Act: act ?? ((c, i) => { Acts.Add((c, i)); return true; }));
        }

        public PanelState State() => new(Fields, Fields.Tabs![0].Options, numbered: false, 0, [' ']);

        public PanelFrame Paint(PanelState s, int w = 80, int room = 10)
        {
            var view = s.View();
            var frame = PanelComposer.Compose(Fields, s.OptionsOf(view.Tab), false, view, w, room, T, GlyphSet.Unicode);
            s.Record(frame.Layout, view.Tab);
            return frame;
        }
    }

    private static readonly ConsoleKeyInfo Right = new('\0', ConsoleKey.RightArrow, false, false, false);
    private static readonly ConsoleKeyInfo Left = new('\0', ConsoleKey.LeftArrow, false, false, false);
    private static readonly ConsoleKeyInfo SpaceKey = new(' ', ConsoleKey.Spacebar, false, false, false);

    private static string KeyRow(PanelFrame f) => TermText.StripAnsiForWidth(f.Rows[^1]).Trim();

    [Fact]
    public void The_key_row_follows_the_tab()
    {
        var t = new Tabbed();
        var s = t.State();
        Assert.Equal("↑↓ move   Enter compose   ←→ tabs   Esc close", KeyRow(t.Paint(s)));
        s.Key(Right);
        Assert.Equal("←→ tabs   Esc close", KeyRow(t.Paint(s)));
        s.Key(Right);
        Assert.Equal("↑↓ move   Space on or off   ←→ tabs   Esc close", KeyRow(t.Paint(s)));
    }

    [Fact]
    public void Space_named_on_one_tab_is_not_taken_on_another_and_acts_with_the_row_s_index_where_named()
    {
        var t = new Tabbed();
        var s = t.State();
        t.Paint(s);
        Assert.Equal(PanelStep.None, s.Key(SpaceKey));
        s.Key(Right);
        t.Paint(s);
        Assert.Equal(PanelStep.None, s.Key(SpaceKey));
        s.Key(Right);
        t.Paint(s);
        s.Key(K(ConsoleKey.DownArrow));
        t.Paint(s);

        var step = s.Key(SpaceKey);

        Assert.Equal(PanelStep.Changed, step);
        Assert.Null(step.Outcome);
        Assert.Equal([(' ', 1)], t.Acts);
    }

    [Fact]
    public void An_act_that_says_true_asks_the_tab_s_rows_again_and_the_cursor_stays()
    {
        var t = new Tabbed();
        t.Fields = t.Fields with
        {
            Act = (_, i) => { t.Extensions[i] = t.Extensions[i] with { Description = "off" }; return true; },
        };
        var s = t.State();
        s.Key(Right);
        s.Key(Right);
        s.Key(K(ConsoleKey.DownArrow));
        t.Paint(s);

        s.Key(SpaceKey);
        t.Paint(s);

        Assert.Equal("off", s.OptionsOf(2)[1].Description);
        Assert.Equal(1, s.CursorIndex);
    }

    [Fact]
    public void A_cursorless_tab_scrolls_with_the_moves_and_the_wheel_takes_the_arrows_and_Esc_and_spends_the_rest()
    {
        var t = new Tabbed(shortcutRows: 20);
        var s = t.State();
        s.Key(Right);
        var f = t.Paint(s);
        Assert.Equal(0, f.Layout.Offset);

        Assert.Equal(PanelStep.Changed, s.Key(K(ConsoleKey.DownArrow)));
        Assert.Equal(1, t.Paint(s).Layout.Offset);
        s.Mouse(new MouseEvent(4, 0, MouseKind.Wheel, MouseButton.None, -3, 0), f.Layout.BodyStart, 80, () => { });
        s.Key(PanelState.Wake);
        Assert.Equal(4, t.Paint(s).Layout.Offset);

        Assert.Equal(PanelStep.None, s.Key(C('a')));
        Assert.Equal(PanelStep.None, s.Key(EnterKey));
        Assert.Equal(PanelStep.None, s.Key(SpaceKey));
        Assert.IsType<SelectOutcome.Cancelled>(s.Key(EscKey).Outcome);
        Assert.Equal(PanelStep.Changed, s.Key(Right));
        Assert.Equal(2, s.View().Tab);
    }

    //the tab's rows are the chrome's text, so a press there starts the copy drag
    [Fact]
    public void A_press_on_a_cursorless_tab_s_text_is_declined_and_a_move_there_only_takes_the_band_off()
    {
        var t = new Tabbed();
        var s = t.State();
        s.Key(Right);
        var f = t.Paint(s);
        var body = f.Layout.BodyStart + 1;

        Assert.False(s.Mouse(new MouseEvent(6, 0, MouseKind.Press, MouseButton.Left, 0, 0), body, 80, () => { }));
        var (row, col) = KeyCell(f, "Esc");
        s.Mouse(new MouseEvent(col, 0, MouseKind.Move, MouseButton.None, 0, 0), row, 80, () => { });
        s.Key(PanelState.Wake);
        Assert.Equal("Esc", s.View().HoverKey);
        Assert.True(s.Mouse(new MouseEvent(6, 0, MouseKind.Move, MouseButton.None, 0, 0), body, 80, () => { }));
        s.Key(PanelState.Wake);
        Assert.Null(s.View().HoverKey);
    }

    [Fact]
    public void A_tab_with_options_keeps_its_cursor_and_draws_its_rows_once_it_has_none()
    {
        var t = new Tabbed();
        var s = t.State();
        s.Key(Right);
        s.Key(Right);
        Assert.Contains(t.Paint(s).Rows.Select(TermText.StripAnsiForWidth), r => r.StartsWith("❯ ask_user", StringComparison.Ordinal));

        s.Key(Left);
        t.Paint(s);
        t.Extensions.Clear();
        s.Key(Right);
        var rows = t.Paint(s).Rows.Select(TermText.StripAnsiForWidth).ToList();

        Assert.False(s.Stale);
        Assert.Contains("  no extensions in the home", rows);
        Assert.Equal("←→ tabs   Esc close", rows[^1].Trim());
    }

    [Fact]
    public void Enter_on_a_tab_whose_keys_do_not_name_it_is_spent_and_a_press_on_its_cursor_row_too()
    {
        var t = new Tabbed();
        var s = t.State();
        s.Key(Right);
        s.Key(Right);
        var f = t.Paint(s);

        Assert.Equal(PanelStep.None, s.Key(EnterKey));
        var hit = f.Layout.Hits.First(h => h.Hit is PanelHit.Option { Index: 0 });
        s.Mouse(new MouseEvent(hit.From, 0, MouseKind.Press, MouseButton.Left, 0, 0), hit.Row, 80, () => { });
        Assert.Equal(PanelStep.None, s.Key(PanelState.Wake));
    }

    [Fact]
    public void A_switch_away_and_back_keeps_the_cursorless_tab_s_offset_and_it_opens_at_its_first_row()
    {
        var t = new Tabbed(shortcutRows: 20);
        var s = t.State();
        s.Key(Right);
        var first = t.Paint(s, w: 120, room: 12);
        Assert.Equal(0, first.Layout.Offset);
        Assert.Equal("  row 1", TermText.StripAnsiForWidth(first.Rows[first.Layout.BodyStart]));

        s.Key(K(ConsoleKey.DownArrow));
        t.Paint(s, w: 120, room: 12);
        s.Key(K(ConsoleKey.DownArrow));
        t.Paint(s, w: 120, room: 12);
        s.Key(Right);
        t.Paint(s, w: 120, room: 12);
        s.Key(Left);

        Assert.Equal(2, t.Paint(s, w: 120, room: 12).Layout.Offset);
    }

    //the arrows cannot switch from the box, so a press on a tab leaves it the same way, the filter kept
    [Fact]
    public void A_tab_press_from_the_box_leaves_the_box_and_the_next_arrow_switches()
    {
        var t = new Tabbed();
        var s = t.State();
        var f = t.Paint(s);
        s.Key(C('?'));
        Type(s, "1");
        f = t.Paint(s);
        var tab = f.Layout.Hits.First(h => h.Hit is PanelHit.Tab { Index: 1 });

        s.Mouse(new MouseEvent(tab.From, 0, MouseKind.Press, MouseButton.Left, 0, 0), tab.Row, 80, () => { });
        s.Key(PanelState.Wake);
        Assert.False(s.View().InBox);
        Assert.Equal(1, s.View().Tab);

        Assert.Equal(PanelStep.Changed, s.Key(Right));
        s.Key(Left);
        s.Key(Left);
        Assert.Equal("1", s.View().Draft);
    }

    [Fact]
    public void A_filter_showing_nothing_draws_the_tab_s_own_keys_with_no_row_act()
    {
        var t = new Tabbed();
        t.Extensions.AddRange(Enumerable.Range(0, 10).Select(i => new SelectOption($"ext{i}", Key: $"ext{i}")));
        var s = t.State();
        s.Key(Right);
        s.Key(Right);
        t.Paint(s);
        s.Key(C('?'));
        Type(s, "zz");
        s.Key(EscKey);
        s.Key(EscKey);
        Type(s, "?zz");
        s.Key(EnterKey);

        Assert.Equal("←→ tabs   Esc close", KeyRow(t.Paint(s)));
    }

    //the row the user saw and the row as it stands must both name the key, so a key row that lags a turn's edge never acts unnamed
    [Fact]
    public void Space_needs_the_published_row_and_the_live_row_to_name_it()
    {
        var t = new Tabbed();
        var s = t.State();
        s.Key(Right);
        s.Key(Right);
        t.Paint(s);

        t.SpaceNamed = false;
        Assert.Equal(PanelStep.None, s.Key(SpaceKey));
        t.Paint(s);
        t.SpaceNamed = true;
        Assert.Equal(PanelStep.None, s.Key(SpaceKey));
        Assert.Empty(t.Acts);

        t.Paint(s);
        Assert.Equal(PanelStep.Changed, s.Key(SpaceKey));
        Assert.Single(t.Acts);
    }

    //a toggle on one tab changes another tab's count, which rebuilds that tab when it is next shown instead of ending the show
    [Fact]
    public void A_count_changed_on_an_inactive_tab_rebuilds_it_on_arrival_with_its_filter_and_cursor_kept()
    {
        var t = new Tabbed();
        var s = t.State();
        t.Paint(s);
        s.Key(C('?'));
        Type(s, "1");
        s.Key(EnterKey);
        s.Key(K(ConsoleKey.DownArrow));
        t.Paint(s);
        var before = s.OptionsOf(0)[s.CursorIndex].Key;
        s.Key(Right);
        t.Paint(s);
        t.Commands.RemoveAt(1);   //an earlier row that matched the filter
        s.Key(Left);

        var view = s.View();
        Assert.False(s.Stale);
        Assert.Equal("1", view.Draft);
        Assert.Equal(t.Commands.Count, s.OptionsOf(0).Count);
        Assert.Equal(before, s.OptionsOf(0)[s.CursorIndex].Key);
        Assert.All(view.Shown, i => Assert.Contains("1", s.OptionsOf(0)[i].Label));
    }
}
