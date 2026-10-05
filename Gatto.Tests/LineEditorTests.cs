using Gatto.Repl.Input;
using Gatto.Terminal;

namespace Gatto.Tests;

public class LineEditorTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gatto-le").FullName;
    private History H() => new(Path.Combine(_dir, "history.txt"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    //the editor drains an IComposerSource, so these key-only fakes wrap each key in a ComposerInput.Key
    private sealed class ScriptedKeys(IEnumerable<ConsoleKeyInfo> keys) : IComposerSource
    {
        private readonly Queue<ConsoleKeyInfo> _q = new(keys);
        public bool KeyAvailable => _q.Count > 0 && _availableOverride;
        private bool _availableOverride = false;
        public ScriptedKeys WithAvailable() { _availableOverride = true; return this; }
        public ComposerInput Read() => new ComposerInput.Key(_q.Dequeue());
    }

    private sealed class ThrowingKeys : IComposerSource
    {
        public bool KeyAvailable => false;
        public ComposerInput Read() => throw new InvalidOperationException("console input redirected");
    }

    //each item says whether another key is buffered after it. a burst sets true on every char except the last, a pause sets false
    private sealed class BurstKeys(IEnumerable<(ConsoleKeyInfo Key, bool AvailAfter)> items) : IComposerSource
    {
        private readonly Queue<(ConsoleKeyInfo Key, bool AvailAfter)> _q = new(items);
        private bool _lastAvail;
        public bool KeyAvailable => _lastAvail;
        public ComposerInput Read() { var (k, a) = _q.Dequeue(); _lastAvail = a; return new ComposerInput.Key(k); }
    }

    private sealed class FlakyThenScripted(int failures, IEnumerable<ConsoleKeyInfo> keys) : IComposerSource
    {
        private int _failures = failures;
        private readonly Queue<ConsoleKeyInfo> _q = new(keys);
        public bool KeyAvailable => false;
        public ComposerInput Read() =>
            _failures-- > 0 ? throw new IOException("transient") : new ComposerInput.Key(_q.Dequeue());
    }

    private static ConsoleKeyInfo K(char c) => new(c, CharToKey(c), false, false, false);

    //alt+v arrives with a printable character, so it needs its own case, and a blanket ignore of alt would break altgr's characters

    private static ConsoleKeyInfo AltKey(char c, ConsoleKey k, bool ctrl = false) =>
        new(c, k, shift: false, alt: true, control: ctrl);

    [Fact]
    public void Backspace_deletes_a_whole_image_placeholder()
    {
        //one character at a time would leave [Image #7, which reads as an attachment and isn't one. backspace must take the whole placeholder
        var keys = new ScriptedKeys(new[] { Key(ConsoleKey.Backspace), Enter() });
        var editor = new LineEditor(keys, H());

        //seed through initial, Read re-seeds the buffer, while production inserts from the paste handler during Read
        Assert.Equal("hi ", editor.Read(_ => { }, initial: "hi [Image #7]"));
    }

    [Fact]
    public void Backspace_after_a_placeholder_deletes_only_that_placeholder()
    {
        var keys = new ScriptedKeys(new[] { Key(ConsoleKey.Backspace), Enter() });
        var editor = new LineEditor(keys, H());

        //seed through initial, Read re-seeds the buffer, while production inserts from the paste handler during Read
        Assert.Equal("[Image #1] and ", editor.Read(_ => { }, initial: "[Image #1] and [Image #2]"));
    }

    [Fact]
    public void Backspace_is_ordinary_when_the_cursor_is_not_after_a_placeholder()
    {
        //the whole-token rule is anchored at the cursor, text that merely holds a placeholder still edits one character at a time
        var keys = new ScriptedKeys(new[] { Key(ConsoleKey.Backspace), Enter() });
        var editor = new LineEditor(keys, H());

        //seed through initial, Read re-seeds the buffer, while production inserts from the paste handler during Read
        Assert.Equal("[Image #1] tai", editor.Read(_ => { }, initial: "[Image #1] tail"));
    }

    [Fact]
    public void Backspace_does_not_eat_a_bracket_that_is_not_a_placeholder()
    {
        var keys = new ScriptedKeys(new[] { Key(ConsoleKey.Backspace), Enter() });
        var editor = new LineEditor(keys, H());

        //seed through initial, Read re-seeds the buffer, while production inserts from the paste handler during Read
        Assert.Equal("see [note", editor.Read(_ => { }, initial: "see [note]"));
    }

    [Fact]
    public void AltV_pastes_and_does_NOT_type_a_v()
    {
        var keys = new ScriptedKeys(new[] { AltKey('v', ConsoleKey.V), K('h'), K('i'), Enter() });
        var editor = new LineEditor(keys, H());
        var pasted = 0;
        editor.OnPasteImage = () => pasted++;

        var text = editor.Read(_ => { });

        Assert.Equal(1, pasted);
        Assert.Equal("hi", text);   //hi alone proves the v never reached the buffer
    }

    [Fact]
    public void AltGr_composed_characters_STILL_type()
    {
        //altgr is ctrl+alt, so a blanket ignore of alt would swallow these and break typing on european layouts
        var keys = new ScriptedKeys(new[]
        {
            AltKey('@', ConsoleKey.D2, ctrl: true),
            AltKey('v', ConsoleKey.V, ctrl: true),   //altgr plus v types a character, so it must not fire the paste chord
            Enter(),
        });
        var editor = new LineEditor(keys, H());
        var pasted = 0;
        editor.OnPasteImage = () => pasted++;

        var text = editor.Read(_ => { });

        Assert.Equal(0, pasted);
        Assert.Equal("@v", text);
    }

    [Fact]
    public void AltV_types_a_v_when_image_paste_is_not_wired()
    {
        //with no paste handler wired, alt+v still types a plain v
        var keys = new ScriptedKeys(new[] { AltKey('v', ConsoleKey.V), Enter() });
        Assert.Equal("v", new LineEditor(keys, H()).Read(_ => { }));
    }

    private static ConsoleKeyInfo Enter(bool shift = false) => new('\r', ConsoleKey.Enter, shift, false, false);
    private static ConsoleKeyInfo Key(ConsoleKey k, bool ctrl = false) => new('\0', k, false, false, ctrl);
    private static ConsoleKey CharToKey(char c) => char.IsAsciiLetterUpper(char.ToUpperInvariant(c))
        ? (ConsoleKey)char.ToUpperInvariant(c) : ConsoleKey.Spacebar;

    private static string Run(IComposerSource keys, History h, out List<EditorView> views)
    {
        var editor = new LineEditor(keys, h);
        var captured = new List<EditorView>();
        var result = editor.Read(v => captured.Add(v));
        views = captured;
        return result;
    }

    [Fact]
    public void TypeAndSubmit()
    {
        var keys = new ScriptedKeys(new[] { K('h'), K('i'), Enter() });
        Assert.Equal("hi", Run(keys, H(), out _));
    }

    [Fact]
    public void BackslashEnter_InsertsNewline_ConsumesBackslash()
    {
        var keys = new ScriptedKeys(new[] { K('a'), K('\\'), Enter(), K('b'), Enter() });
        Assert.Equal("a\nb", Run(keys, H(), out _));
    }

    [Fact]
    public void ShiftEnter_InsertsNewline()
    {
        var keys = new ScriptedKeys(new[] { K('a'), Enter(shift: true), K('b'), Enter() });
        Assert.Equal("a\nb", Run(keys, H(), out _));
    }

    [Fact]
    public void UpDown_MovesBetweenLines_HistoryOnlyAtEdges()
    {
        var h = H();
        h.Record("old entry");
        //the cursor ends on line 1, so the first up arrow moves to line 0 and the second pulls history
        var keys = new ScriptedKeys(new[]
        {
            K('x'), Enter(shift: true), K('y'),
            Key(ConsoleKey.UpArrow), Key(ConsoleKey.UpArrow),
            Enter(),
        });
        Assert.Equal("old entry", Run(keys, h, out var views));
        Assert.Contains(views, v => v.Lines.Count == 2 && v.CursorLine == 0);  //this is the mid-step view, cursor on line 0 before history was pulled
    }

    [Fact]
    public void BackspaceAtCol0_JoinsLines()
    {
        var keys = new ScriptedKeys(new[]
        {
            K('a'), Enter(shift: true), K('b'),
            Key(ConsoleKey.Home), Key(ConsoleKey.Backspace),
            Enter(),
        });
        Assert.Equal("ab", Run(keys, H(), out _));
    }

    [Fact]
    public void CtrlU_ClearsAll()
    {
        var keys = new ScriptedKeys(new[]
        {
            K('a'), Enter(shift: true), K('b'), Key(ConsoleKey.U, ctrl: true), K('z'), Enter(),
        });
        Assert.Equal("z", Run(keys, H(), out _));
    }

    [Fact]
    public void CtrlW_DeletesWordBeforeCursor()
    {
        var keys = new ScriptedKeys(new[]
        {
            K('a'), K('b'), K(' '), K('c'), K('d'), Key(ConsoleKey.W, ctrl: true), Enter(),
        });
        Assert.Equal("ab ", Run(keys, H(), out _));
    }

    [Fact]
    public void AltGrChar_MappedToU_InsertsInsteadOfClearingBuffer()
    {
        //windows sends altgr as ctrl+alt, so ú with altgr+u looks like ctrl+alt+u and must insert instead of clearing the buffer
        var keys = new ScriptedKeys(new[]
        {
            K('a'), new ConsoleKeyInfo('ú', ConsoleKey.U, false, true, true), K('b'), Enter(),
        });
        Assert.Equal("aúb", Run(keys, H(), out _));
    }

    [Fact]
    public void AltGrChar_MappedToW_InsertsInsteadOfDeletingWord()
    {
        //altgr+w types ł on the polish layout and windows sends it as ctrl+alt, so the character must insert rather than delete a word
        var keys = new ScriptedKeys(new[]
        {
            K('o'), K('n'), K('e'), K(' '), K('t'), K('w'), K('o'), K(' '),
            new ConsoleKeyInfo('ł', ConsoleKey.W, false, true, true), Enter(),
        });
        Assert.Equal("one two ł", Run(keys, H(), out _));
    }

    [Fact]
    public void RealCtrlU_StillClearsBuffer()
    {
        var keys = new ScriptedKeys(new[]
        {
            K('a'), K('b'), K('c'), Key(ConsoleKey.U, ctrl: true), K('z'), Enter(),
        });
        Assert.Equal("z", Run(keys, H(), out _));
    }

    [Fact]
    public void RealCtrlW_StillDeletesWordBack()
    {
        var keys = new ScriptedKeys(new[]
        {
            K('o'), K('n'), K('e'), K(' '), K('t'), K('w'), K('o'), Key(ConsoleKey.W, ctrl: true), Enter(),
        });
        Assert.Equal("one ", Run(keys, H(), out _));
    }

    [Fact]
    public void PasteEnter_MidBurst_InsertsNewline()
    {
        //an enter with another key buffered after it reads as mid-paste and splits the line instead of submitting
        var keys = new BurstKeys(new[]
        {
            (K('a'), true), (Enter(), true), (K('b'), false),
            (Enter(), false),   //nothing is buffered after it, so this one submits
        });
        Assert.Equal("a\nb", Run(keys, H(), out _));
    }

    [Fact]
    public void PasteTrailingNewline_DoesNotAutoSubmit_StripsIt()
    {
        //pasted text ending in a newline must not auto-send, a trailing enter with nothing after it is stripped
        var keys = new BurstKeys(new[]
        {
            (K('h'), true), (K('e'), true), (K('l'), true), (K('l'), true), (K('o'), true),
            (Enter(), false),                                                     //this enter ends the pasted burst, so it is stripped
            (K(' '), false), (K('w'), false), (K('o'), false), (K('r'), false), (K('l'), false), (K('d'), false),
            (Enter(), false),                                                     //nothing is buffered after it, so this one submits
        });
        Assert.Equal("hello world", Run(keys, H(), out _));
    }

    [Fact]
    public void PasteMultiline_SplitsMidNewlines_StripsTrailingNewline()
    {
        //a mid-paste enter splits the line, and the trailing one is stripped so no blank line is left at the cursor
        var keys = new BurstKeys(new[]
        {
            (K('a'), true), (Enter(), true), (K('b'), true),
            (Enter(), false),                     //this enter ends the pasted burst, so it is stripped
            (K('!'), false), (Enter(), false),    //nothing is buffered after either key, so both are typed on purpose and the enter submits
        });
        Assert.Equal("a\nb!", Run(keys, H(), out _));
    }

    private static readonly ConsoleKeyInfo Tab = new('\t', ConsoleKey.Tab, false, false, false);

    [Fact]
    public void PastedTab_IsKeptInTheMessage()
    {
        //a tab inside a burst is pasted text, so it goes into the buffer instead of asking for a completion
        var keys = new BurstKeys(new[]
        {
            (K('a'), true), (Tab, true), (K('b'), true), (Tab, true), (K('c'), false),
            (Enter(), false),
        });
        Assert.Equal("a\tb\tc", Run(keys, H(), out _));
    }

    [Fact]
    public void TypedTab_WithNoCompletion_StaysANoOp()
    {
        //nothing is buffered around this tab, so it is the completion key and types nothing
        var keys = new BurstKeys(new[] { (K('a'), false), (Tab, false), (K('b'), false), (Enter(), false) });
        Assert.Equal("ab", Run(keys, H(), out _));
    }

    [Fact]
    public void SubmittedText_IsRecordedInHistory()
    {
        var h = H();
        Run(new ScriptedKeys(new[] { K('q'), Enter() }), h, out _);
        Assert.Equal(new[] { "q" }, h.Entries);
    }

    [Fact]
    public void EmptyBuffer_Enter_ReturnsEmpty()
    {
        var keys = new ScriptedKeys(new[] { Enter() });
        Assert.Equal("", Run(keys, H(), out _));
    }

    [Fact]
    public void WhitespaceOnlySubmit_NotRecordedInHistory()
    {
        var h = H();
        Assert.Equal("  ", Run(new ScriptedKeys(new[] { K(' '), K(' '), Enter() }), h, out _));
        Assert.Empty(h.Entries);
    }

    [Fact]
    public void TransientReadKeyFailures_AreSkipped()
    {
        var keys = new FlakyThenScripted(3, new[] { K('h'), K('i'), Enter() });
        Assert.Equal("hi", Run(keys, H(), out _));
    }

    [Fact]
    public void PersistentReadKeyFailure_Escapes_InsteadOfSpinningForever()
    {
        var editor = new LineEditor(new ThrowingKeys(), H());
        Assert.Throws<InvalidOperationException>(() => editor.Read(_ => { }));
    }

    [Fact]
    public void Read_WithInitialSeed_TextSurvives_CursorAtEnd()
    {
        var keys = new ScriptedKeys(new[] { K('x'), Enter() });
        var editor = new LineEditor(keys, H());
        var result = editor.Read(_ => { }, initial: "held");
        Assert.Equal("heldx", result);   //the appended x shows the cursor sat at the end of the seed
    }
    private static ConsoleKeyInfo ShiftKey(ConsoleKey k) => new('\0', k, true, false, false);
    //ctrl+c arrives as ConsoleKey.C with control set, which is what the Key helper builds

    [Fact]
    public void Esc_twice_clears_a_non_empty_composer()
    {
        //without this chord, clearing long pasted text means holding backspace
        var keys = new ScriptedKeys(new[]
        {
            K('h'), K('e'), K('l'), K('l'), K('o'),
            Key(ConsoleKey.Escape), Key(ConsoleKey.Escape), Enter(),
        });
        var editor = new LineEditor(keys, H());
        Assert.Equal("", editor.Read(_ => { }, chords: new ArmedChord()));
    }

    [Fact]
    public void One_Esc_alone_leaves_the_composer_untouched()
    {
        var keys = new ScriptedKeys(new[]
        {
            K('h'), K('e'), K('l'), K('l'), K('o'), Key(ConsoleKey.Escape), Enter(),
        });
        var editor = new LineEditor(keys, H());
        Assert.Equal("hello", editor.Read(_ => { }, chords: new ArmedChord()));
    }
    [Fact]
    public void Ctrl_C_twice_on_an_empty_composer_quits()
    {
        var quit = false;
        //the second ctrl+c fires the quit callback, and the final enter only lets Read return
        var keys = new ScriptedKeys(new[] { Key(ConsoleKey.C, ctrl: true), Key(ConsoleKey.C, ctrl: true), Enter() });
        var editor = new LineEditor(keys, H());
        editor.Read(_ => { }, chords: new ArmedChord(), onQuit: () => quit = true);
        Assert.True(quit);
    }

    [Fact]
    public void Ctrl_C_on_a_NON_empty_composer_does_nothing_at_all()
    {
        //the two chords stay separate, so ctrl+c on a non-empty composer neither clears nor quits
        var quit = false;
        var keys = new ScriptedKeys(new[]
        {
            K('h'), K('e'), K('l'), K('l'), K('o'),
            Key(ConsoleKey.C, ctrl: true), Key(ConsoleKey.C, ctrl: true), Enter(),
        });
        var editor = new LineEditor(keys, H());
        var result = editor.Read(_ => { }, chords: new ArmedChord(), onQuit: () => quit = true);
        Assert.False(quit);
        Assert.Equal("hello", result);
    }

    [Fact]
    public void Esc_then_Ctrl_C_on_an_emptied_composer_does_not_quit()
    {
        //arming one chord disarms the other, so a lone ctrl+c after esc does not quit. the controller's own tests can't show this path
        var quit = false;
        var keys = new ScriptedKeys(new[]
        {
            K('h'), K('i'), Key(ConsoleKey.Escape), Key(ConsoleKey.Escape),
            Key(ConsoleKey.C, ctrl: true), Enter(),
        });
        var editor = new LineEditor(keys, H());
        editor.Read(_ => { }, chords: new ArmedChord(), onQuit: () => quit = true);
        Assert.False(quit);
    }

    [Fact]
    public void Typing_after_arming_disarms_so_a_later_Esc_only_re_arms()
    {
        //typing disarms the armed esc hint, the hint may be off-screen, so a later esc re-arms instead of clearing
        var keys = new ScriptedKeys(new[]
        {
            K('h'), K('i'), Key(ConsoleKey.Escape), K('x'), Key(ConsoleKey.Escape), Enter(),
        });
        var editor = new LineEditor(keys, H());
        Assert.Equal("hix", editor.Read(_ => { }, chords: new ArmedChord()));
    }

    //a ctrl arrow key sets control and clears shift and alt, the shape the editor matches

    private static IEnumerable<ConsoleKeyInfo> Type(string s) => s.Select(K);

    [Fact]
    public void Ctrl_Left_jumps_to_the_start_of_the_previous_word()
    {
        //ctrl+left follows the same boundary as DeleteWordBack, skip spaces then the word, so one jump moves one word
        var keys = new ScriptedKeys(Type("foo bar baz")
            .Append(Key(ConsoleKey.LeftArrow, ctrl: true)).Append(K('X')).Append(Enter()));
        var editor = new LineEditor(keys, H());
        Assert.Equal("foo bar Xbaz", editor.Read(_ => { }));
    }

    [Fact]
    public void Ctrl_Right_jumps_past_the_next_word()
    {
        var keys = new ScriptedKeys(Type("foo bar")
            .Append(Key(ConsoleKey.Home)).Append(Key(ConsoleKey.RightArrow, ctrl: true))
            .Append(K('X')).Append(Enter()));
        var editor = new LineEditor(keys, H());
        Assert.Equal("fooX bar", editor.Read(_ => { }));
    }

    [Fact]
    public void Ctrl_Left_at_column_zero_crosses_to_the_previous_line_end()
    {
        var keys = new ScriptedKeys(Type("ab").Append(Enter(shift: true)).Concat(Type("cd"))
            .Append(Key(ConsoleKey.Home)).Append(Key(ConsoleKey.LeftArrow, ctrl: true))
            .Append(K('X')).Append(Enter()));
        var editor = new LineEditor(keys, H());
        Assert.Equal("abX\ncd", editor.Read(_ => { }));
    }

    //a ctrl+a key sets control and clears shift and alt, the shape the editor matches

    [Fact]
    public void Ctrl_A_selects_all_and_a_printable_replaces_it()
    {
        var keys = new ScriptedKeys(Type("five paragraphs")
            .Append(Key(ConsoleKey.A, ctrl: true)).Append(K('x')).Append(Enter()));
        var editor = new LineEditor(keys, H());
        Assert.Equal("x", editor.Read(_ => { }));
    }

    [Fact]
    public void Ctrl_A_then_Delete_clears_the_composer()
    {
        var keys = new ScriptedKeys(Type("five paragraphs")
            .Append(Key(ConsoleKey.A, ctrl: true)).Append(Key(ConsoleKey.Delete)).Append(Enter()));
        var editor = new LineEditor(keys, H());
        Assert.Equal("", editor.Read(_ => { }));
    }

    [Fact]
    public void Ctrl_A_then_Backspace_clears_the_composer()
    {
        var keys = new ScriptedKeys(Type("abc")
            .Append(Key(ConsoleKey.A, ctrl: true)).Append(Key(ConsoleKey.Backspace)).Append(Enter()));
        var editor = new LineEditor(keys, H());
        Assert.Equal("", editor.Read(_ => { }));
    }

    [Fact]
    public void Ctrl_A_then_Ctrl_C_copies_all_composer_text()
    {
        string? copied = null;
        var keys = new ScriptedKeys(Type("line one").Append(Enter(shift: true))
            .Concat(Type("line two via shift-enter"))
            .Append(Key(ConsoleKey.A, ctrl: true)).Append(Key(ConsoleKey.C, ctrl: true)).Append(Enter()));
        var editor = new LineEditor(keys, H());
        editor.Read(_ => { }, onCopyText: t => copied = t);
        Assert.Equal("line one\nline two via shift-enter", copied);
    }

    [Fact]
    public void A_cursor_move_deselects_so_the_next_key_does_not_replace()
    {
        //a cursor move after ctrl+a deselects and still moves, so the next key inserts. left and right collapse the selection and return early
        var keys = new ScriptedKeys(Type("abc")
            .Append(Key(ConsoleKey.A, ctrl: true)).Append(Key(ConsoleKey.End))
            .Append(K('X')).Append(Enter()));
        var editor = new LineEditor(keys, H());
        var result = editor.Read(_ => { });
        Assert.NotEqual("X", result);      //the result is not a lone x, so the move consumed the selection
        Assert.Equal("abcX", result);      //abcX shows the ordinary end move still ran
    }

    [Fact]
    public void Esc_deselects_a_select_all_and_the_text_survives()
    {
        //one esc drops the selection range and leaves the text alone, and the same press arms composer-clear
        var views = new List<EditorView>();
        var keys = new ScriptedKeys(Type("abc")
            .Append(Key(ConsoleKey.A, ctrl: true)).Append(Key(ConsoleKey.Escape)).Append(Enter()));
        var editor = new LineEditor(keys, H());
        Assert.Equal("abc", editor.Read(views.Add));
        Assert.Contains(views, v => v.SelStart == (0, 0) && v.SelEnd == (0, 3));   //the range must exist before esc drops it, so this checks the selection was really there
        Assert.Null(views[^1].SelStart);                                          //the last view has no selection, esc dropped the range
    }

    [Fact]
    public void Ctrl_A_on_an_empty_composer_selects_nothing()
    {
        //the result string is a weak oracle, the replace branch inserts x even with the empty guard gone. assert that no view has a SelStart
        var views = new List<EditorView>();
        var keys = new ScriptedKeys(new[] { Key(ConsoleKey.A, ctrl: true), K('x'), Enter() });
        var editor = new LineEditor(keys, H());
        var result = editor.Read(views.Add);
        Assert.Equal("x", result);
        Assert.DoesNotContain(views, v => v.SelStart is not null);
    }

    //these tests set selection state through the test-only ForTest seams rather than through real gestures

    private LineEditor EditorWith(string joined)
    {
        var editor = new LineEditor(new ScriptedKeys(Array.Empty<ConsoleKeyInfo>()), H());
        editor.SeedForTest(joined);
        return editor;
    }

    [Fact]
    public void Backspace_on_a_single_line_range_deletes_it_and_clears_the_selection()
    {
        var ed = EditorWith("hello world");
        ed.SetComposerSelectionForTest((0, 5), (0, 11));   //cols 5 to 11 covers the space and world
        ed.HandleForTest(Key(ConsoleKey.Backspace));
        Assert.Equal(new[] { "hello" }, ed.LinesForTest);
        Assert.False(ed.HasComposerSelectionForTest);
    }

    [Fact]
    public void Backspace_on_a_multiline_range_deletes_it_and_clears_the_selection()
    {
        var ed = EditorWith("alpha\nbeta\ngamma");
        ed.SetComposerSelectionForTest((0, 2), (2, 3));   //anchor at (0,2) and caret at (2,3), so the range spans lines 0 to 2
        ed.HandleForTest(Key(ConsoleKey.Backspace));
        //the kept text is line 0 up to col 2 joined to line 2 from col 3, with the middle line dropped
        Assert.Equal(new[] { "alma" }, ed.LinesForTest);
        Assert.False(ed.HasComposerSelectionForTest);
    }

    [Fact]
    public void Delete_key_on_a_range_deletes_it_the_same_as_backspace()
    {
        var ed = EditorWith("hello world");
        ed.SetComposerSelectionForTest((0, 0), (0, 6));   //the six leading chars, space included
        ed.HandleForTest(Key(ConsoleKey.Delete));
        Assert.Equal(new[] { "world" }, ed.LinesForTest);
        Assert.False(ed.HasComposerSelectionForTest);
    }

    [Fact]
    public void A_reversed_anchor_after_the_caret_still_deletes_the_normalized_range()
    {
        //the second argument is the caret. the SelRange result is normalized whichever end was dragged last
        var ed = EditorWith("hello world");
        ed.SetComposerSelectionForTest((0, 11), (0, 5));   //the anchor is at the end, the caret dragged back to 5
        ed.HandleForTest(Key(ConsoleKey.Backspace));
        Assert.Equal(new[] { "hello" }, ed.LinesForTest);
    }

    [Fact]
    public void A_range_touching_buffer_start_and_end_deletes_everything()
    {
        var ed = EditorWith("alpha\nbeta\ngamma");
        ed.SetComposerSelectionForTest((0, 0), (2, 5));   //the whole buffer, first char to last
        ed.HandleForTest(Key(ConsoleKey.Backspace));
        Assert.Equal(new[] { "" }, ed.LinesForTest);
        Assert.False(ed.HasComposerSelectionForTest);
    }

    [Fact]
    public void A_mid_line_range_on_a_long_line_deletes_correctly()
    {
        //a range indexes logical lines, so wrap boundaries only matter at render time. a long line exercises the same splice math
        var longLine = new string('a', 40) + "MIDDLE" + new string('b', 40);
        var ed = EditorWith(longLine);
        ed.SetComposerSelectionForTest((0, 40), (0, 46));   //cols 40 to 46, the MIDDLE text
        ed.HandleForTest(Key(ConsoleKey.Backspace));
        Assert.Equal(new[] { new string('a', 40) + new string('b', 40) }, ed.LinesForTest);
    }

    [Fact]
    public void Typing_over_a_range_replaces_it_with_the_typed_char()
    {
        var ed = EditorWith("hello world");
        ed.SetComposerSelectionForTest((0, 6), (0, 11));   //cols 6 to 11, the word world
        ed.HandleForTest(K('X'));
        Assert.Equal(new[] { "hello X" }, ed.LinesForTest);
        Assert.False(ed.HasComposerSelectionForTest);
    }

    [Fact]
    public void CtrlC_over_a_range_copies_it_and_the_selection_survives()
    {
        var ed = EditorWith("alpha\nbeta\ngamma");
        ed.SetComposerSelectionForTest((0, 2), (2, 3));
        string? copied = null;
        ed.HandleForTest(Key(ConsoleKey.C, ctrl: true), onCopyText: t => copied = t);
        Assert.Equal("pha\nbeta\ngam", copied);
        Assert.True(ed.HasComposerSelectionForTest);   //copying keeps the selection in place, a delete drops it
    }

    [Fact]
    public void A_plain_arrow_over_a_range_only_collapses_it_and_still_moves()
    {
        //the arrow deleted nothing, the caret ends in the same spot either way
        var ed = EditorWith("abc");
        ed.SetComposerSelectionForTest((0, 0), (0, 3));
        ed.HandleForTest(Key(ConsoleKey.LeftArrow));
        Assert.False(ed.HasComposerSelectionForTest);
        Assert.Equal(new[] { "abc" }, ed.LinesForTest);   //the text is intact, so the arrow only deselected and moved.
    }

    [Fact]
    public void BeginComposerSelect_then_ExtendComposerSelect_builds_a_range()
    {
        var ed = EditorWith("hello world");
        ed.BeginComposerSelect(0, 0);
        Assert.True(ed.HasComposerSelection);
        ed.ExtendComposerSelect(0, 5);
        ed.HandleForTest(Key(ConsoleKey.Backspace));
        Assert.Equal(new[] { " world" }, ed.LinesForTest);
    }

    [Fact]
    public void ClearComposerSelection_drops_a_live_selection()
    {
        var ed = EditorWith("hello world");
        ed.BeginComposerSelect(0, 0);
        ed.ExtendComposerSelect(0, 5);
        ed.ClearComposerSelection();
        Assert.False(ed.HasComposerSelection);
    }

    [Fact]
    public void BeginComposerSelect_clamps_an_out_of_range_line_and_col()
    {
        var ed = EditorWith("hi");
        ed.BeginComposerSelect(99, 99);   //the line and col come from a layout that went stale when the buffer shrank
        Assert.True(ed.HasComposerSelection);
        ed.HandleForTest(Key(ConsoleKey.Backspace));
        Assert.Equal(new[] { "hi" }, ed.LinesForTest);   //the position clamps to (0,2), so the range is empty and nothing is deleted
    }

    [Fact]
    public void Shift_Right_extends_a_selection_from_the_caret_and_a_type_replaces_it()
    {
        var keys = new ScriptedKeys(Type("abc")
            .Append(Key(ConsoleKey.Home))
            .Append(ShiftKey(ConsoleKey.RightArrow)).Append(ShiftKey(ConsoleKey.RightArrow))
            .Append(K('X')).Append(Enter()));
        var editor = new LineEditor(keys, H());
        Assert.Equal("Xc", editor.Read(_ => { }));
    }

    [Fact]
    public void Shift_Left_extends_a_selection_backward_from_the_caret()
    {
        var keys = new ScriptedKeys(Type("abc")
            .Append(ShiftKey(ConsoleKey.LeftArrow)).Append(ShiftKey(ConsoleKey.LeftArrow))
            .Append(Key(ConsoleKey.Backspace)).Append(Enter()));
        var editor = new LineEditor(keys, H());
        Assert.Equal("a", editor.Read(_ => { }));
    }

    [Fact]
    public void Shift_arrow_on_an_empty_composer_neither_arms_a_selection_nor_fires_the_cross_clear()
    {
        //shift+arrow on an empty buffer arms no range and fires no selection-set callback, which would wipe a live transcript selection
        var keys = new ScriptedKeys(new[]
        {
            ShiftKey(ConsoleKey.RightArrow), ShiftKey(ConsoleKey.LeftArrow), Enter(),
        });
        var editor = new LineEditor(keys, H());
        var crossClears = 0;
        var result = editor.Read(_ => { }, onComposerSelectionSet: () => crossClears++);
        Assert.Equal("", result);
        Assert.Equal(0, crossClears);
    }

    //a plain left or right key collapses a live selection to the near edge and takes no extra step

    [Fact]
    public void A_plain_Right_after_a_selection_collapses_to_the_end_edge()
    {
        //a plain right collapses to the selection's end edge, so the x is typed between b and c
        var keys = new ScriptedKeys(Type("abc")
            .Append(Key(ConsoleKey.Home))
            .Append(ShiftKey(ConsoleKey.RightArrow)).Append(ShiftKey(ConsoleKey.RightArrow))
            .Append(Key(ConsoleKey.RightArrow))
            .Append(K('X')).Append(Enter()));
        var editor = new LineEditor(keys, H());
        Assert.Equal("abXc", editor.Read(_ => { }));
    }

    [Fact]
    public void A_plain_Left_after_a_selection_collapses_to_the_start_edge()
    {
        //a plain left collapses to the selection's start edge, so the x is typed before a
        var keys = new ScriptedKeys(Type("abc")
            .Append(Key(ConsoleKey.Home))
            .Append(ShiftKey(ConsoleKey.RightArrow)).Append(ShiftKey(ConsoleKey.RightArrow))
            .Append(Key(ConsoleKey.LeftArrow))
            .Append(K('X')).Append(Enter()));
        var editor = new LineEditor(keys, H());
        Assert.Equal("Xabc", editor.Read(_ => { }));
    }

    [Fact]
    public void View_reports_a_normalized_SelStart_SelEnd_while_a_selection_is_live()
    {
        var views = new List<EditorView>();
        var keys = new ScriptedKeys(Type("abc")
            .Append(Key(ConsoleKey.A, ctrl: true)).Append(Enter()));
        var editor = new LineEditor(keys, H());
        editor.Read(views.Add);
        Assert.Contains(views, v => v.SelStart == (0, 0) && v.SelEnd == (0, 3));
    }

    //the editor drains the ComposerInput union, and gestures arrive resolved, so no layout work runs here and one thread mutates the state

    private sealed class ScriptedInputs(IEnumerable<ComposerInput> items) : IComposerSource
    {
        private readonly Queue<ComposerInput> _q = new(items);
        public bool KeyAvailable => false;
        public ComposerInput Read() => _q.Dequeue();
    }

    [Fact]
    public void Keys_and_gestures_interleaved_on_one_channel_mutate_one_state_deterministically()
    {
        //the gesture builds a one-char range over the fresh a, and backspace consumes exactly it, so this checks ordering on one writer
        var input = new ScriptedInputs(new ComposerInput[]
        {
            new ComposerInput.Key(K('a')),
            new ComposerInput.Select(0, 0, ComposerGesture.Begin),
            new ComposerInput.Select(0, 1, ComposerGesture.Extend),
            new ComposerInput.Key(Key(ConsoleKey.Backspace)),
        });
        var editor = new LineEditor(input, H());
        List<string>? lastLines = null;
        //the script never sends enter, so the queue drains and Read throws the empty-dequeue exception, which ends this test on purpose
        Assert.Throws<InvalidOperationException>(() => editor.Read(v => lastLines = v.Lines.ToList()));
        Assert.Equal(new[] { "" }, lastLines);
    }

    //the composer side of selection exclusion is pinned here. a clear gesture drops a live selection, and every site that sets one fires the cross-clear hook

    [Fact]
    public void A_Clear_gesture_drops_a_live_composer_selection_and_ignores_its_line_col()
    {
        //a clear gesture from a non-composer drag has no meaningful line or column, so 99, 99 proves the values are ignored
        var input = new ScriptedInputs(new ComposerInput[]
        {
            new ComposerInput.Select(0, 0, ComposerGesture.Begin),
            new ComposerInput.Select(0, 3, ComposerGesture.Extend),
            new ComposerInput.Select(99, 99, ComposerGesture.Clear),
        });
        var editor = new LineEditor(input, H());
        var views = new List<EditorView>();
        //no enter is scripted, so the drained queue makes Read rethrow, which ends the test on purpose
        Assert.Throws<InvalidOperationException>(() => editor.Read(views.Add));
        Assert.False(editor.HasComposerSelectionForTest);
        Assert.Null(views[^1].SelStart);
    }

    [Fact]
    public void Ctrl_A_fires_the_composer_selection_set_hook()
    {
        //this pins only that ctrl+a fires the hook, since the repl's wiring of it to the transcript belongs to another test
        var fired = 0;
        var keys = new ScriptedKeys(Type("abc").Append(Key(ConsoleKey.A, ctrl: true)).Append(Enter()));
        var editor = new LineEditor(keys, H());
        editor.Read(_ => { }, onComposerSelectionSet: () => fired++);
        Assert.Equal(1, fired);
    }

    [Fact]
    public void Shift_arrow_fires_the_hook_only_on_the_first_press_that_arms_the_anchor()
    {
        //extending a live selection does not re-fire the cross-clear hook, which would take the paint gate on every shift+arrow
        var fired = 0;
        var keys = new ScriptedKeys(Type("abc")
            .Append(Key(ConsoleKey.Home))
            .Append(ShiftKey(ConsoleKey.RightArrow)).Append(ShiftKey(ConsoleKey.RightArrow))
            .Append(Enter()));
        var editor = new LineEditor(keys, H());
        editor.Read(_ => { }, onComposerSelectionSet: () => fired++);
        Assert.Equal(1, fired);
    }

    [Fact]
    public void A_mouse_driven_BeginComposerSelect_gesture_fires_the_composer_selection_set_hook()
    {
        //a mouse-started begin gesture fires the same hook as ctrl+a, otherwise a live transcript span stays highlighted
        var fired = 0;
        var input = new ScriptedInputs(new ComposerInput[] { new ComposerInput.Select(0, 0, ComposerGesture.Begin) });
        var editor = new LineEditor(input, H());
        Assert.Throws<InvalidOperationException>(() => editor.Read(_ => { }, onComposerSelectionSet: () => fired++));
        Assert.Equal(1, fired);
    }

    //these tests drive the composer's key path, the ghost that shows the same candidates belongs to InputFrame

    private string Typed(string initial, params ConsoleKeyInfo[] keys) =>
        new LineEditor(new ScriptedKeys(keys.Append(Enter())), H()).Read(_ => { }, initial: initial);

    [Fact]
    public void Tab_completes_a_prefix_that_matches_one_command()
    {
        Assert.Equal("/model", Typed("/m", Key(ConsoleKey.Tab)));
    }

    //tab types only the command name, the bracketed argument part never enters the buffer
    [Fact]
    public void Tab_types_the_name_and_never_the_arguments()
    {
        Assert.Equal("/model", Typed("/mo", Key(ConsoleKey.Tab)));
        Assert.DoesNotContain("[", Typed("/mo", Key(ConsoleKey.Tab)));
    }

    //cycling follows the windows shell convention, and the one ambiguous prefix /r returns to the first candidate on the third tab
    [Fact]
    public void Tab_cycles_the_ambiguous_prefix_and_wraps()
    {
        Assert.Equal("/role", Typed("/r", Key(ConsoleKey.Tab)));
        Assert.Equal("/remember", Typed("/r", Key(ConsoleKey.Tab), Key(ConsoleKey.Tab)));
        Assert.Equal("/role", Typed("/r", Key(ConsoleKey.Tab), Key(ConsoleKey.Tab), Key(ConsoleKey.Tab)));
    }

    //an edit between tabs resets the cycle index, or it completes against a stale token. the fixture returns to /r, a one-candidate prefix hides the difference
    [Fact]
    public void An_edit_between_tabs_restarts_the_cycle()
    {
        Assert.Equal("/role", Typed("/r",
            Key(ConsoleKey.Tab),
            Key(ConsoleKey.Backspace), Key(ConsoleKey.Backspace), Key(ConsoleKey.Backspace),   //three backspaces leave the ambiguous prefix /r
            Key(ConsoleKey.Tab)));
    }

    [Fact]
    public void Tab_stays_the_no_op_it_has_always_been_when_nothing_completes()
    {
        Assert.Equal("/zz", Typed("/zz", Key(ConsoleKey.Tab)));
        Assert.Equal("hello", Typed("hello", Key(ConsoleKey.Tab)));
        Assert.Equal("", Typed("", Key(ConsoleKey.Tab)));                  //with no completion, tab inserts nothing at all
        Assert.Equal("//m", Typed("//m", Key(ConsoleKey.Tab)));            //text starting with // is an escape hatch, so tab leaves it alone
    }

    //completion is anchored at the end of the line, so a tab pressed mid-token rewrites nothing
    [Fact]
    public void Tab_does_nothing_when_the_cursor_is_not_at_the_end()
    {
        Assert.Equal("/m", Typed("/m", Key(ConsoleKey.LeftArrow), Key(ConsoleKey.Tab)));
    }

    //two reads from one editor are the only shape that shows the reset, otherwise the second submission continues the old cycle and types /remember
    [Fact]
    public void A_cycle_does_not_survive_into_the_next_submission()
    {
        var keys = new ScriptedKeys(new[] { Key(ConsoleKey.Tab), Enter(), Key(ConsoleKey.Tab), Enter() });
        var editor = new LineEditor(keys, H());

        Assert.Equal("/role", editor.Read(_ => { }, initial: "/r"));
        Assert.Equal("/role", editor.Read(_ => { }, initial: "/r"));
    }
}
