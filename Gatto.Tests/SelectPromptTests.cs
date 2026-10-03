using System.Text.RegularExpressions;
using Gatto.Repl;
using Gatto.Repl.Input;
using Gatto.Repl.Render;
using Gatto.Repl.Term;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests;

//every rendering oracle asserts through the live render path. a helper that rebuilds the row format would prove nothing.
public class SelectPromptTests
{
    //keys come from a queue and empty one per read. reading an empty queue throws, so over-reading fails loudly.
    private sealed class ScriptedKeys(IEnumerable<ConsoleKeyInfo> keys) : IKeySource
    {
        private readonly Queue<ConsoleKeyInfo> _q = new(keys);
        public bool KeyAvailable => false;
        public ConsoleKeyInfo ReadKey() => _q.Count > 0
            ? _q.Dequeue()
            : throw new InvalidOperationException("test scripted too few keys");
    }

    //like ScriptedKeys, but a hook runs before each key, the only window to change state between two renders
    private sealed class ProbingKeys(Action<int> beforeRead, IEnumerable<ConsoleKeyInfo> keys) : IKeySource
    {
        private readonly Queue<ConsoleKeyInfo> _q = new(keys);
        private int _n;
        public bool KeyAvailable => false;
        public ConsoleKeyInfo ReadKey()
        {
            beforeRead(_n++);
            return _q.Count > 0 ? _q.Dequeue() : throw new InvalidOperationException("test scripted too few keys");
        }
    }

    private sealed class ThrowingKeys : IKeySource
    {
        public bool KeyAvailable => false;
        public ConsoleKeyInfo ReadKey() => throw new InvalidOperationException("pump dead");
    }

    //each wait advances the elapsed time by the budget. a watch that spans hours then runs in microseconds, with no machine clock involved
    private sealed class ScriptedClock(int? keyAfter = null) : IPollClock
    {
        public int Waits { get; private set; }
        public long ElapsedMs { get; private set; }

        public bool WaitForKey(TimeSpan budget)
        {
            Waits++;
            ElapsedMs += (long)budget.TotalMilliseconds;
            return keyAfter is { } n && Waits >= n;
        }
    }

    private static ConsoleKeyInfo Special(ConsoleKey k) => new('\0', k, false, false, false);
    private static ConsoleKeyInfo Digit(char d) => new(d, (ConsoleKey)((int)ConsoleKey.D0 + (d - '0')), false, false, false);
    private static ConsoleKeyInfo Space() => new(' ', ConsoleKey.Spacebar, false, false, false);
    private static ConsoleKeyInfo Letter(char c) => new(c, (ConsoleKey)((int)ConsoleKey.A + (char.ToUpperInvariant(c) - 'A')), false, false, false);

    //builds one key per char with the char in the char field, as a real terminal delivers it.
    private static ConsoleKeyInfo[] Type(string s) =>
        s.Select(c => new ConsoleKeyInfo(c, ConsoleKey.None, false, false, false)).ToArray();

    //tab arrives as ConsoleKey.Tab with the char field set, as a real terminal does
    private static ConsoleKeyInfo Tab() => new('\t', ConsoleKey.Tab, false, false, false);

    //a regex for the cursor-up escape of the inline path.
    private const string CursorUp = "\\u001b\\[(\\d+)A";

    private static readonly Theme T = new(new TermCaps(true, true));

    private static SelectSpec Spec(params SelectOption[] options) =>
        new(["Write new file test.txt"], "Do you want to proceed?", options);

    private static (RecordingSurface Surface, SelectOutcome Outcome) Run(SelectSpec spec, params ConsoleKeyInfo[] keys)
    {
        var surface = new RecordingSurface();
        var widget = new SelectPrompt(surface, T, new ScriptedKeys(keys));
        return (surface, widget.Show(spec));
    }

    private static int Chosen(SelectOutcome outcome) => Assert.IsType<SelectOutcome.Chosen>(outcome).Index;

    //a watching screen resolves with no key typed, so nothing is scripted and a widget that blocks on ReadKey throws here
    [Fact]
    public void A_WATCHING_SCREEN_RESOLVES_ITSELF_when_the_thing_it_waits_for_arrives()
    {
        var clock = new ScriptedClock();
        var checks = 0;
        var rows = new List<string>();

        var spec = Spec(new SelectOption("I'll provide the path"), new SelectOption("Pick another"))
            with
            {
                Poll = new PollSpec(
                    Arrived: () => ++checks >= 3,
                    Row: ms => { rows.Add($"watching… {ms}ms"); return $"watching… {ms}ms"; },
                    Clock: clock,
                    Interval: TimeSpan.FromSeconds(2)),
            };

        var outcome = new SelectPrompt(new RecordingSurface(), T, new ScriptedKeys([])).Show(spec);

        Assert.IsType<SelectOutcome.Arrived>(outcome);
        Assert.Equal(3, checks);                       //the poll stops once Arrived is true, so the count is exact
        Assert.True(clock.Waits >= 3, "the widget waited on the clock rather than on a keystroke");
        Assert.NotEmpty(rows);                         //the live row keeps updating while it waits.
    }

    //watching a screen must not remove the ability to press a key. without this the guard above proves only that the wait ends
    [Fact]
    public void A_WATCHING_SCREEN_STILL_TAKES_A_KEYPRESS_because_watching_is_not_a_trap()
    {
        var clock = new ScriptedClock(keyAfter: 2);

        var spec = Spec(new SelectOption("I'll provide the path"), new SelectOption("Pick another"))
            with
            {
                Poll = new PollSpec(
                    //the probe never arrives, which is the case the option rows are there for
                    Arrived: () => false,
                    Row: ms => $"watching… {ms}ms",
                    Clock: clock,
                    Interval: TimeSpan.FromSeconds(2)),
            };

        var outcome = new SelectPrompt(new RecordingSurface(), T, new ScriptedKeys([Special(ConsoleKey.Enter)]))
            .Show(spec);

        Assert.Equal(0, Chosen(outcome));
    }

    [Fact]
    public void THE_COMPLETION_ECHO_WRAPS_the_answer_instead_of_TRUNCATING_it()
    {
        //the echo is committed text, so a truncation there is permanent
        var answer = @"C:\llama-lab\llama\llama-b10076-bin-win-vulkan-x64\llama-server.exe";
        var spec = new SelectSpec(
            TitleRows: [],
            Question: new PromptQuestion("Where is llama-server.exe?"),
            Options: [],
            FreeTextLabel: "type the path",
            EchoOnCompletion: true);

        var surface = new RecordingSurface { Width = 40 };
        var keys = new ScriptedKeys([.. Type(answer), Special(ConsoleKey.Enter)]);
        new SelectPrompt(surface, T, keys).Show(spec);

        //read the whole surface, escapes and line breaks stripped, so no frame can hold only part of the path
        var painted = TermText.StripAnsiForWidth(surface.Text)
            .Replace("\r", "").Replace("\n", "").Replace(" ", "");
        Assert.Contains(answer, painted, StringComparison.Ordinal);
    }

    //the disabled row sits in the middle, so a skip that clamps to the end fails here
    private static SelectSpec WithDisabledMiddle() => new(
        TitleRows: [],
        Question: new PromptQuestion("pick"),
        Options: [new SelectOption("first"), new SelectOption("soon", Disabled: true), new SelectOption("last")]);

    [Fact]
    public void A_DISABLED_ROW_CANNOT_BE_REACHED_BY_THE_CURSOR()
    {
        //down from the top skips the disabled row and rests on row two, the property a user can see
        var (_, outcome) = Run(WithDisabledMiddle(), Special(ConsoleKey.DownArrow), Special(ConsoleKey.Enter));

        Assert.Equal(2, Chosen(outcome));
    }

    [Fact]
    public void A_DISABLED_ROW_CANNOT_BE_REACHED_GOING_BACK_UP_EITHER()
    {
        //the up direction needs its own test, one guard proves nothing about its twin
        var (_, outcome) = Run(WithDisabledMiddle(),
            Special(ConsoleKey.DownArrow), Special(ConsoleKey.UpArrow), Special(ConsoleKey.Enter));

        Assert.Equal(0, Chosen(outcome));
    }

    [Fact]
    public void A_DISABLED_ROWS_DIGIT_IS_DEAD()
    {
        //the rejected digit is not remembered, the next key resolves on its own
        var (_, outcome) = Run(WithDisabledMiddle(), Digit('2'), Special(ConsoleKey.Enter));

        Assert.Equal(0, Chosen(outcome));
    }

    [Fact]
    public void A_DISABLED_ROW_IS_SHOWN_WITHOUT_A_NUMBER_because_a_number_is_an_instruction()
    {
        //a disabled row must stay visible without a number. the other rows keep their numbers unshifted.
        var (surface, _) = Run(WithDisabledMiddle(), Special(ConsoleKey.Enter));
        var rows = LastFrame(surface.Text);

        Assert.Contains(rows, r => r.Contains("soon", StringComparison.Ordinal));
        Assert.DoesNotContain(rows, r => r.Contains("2. soon", StringComparison.Ordinal));
        Assert.Contains(rows, r => r.Contains("1. first", StringComparison.Ordinal));
        Assert.Contains(rows, r => r.Contains("3. last", StringComparison.Ordinal));
    }

    [Fact]
    public void A_PROMPT_WHOSE_FIRST_ROW_IS_DISABLED_STILL_OPENS_SOMEWHERE_USABLE()
    {
        //the cursor must never open on a disabled row. every key refusing there reads as a frozen program.
        var spec = new SelectSpec(
            TitleRows: [],
            Question: new PromptQuestion("pick"),
            Options: [new SelectOption("soon", Disabled: true), new SelectOption("real")]);

        var (_, outcome) = Run(spec, Special(ConsoleKey.Enter));

        Assert.Equal(1, Chosen(outcome));
    }

    //the rows the inline path wrote, minus the trailing cursor-restore row and each row's leading moves. the erase stays, NoPainter_RepaintsInPlace asserts on it
    private static string[] RowsOf(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Where(r => r != Ansi.ShowCursor)
        .Select(Gatto.Tests.Fakes.TerminalReplay.StripRowOpener).ToArray();

    //the rows after the last cursor-up, ansi stripped. claims about order need one frame, the block is redrawn on every keystroke
    private static string[] LastFrame(string text)
    {
        var ups = Regex.Matches(text, CursorUp);
        var from = ups.Count == 0 ? 0 : ups[^1].Index + ups[^1].Length;
        return RowsOf(text[from..].TrimStart('\r')).Select(TermText.StripAnsiForWidth).ToArray();
    }

    private static int Occurrences(string haystack, string needle)
    {
        var n = 0;
        var i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }

    [Fact]
    public void Enter_ConfirmsCursor()
    {
        var (_, outcome) = Run(Spec("Yes", "No", "Cancel"), Special(ConsoleKey.Enter));
        Assert.Equal(0, Chosen(outcome));
    }

    [Fact]
    public void Enter_ConfirmsInitialCursor_WhenCallerMovedIt()
    {
        var spec = Spec("Yes", "No", "Cancel") with { InitialCursor = 2 };
        var (_, outcome) = Run(spec, Special(ConsoleKey.Enter));
        Assert.Equal(2, Chosen(outcome));
    }

    [Fact]
    public void UpDown_ClampNeverWrap()
    {
        //up at the top stays there and never wraps.
        var (_, top) = Run(Spec("Yes", "No", "Cancel"),
            Special(ConsoleKey.UpArrow), Special(ConsoleKey.UpArrow), Special(ConsoleKey.Enter));
        Assert.Equal(0, Chosen(top));

        //down at the last row stays there and never wraps.
        var (_, bottom) = Run(Spec("Yes", "No", "Cancel"),
            Special(ConsoleKey.DownArrow), Special(ConsoleKey.DownArrow), Special(ConsoleKey.DownArrow),
            Special(ConsoleKey.DownArrow), Special(ConsoleKey.Enter));
        Assert.Equal(2, Chosen(bottom));
    }

    [Fact]
    public void Digit_InstantConfirm()
    {
        //only the digit is scripted, so a widget that waits for enter after it throws here instead of passing
        var (_, outcome) = Run(Spec("Yes", "No", "Cancel"), Digit('2'));
        Assert.Equal(1, Chosen(outcome));
    }

    [Fact]
    public void Digit_BeyondCount_Ignored()
    {
        var (_, outcome) = Run(Spec("Yes", "No", "Cancel"), Digit('9'), Special(ConsoleKey.Enter));
        Assert.Equal(0, Chosen(outcome));
    }

    [Fact]
    public void Space_DeadInSingleSelect()
    {
        //space does nothing here, the widget is still waiting for a real answer afterwards
        var (_, outcome) = Run(Spec("Yes", "No", "Cancel"), Space(), Special(ConsoleKey.Enter));
        Assert.Equal(0, Chosen(outcome));
    }

    [Fact]
    public void Esc_Cancels()
    {
        var (_, outcome) = Run(Spec("Yes", "No", "Cancel"), Special(ConsoleKey.Escape));
        Assert.IsType<SelectOutcome.Cancelled>(outcome);
    }

    [Fact]
    public void UnrecognizedKey_Ignored()
    {
        //y, a and n must do nothing, the widget answers numbers and the cursor keys only
        var (_, outcome) = Run(Spec("Yes", "No", "Cancel"),
            Special(ConsoleKey.F5), Letter('y'), Letter('a'), Letter('n'), Special(ConsoleKey.Tab),
            Special(ConsoleKey.Enter));
        Assert.Equal(0, Chosen(outcome));
    }

    [Fact]
    public void CursorRow_IsAccentPainted_OthersPlain()
    {
        var (surface, _) = Run(Spec("Yes", "No"), Special(ConsoleKey.Enter));
        var rows = RowsOf(surface.Text);
        var cursorRow = Assert.Single(rows, r => r.Contains('❯'));
        Assert.Contains("1. Yes", TermText.StripAnsiForWidth(cursorRow));
        Assert.Contains(Ansi.Fg(T.Map(Theme.Accent), true), cursorRow, StringComparison.Ordinal);

        var otherRow = Assert.Single(rows, r => TermText.StripAnsiForWidth(r).Contains("2. No"));
        Assert.DoesNotContain(Ansi.Fg(T.Map(Theme.Accent), true), otherRow, StringComparison.Ordinal);
        Assert.StartsWith("  2. No", TermText.StripAnsiForWidth(otherRow));
    }

    [Fact]
    public void Recommended_SuffixOnFirstOnly()
    {
        //only the first recommended option shows the (Recommended) suffix, even with two flagged in the spec
        var spec = Spec(
            new SelectOption("Yes"),
            new SelectOption("Yes, always", Recommended: true),
            new SelectOption("No", Recommended: true));
        var (surface, _) = Run(spec, Special(ConsoleKey.Enter));

        Assert.Equal(1, Occurrences(surface.Text, "(Recommended)"));
        Assert.Contains(T.Paint(" (Recommended)", Theme.Dim), surface.Text, StringComparison.Ordinal);

        var suffixRow = Assert.Single(RowsOf(surface.Text), r => r.Contains("(Recommended)"));
        Assert.Contains("2. Yes, always", TermText.StripAnsiForWidth(suffixRow));
    }

    //the suffix rides outside the label, so a too-wide label gives cells of its own and the row keeps the suffix whole at its end
    [Fact]
    public void A_LONG_LABEL_ON_THE_RECOMMENDED_ROW_KEEPS_ITS_SUFFIX_WHOLE()
    {
        var spec = Spec(
            new SelectOption("Yes"),
            new SelectOption("Yes, always rerun this exact command without asking", Recommended: true));
        var surface = new RecordingSurface { Width = 30 };
        new SelectPrompt(surface, T, new ScriptedKeys([Special(ConsoleKey.Enter)])).Show(spec);

        var plain = Assert.Single(RowsOf(surface.Text)
            .Select(TermText.StripAnsiForWidth), r => r.Contains("Recommended", StringComparison.Ordinal));

        Assert.EndsWith(" (Recommended)", plain);
        Assert.True(UnicodeWidth.Of(plain) <= 30, $"the row outgrew the width: {plain}");
    }

    [Fact]
    public void Description_RendersDimUnderOption()
    {
        var spec = Spec(
            new SelectOption("Yes"),
            new SelectOption("No", Description: "the model is told and keeps going"));
        var (surface, _) = Run(spec, Special(ConsoleKey.Enter));

        var rows = RowsOf(surface.Text);
        var optionAt = Array.FindIndex(rows, r => TermText.StripAnsiForWidth(r).Contains("2. No"));
        Assert.True(optionAt >= 0, "the described option never rendered");

        var descRow = rows[optionAt + 1];
        Assert.Contains(T.Paint("the model is told and keeps going", Theme.Dim), descRow, StringComparison.Ordinal);
        //the description indents past the number column, so it reads as a child of its option.
        Assert.StartsWith("     the model is told", TermText.StripAnsiForWidth(descRow));
    }

    [Fact]
    public void Marked_DotRendersOffCursor()
    {
        //a marked row off the cursor shows a tinted dot with its label unpainted, the dot marks status
        var spec = Spec(
            new SelectOption("gemma-4-26b"),
            new SelectOption("qwen3.6-35b", Marked: true));
        var (surface, _) = Run(spec, Special(ConsoleKey.Enter));

        //the status dot is painted with the accent tint and closed before the label, the row text stays unpainted
        var markedRow = Assert.Single(RowsOf(surface.Text), r => r.Contains("qwen3.6-35b"));
        Assert.Contains(T.Paint("●", Theme.Accent) + " qwen3.6-35b", markedRow, StringComparison.Ordinal);
        //an unmarked sibling keeps the dot column so the labels stay aligned.
        var plainRow = Assert.Single(RowsOf(surface.Text), r => r.Contains("gemma-4-26b"));
        Assert.Contains("1.   gemma-4-26b", TermText.StripAnsiForWidth(plainRow));
    }

    [Fact]
    public void Regions_RenderTitleQuestionDetailFooter()
    {
        //each detail row gets a gutter, as real callers pass. a gutterless fixture would never arm the dashed rules
        var spec = new SelectSpec(
            ["Write new file test.txt"],
            "Do you want to proceed?",
            [new SelectOption("Yes"), new SelectOption("No")],
            DetailRows: [new DetailRow("Hello!", Gutter: "1  "), new DetailRow("<?php", Gutter: "2  ")]);
        var (surface, _) = Run(spec, Special(ConsoleKey.Enter));
        var text = surface.Text;
        var plain = TermText.StripAnsiForWidth(text);

        Assert.Contains(T.Paint("Write new file test.txt", Theme.Bright, bold: true), text, StringComparison.Ordinal);
        Assert.Contains("  Do you want to proceed?", plain);
        Assert.Contains("  1  Hello!", plain);
        Assert.Contains("  2  <?php", plain);
        Assert.Contains(T.Paint("Esc to cancel", Theme.Dim), text, StringComparison.Ordinal);
        //the detail rows sit between two full-width rules, each made of 80 plain dashes
        var rows = RowsOf(text);
        var rules = Enumerable.Range(0, rows.Length)
            .Where(i => TermText.StripAnsiForWidth(rows[i]).TrimEnd() == new string('-', 80)).ToList();
        Assert.Equal(2, rules.Count);
        var first = Array.FindIndex(rows, r => TermText.StripAnsiForWidth(r).Contains("Hello!"));
        Assert.InRange(first, rules[0] + 1, rules[1] - 1);

        //the detail block must sit above the question, so the user reads what they approve before choosing. presence checks alone would miss a reordering
        int At(string needle) => Array.FindIndex(rows, r => TermText.StripAnsiForWidth(r).Contains(needle));
        var title = At("Write new file test.txt");
        var question = At("Do you want to proceed?");
        var firstOpt = At("1. Yes");
        var footer = At("Esc to cancel");
        Assert.True(title < rules[0] && rules[1] < question && question < firstOpt && firstOpt < footer,
            "regions must render top-to-bottom: title, rules-bound detail, question, options, footer");
        //no row sits between the title and the opening rule, or between the closing rule and the question
        Assert.Equal(title + 1, rules[0]);
        Assert.Equal(rules[1] + 1, question);
        //the one blank spacer left in the layout sits right before the footer
        Assert.Equal("", TermText.StripAnsiForWidth(rows[footer - 1]));
    }

    [Fact]
    public void GutterlessDetails_RenderWithoutRules_BlankSeparated()
    {
        //dashed rules are for guttered content, like a file preview. a one-line detail renders blank-separated, so not every prompt looks like a preview
        var spec = new SelectSpec(
            ["shell command"],
            "Do you want to proceed?",
            [new SelectOption("Yes"), new SelectOption("No")],
            DetailRows: ["date"]);
        var (surface, _) = Run(spec, Special(ConsoleKey.Enter));
        var rows = RowsOf(surface.Text);

        Assert.DoesNotContain(rows, r => TermText.StripAnsiForWidth(r).TrimEnd() == new string('-', 80));
        int At(string needle) => Array.FindIndex(rows, r => TermText.StripAnsiForWidth(r).Contains(needle));
        var title = At("shell command");
        var detail = At("date");
        var question = At("Do you want to proceed?");
        Assert.True(title < detail && detail < question, "title, then detail, then question");
        Assert.Equal("", TermText.StripAnsiForWidth(rows[title + 1]));      //the blank line gives the title and its detail space while keeping them one block.
        Assert.Equal(title + 2, detail);
        Assert.Equal("", TermText.StripAnsiForWidth(rows[detail + 1]));
        Assert.Equal(detail + 2, question);
    }

    [Fact]
    public void GutteredDetails_KeepTheRules()
    {
        //one guttered row arms the dashed rules, and a gutterless dim hint inside the block does not turn them off
        var spec = new SelectSpec(
            ["Write new file test.txt"],
            "Do you want to proceed?",
            [new SelectOption("Yes"), new SelectOption("No")],
            DetailRows: [new DetailRow("Hello!", Gutter: "1  "), new DetailRow("… +3 lines", Dim: true)]);
        var (surface, _) = Run(spec, Special(ConsoleKey.Enter));
        var rows = RowsOf(surface.Text);

        var rules = Enumerable.Range(0, rows.Length)
            .Where(i => TermText.StripAnsiForWidth(rows[i]).TrimEnd() == new string('-', 80)).ToList();
        Assert.Equal(2, rules.Count);
    }

    [Fact]
    public void TitleRow_CodeSpan_PaintsInlineCode()
    {
        //the caller passes a Code span and the widget picks the paint. a title file name then shows in the inline code style
        var spec = new SelectSpec([new TitleRow("Write new file ", Code: "current_date.ps1")],
            "Do you want to proceed?", [new SelectOption("Yes")]);
        var (surface, _) = Run(spec, Special(ConsoleKey.Enter));

        Assert.Contains(T.Chip("current_date.ps1"), surface.Text, StringComparison.Ordinal);
        Assert.Contains("Write new file current_date.ps1",
            TermText.StripAnsiForWidth(surface.Text), StringComparison.Ordinal);
    }

    [Fact]
    public void TitleRow_MarkLegend_AccentDotDimText()
    {
        //the legend is an accent dot plus dim words, composed by the widget so no caller escape gets through
        var spec = new SelectSpec([new TitleRow("select a model", MarkLegend: "= weights loaded")],
            Question: null, [new SelectOption("gemma")]);
        var (surface, _) = Run(spec, Special(ConsoleKey.Enter));

        Assert.Contains(T.Paint("●", Theme.Accent) + " " + T.Paint("= weights loaded", Theme.Dim),
            surface.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void QuestionCodeSpan_PaintsInlineCode()
    {
        //a question file name gets the same inline code style as the title's, the widget picks the paint
        var spec = new SelectSpec(["Write new file "],
            new PromptQuestion("Do you want to write new file ", Code: "a.ps1", After: "?"),
            [new SelectOption("Yes")]);
        var (surface, _) = Run(spec, Special(ConsoleKey.Enter));

        Assert.Contains(T.Chip("a.ps1"), surface.Text, StringComparison.Ordinal);
        Assert.Contains("Do you want to write new file a.ps1?",
            TermText.StripAnsiForWidth(surface.Text), StringComparison.Ordinal);
    }

    //flatten a painted surface to one line, so a wrapped question reassembles and a truncated one cannot at any width
    private static string Flatten(string painted) => Regex.Replace(
        TermText.StripAnsiForWidth(painted).Replace("\r", "\n").Replace("\n", " "), " +", " ");

    [Fact]
    public void THE_QUESTION_WRAPS_instead_of_being_TRUNCATED()
    {
        //the question must wrap rather than truncate. the oracle is the whole question read back from the frame
        var question = "The About page: templates.html marks it with a .lbl chip, "
            + "which p1-02 explicitly bans, and every other page opens with an h1 heading instead.";
        var spec = new SelectSpec(
            TitleRows: [],
            Question: new PromptQuestion(question),
            Options: [new SelectOption("h1 ABOUT, like the other pages")]);

        var surface = new RecordingSurface { Width = 60 };
        new SelectPrompt(surface, T, new ScriptedKeys([Special(ConsoleKey.Enter)])).Show(spec);

        Assert.Contains(question, Flatten(surface.Text), StringComparison.Ordinal);
    }

    [Fact]
    public void THE_QUESTION_SURVIVES_INTACT_at_every_width_and_no_row_overflows()
    {
        //sweep every width, a defect only shows where a row hits the edge. check reassembly first, a wrap budget one cell too wide shows up as a truncation
        var question = "gatto needs to know which of the installed engines this model should run on, "
            + "and the answer is remembered for the next session as well.";
        for (var w = 24; w <= 90; w++)
        {
            var surface = new RecordingSurface { Width = w };
            new SelectPrompt(surface, T, new ScriptedKeys([Special(ConsoleKey.Enter)])).Show(new SelectSpec(
                TitleRows: [],
                Question: new PromptQuestion(question),
                Options: [new SelectOption("vulkan")]));

            Assert.Contains(question, Flatten(surface.Text), StringComparison.Ordinal);

            foreach (var row in TermText.StripAnsiForWidth(surface.Text).Split('\n'))
            {
                //a committed row leads with \r, so trim it before measuring width, the counter would report a false one-cell overflow
                var cells = UnicodeWidth.Of(row.Trim('\r'));
                Assert.True(cells <= w, $"width {w}: a row measured {cells} cells: <{row.Trim('\r')}>");
            }
        }
    }

    [Fact]
    public void A_WRAPPED_QUESTION_still_paints_its_CODE_CHIP()
    {
        //the chip is an offset into the question, so wrapping moves it to a continuation row. a mapping that assumes the first row would paint nothing there
        var spec = new SelectSpec(
            TitleRows: [],
            Question: new PromptQuestion(
                "Do you want gatto to write a new file into the project folder called ",
                Code: "current_date.ps1", After: "?"),
            Options: [new SelectOption("Yes")]);

        var surface = new RecordingSurface { Width = 40 };
        new SelectPrompt(surface, T, new ScriptedKeys([Special(ConsoleKey.Enter)])).Show(spec);

        Assert.Contains(T.Chip("current_date.ps1"), surface.Text, StringComparison.Ordinal);
        Assert.Contains(
            "Do you want gatto to write a new file into the project folder called current_date.ps1?",
            Flatten(surface.Text), StringComparison.Ordinal);
    }

    [Fact]
    public void A_CODE_CHIP_BROKEN_ACROSS_ROWS_keeps_EVERY_half_chipped()
    {
        //a long path hard-breaks mid-word, so a chip resolves per row. the oracle is the chipped fragments equalling the whole path
        var code = @"C:\llama-lab\llama\llama-b10076-bin-win-vulkan-x64\llama-server.exe";
        var spec = new SelectSpec(
            TitleRows: [],
            Question: new PromptQuestion("Use ", Code: code, After: "?"),
            Options: [new SelectOption("Yes")]);

        var surface = new RecordingSurface { Width = 30 };
        new SelectPrompt(surface, T, new ScriptedKeys([Special(ConsoleKey.Enter)])).Show(spec);

        var mark = T.Chip("X");
        var at = mark.IndexOf('X');
        var open = Regex.Escape(mark[..at]);
        var close = Regex.Escape(mark[(at + 1)..]);
        var runs = Regex.Matches(surface.Text, open + "(.*?)" + close)
            .Select(m => m.Groups[1].Value).ToList();

        Assert.True(runs.Count >= 2, $"the path was chipped as {runs.Count} run(s), so it never broke");
        Assert.Equal("", string.Concat(runs).Replace(code, ""));
    }

    //four headers at the schema's maximum count and ordinary length, so the strip overflows 80 columns on a shape the tool reaches routinely
    private static readonly string[] WideTabs =
    [
        "About heading, missing copy",
        "Nav order on the log index",
        "Footer wording and its link",
        "Language switch placement",
    ];

    private static string[] Frame(RecordingSurface surface) =>
        TermText.StripAnsiForWidth(surface.Text).Split('\n').Select(r => r.Trim('\r')).ToArray();

    private static RecordingSurface ShowTabs(int width, int active, params string[] tabs)
    {
        var surface = new RecordingSurface { Width = width };
        new SelectPrompt(surface, T, new ScriptedKeys([Special(ConsoleKey.Enter)])).Show(new SelectSpec(
            TitleRows: [],
            Question: new PromptQuestion("pick"),
            Options: [new SelectOption("Yes")],
            Tabs: tabs,
            ActiveTab: active));
        return surface;
    }

    [Fact]
    public void THE_TAB_STRIP_FLOWS_ONTO_ANOTHER_ROW_instead_of_SHORTENING_EVERY_CHIP()
    {
        //every header must show whole at 80 columns, equal shares of the row would cut them all
        var surface = ShowTabs(80, 0, WideTabs);
        var frame = Frame(surface);

        foreach (var tab in WideTabs)
            Assert.Contains(frame, r => r.Contains(tab, StringComparison.Ordinal));
    }

    [Fact]
    public void EVERY_HEADER_SURVIVES_at_every_width_and_no_row_overflows()
    {
        //check every header survives before the overflow, a running-over row comes back truncated. the strip must never soft-wrap
        for (var w = 20; w <= 90; w++)
        {
            var surface = ShowTabs(w, 1, WideTabs);
            foreach (var tab in WideTabs)
                Assert.Contains(tab, Flatten(surface.Text), StringComparison.Ordinal);
            foreach (var row in Frame(surface))
                Assert.True(UnicodeWidth.Of(row) <= w,
                    $"width {w}: a row measured {UnicodeWidth.Of(row)} cells: <{row}>");
        }
    }

    [Fact]
    public void A_FLOWED_TAB_STRIP_still_paints_the_ACTIVE_chip_in_the_accent()
    {
        //the active chip may flow to a later row, so the assert names the last tab, which a single-row strip loses first
        var surface = ShowTabs(80, WideTabs.Length - 1, WideTabs);

        Assert.Contains(T.Paint(WideTabs[^1], Theme.Accent), surface.Text, StringComparison.Ordinal);
        Assert.Contains(T.Paint(WideTabs[0], Theme.Dim), surface.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ONE_HEADER_WIDER_THAN_THE_WHOLE_STRIP_wraps_rather_than_being_CUT()
    {
        //a header wider than the terminal has no row to flow to, so it wraps on rows of its own in one colour
        var tab = "A single header long enough that it cannot fit on any row of this narrow terminal";
        var surface = ShowTabs(40, 0, tab);

        Assert.Contains(tab, Flatten(surface.Text), StringComparison.Ordinal);
    }

    [Fact]
    public void OptionLabelCodeSpan_ChipOffCursor_AllAccentOnCursor()
    {
        //off the cursor the scope renders as inline code, on the cursor the whole row is one accent run
        var always = new SelectOption("Yes, always allow ", LabelCode: @"proj\*", LabelAfter: " on this project");
        var spec = new SelectSpec(["t"], "q?", [new SelectOption("Yes"), always]);

        var (offCursor, _) = Run(spec, Special(ConsoleKey.Enter));
        Assert.Contains(T.Chip(@"proj\*"), offCursor.Text, StringComparison.Ordinal);
        Assert.Contains("2. Yes, always allow proj\\* on this project",
            TermText.StripAnsiForWidth(offCursor.Text), StringComparison.Ordinal);

        var (onCursor, _) = Run(spec, Special(ConsoleKey.DownArrow), Special(ConsoleKey.Enter));
        Assert.Contains(T.Paint(@"❯ 2. Yes, always allow proj\* on this project", Theme.Accent),
            onCursor.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void DetailRow_Gutter_PaintsDim_TextPlain()
    {
        //the gutter is dim chrome while the preview text stays plain, and a Dim row is dim throughout
        var spec = new SelectSpec(["Write new file a.ps1"], "Proceed?", [new SelectOption("Yes")],
            DetailRows: [new DetailRow("Get-Date", Gutter: "1  "), new DetailRow("… +13 lines", Dim: true)]);
        var (surface, _) = Run(spec, Special(ConsoleKey.Enter));

        Assert.Contains(T.Paint("1  ", Theme.Dim) + "Get-Date", surface.Text, StringComparison.Ordinal);
        Assert.Contains(T.Paint("… +13 lines", Theme.Dim), surface.Text, StringComparison.Ordinal);
        var plain = TermText.StripAnsiForWidth(surface.Text);
        Assert.Contains("  1  Get-Date", plain, StringComparison.Ordinal);
    }

    [Fact]
    public void QuestionNull_BlankRowSeparatesTitleFromOptions()
    {
        //with no question row the options would glue onto the bold title, so exactly one blank separates them
        var spec = new SelectSpec(["select a model"], Question: null,
            [new SelectOption("gemma-4-26b"), new SelectOption("qwen3.6-35b")]);
        var (surface, _) = Run(spec, Special(ConsoleKey.Enter));
        var frame = LastFrame(surface.Text);

        var title = Array.FindIndex(frame, r => r.Contains("select a model"));
        var firstOpt = Array.FindIndex(frame, r => r.Contains("1.  gemma-4-26b") || r.Contains("1. gemma-4-26b"));
        Assert.True(title >= 0 && firstOpt == title + 2, "expected exactly one blank between title and options");
        Assert.Equal("", frame[title + 1].TrimEnd());
    }

    [Fact]
    public void QuestionNull_TrailingBlankTitleRow_AddsNoSecondBlank()
    {
        //the caller already ends its title rows with a blank, so adding another would leave a two-row hole above the options
        var spec = new SelectSpec(["Review your answers", ""], Question: null,
            [new SelectOption("Submit")]);
        var (surface, _) = Run(spec, Special(ConsoleKey.Enter));
        var frame = LastFrame(surface.Text);

        var title = Array.FindIndex(frame, r => r.Contains("Review your answers"));
        var submit = Array.FindIndex(frame, r => r.Contains("1. Submit"));
        Assert.Equal(title + 2, submit);   //the frame is the title, the caller's own blank, then the option row.
    }

    [Fact]
    public void FooterHint_Null_RendersNoFooterRow()
    {
        var spec = Spec("Yes", "No") with { FooterHint = null };
        var (surface, _) = Run(spec, Special(ConsoleKey.Enter));
        Assert.DoesNotContain("Esc to cancel", surface.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void HostileLabel_SanitizedInert()
    {
        //a model label may hold escape bytes and a carriage return. the escape must show as inert text and the carriage return must go
        const string hostile = "\u001b[31mred\rX";
        var (surface, _) = Run(Spec(hostile, "No"), Special(ConsoleKey.Enter));

        //match a control byte with StringComparison.Ordinal, culture comparison gives escape and carriage return zero weight and would fail a correct widget
        Assert.DoesNotContain("\u001b[31m", surface.Text, StringComparison.Ordinal);
        Assert.Contains("[31mredX", TermText.StripAnsiForWidth(surface.Text), StringComparison.Ordinal);

        //every carriage return on the surface is a row opener, a blanket absence check would fail on that chrome
        for (var i = surface.Text.IndexOf('\r'); i >= 0; i = surface.Text.IndexOf('\r', i + 1))
            Assert.StartsWith("\r" + Ansi.ClearToEol, surface.Text[i..], StringComparison.Ordinal);
    }

    [Fact]
    public void HostileTitleQuestionDetail_SanitizedInert()
    {
        var spec = new SelectSpec(
            ["\u001b[32mtitle"],
            "\u001b[33mquestion",
            [new SelectOption("Yes", Description: "\u001b[34mdesc")],
            DetailRows: ["\u001b[35mdetail"],
            FreeTextLabel: "\u001b[36mfree");
        var (surface, _) = Run(spec, Special(ConsoleKey.Enter));

        foreach (var raw in new[] { "\u001b[31m", "\u001b[32m", "\u001b[33m", "\u001b[34m", "\u001b[35m", "\u001b[36m" })
            Assert.DoesNotContain(raw, surface.Text, StringComparison.Ordinal);
        var plain = TermText.StripAnsiForWidth(surface.Text);
        foreach (var inert in new[] { "[32mtitle", "[33mquestion", "[34mdesc", "[35mdetail", "[36mfree" })
            Assert.Contains(inert, plain, StringComparison.Ordinal);
    }

    [Fact]
    public void NoPainter_RepaintsInPlace()
    {
        var (surface, outcome) = Run(Spec("Yes", "No", "Cancel"),
            Special(ConsoleKey.DownArrow), Special(ConsoleKey.Enter));
        Assert.Equal(1, Chosen(outcome));

        //the cursor moves back up over exactly the rows just written, otherwise the down key leaves a cursor the user can no longer see
        var up = Regex.Match(surface.Text, "\u001b\\[(\\d+)A");
        Assert.True(up.Success, "no in-place repaint fired");
        var rowsWrittenFirst = surface.Text[..up.Index].Count(c => c == '\n');
        Assert.Equal(rowsWrittenFirst, int.Parse(up.Groups[1].Value));

        //the cursor glyph must be visible inside the rewritten block.
        var lastCaret = surface.Text.LastIndexOf('❯');
        Assert.True(lastCaret >= 0, "no cursor glyph was ever rendered");
        Assert.Contains("2. No", TermText.StripAnsiForWidth(surface.Text[lastCaret..]));

        //every inline row opens with erase-to-end-of-line, so a shorter row leaves no debris. it sits at column zero, where a pending wrap cannot eat the row above
        foreach (var row in RowsOf(surface.Text))
            Assert.StartsWith(Ansi.ClearToEol, row, StringComparison.Ordinal);
    }

    [Fact]
    public void NoPainter_ResizeMidPrompt_RowsFit()
    {
        //re-read Width on every render, a cached width would overflow after a resize and terminal wrapping corrupts the cursor-up math
        var surface = new RecordingSurface { Width = 80 };
        var spec = new SelectSpec(
            ["Write a rather long new file name that will not fit a narrow terminal.txt"],
            "Do you want to proceed with this comparatively verbose question?",
            [
                new SelectOption("Yes, allow this particular call exactly once for now",
                    Description: "the tool runs and the model continues with its result in hand"),
                new SelectOption("No, deny it and tell the model why that happened"),
            ],
            DetailRows: ["1 a detail row that is comfortably wider than thirty columns of terminal"]);

        var keys = new ProbingKeys(
            n => { if (n == 0) surface.Width = 30; },
            [Special(ConsoleKey.DownArrow), Special(ConsoleKey.Enter)]);
        var outcome = new SelectPrompt(surface, T, keys).Show(spec);
        Assert.Equal(1, Chosen(outcome));

        var up = Regex.Match(surface.Text, "\u001b\\[(\\d+)A");
        Assert.True(up.Success, "no in-place repaint fired");
        //skip the carriage return written after the cursor-up, UnicodeWidth would count it as a cell
        var afterResize = surface.Text[(up.Index + up.Length)..].TrimStart('\r');
        foreach (var row in RowsOf(afterResize))
        {
            var width = UnicodeWidth.Of(TermText.StripAnsiForWidth(row));
            Assert.True(width <= 30, $"row overflows the resized surface: width={width}, max=30 — '{row}'");
        }
    }

    [Fact]
    public void NoPainter_WidenMidPrompt_BlanksSurplus_AndKeepsCursorUpExact()
    {
        //a shrink only adds rows and never reaches the surplus-blanking branch widening does, so what matters here is the cursor-up distance
        var surface = new RecordingSurface { Width = 30 };
        var spec = new SelectSpec(
            ["Write a rather long new file name that will not fit a narrow terminal.txt"],
            "Do you want to proceed with this comparatively verbose question?",
            [
                new SelectOption("Yes, allow this particular call exactly once for now",
                    Description: "the tool runs and the model continues with its result in hand"),
                new SelectOption("No, deny it and tell the model why that happened"),
            ]);
        var keys = new ProbingKeys(
            n => { if (n == 0) surface.Width = 80; },
            [Special(ConsoleKey.DownArrow), Special(ConsoleKey.UpArrow), Special(ConsoleKey.Enter)]);
        Assert.Equal(0, Chosen(new SelectPrompt(surface, T, keys).Show(spec)));

        var ups = Regex.Matches(surface.Text, "\u001b\\[(\\d+)A");
        Assert.Equal(2, ups.Count);
        var frame1 = surface.Text[..ups[0].Index];
        var frame2 = surface.Text[(ups[0].Index + ups[0].Length)..ups[1].Index];
        var h1 = frame1.Count(c => c == '\n');
        var h2 = frame2.Count(c => c == '\n');

        //each cursor-up travels exactly the frame it rewinds over. consistency alone proves nothing, a widget that never blanks its surplus is equally consistent
        Assert.Equal(h1, int.Parse(ups[0].Groups[1].Value));
        Assert.Equal(h2, int.Parse(ups[1].Groups[1].Value));

        //frame 2 needs fewer rows at the wider width, so it must cover frame 1's whole height
        Assert.True(h2 >= h1, $"frame 2 ({h2} rows) left {h1 - h2} orphan row(s) of frame 1 on screen");
        //the surplus rows come out blank, which is what the blanking branch writes
        var tail = RowsOf(frame2).Last();
        Assert.Equal("", TermText.StripAnsiForWidth(tail));
    }

    [Fact]
    public void ChordedDigit_IsNotAConfirmation()
    {
        //a chord typed while the prompt has focus arrives as the character 2, and it must not answer the prompt
        var alt = new ConsoleKeyInfo('2', ConsoleKey.D2, false, alt: true, control: false);
        var ctrl = new ConsoleKeyInfo('2', ConsoleKey.D2, false, alt: false, control: true);
        var (_, outcome) = Run(Spec("Yes", "No", "Cancel"), alt, ctrl, Special(ConsoleKey.Enter));
        Assert.Equal(0, Chosen(outcome));
    }

    [Fact]
    public void ShiftedDigit_StillConfirms()
    {
        //shift must stay accepted, a layout whose digit row is shifted sends a plain 2 with shift set
        var (_, outcome) = Run(Spec("Yes", "No", "Cancel"),
            new ConsoleKeyInfo('2', ConsoleKey.D2, shift: true, alt: false, control: false));
        Assert.Equal(1, Chosen(outcome));
    }

    [Fact]
    public void FreeText_IsNumberedLastOption()
    {
        var spec = Spec("Yes", "No") with { FreeTextLabel = "Type my own answer…" };
        var (surface, outcome) = Run(spec, Special(ConsoleKey.Enter));
        Assert.Equal(0, Chosen(outcome));

        var plain = TermText.StripAnsiForWidth(surface.Text);
        Assert.Contains("3. Type my own answer…", plain);
    }

    [Fact]
    public void FreeText_ArrowReachable_AndCursorLandsOnIt()
    {
        var spec = Spec("Yes", "No") with { FreeTextLabel = "Type my own answer…" };
        var surface = new RecordingSurface();
        var widget = new SelectPrompt(surface, T,
            new ScriptedKeys([Special(ConsoleKey.DownArrow), Special(ConsoleKey.DownArrow), Special(ConsoleKey.Escape)]));
        Assert.IsType<SelectOutcome.Cancelled>(widget.Show(spec));

        var lastCaret = surface.Text.LastIndexOf('❯');
        Assert.Contains("3. Type my own answer…", TermText.StripAnsiForWidth(surface.Text[lastCaret..]));
    }

    [Fact]
    public void FreeText_KeepsTheDotColumn_WhenAnyOptionIsMarked()
    {
        //the dot column applies to the free-text row too, otherwise its label sits two cells left of the others
        var spec = new SelectSpec(
            ["title"], "question",
            [new SelectOption("gemma-4-26b"), new SelectOption("qwen3.6-35b", Marked: true)],
            FreeTextLabel: "Type my own answer");
        var (surface, _) = Run(spec, Special(ConsoleKey.Enter));

        var plain = TermText.StripAnsiForWidth(surface.Text);
        Assert.Contains("1.   gemma-4-26b", plain, StringComparison.Ordinal);
        Assert.Contains("3.   Type my own answer", plain, StringComparison.Ordinal);
    }

    [Fact]
    public void NO_ROW_RENDERS_A_NUMBER_A_KEYSTROKE_CANNOT_PRESS()
    {
        //a digit confirms at once, so numbers stop at nine, and the rows past nine show unnumbered with a hint naming the arrow key
        var options = Enumerable.Range(1, 12).Select(n => new SelectOption("option " + n)).ToArray();
        var (surface, _) = Run(Spec(options), Special(ConsoleKey.Enter));

        var rows = LastFrame(surface.Text);

        Assert.Contains(rows, r => r.Contains("9. option 9", StringComparison.Ordinal));
        foreach (var n in new[] { 10, 11, 12 })
        {
            Assert.Contains(rows, r => r.Contains($"option {n}", StringComparison.Ordinal));
            Assert.DoesNotContain(rows, r => r.Contains($"{n}. option {n}", StringComparison.Ordinal));
        }
        //the list must explain the unnumbered rows, and the assert looks for a hint naming the arrow key rather than an exact sentence
        Assert.Contains(rows, r => r.Contains("no number", StringComparison.Ordinal)
                                   && r.Contains("arrow", StringComparison.Ordinal));
    }

    [Fact]
    public void A_NINE_ROW_LIST_IS_UNTOUCHED()
    {
        //nine is the cap, a list that fits keeps every number and shows no hint, otherwise the rule would tax every ordinary prompt
        var options = Enumerable.Range(1, 9).Select(n => new SelectOption("option " + n)).ToArray();
        var (surface, _) = Run(Spec(options), Special(ConsoleKey.Enter));

        var rows = LastFrame(surface.Text);

        Assert.Contains(rows, r => r.Contains("9. option 9", StringComparison.Ordinal));
        Assert.DoesNotContain(rows, r => r.Contains("more", StringComparison.Ordinal)
                                         && r.Contains("arrow", StringComparison.Ordinal));
    }

    private static IReadOnlyList<int> Multi(SelectOutcome outcome) =>
        Assert.IsType<SelectOutcome.Multi>(outcome).Indices;

    private static SelectSpec MultiSpec(params SelectOption[] options) =>
        new(["Audition these models"], "Which ones should I run?", options, MultiSelect: true);

    [Fact]
    public void Multi_SpaceToggles_EnterOnNextConfirms()
    {
        //space toggles the cursor row, the arrow keys reach the Next row past the last option, and only enter there confirms
        var (_, outcome) = Run(MultiSpec("Yes", "No"),
            Space(), Special(ConsoleKey.DownArrow), Special(ConsoleKey.DownArrow), Special(ConsoleKey.Enter));
        Assert.Equal(new[] { 0 }, Multi(outcome));
    }

    [Fact]
    public void Multi_EnterTogglesOnOption()
    {
        //enter on an option toggles it and keeps waiting. the expected index is the discriminator, a widget that confirmed there would return no toggles
        var (_, outcome) = Run(MultiSpec("Yes", "No"),
            Special(ConsoleKey.Enter), Special(ConsoleKey.DownArrow), Special(ConsoleKey.DownArrow),
            Special(ConsoleKey.Enter));
        Assert.Equal(new[] { 0 }, Multi(outcome));
    }

    [Fact]
    public void Multi_DigitToggles_NeverConfirms()
    {
        //a digit toggles in multi-select, and the user still has to press Enter on the Next row
        var (surface, outcome) = Run(MultiSpec("Yes", "No"),
            Digit('2'), Special(ConsoleKey.DownArrow), Special(ConsoleKey.DownArrow), Special(ConsoleKey.Enter));
        Assert.Equal(new[] { 1 }, Multi(outcome));

        var frame = LastFrame(surface.Text);
        Assert.Contains(frame, r => r.Contains("1. [ ] Yes", StringComparison.Ordinal));
        Assert.Contains(frame, r => r.Contains("2. [x] No", StringComparison.Ordinal));
    }

    [Fact]
    public void Multi_InitialSelectedRespected()
    {
        var spec = MultiSpec(new SelectOption("Yes", Selected: true), new SelectOption("No"));
        var (surface, outcome) = Run(spec,
            Special(ConsoleKey.DownArrow), Special(ConsoleKey.DownArrow), Special(ConsoleKey.Enter));

        var frame = LastFrame(surface.Text);
        Assert.Contains(frame, r => r.Contains("1. [x] Yes", StringComparison.Ordinal));
        Assert.Contains(frame, r => r.Contains("2. [ ] No", StringComparison.Ordinal));
        //an initially selected option reaches the outcome without any keypress.
        Assert.Equal(new[] { 0 }, Multi(outcome));
    }

    [Fact]
    public void Multi_NextRow_RenderedUnnumbered()
    {
        var (surface, _) = Run(MultiSpec("Yes", "No"),
            Special(ConsoleKey.DownArrow), Special(ConsoleKey.DownArrow), Special(ConsoleKey.Enter));

        var frame = LastFrame(surface.Text);
        var lastOption = Array.FindIndex(frame, r => r.Contains("2. [ ] No", StringComparison.Ordinal));
        var next = Array.FindIndex(frame, r => r.Contains("Next", StringComparison.Ordinal));
        Assert.True(lastOption >= 0, "the option rows never rendered");
        Assert.True(next > lastOption, "Next must render AFTER the last numbered option");

        //the Next row is widget chrome, with no number, checkbox or description, and the cursor still reaches it
        Assert.DoesNotContain("3.", frame[next], StringComparison.Ordinal);
        Assert.DoesNotContain("[", frame[next], StringComparison.Ordinal);
        Assert.Contains("❯", frame[next], StringComparison.Ordinal);
        //a blank row separates it from the choices, so it reads as the action
        Assert.Equal("", frame[next - 1]);
        //its label sits in the same column as the option labels.
        Assert.Equal(frame[lastOption].IndexOf("No", StringComparison.Ordinal),
                     frame[next].IndexOf("Next", StringComparison.Ordinal));
    }

    [Fact]
    public void Multi_EmptyToggle_ConfirmsEmpty()
    {
        var (_, outcome) = Run(MultiSpec("Yes", "No"),
            Special(ConsoleKey.DownArrow), Special(ConsoleKey.DownArrow), Special(ConsoleKey.Enter));
        Assert.Empty(Multi(outcome));
    }

    [Fact]
    public void Multi_IndicesAscending_AndToggleIsReversible()
    {
        //the digits pressed are 3, 1, 2, 2, so the repeated 2 cancels, and the result comes out in index order
        var (_, outcome) = Run(MultiSpec("Yes", "No", "Maybe"),
            Digit('3'), Digit('1'), Digit('2'), Digit('2'),
            Special(ConsoleKey.DownArrow), Special(ConsoleKey.DownArrow), Special(ConsoleKey.DownArrow),
            Special(ConsoleKey.Enter));
        Assert.Equal(new[] { 0, 2 }, Multi(outcome));
    }

    [Fact]
    public void Multi_SpaceOnNextIsDead()
    {
        //space on the Next row is dead, and the keys are scripted so a space that confirmed would return an empty result
        var (_, outcome) = Run(MultiSpec("Yes", "No"),
            Special(ConsoleKey.DownArrow), Special(ConsoleKey.DownArrow),
            Space(),
            Special(ConsoleKey.UpArrow), Space(),
            Special(ConsoleKey.DownArrow), Special(ConsoleKey.Enter));
        Assert.Equal(new[] { 1 }, Multi(outcome));
    }

    [Fact]
    public void Multi_EscCancels()
    {
        var (_, outcome) = Run(MultiSpec("Yes", "No"), Space(), Special(ConsoleKey.Escape));
        Assert.IsType<SelectOutcome.Cancelled>(outcome);
    }

    [Fact]
    public void Multi_ZeroOptions_RendersNextOnly_AndResolvesEmpty()
    {
        //an empty multi-select still renders Next and resolves to none, an empty single-select cancels since nothing is answerable
        var spec = new SelectSpec(["title"], "question", [], MultiSelect: true);
        var (surface, outcome) = Run(spec, Special(ConsoleKey.Enter));
        Assert.Empty(Multi(outcome));
        Assert.Contains(LastFrame(surface.Text), r => r.Contains("Next", StringComparison.Ordinal));
    }

    [Fact]
    public void Multi_WithFreeTextLabel_IsRejected()
    {
        //the two modes clash on the same row and a multi-select outcome holds no text, so the combination throws before any key is read
        var spec = Spec("Yes", "No") with { MultiSelect = true, FreeTextLabel = "Type my own answer…" };
        Assert.Throws<ArgumentException>(() =>
            new SelectPrompt(new RecordingSurface(), T, new ThrowingKeys()).Show(spec));
    }

    private static SelectSpec FreeTextSpec() =>
        Spec("Yes", "No") with { FreeTextLabel = "Type my own answer…" };

    private static string FreeTextOf(SelectOutcome outcome) =>
        Assert.IsType<SelectOutcome.FreeText>(outcome).Text;

    [Fact]
    public void FreeText_TypeAndEnter_ReturnsRaw()
    {
        //the free-text row edits in place, so typed text replaces its label, and no separate > input line exists
        var (surface, outcome) = Run(FreeTextSpec(),
            [Digit('3'), .. Type("my answer"), Special(ConsoleKey.Enter)]);
        Assert.Equal("my answer", FreeTextOf(outcome));
        var frame = LastFrame(surface.Text);
        Assert.Contains(frame, r => r.Contains("❯ 3. my answer", StringComparison.Ordinal));
        Assert.DoesNotContain(frame, r => r.Contains("> ", StringComparison.Ordinal));
        Assert.DoesNotContain(frame, r => r.Contains("Type my own answer…", StringComparison.Ordinal));
    }

    [Fact]
    public void FreeText_DigitsTypeIntoTheDraft_NotConfirm()
    {
        //on the free-text row a digit is typed into the draft, and digits still confirm at once on the other rows
        var (_, outcome) = Run(FreeTextSpec(),
            [Digit('3'), .. Type("42"), Special(ConsoleKey.Enter)]);
        Assert.Equal("42", FreeTextOf(outcome));
    }

    [Fact]
    public void FreeText_LabelRendersDim_WhenSelectedAndEmpty()
    {
        //with the cursor on the row and nothing typed the label is dim, a typed character turns the row into accent content
        var (surface, _) = Run(FreeTextSpec(),
            [Digit('3'), Special(ConsoleKey.Escape)]);
        Assert.Contains(T.Paint("Type my own answer…", Theme.Dim), surface.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void FreeText_DraftSurvivesCursorMove()
    {
        //moving the cursor away leaves the draft on the row in plain text, and moving back resumes editing it
        var (surface, outcome) = Run(FreeTextSpec(),
            [Digit('3'), .. Type("dr"), Special(ConsoleKey.UpArrow), Special(ConsoleKey.DownArrow),
             .. Type("aft"), Special(ConsoleKey.Enter)]);
        Assert.Equal("draft", FreeTextOf(outcome));
    }

    [Fact]
    public void FreeText_Armed_CursorVisibleOnTheRow_HiddenElsewhere()
    {
        //the hardware cursor returns only while the selection sits on the free-text row, and it parks on that row mid-panel
        var (surface, painter, handle) = Armed();
        var showCursorSeen = new List<bool>();
        var keys = new ProbingKeys(_ => showCursorSeen.Add(
            LastPaint(surface.Text).Contains(Ansi.ShowCursor, StringComparison.Ordinal)),
            [Digit('3'), Special(ConsoleKey.UpArrow), Special(ConsoleKey.Escape)]);

        new SelectPrompt(surface, T, keys, pump: null, chrome: handle).Show(FreeTextSpec());

        //hidden on an option row, visible on the free-text row, hidden once the cursor moves away
        Assert.Equal(new[] { false, true, false }, showCursorSeen);
    }

    [Fact]
    public void FreeText_EscClearsTheDraft_SecondEscCancels()
    {
        //esc clears a non-empty draft first, and with the draft empty it cancels as it does everywhere else
        var (_, cancelled) = Run(FreeTextSpec(),
            [Digit('3'), .. Type("ab"), Special(ConsoleKey.Escape), Special(ConsoleKey.Escape)]);
        Assert.IsType<SelectOutcome.Cancelled>(cancelled);

        //after the clear the prompt is still live, an arrow off the row and an option answers it
        var (_, reopened) = Run(FreeTextSpec(),
            [Digit('3'), .. Type("ab"), Special(ConsoleKey.Escape),
             Special(ConsoleKey.UpArrow), Special(ConsoleKey.UpArrow), Special(ConsoleKey.Enter)]);
        Assert.Equal(0, Chosen(reopened));
    }

    [Fact]
    public void FreeText_Backspace_Erases()
    {
        var (_, outcome) = Run(FreeTextSpec(),
            [Digit('3'), .. Type("abc"), Special(ConsoleKey.Backspace), Special(ConsoleKey.Backspace),
             .. Type("z"), Special(ConsoleKey.Enter)]);
        Assert.Equal("az", FreeTextOf(outcome));
    }

    [Fact]
    public void FreeText_EmptyEnter_IsANoOp_PromptStaysLive()
    {
        //enter with nothing typed neither answers nor cancels, an empty answer is indistinguishable from none, so the prompt stays live
        var (_, answered) = Run(FreeTextSpec(), Digit('3'), Special(ConsoleKey.Enter),
            Special(ConsoleKey.UpArrow), Special(ConsoleKey.UpArrow), Special(ConsoleKey.Enter));
        Assert.Equal(0, Chosen(answered));

        //esc with an empty draft cancels, as it does everywhere else.
        var (_, cancelled) = Run(FreeTextSpec(), Digit('3'), Special(ConsoleKey.Enter), Special(ConsoleKey.Escape));
        Assert.IsType<SelectOutcome.Cancelled>(cancelled);
    }

    [Fact]
    public void FreeText_WhitespaceOnlyEnter_ClearsAndStays()
    {
        //trimming only decides, so a draft of spaces must not resolve. it clears and the prompt stays live with the placeholder back
        var (_, outcome) = Run(FreeTextSpec(),
            [Digit('3'), .. Type("   "), Special(ConsoleKey.Enter),
             Special(ConsoleKey.UpArrow), Special(ConsoleKey.UpArrow), Special(ConsoleKey.Enter)]);
        Assert.Equal(0, Chosen(outcome));
    }

    [Fact]
    public void FreeText_SurroundingWhitespace_SurvivesInTheValue()
    {
        //trimming only decides, the value passes on verbatim with its spaces, a widget must not edit what the user typed
        var (_, outcome) = Run(FreeTextSpec(),
            [Digit('3'), .. Type("  hi  "), Special(ConsoleKey.Enter)]);
        Assert.Equal("  hi  ", FreeTextOf(outcome));
    }

    [Fact]
    public void FreeText_DelNeverReachesTheValue()
    {
        //the DEL byte sits above the printable range, so it would pass into the draft while the echo sanitizer hides it from the screen
        var del = new ConsoleKeyInfo((char)0x7F, ConsoleKey.Delete, false, false, false);
        var (surface, outcome) = Run(FreeTextSpec(),
            [Digit('3'), .. Type("a"), del, .. Type("b"), Special(ConsoleKey.Enter)]);

        Assert.Equal("ab", FreeTextOf(outcome));
        Assert.DoesNotContain(((char)0x7F).ToString(), surface.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadLinePanel_DoesNotMutateTheCallersHeadRows()
    {
        //the reader must not mutate the caller's head-row list, otherwise the shared list gains a stale input row per keystroke
        var shared = new List<string> { "reason (optional, Enter to skip):" };
        var frames = new List<List<string>>();

        var line = SelectPrompt.ReadLinePanel(
            new ScriptedKeys([.. Type("hi"), Special(ConsoleKey.Enter)]),
            () => shared, "> ", t => [t], frames.Add);

        Assert.Equal(PanelLineStatus.Submitted, line.Status);
        Assert.Equal("hi", line.Text);
        Assert.Equal(new[] { "reason (optional, Enter to skip):" }, shared);
        Assert.Equal(3, frames.Count);
        Assert.All(frames, f => Assert.Equal(2, f.Count));                      //each frame holds the head row plus exactly one input row.
        Assert.Equal(new[] { "reason (optional, Enter to skip):", "> hi" }, frames[^1]);
    }

    [Fact]
    public void FreeText_EchoSanitized_ValueRaw()
    {
        //control bytes are dropped, while a tab stays raw in the value and echoes expanded, so a row's width stays right
        var (surface, outcome) = Run(FreeTextSpec(),
            [Digit('3'), .. Type("a"), .. Type("\u0001"), Tab(), .. Type("b"), Special(ConsoleKey.Enter)]);

        Assert.Equal("a\tb", FreeTextOf(outcome));

        //assert on a control byte with StringComparison.Ordinal only, a culture comparison gives it zero weight and the check fails on every string
        Assert.DoesNotContain("\u0001", surface.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("\t", surface.Text, StringComparison.Ordinal);
        //the draft echoes on the free-text row, where a tab expands to the next 4-column stop
        Assert.Contains(LastFrame(surface.Text), r => r.Contains("3. a   b", StringComparison.Ordinal));
    }

    [Fact]
    public void FreeText_FooterStaysTheCallers_WhileTyping()
    {
        //there is no editing sub-mode, so the footer hint never swaps while typing and esc keeps its two-step meaning
        var (surface, _) = Run(FreeTextSpec(), [Digit('3'), .. Type("x"), Special(ConsoleKey.Enter)]);
        var frame = LastFrame(surface.Text);
        Assert.Contains(frame, r => r.Contains("Esc to cancel", StringComparison.Ordinal));
        Assert.DoesNotContain(frame, r => r.Contains("Esc to go back", StringComparison.Ordinal));
    }

    [Fact]
    public void FreeText_NullFooter_StaysFooterless_WhileTyping()
    {
        var spec = FreeTextSpec() with { FooterHint = null };
        var (surface, _) = Run(spec, [Digit('3'), .. Type("x"), Special(ConsoleKey.Enter)]);
        Assert.DoesNotContain("Esc to", surface.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void FreeText_DeadPump_Throws_AndClearsThePromptBlock()
    {
        //a key source that throws mid-prompt propagates out of Show, and the finally still clears the panel
        var (_, painter, handle) = Armed();
        var widget = new SelectPrompt(new RecordingSurface(), T, new ScriptedKeys([Digit('3')]), pump: null, chrome: handle);

        Assert.Throws<InvalidOperationException>(() => widget.Show(FreeTextSpec()));

        Assert.Null(painter.State.PanelRows);
        Assert.False(painter.State.PanelUp);
    }

    [Fact]
    public void FreeText_Armed_CaretTargetsTheDraftRow_MidPanel()
    {
        //the hardware cursor parks after the typed text on the free-text row, and PanelCaret is what InputFrame.Compose consumes
        var (surface, painter, handle) = Armed();
        var caret = ((int Row, int Col)?)null;
        IReadOnlyList<string>? frame = null;
        var keys = new ProbingKeys(n =>
        {
            if (n != 3) return;   //sample before enter arrives, so the frame already holds the draft
            caret = painter.State.PanelCaret;
            frame = painter.State.PanelRows;
        }, [Digit('3'), .. Type("hi"), Special(ConsoleKey.Enter)]);
        var widget = new SelectPrompt(surface, T, keys, pump: null, chrome: handle);

        Assert.Equal("hi", FreeTextOf(widget.Show(FreeTextSpec())));
        Assert.NotNull(frame);
        Assert.NotNull(caret);
        var draftRow = Array.FindIndex(frame!.ToArray(), r => r.Contains("3. hi", StringComparison.Ordinal));
        Assert.Equal(draftRow, caret!.Value.Row);
        //the caret column is the width of the row prefix plus the draft text
        Assert.Equal(UnicodeWidth.Of("❯ 3. hi"), caret.Value.Col);
    }

    [Fact]
    public void FreeText_NoPainter_RewindsExactlyOverEveryFrame()
    {
        //the editor repaints in place through the same painted account as the option list. a sub-loop that skips that update rewinds over the wrong count
        var (surface, _) = Run(FreeTextSpec(), [Digit('3'), .. Type("ab"), Special(ConsoleKey.Enter)]);

        var ups = Regex.Matches(surface.Text, CursorUp);
        Assert.True(ups.Count >= 3, $"expected a repaint per keystroke, saw {ups.Count}");
        var start = 0;
        foreach (Match up in ups)
        {
            var frame = surface.Text[start..up.Index];
            Assert.Equal(frame.Count(c => c == '\n'), int.Parse(up.Groups[1].Value));
            start = up.Index + up.Length;
        }
    }

    [Fact]
    public void FreeText_NoPainter_ResizeMidEdit_RowsFit()
    {
        //rows re-compose on every render, so a terminal that shrinks mid-typing still fits on the next keystroke
        var surface = new RecordingSurface { Width = 80 };
        var spec = new SelectSpec(
            ["Write a rather long new file name that will not fit a narrow terminal.txt"],
            "Do you want to proceed with this comparatively verbose question?",
            [new SelectOption("Yes, allow this particular call exactly once for now")],
            FreeTextLabel: "Type my own answer instead of picking from this list");

        var keys = new ProbingKeys(
            n => { if (n == 1) surface.Width = 30; },
            [Digit('2'), .. Type("a free text answer long enough to need wrapping"), Special(ConsoleKey.Enter)]);
        var outcome = new SelectPrompt(surface, T, keys).Show(spec);
        Assert.Equal("a free text answer long enough to need wrapping", FreeTextOf(outcome));

        foreach (var row in LastFrame(surface.Text))
            Assert.True(UnicodeWidth.Of(row) <= 30, $"row overflows the resized surface: '{row}'");
    }

    [Fact]
    public void NoOptions_CancelsWithoutTakingKeys()
    {
        //a prompt with no options cancels without reading a key, otherwise it would block forever with nothing to answer
        var spec = new SelectSpec(["title"], "question", []);
        var widget = new SelectPrompt(new RecordingSurface(), T, new ThrowingKeys());
        Assert.IsType<SelectOutcome.Cancelled>(widget.Show(spec));
    }

    [Fact]
    public void SelectOption_ImplicitFromString()
    {
        SelectOption o = "Yes";
        Assert.Equal(new SelectOption("Yes"), o);
    }

    private static (RecordingSurface S, ChromePainter P, ChromeHandle H) Armed()
    {
        var surface = new RecordingSurface { Width = 80, Height = 0 };
        var gate = new object();
        var status = new StatusInfo(@"C:\proj", "qwen", "coder", new CtxState(), @"C:\Users\x");
        var painter = new ChromePainter(surface, T, gate)
        {
            Frame = new InputFrame(surface, T, "coder", status, glyphs: GlyphSet.Unicode),
            RoleForTint = "coder",
        };
        var renderer = new StreamRenderer(painter, surface, T, "coder", new ChromeTicker(painter, gate), gate);
        return (surface, painter, new ChromeHandle { Painter = painter, Renderer = renderer });
    }

    //snapshot PanelRows just before each scripted key, Show is synchronous so no other moment shows live panel state
    private sealed class SnapshotKeys(ChromePainter painter, IEnumerable<ConsoleKeyInfo> keys) : IKeySource
    {
        private readonly Queue<ConsoleKeyInfo> _q = new(keys);
        public List<IReadOnlyList<string>?> Snapshots { get; } = new();
        public bool KeyAvailable => false;
        public ConsoleKeyInfo ReadKey()
        {
            Snapshots.Add(painter.State.PanelRows);
            return _q.Dequeue();
        }
    }

    [Fact]
    public void Armed_RowsLiveInPanelRows_AndUpdateOnEachMove()
    {
        var (_, painter, handle) = Armed();
        var keys = new SnapshotKeys(painter, [Special(ConsoleKey.DownArrow), Special(ConsoleKey.Enter)]);
        var widget = new SelectPrompt(new RecordingSurface(), T, keys, pump: null, chrome: handle);

        Assert.Equal(1, Chosen(widget.Show(Spec("Yes", "No", "Cancel"))));

        Assert.Equal(2, keys.Snapshots.Count);
        var before = keys.Snapshots[0];
        var after = keys.Snapshots[1];
        Assert.NotNull(before);
        Assert.NotNull(after);
        Assert.Contains(before!, r => r.Contains('❯') && r.Contains("1. Yes"));
        Assert.Contains(after!, r => r.Contains('❯') && r.Contains("2. No"));
        Assert.DoesNotContain(after!, r => r.Contains('❯') && r.Contains("1. Yes"));
        Assert.Null(painter.State.PanelRows);   //once answered, the panel leaves the frame.
    }

    //the most recent compositor frame, everything after the last sync-start sequence
    private static string LastPaint(string text)
    {
        var i = text.LastIndexOf(Ansi.SyncStart, StringComparison.Ordinal);
        return i < 0 ? text : text[i..];
    }

    [Fact]
    public void Armed_OptionPanel_HidesTheHardwareCursor_InputPanelAndClearShowIt()
    {
        //an option list hides the hardware cursor, an input panel keeps it, and clearing the panel restores the composer's cursor
        var (surface, painter, _) = Armed();

        painter.SetPanelFactory(w => new[] { "  1. Yes" });
        Assert.DoesNotContain(Ansi.ShowCursor, LastPaint(surface.Text), StringComparison.Ordinal);

        painter.SetPanel(new[] { "  reason: " });
        Assert.Contains(Ansi.ShowCursor, LastPaint(surface.Text), StringComparison.Ordinal);

        painter.SetPanel(null);
        Assert.Contains(Ansi.ShowCursor, LastPaint(surface.Text), StringComparison.Ordinal);
    }

    [Fact]
    public void NoPainter_CursorHiddenDuringPrompt_RestoredAfter()
    {
        //without a painter the cursor is hidden for the whole prompt and shown again when it resolves
        var (surface, _) = Run(Spec("Yes", "No"), Special(ConsoleKey.Enter));
        Assert.StartsWith(Ansi.HideCursor, surface.Text, StringComparison.Ordinal);
        Assert.EndsWith(Ansi.ShowCursor, surface.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Armed_ResizeBetweenKeys_RepaintRecomposesAtNewWidth()
    {
        //a repaint at the new width brings the full label back, stored rows can only be clipped again
        var (surface, painter, handle) = Armed();
        surface.Width = 40;
        const string longLabel = "laguna-s-2.1-118b-a8b   128k   laguna-s-2.1-Q4_K_M-and-then-some";
        IReadOnlyList<string>? afterGrow = null;
        var probe = new ProbingKeys(n =>
        {
            if (n != 0) return;
            surface.Width = 100;
            painter.Repaint();
            afterGrow = painter.State.PanelRows;
        }, [Special(ConsoleKey.Enter)]);

        new SelectPrompt(surface, T, probe, pump: null, chrome: handle)
            .Show(new SelectSpec(["select a model"], null, [new SelectOption(longLabel)]));

        Assert.NotNull(afterGrow);
        Assert.Contains(afterGrow!, r => TermText.StripAnsiForWidth(r).Contains(longLabel));
    }

    [Fact]
    public void Armed_DetailRows_RewrapOnRepaint_CommandNeverCut()
    {
        //the detail block is the command under review, so a resize re-wraps it rather than clipping it
        var (surface, painter, handle) = Armed();
        surface.Width = 80;
        var command = string.Join(" ", Enumerable.Repeat("Write-Output word;", 12));
        IReadOnlyList<string>? at40 = null;
        var probe = new ProbingKeys(n =>
        {
            if (n != 0) return;
            surface.Width = 40;
            painter.Repaint();
            at40 = painter.State.PanelRows;
        }, [Special(ConsoleKey.Enter)]);

        new SelectPrompt(surface, T, probe, pump: null, chrome: handle)
            .Show(new SelectSpec(["shell command"], "Do you want to proceed?",
                [new SelectOption("Yes"), new SelectOption("No")], DetailRows: [command]));

        Assert.NotNull(at40);
        var plain = at40!.Select(TermText.StripAnsiForWidth).ToList();
        Assert.DoesNotContain(plain, r => r.Contains('…'));
        Assert.Equal(12, plain.Sum(r => Occurrences(r, "Write-Output")));
        Assert.All(plain, r => Assert.True(UnicodeWidth.Of(r.TrimEnd()) <= 40,
            $"row wider than the resized terminal: \"{r}\""));
    }

    [Fact]
    public void Armed_RowsRenderInsideTheComposerFrame()
    {
        //the rows being in PanelRows only proves they got built, so compose the chrome block and check the composer's text comes back
        var (_, painter, handle) = Armed();
        painter.State.Composer = new EditorView(new List<string> { "half-typed" }, 0, 10);
        IReadOnlyList<ChromeRow>? rows = null;
        var keys = new ProbingKeys(_ => rows = painter.ComposeChromeBlock(80, 24).Rows, [Special(ConsoleKey.Enter)]);
        var widget = new SelectPrompt(new RecordingSurface(), T, keys, pump: null, chrome: handle);

        Assert.Equal(0, Chosen(widget.Show(Spec("Yes", "No"))));

        Assert.NotNull(rows);
        var visible = rows!.Select(r => r.Visible).ToList();
        Assert.Contains(visible, r => r.Contains("gatto · coder", StringComparison.Ordinal));
        Assert.Contains(visible, r => r.Contains("1. Yes", StringComparison.Ordinal));
        Assert.DoesNotContain(visible, r => r.Contains("half-typed", StringComparison.Ordinal));

        Assert.Contains(painter.ComposeChromeBlock(80, 24).Rows,
            r => r.Visible.Contains("half-typed", StringComparison.Ordinal));
    }

    [Fact]
    public void Armed_AbortedKeyRead_ClearsThePromptBlock()
    {
        var (_, painter, handle) = Armed();
        var widget = new SelectPrompt(new RecordingSurface(), T, new ThrowingKeys(), pump: null, chrome: handle);

        Assert.Throws<InvalidOperationException>(() => widget.Show(Spec("Yes", "No")));

        Assert.Null(painter.State.PanelRows);
        Assert.False(painter.State.PanelUp);   //once the panel flag is cleared, the frame renders the editor again.
    }

    [Fact]
    public void ArmedButTornDownPainter_FallsBackToInline()
    {
        //a torn-down painter counts as unarmed, otherwise the prompt draws nowhere and still takes the keys
        var (_, painter, handle) = Armed();
        painter.Teardown();
        var surface = new RecordingSurface();
        var widget = new SelectPrompt(surface, T, new ScriptedKeys([Special(ConsoleKey.Enter)]), pump: null, chrome: handle);

        Assert.Equal(0, Chosen(widget.Show(Spec("Yes", "No"))));

        Assert.Contains("Do you want to proceed?", TermText.StripAnsiForWidth(surface.Text));
        Assert.Null(painter.State.PanelRows);
    }

    //the Tabs strip and the Horizontal flag stay opt-in, otherwise a caller written before them has no arm for Moved

    private static readonly string[] Chips = ["Lang", "Build", "Style"];

    //the raw row from the last inline frame, LastFrame strips ANSI so a paint check on the stripped text would pass vacuously
    private static string PaintedRow(string text, string needle)
    {
        var ups = Regex.Matches(text, CursorUp);
        var from = ups.Count == 0 ? 0 : ups[^1].Index + ups[^1].Length;
        foreach (var row in RowsOf(text[from..].TrimStart('\r')))
            if (row.Contains(needle, StringComparison.Ordinal)) return row;
        Assert.Fail($"no row in the last frame carries \"{needle}\"");
        return "";
    }

    [Fact]
    public void Tabs_RenderAsTheFirstRow_ActiveAccentOthersDim()
    {
        var spec = Spec("Yes", "No") with { Tabs = Chips, ActiveTab = 1 };
        var (s, _) = Run(spec, Special(ConsoleKey.Enter));

        var frame = LastFrame(s.Text);
        Assert.Contains("Lang", frame[0], StringComparison.Ordinal);
        Assert.Contains("Build", frame[0], StringComparison.Ordinal);
        Assert.Contains("Style", frame[0], StringComparison.Ordinal);
        Assert.Contains("Write new file test.txt", frame[1], StringComparison.Ordinal);

        //compare the raw painted row with StringComparison.Ordinal, a culture-aware compare weighs escape bytes as zero so the bare word would pass
        var row = PaintedRow(s.Text, "Lang");
        Assert.Contains(T.Paint("Build", Theme.Accent), row, StringComparison.Ordinal);
        Assert.Contains(T.Paint("Lang", Theme.Dim), row, StringComparison.Ordinal);
        Assert.Contains(T.Paint("Style", Theme.Dim), row, StringComparison.Ordinal);
    }

    [Fact]
    public void Tabs_ActiveOutOfRange_PaintsEveryChipDim()
    {
        //an ActiveTab of -1 is legal and means no chip is active, the review step past every question
        var spec = Spec("Yes", "No") with { Tabs = Chips, ActiveTab = -1 };
        var (s, _) = Run(spec, Special(ConsoleKey.Enter));

        var row = PaintedRow(s.Text, "Lang");
        Assert.DoesNotContain(T.Paint("Lang", Theme.Accent), row, StringComparison.Ordinal);
        Assert.DoesNotContain(T.Paint("Build", Theme.Accent), row, StringComparison.Ordinal);
        Assert.DoesNotContain(T.Paint("Style", Theme.Accent), row, StringComparison.Ordinal);
        Assert.Contains(T.Paint("Style", Theme.Dim), row, StringComparison.Ordinal);
    }

    [Fact]
    public void Tabs_ModelEscapesRenderInert()
    {
        //the widget sanitizes the strip like any caller-supplied string, so an escape byte in it renders as literal characters
        var spec = Spec("Yes", "No") with { Tabs = ["Go\x1b[5A\x1b[2K", "Rust"], ActiveTab = 0 };
        var (s, _) = Run(spec, Special(ConsoleKey.Enter));

        //the stripped frame keeps the trailing bytes as literal text, the escape byte itself is gone
        Assert.Contains("[5A", LastFrame(s.Text)[0], StringComparison.Ordinal);

        //check the raw row for the model's own sequence, the widget paints with escapes so a blanket absence check would fail
        Assert.DoesNotContain("\x1b[5A", PaintedRow(s.Text, "[5A"), StringComparison.Ordinal);
        Assert.DoesNotContain("\x1b[2K", PaintedRow(s.Text, "[5A"), StringComparison.Ordinal);
    }

    [Fact]
    public void Tabs_Absent_NoStripRow()
    {
        var (s, _) = Run(Spec("Yes", "No"), Special(ConsoleKey.Enter));
        Assert.Equal("Write new file test.txt", LastFrame(s.Text)[0].Trim());
    }

    [Fact]
    public void Tabs_Empty_NoStripRow()
    {
        var spec = Spec("Yes", "No") with { Tabs = Array.Empty<string>() };
        var (s, _) = Run(spec, Special(ConsoleKey.Enter));
        Assert.Equal("Write new file test.txt", LastFrame(s.Text)[0].Trim());
    }

    [Fact]
    public void Tabs_TooWideForTheTerminal_NoRowOverflows()
    {
        //no row may exceed the terminal, a soft-wrapped row breaks the inline cursor-up arithmetic and the strip flows rather than shortening its chips
        var surface = new RecordingSurface { Width = 20, Height = 0 };
        var spec = Spec("Yes", "No") with { Tabs = ["AAAAAAAAAA", "BBBBBBBBBB", "CCCCCCCCCC"], ActiveTab = 0 };
        new SelectPrompt(surface, T, new ScriptedKeys([Special(ConsoleKey.Enter)])).Show(spec);

        foreach (var row in LastFrame(surface.Text))
            Assert.True(UnicodeWidth.Of(row) <= 20, $"row wider than the terminal: \"{row}\"");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Tabs_AtTheSchemaMaximum_EveryChipSurvives_AndTheActiveOneIsStillPainted(int active)
    {
        //four 32-character headers overflow 80 columns and the strip flows, so every chip survives and each active index gets its own case
        var headers = new[]
        {
            new string('A', 32), new string('B', 32), new string('C', 32), new string('D', 32),
        };
        var surface = new RecordingSurface { Width = 80, Height = 0 };
        var spec = Spec("Yes", "No") with { Tabs = headers, ActiveTab = active };
        new SelectPrompt(surface, T, new ScriptedKeys([Special(ConsoleKey.Enter)])).Show(spec);

        var frame = LastFrame(surface.Text);
        foreach (var row in frame)
            Assert.True(UnicodeWidth.Of(row) <= 80, $"strip overflows: \"{row}\"");
        foreach (var header in headers)
            Assert.Contains(frame, r => r.Contains(header, StringComparison.Ordinal));
        //the live chip is painted accent and its neighbours dim, that contrast shows where you are
        Assert.Contains(T.Paint(headers[active], Theme.Accent), surface.Text, StringComparison.Ordinal);
        foreach (var i in Enumerable.Range(0, headers.Length).Where(i => i != active))
            Assert.Contains(T.Paint(headers[i], Theme.Dim), surface.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Tabs_ThatFit_AreNotElided()
    {
        //short headers that fit render whole, elision is only for a strip too wide for the terminal
        var spec = Spec("Yes", "No") with { Tabs = Chips, ActiveTab = 0 };
        var (s, _) = Run(spec, Special(ConsoleKey.Enter));
        Assert.Equal("Lang  Build  Style", LastFrame(s.Text)[0].Trim());
    }

    [Fact]
    public void Tabs_OneLongChipThatStillFits_KeepsItsWholeLabel()
    {
        var wide = new string('A', 50);
        var spec = Spec("Yes", "No") with { Tabs = new[] { wide, "B" }, ActiveTab = 0 };
        var (s, _) = Run(spec, Special(ConsoleKey.Enter));

        var strip = LastFrame(s.Text)[0];
        Assert.Contains(wide, strip, StringComparison.Ordinal);
        Assert.DoesNotContain("…", strip, StringComparison.Ordinal);
    }

    [Fact]
    public void Horizontal_Off_LeftAndRightAreDeadKeys()
    {
        //with Horizontal off both arrows are dead and the widget keeps waiting, so Moved stays unreachable for a caller that never opted in
        var (_, outcome) = Run(Spec("Yes", "No"),
            Special(ConsoleKey.LeftArrow), Special(ConsoleKey.RightArrow), Special(ConsoleKey.Enter));
        Assert.Equal(0, Chosen(outcome));
    }

    [Fact]
    public void Horizontal_RightArrow_ResolvesAsMovedForward()
    {
        var spec = Spec("Yes", "No", "Cancel") with { Horizontal = true };
        var (_, outcome) = Run(spec, Special(ConsoleKey.DownArrow), Special(ConsoleKey.RightArrow));

        var moved = Assert.IsType<SelectOutcome.Moved>(outcome);
        Assert.Equal(1, moved.Delta);
        Assert.Equal(1, moved.Cursor);     //the live cursor travels too, so the caller can restore this step.
        Assert.Empty(moved.Selected);      //a single-select result holds no toggles.
    }

    [Fact]
    public void Horizontal_LeftArrow_ResolvesAsMovedBackward()
    {
        var spec = Spec("Yes", "No", "Cancel") with { Horizontal = true };
        var (_, outcome) = Run(spec, Special(ConsoleKey.LeftArrow));

        var moved = Assert.IsType<SelectOutcome.Moved>(outcome);
        Assert.Equal(-1, moved.Delta);
        Assert.Equal(0, moved.Cursor);
    }

    [Fact]
    public void Horizontal_MultiSelect_MovedCarriesTheToggledSetAscending()
    {
        var spec = new SelectSpec(["Pick"], "Which?", [new("Go"), new("Rust"), new("Zig")])
            { MultiSelect = true, Horizontal = true };
        //the toggled set comes back ascending, so the press order never shows
        var (_, outcome) = Run(spec, Digit('3'), Digit('1'), Special(ConsoleKey.RightArrow));

        var moved = Assert.IsType<SelectOutcome.Moved>(outcome);
        Assert.Equal([0, 2], moved.Selected);
    }

    [Fact]
    public void Horizontal_DoesNotDisturbUpDownOrEnter()
    {
        var spec = Spec("Yes", "No", "Cancel") with { Horizontal = true };
        var (_, outcome) = Run(spec,
            Special(ConsoleKey.DownArrow), Special(ConsoleKey.DownArrow), Special(ConsoleKey.Enter));
        Assert.Equal(2, Chosen(outcome));
    }

    [Fact]
    public void Horizontal_EscStillCancels_NotAMove()
    {
        var spec = Spec("Yes", "No") with { Horizontal = true };
        var (_, outcome) = Run(spec, Special(ConsoleKey.Escape));
        Assert.IsType<SelectOutcome.Cancelled>(outcome);
    }

    private sealed class SyncSurface : ITermSurface
    {
        private readonly System.Text.StringBuilder _sb = new();
        public int Width { get; set; } = 80;
        public int Height { get; set; }
        public void Write(string s) { lock (_sb) _sb.Append(s); }
        public string Text { get { lock (_sb) return _sb.ToString(); } }
    }

    private sealed class FeedKeys(System.Collections.Concurrent.BlockingCollection<ConsoleKeyInfo> q) : IKeySource
    {
        public ConsoleKeyInfo ReadKey() => q.Take();
        public bool KeyAvailable => q.Count > 0;
    }

    private sealed class NeverKeys : IKeySource
    {
        public ConsoleKeyInfo ReadKey() => throw new InvalidOperationException("constructor keys must not be used when a pump is present");
        public bool KeyAvailable => false;
    }

    //bounded wait for the widget's answer, null means it never came (the widget blocks by contract with no async seam)
    private static SelectOutcome? WaitFor(Task<SelectOutcome> show, int ms = 30000) =>
        show.Wait(ms) ? show.Result : null;

    [Fact]
    public void WithPump_ReadsKeysViaFocus_TypeAheadSurvivesInComposer()
    {
        var src = new System.Collections.Concurrent.BlockingCollection<ConsoleKeyInfo>();
        var pump = new InputPump(new KeyInputSource(new FeedKeys(src)));
        pump.Start();
        src.Add(new ConsoleKeyInfo('t', ConsoleKey.T, false, false, false));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while ((src.Count > 0 || !pump.Composer.KeyAvailable) && sw.ElapsedMilliseconds < 30000) Thread.Sleep(5);
        Assert.Empty(src);
        Assert.True(pump.Composer.KeyAvailable);

        var surface = new SyncSurface();
        //keys come through the focus scope, and the push above the first render makes that render the sync point for feeding answers
        var widget = new SelectPrompt(surface, T, new NeverKeys(), pump);
        var show = Task.Run(() => widget.Show(Spec("Yes", "No", "Cancel")));

        var render = System.Diagnostics.Stopwatch.StartNew();
        //the loop is a sync on the first surface write, the 30000ms deadline only keeps a missing render from hanging the test
        while (!surface.Text.Contains("Do you want to proceed?") && render.ElapsedMilliseconds < 30000) Thread.Sleep(5);
        Assert.Contains("Do you want to proceed?", surface.Text);

        src.Add(Special(ConsoleKey.DownArrow));
        src.Add(Special(ConsoleKey.Enter));
        var result = WaitFor(show);
        Assert.NotNull(result);
        Assert.Equal(1, Chosen(result!));

        //the type-ahead character was not consumed by the widget and still awaits the composer.
        Assert.True(pump.Composer.KeyAvailable);
    }

    [Fact]
    public void AN_EMPTY_FREE_TEXT_ROW_IS_DIM_WHEREVER_THE_CURSOR_IS()
    {
        //an empty placeholder row is dim wherever the cursor is, a dim that only follows the cursor reads backwards
        var spec = new SelectSpec(
            ["title"], "question",
            [new SelectOption("Yes"), new SelectOption("No")],
            FreeTextLabel: "Type my own answer");
        var (surface, _) = Run(spec, Special(ConsoleKey.Enter));   //the cursor stays on the first row, so the free-text row is off-cursor.

        Assert.Contains(T.Paint("Type my own answer", Theme.Dim), surface.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_TYPED_DRAFT_IS_NOT_DIMMED_WHEN_THE_CURSOR_LEAVES_IT()
    {
        //a typed draft is content, dimming it off-cursor would make the user's own typing look like chrome
        var spec = new SelectSpec(
            ["title"], "question",
            [new SelectOption("Yes"), new SelectOption("No")],
            FreeTextLabel: "Type my own answer");
        var (surface, _) = Run(spec,
            Special(ConsoleKey.DownArrow), Special(ConsoleKey.DownArrow),
            new ConsoleKeyInfo('h', ConsoleKey.H, false, false, false),
            new ConsoleKeyInfo('i', ConsoleKey.I, false, false, false),
            Special(ConsoleKey.UpArrow),
            Special(ConsoleKey.Enter));

        //assert both halves, an absence check alone passes even when the draft is not painted
        Assert.DoesNotContain(T.Paint("hi", Theme.Dim), surface.Text, StringComparison.Ordinal);
        Assert.Contains("hi", TermText.StripAnsiForWidth(surface.Text), StringComparison.Ordinal);
    }

    [Fact]
    public void A_SEEDED_DRAFT_IS_NOT_DIMMED_EITHER()
    {
        //a seeded draft takes the same path as a typed one, draftShown looks at the buffer's content alone
        var spec = new SelectSpec(
            ["title"], "question",
            [new SelectOption("Yes"), new SelectOption("No")],
            FreeTextLabel: "Type my own answer",
            InitialDraft: "seeded");
        var (surface, _) = Run(spec, Special(ConsoleKey.Enter));   //the cursor stays on the first row, so the seeded row is off-cursor.

        Assert.DoesNotContain(T.Paint("seeded", Theme.Dim), surface.Text, StringComparison.Ordinal);
        Assert.Contains("seeded", TermText.StripAnsiForWidth(surface.Text), StringComparison.Ordinal);
    }

    [Fact]
    public void THE_DIGIT_HINT_SAYS_WHAT_IS_TRUE_AND_COUNTS_NOTHING()
    {
        //the hint names no count and no arrow, rows past nine keep their place and lose their digit
        var spec = new SelectSpec(
            ["title"], "question",
            [.. Enumerable.Range(1, 12).Select(i => new SelectOption($"option {i}"))]);
        var (surface, _) = Run(spec, Special(ConsoleKey.Enter));

        var plain = TermText.StripAnsiForWidth(surface.Text);
        Assert.Contains("the rest have no number — use the arrow keys", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("↓", plain, StringComparison.Ordinal);
        Assert.DoesNotContain(" more —", plain, StringComparison.Ordinal);
        //every row the hint described is still rendered, without a number.
        Assert.Contains("option 12", plain, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(40)]
    [InlineData(60)]
    [InlineData(80)]
    [InlineData(120)]
    public void THE_DIM_EMPTY_ROW_AND_THE_DIGIT_HINT_HOLD_AT_EVERY_WIDTH(int width)
    {
        //the hint is a full-width row that Fit truncates, so sweep widths rather than asserting at one
        var surface = new RecordingSurface { Width = width };
        var spec = new SelectSpec(
            ["title"], "question",
            [.. Enumerable.Range(1, 12).Select(i => new SelectOption($"option {i}"))],
            FreeTextLabel: "Type my own answer");
        new SelectPrompt(surface, T, new ScriptedKeys([Special(ConsoleKey.Enter)])).Show(spec);

        Assert.Contains(T.Paint("Type my own answer", Theme.Dim), surface.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("↓", TermText.StripAnsiForWidth(surface.Text), StringComparison.Ordinal);
    }


    [Fact]
    public void AN_EMPTY_TAIL_ERASES_THE_BLOCK_AND_WRITES_NOTHING()
    {
        //an empty EchoTail erases the block, body rows included, so a screen that re-asks itself never stacks its rows twice
        var spec = new SelectSpec(
            [], new PromptQuestion("Nothing new here yet"),
            [new SelectOption("Look again")],
            BodyRows: [new BodyRow("the file will land in Downloads")],
            EchoOnCompletion: true,
            EchoCompose: _ => new EchoTail([]));
        var (surface, _) = Run(spec, Special(ConsoleKey.Enter));

        var screen = TerminalReplay.Plain(surface.Text);
        Assert.DoesNotContain("Nothing new here yet", screen, StringComparison.Ordinal);
        Assert.DoesNotContain("Look again", screen, StringComparison.Ordinal);
        Assert.DoesNotContain("the file will land in Downloads", screen, StringComparison.Ordinal);
    }

    [Fact]
    public void A_REAL_TAIL_STILL_COMMITS_THROUGH_THE_SAME_SEAM()
    {
        //the control case for the empty tail, a broken echo path would empty the screen too and pass the absence check
        var spec = new SelectSpec(
            [], new PromptQuestion("Nothing new here yet"),
            [new SelectOption("Look again")],
            EchoOnCompletion: true,
            EchoCompose: i => new EchoTail(["  committed: " + i.Answer]));
        var (surface, _) = Run(spec, Special(ConsoleKey.Enter));

        Assert.Contains("committed: Look again", TerminalReplay.Plain(surface.Text), StringComparison.Ordinal);
    }

    //the widget is shared, so every capability it gained is opt-in and an existing caller must render exactly as before

    //on a numbered row with no mark and no box the label starts at column 5, the cursor glyph and number take those cells
    private const int LabelColumn = 5;

    private static readonly string[] LadderNames = ["qwen3-30b-a3b-instruct", "gemma-3-12b-it"];

    //return the widest rung that fits, measured against the real label column so no threshold table can drift
    private static IReadOnlyList<string> LadderAt(int w)
    {
        var rungs = new[]
        {
            LadderNames.Select(n => $"{n}  18.2 GB  Q4_K_M  2026-07").ToList(),   //the full rung drops nothing.
            LadderNames.Select(n => $"{n}  18.2 GB  Q4_K_M").ToList(),            //the middle rung drops the update date.
            LadderNames.Select(n => $"{n}  18.2 GB").ToList(),                    //the last rung is the floor of the ladder.
        };
        foreach (var rung in rungs)
            if (rung.All(l => UnicodeWidth.Of(l) <= w - LabelColumn)) return rung;
        return rungs[^1];
    }

    //the option's Label stays the identity, LabelsAt only changes what is displayed
    private static SelectSpec LadderSpec() => new(
        TitleRows: [],
        Question: null,
        Options: [.. LadderNames.Select(n => new SelectOption(n))],
        FooterHint: null,
        LabelsAt: LadderAt);

    private static string[] FrameAt(SelectSpec spec, int width, params ConsoleKeyInfo[] keys)
    {
        var surface = new RecordingSurface { Width = width };
        new SelectPrompt(surface, T, new ScriptedKeys(keys)).Show(spec);
        return LastFrame(surface.Text);
    }

    [Fact]
    public void LabelsAt_A_WIDTH_SWEEP_RENDERS_THE_RUNG_THAT_FITS_AND_TRUNCATES_NOTHING()
    {
        //the widget re-reads its width on every render, so LabelsAt is asked again at each width and the sweep covers every rung
        var rungs = new HashSet<string>();

        for (var w = 36; w <= 70; w++)
        {
            var frame = FrameAt(LadderSpec(), w, Special(ConsoleKey.Enter));
            var expected = LadderAt(w);
            rungs.Add(string.Join("|", expected));

            foreach (var label in expected)
                Assert.True(frame.Any(r => r.Contains(label, StringComparison.Ordinal)),
                    $"width {w}: no row carries the rung's label \"{label}\"\n  {string.Join("\n  ", frame)}");

            foreach (var row in frame)
            {
                Assert.DoesNotContain("…", row, StringComparison.Ordinal);
                Assert.True(UnicodeWidth.Of(row) <= w,
                    $"width {w}: row overflows at {UnicodeWidth.Of(row)} cells — \"{row}\"");
            }
        }

        //assert three distinct rungs, a widget that asked once and cached would pass the sweep otherwise
        Assert.Equal(3, rungs.Count);
    }

    [Fact]
    public void LabelsAt_A_NARROWED_PROMPT_REPAINTS_AT_THE_NEW_RUNG()
    {
        //the sweep builds a fresh widget each time, so this test narrows one prompt mid-flight to catch a cache at construction
        var surface = new RecordingSurface { Width = 70 };
        var keys = new ProbingKeys(
            n => { if (n == 0) surface.Width = 44; },
            [Special(ConsoleKey.DownArrow), Special(ConsoleKey.Enter)]);
        new SelectPrompt(surface, T, keys).Show(LadderSpec());

        var frame = LastFrame(surface.Text);
        Assert.Contains(frame, r => r.Contains("qwen3-30b-a3b-instruct  18.2 GB  Q4_K_M", StringComparison.Ordinal));
        //the dropped date is the oracle, present at 70 and gone at 44, survivors alone would pass a widget still painting the wide rung
        Assert.DoesNotContain(frame, r => r.Contains("2026-07", StringComparison.Ordinal));
    }

    [Fact]
    public void LabelsAt_THE_ECHO_STILL_REPORTS_THE_OPTIONS_OWN_LABEL()
    {
        //the display override must not become the identity, or the echo reports a whole table row
        string? answered = null;
        var surface = new RecordingSurface { Width = 70 };
        new SelectPrompt(surface, T, new ScriptedKeys([Special(ConsoleKey.Enter)])).Show(
            LadderSpec() with
            {
                EchoOnCompletion = true,
                EchoCompose = i => { answered = i.Answer; return new EchoTail([]); },
            });

        Assert.Equal("qwen3-30b-a3b-instruct", answered);
    }

    [Fact]
    public void LabelsAt_A_LIST_THAT_DOES_NOT_MATCH_THE_OPTIONS_IS_A_CALLER_ERROR()
    {
        //refuse a mismatched label list before any key is read, the empty key queue is what proves the order
        var spec = LadderSpec() with { LabelsAt = _ => ["only one"] };

        var ex = Assert.Throws<ArgumentException>(() =>
            new SelectPrompt(new RecordingSurface { Width = 70 }, T, new ScriptedKeys([])).Show(spec));
        Assert.Contains("LabelsAt", ex.Message, StringComparison.Ordinal);
    }

    //three options that each have a description, for the check that a single-line description renders under its option
    private static SelectSpec ThreeDescribed() => new(
        TitleRows: [],
        Question: null,
        Options:
        [
            new SelectOption("alpha", Description: "about alpha"),
            new SelectOption("beta", Description: "about beta"),
            new SelectOption("gamma", Description: "about gamma"),
        ],
        FooterHint: null);

    //a wrapped identifier arrives as one label holding a newline, which is the shape AlignedRows produces
    private static SelectSpec TwoLineFirst() => new(
        TitleRows: [],
        Question: null,
        Options: [new SelectOption("alpha"), new SelectOption("beta")],
        FooterHint: null,
        LabelsAt: _ => ["alpha-wrapped-name\n  18.2 GB  Q4_K_M", "beta"]);

    [Fact]
    public void MultiLineLabel_THE_CONTINUATION_ALIGNS_UNDER_THE_LABEL_COLUMN()
    {
        //a continuation starts under the label column with no number, at column zero it would read as a second option
        var frame = FrameAt(TwoLineFirst(), 60, Special(ConsoleKey.Enter));

        var cont = Assert.Single(frame, r => r.Contains("18.2 GB  Q4_K_M", StringComparison.Ordinal));
        //blanks through the label column prove no caret and no number, the caret takes cells 0-1 and the number 2-4
        Assert.StartsWith(new string(' ', LabelColumn), cont, StringComparison.Ordinal);
        //the head line keeps the number.
        Assert.Contains(frame, r => r.Contains("1. alpha-wrapped-name", StringComparison.Ordinal));
    }

    [Fact]
    public void MultiLineLabel_THE_CURSOR_STEPS_OVER_IT_IN_ONE_PRESS()
    {
        //a wrapped identifier stays one selectable row, splitting the lines would put the cursor on the continuation and enter the wrong model
        var surface = new RecordingSurface { Width = 60 };
        var outcome = new SelectPrompt(surface, T,
                new ScriptedKeys([Special(ConsoleKey.DownArrow), Special(ConsoleKey.Enter)]))
            .Show(TwoLineFirst());

        Assert.Equal(1, Chosen(outcome));
        var cursorRow = Assert.Single(LastFrame(surface.Text), r => r.StartsWith("❯", StringComparison.Ordinal));
        Assert.Contains("beta", cursorRow, StringComparison.Ordinal);
    }

    [Fact]
    public void MultiLineLabel_A_DIGIT_PICKS_THE_ROW_AFTER_IT_BY_ITS_OWN_NUMBER()
    {
        //numbering is per option, the two-line first option owns 1 and the next option gets 2
        var outcome = new SelectPrompt(new RecordingSurface { Width = 60 }, T,
            new ScriptedKeys([Digit('2')])).Show(TwoLineFirst());

        Assert.Equal(1, Chosen(outcome));
    }

    [Fact]
    public void PaintPassthrough_A_PAINTED_LABEL_REACHES_THE_SURFACE_INTACT()
    {
        //a pre-painted label is not sanitized here, so the supplier must sanitize it (CellSpans already does, before it measures)
        var painted = T.Paint("verified", Theme.Accent);
        var surface = new RecordingSurface { Width = 60 };
        new SelectPrompt(surface, T, new ScriptedKeys([Special(ConsoleKey.Enter)])).Show(new SelectSpec(
            TitleRows: [], Question: null,
            Options: [new SelectOption("m")], FooterHint: null,
            LabelsAt: _ => [painted]));

        Assert.Contains(painted, surface.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void PaintPassthrough_THE_CURSOR_ROW_PAINTS_ITS_CHROME_AND_LEAVES_THE_LABEL_ALONE()
    {
        //chrome paints on its own and the label passes through verbatim, one run would break at the label's first reset
        var painted = T.Paint("verified", Theme.Accent);
        var surface = new RecordingSurface { Width = 60 };
        new SelectPrompt(surface, T, new ScriptedKeys([Special(ConsoleKey.Enter)])).Show(new SelectSpec(
            TitleRows: [], Question: null,
            Options: [new SelectOption("m")], FooterHint: null,
            LabelsAt: _ => [painted]));

        Assert.Contains(T.Paint("❯ 1. ", Theme.Accent) + painted, surface.Text, StringComparison.Ordinal);
    }

    //a structural row takes no digit, or the nine options after it lose theirs

    //six models and three escape options fill exactly nine numbered rows, so headings must stay free of numbering
    private static SelectSpec ShelfShaped(bool withHeadings) => new(
        TitleRows: [],
        Question: null,
        Options:
        [
            new SelectOption("model-a"), new SelectOption("model-b"), new SelectOption("model-c"),
            new SelectOption("model-d"), new SelectOption("model-e"), new SelectOption("model-f"),
            new SelectOption("point at a folder"), new SelectOption("type an id"),
            new SelectOption("every publisher"),
        ],
        FooterHint: null,
        HeadingsAt: !withHeadings ? null : _ =>
        [
            new SelectHeading(0, "model   size"),                  //the header row of the table, inserted at the top.
            new SelectHeading(0, "── fits your graphics card"),    //the first tier heading.
            new SelectHeading(3, "── fits in system memory"),      //the second tier heading, inserted between groups.
        ]);

    [Fact]
    public void Headings_RENDER_ABOVE_THE_OPTION_THEY_NAME()
    {
        var frame = FrameAt(ShelfShaped(withHeadings: true), 60, Special(ConsoleKey.Enter));

        int At(string needle) => Array.FindIndex(frame, r => r.Contains(needle, StringComparison.Ordinal));
        Assert.True(At("model   size") < At("fits your graphics card"),
            "the table header renders above the first tier line");
        Assert.True(At("fits your graphics card") < At("model-a"), "tier one opens its group");
        //a heading sits mid-list, which a region above the options can never express, so headings are not BodyRows
        Assert.True(At("model-c") < At("fits in system memory") && At("fits in system memory") < At("model-d"),
            "tier two renders BETWEEN the groups it separates");
    }

    [Fact]
    public void Headings_COST_NO_DIGIT_AND_ALL_NINE_OPTIONS_KEEP_THEIRS()
    {
        //six models plus three escapes is exactly nine, so a heading that took a digit would strip the escape rows
        var frame = FrameAt(ShelfShaped(withHeadings: true), 60, Special(ConsoleKey.Enter));
        var labels = new[]
        {
            "model-a", "model-b", "model-c", "model-d", "model-e", "model-f",
            "point at a folder", "type an id", "every publisher",
        };

        for (var i = 0; i < labels.Length; i++)
        {
            var expected = $"{i + 1}. {labels[i]}";
            Assert.True(frame.Any(r => r.Contains(expected, StringComparison.Ordinal)),
                $"option {i} lost its digit: expected \"{expected}\"\n  {string.Join("\n  ", frame)}");
        }

        //the no-number hint must not appear, because nothing is past nine.
        Assert.DoesNotContain(frame, r => r.Contains("the rest have no number", StringComparison.Ordinal));
    }

    [Fact]
    public void Headings_THE_CURSOR_CANNOT_LAND_ON_ONE()
    {
        //a heading between model-c and model-d catches a skip implemented as an end clamp, three presses from the top must reach model-d
        var surface = new RecordingSurface { Width = 60 };
        var outcome = new SelectPrompt(surface, T, new ScriptedKeys(
        [
            Special(ConsoleKey.DownArrow), Special(ConsoleKey.DownArrow),
            Special(ConsoleKey.DownArrow), Special(ConsoleKey.Enter),
        ])).Show(ShelfShaped(withHeadings: true));

        Assert.Equal(3, Chosen(outcome));
        var cursorRow = Assert.Single(LastFrame(surface.Text), r => r.StartsWith("❯", StringComparison.Ordinal));
        Assert.Contains("model-d", cursorRow, StringComparison.Ordinal);
    }

    [Fact]
    public void Headings_ARE_RE_ASKED_AT_EVERY_WIDTH()
    {
        //a width-derived heading cached at construction goes stale, and only one Show spanning two widths shows it
        var surface = new RecordingSurface { Width = 70 };
        var keys = new ProbingKeys(
            n => { if (n == 0) surface.Width = 40; },
            [Special(ConsoleKey.DownArrow), Special(ConsoleKey.Enter)]);
        new SelectPrompt(surface, T, keys).Show(new SelectSpec(
            TitleRows: [], Question: null,
            Options: [new SelectOption("a"), new SelectOption("b")], FooterHint: null,
            HeadingsAt: w => [new SelectHeading(0, w >= 70 ? "wide heading" : "narrow")]));

        var frame = LastFrame(surface.Text);
        Assert.Contains(frame, r => r.Contains("narrow", StringComparison.Ordinal));
        Assert.DoesNotContain(frame, r => r.Contains("wide heading", StringComparison.Ordinal));
    }

    [Fact]
    public void Headings_ARE_OFF_BY_DEFAULT()
    {
        //without headings the screen keeps its old shape exactly, which is the guard for every existing caller
        var frame = FrameAt(ShelfShaped(withHeadings: false), 60, Special(ConsoleKey.Enter));

        Assert.DoesNotContain(frame, r => r.Contains("fits your graphics card", StringComparison.Ordinal));
        Assert.Contains(frame, r => r.Contains("1. model-a", StringComparison.Ordinal));
    }

    [Fact]
    public void A_DESCRIPTION_CARRYING_NEWLINES_RENDERS_ONE_ROW_PER_LINE()
    {
        //each line of a description renders its own row, folding them into one paragraph makes the urls uncopyable
        var frame = FrameAt(new SelectSpec(
            TitleRows: [], Question: null,
            Options: [new SelectOption("m", Description: "context size 32,768\nhttps://example/one\nhttps://example/two")],
            FooterHint: null), 60, Special(ConsoleKey.Enter));

        var one = Array.FindIndex(frame, r => r.Contains("https://example/one", StringComparison.Ordinal));
        var two = Array.FindIndex(frame, r => r.Contains("https://example/two", StringComparison.Ordinal));
        Assert.True(one >= 0 && two == one + 1, "each line of a description is its own row, in order");
        //the line above must not absorb the rows below.
        Assert.DoesNotContain(frame, r =>
            r.Contains("32,768", StringComparison.Ordinal) && r.Contains("https://", StringComparison.Ordinal));
    }

    [Fact]
    public void A_DESCRIPTION_WITHOUT_NEWLINES_IS_UNCHANGED()
    {
        //a description with no newline takes the old path, so an existing caller wraps exactly as before
        var frame = FrameAt(ThreeDescribed(), 60, Special(ConsoleKey.Enter));

        //the description sits directly under its option, the split adds no blank row
        var opt = Array.FindIndex(frame, r => r.Contains("1. alpha", StringComparison.Ordinal));
        Assert.True(opt >= 0, "the option row is missing");
        Assert.Equal("about alpha", frame[opt + 1].Trim());
    }

    [Fact]
    public void Headings_A_HEADING_BEFORE_NO_REAL_OPTION_IS_A_CALLER_ERROR()
    {
        //a heading naming no option is a caller error, refused before a key is read, dropping it silently would lose a table header
        var spec = ShelfShaped(withHeadings: true) with
        {
            HeadingsAt = _ => [new SelectHeading(99, "nowhere")],
        };

        var ex = Assert.Throws<ArgumentException>(() =>
            new SelectPrompt(new RecordingSurface { Width = 60 }, T, new ScriptedKeys([])).Show(spec));
        Assert.Contains("HeadingsAt", ex.Message, StringComparison.Ordinal);
    }

    //a control press commits nothing, and a frame cannot show that so the oracle is whether EchoCompose ran

    private static SelectSpec WithControls(params char[] keys) => new(
        TitleRows: [],
        Question: null,
        Options: [new SelectOption("alpha"), new SelectOption("beta"), new SelectOption("gamma")],
        FooterHint: null,
        ControlKeys: keys);

    [Fact]
    public void ControlKey_RESOLVES_THE_PROMPT_and_names_the_key()
    {
        var (_, outcome) = Run(WithControls('f', 's'), Letter('s'));

        var control = Assert.IsType<SelectOutcome.Control>(outcome);
        Assert.Equal('s', control.Key);
    }

    [Fact]
    public void ControlKey_CARRIES_THE_LIVE_CURSOR_so_the_caller_can_put_the_user_back()
    {
        //the caller re-opens the prompt after a control press, so the outcome must report the live cursor or the user loses their row
        var (_, outcome) = Run(WithControls('f'),
            Special(ConsoleKey.DownArrow), Special(ConsoleKey.DownArrow), Letter('f'));

        Assert.Equal(2, Assert.IsType<SelectOutcome.Control>(outcome).Cursor);
    }

    [Fact]
    public void A_CONTROL_PRESS_COMPOSES_NO_TAIL()
    {
        //the oracle is whether EchoCompose was invoked, so keep EchoOnCompletion on or the test checks the gate instead
        var composed = 0;
        var spec = WithControls('f') with
        {
            EchoOnCompletion = true,
            EchoCompose = _ => { composed++; return new EchoTail([]); },
        };

        Run(spec, Letter('f'));
        Assert.Equal(0, composed);

        //an answer on the same spec composes exactly once, otherwise a composer that was never wired would pass too
        Run(spec, Special(ConsoleKey.Enter));
        Assert.Equal(1, composed);
    }

    [Fact]
    public void A_LETTER_THAT_IS_NOT_REGISTERED_STAYS_DEAD()
    {
        //an unknown letter is not a cancel, the enter after it proves the prompt was still live
        var (_, outcome) = Run(WithControls('f'), Letter('z'), Special(ConsoleKey.Enter));

        Assert.Equal(0, Chosen(outcome));
    }

    [Fact]
    public void ControlKeys_ARE_ABSENT_BY_DEFAULT_and_every_letter_stays_as_dead_as_it_was()
    {
        //a caller that registers no control keys must never reach SelectOutcome.Control, so the spec leaves the parameter out
        var spec = new SelectSpec(
            TitleRows: [], Question: null,
            Options: [new SelectOption("alpha")], FooterHint: null);

        var (_, outcome) = Run(spec, Letter('f'), Special(ConsoleKey.Enter));
        Assert.Equal(0, Chosen(outcome));
    }

    [Fact]
    public void ON_THE_FREE_TEXT_ROW_A_CONTROL_KEY_IS_A_CHARACTER()
    {
        //every printable belongs to the draft on that row, a field that ate an f as a control would be unusable
        var spec = WithControls('f') with { FreeTextLabel = "Type a name" };

        ConsoleKeyInfo[] keys =
        [
            Special(ConsoleKey.DownArrow), Special(ConsoleKey.DownArrow), Special(ConsoleKey.DownArrow),
            .. Type("f"), Special(ConsoleKey.Enter),
        ];
        var (_, outcome) = Run(spec, keys);

        Assert.Equal("f", Assert.IsType<SelectOutcome.FreeText>(outcome).Text);
    }

    [Fact]
    public void A_RENDERED_NUMBER_BEATS_A_CONTROL_KEY_OF_THE_SAME_DIGIT()
    {
        //a rendered number is a promise about a key, so it wins over a control key of the same digit
        var (_, numbered) = Run(WithControls('2'), Digit('2'));
        Assert.Equal(1, Chosen(numbered));

        //on an unnumbered screen nothing promises the digit, so the control key gets it
        var (_, unnumbered) = Run(WithControls('2') with { Numbered = false }, Digit('2'));
        Assert.Equal('2', Assert.IsType<SelectOutcome.Control>(unnumbered).Key);
    }

    //only the widget knows the width at paint time, so it right-aligns the footer legend itself

    private static SelectSpec WithLegend(string? hint, string? legend) => new(
        TitleRows: [],
        Question: null,
        Options: [new SelectOption("alpha"), new SelectOption("beta")],
        FooterHint: hint,
        FooterLegend: legend);

    [Fact]
    public void FooterLegend_SHARES_THE_HINTS_ROW_and_ends_at_the_RIGHT_EDGE()
    {
        var frame = FrameAt(WithLegend("Esc to leave", "◈ vision"), 60, Special(ConsoleKey.Enter));

        var row = Assert.Single(frame, r => r.Contains("Esc to leave", StringComparison.Ordinal));
        Assert.Contains("◈ vision", row, StringComparison.Ordinal);
        //right-aligned means the row ends with the legend at the full width, one space between the two would pass a sharing-only check
        Assert.EndsWith("◈ vision", row.TrimEnd(), StringComparison.Ordinal);
        Assert.Equal(60, UnicodeWidth.Of(row.TrimEnd()));
    }

    [Fact]
    public void FooterLegend_TAKES_ITS_OWN_ROW_rather_than_DROPPING_when_it_cannot_share()
    {
        //the legend explains the marks on screen, so it keeps its own row when the terminal cannot hold both
        var frame = FrameAt(WithLegend("Esc to leave", "◈ vision · ✓ tool-calling verified"), 40,
            Special(ConsoleKey.Enter));

        Assert.Contains(frame, r => r.Contains("Esc to leave", StringComparison.Ordinal));
        var legend = Assert.Single(frame, r => r.Contains("tool-calling verified", StringComparison.Ordinal));
        Assert.DoesNotContain("Esc to leave", legend, StringComparison.Ordinal);
    }

    [Fact]
    public void FooterLegend_WRAPS_rather_than_TRUNCATING_when_it_is_wider_than_the_terminal()
    {
        //the Fit helper truncates, so check that every word survives somewhere rather than pinning a wrap point
        var frame = FrameAt(WithLegend(null, "◈ vision · ✓ tool-calling verified"), 20,
            Special(ConsoleKey.Enter));

        var joined = string.Join(" ", frame);
        foreach (var word in new[] { "vision", "tool-calling", "verified" })
            Assert.Contains(word, joined, StringComparison.Ordinal);
    }

    [Fact]
    public void FooterLegend_WITHOUT_A_HINT_STILL_RENDERS()
    {
        //the width ladder drops the hint first, so the legend must render without one
        var frame = FrameAt(WithLegend(null, "◈ vision"), 60, Special(ConsoleKey.Enter));

        Assert.Contains(frame, r => r.Contains("◈ vision", StringComparison.Ordinal));
    }

    [Fact]
    public void FooterLegend_IS_ABSENT_BY_DEFAULT_and_every_other_footer_is_byte_identical()
    {
        //a caller that passes no legend must render the same footer bytes as before, so the spec leaves the parameter out
        var spec = new SelectSpec(
            TitleRows: [], Question: null,
            Options: [new SelectOption("alpha")], FooterHint: "Esc to leave");
        var frame = FrameAt(spec, 60, Special(ConsoleKey.Enter));

        var row = Assert.Single(frame, r => r.Contains("Esc to leave", StringComparison.Ordinal));
        Assert.Equal("Esc to leave", row.Trim());
    }

    //turning numbering off removes four things together, the digits, the column they reserved, the digit key and the overflow hint

    private static SelectSpec Numbered(bool numbered, int count = 3) => new(
        TitleRows: [],
        Question: null,
        Options: [.. Enumerable.Range(0, count).Select(i => new SelectOption($"row{i}"))],
        FooterHint: null,
        Numbered: numbered);

    [Fact]
    public void Unnumbered_NO_ROW_CARRIES_A_DIGIT()
    {
        var frame = FrameAt(Numbered(false), 60, Special(ConsoleKey.Enter));

        //the frame must hold the three rows, or the digit check passes on a frame that painted nothing
        Assert.Equal(3, frame.Count(r => r.Contains("row", StringComparison.Ordinal)));
        foreach (var row in frame)
            Assert.False(Regex.IsMatch(row, @"\d\.\s*row"), row);
    }

    [Fact]
    public void Numbered_IS_ON_BY_DEFAULT_AND_EVERY_OTHER_SCREEN_KEEPS_ITS_DIGITS()
    {
        //numbering is on by default, so the spec leaves the parameter out and existing screens keep their digits
        var spec = new SelectSpec(
            TitleRows: [],
            Question: null,
            Options: [new SelectOption("row0"), new SelectOption("row1"), new SelectOption("row2")],
            FooterHint: null);
        var frame = FrameAt(spec, 60, Special(ConsoleKey.Enter));

        Assert.Contains(frame, r => r.Contains("1. row0", StringComparison.Ordinal));
        Assert.Contains(frame, r => r.Contains("2. row1", StringComparison.Ordinal));
    }

    [Fact]
    public void Unnumbered_MULTI_SELECTS_NEXT_ROW_TRACKS_THE_LABEL_COLUMN_TOO()
    {
        //the Next row pads itself to the option column, so it has its own numbering branch
        var spec = new SelectSpec(
            TitleRows: [],
            Question: null,
            Options: [new SelectOption("row0"), new SelectOption("row1")],
            MultiSelect: true,
            FooterHint: null,
            Numbered: false);

        var frame = FrameAt(spec, 60,
            Special(ConsoleKey.DownArrow), Special(ConsoleKey.DownArrow), Special(ConsoleKey.Enter));

        Assert.Equal(
            SelectPrompt.LabelColumn(anyMarked: false, multiSelect: true, numbered: false),
            ColumnOf(frame, "Next"));
    }

    [Theory]
    [InlineData(40)]
    [InlineData(60)]
    [InlineData(100)]
    public void Unnumbered_FREES_THE_NUMBER_COLUMN_AND_LabelColumn_AGREES_WITH_THE_RENDER(int width)
    {
        //an unnumbered screen must free the number column too, or a caller laying a table out against LabelColumn measures three cells narrow
        var on = FrameAt(Numbered(true), width, Special(ConsoleKey.Enter));
        var off = FrameAt(Numbered(false), width, Special(ConsoleKey.Enter));

        var onCol = ColumnOf(on, "row0");
        var offCol = ColumnOf(off, "row0");

        Assert.Equal(3, onCol - offCol);
        Assert.Equal(onCol, SelectPrompt.LabelColumn(anyMarked: false, multiSelect: false, numbered: true));
        Assert.Equal(offCol, SelectPrompt.LabelColumn(anyMarked: false, multiSelect: false, numbered: false));
    }

    private static int ColumnOf(IEnumerable<string> frame, string label)
    {
        var row = Assert.Single(frame, r => r.Contains(label, StringComparison.Ordinal));
        return row.IndexOf(label, StringComparison.Ordinal);
    }

    [Fact]
    public void Unnumbered_A_DIGIT_SELECTS_NOTHING()
    {
        //a digit selects and confirms, so an unnumbered screen must ignore it or a stray press commits an unaimed choice
        var (_, outcome) = Run(Numbered(false), Digit('2'), Special(ConsoleKey.Enter));

        Assert.Equal(0, Chosen(outcome));
    }

    [Fact]
    public void Unnumbered_OVERFLOW_HINT_DOES_NOT_RENDER()
    {
        //the overflow hint describes the boundary at nine, so an unnumbered screen shows no hint
        var frame = FrameAt(Numbered(false, count: 12), 60, Special(ConsoleKey.Enter));

        Assert.DoesNotContain(frame, r => r.Contains("the rest have no number", StringComparison.Ordinal));
    }

    [Fact]
    public void Numbered_OVERFLOW_HINT_STILL_RENDERS_WHERE_IT_IS_TRUE()
    {
        //a numbered screen still shows the overflow hint, so the removal is scoped to unnumbered mode
        var frame = FrameAt(Numbered(true, count: 12), 60, Special(ConsoleKey.Enter));

        Assert.Contains(frame, r => r.Contains("the rest have no number", StringComparison.Ordinal));
    }

    //three distinct answers, so a test cannot pass on the wrong row by accident
    private static SelectSpec NoDefault() => new(
        TitleRows: [],
        Question: new PromptQuestion("Should gatto replace the installed binary?"),
        Options: [new SelectOption("Yes — update"), new SelectOption("Not now"), new SelectOption("Third")],
        NoInitialCursor: true);

    //a highlighted default on a network question is a nudge, so the first frame highlights no row
    [Fact]
    public void NO_INITIAL_CURSOR_HIGHLIGHTS_NOTHING_on_the_first_frame()
    {
        var (surface, outcome) = Run(NoDefault(), Special(ConsoleKey.Escape));

        Assert.DoesNotContain(RowsOf(surface.Text), r => r.Contains('❯'));
        Assert.IsType<SelectOutcome.Cancelled>(outcome);
    }

    //enter before a choice does nothing, two downs then reach row 1 only if the earlier enter was inert
    [Fact]
    public void ENTER_BEFORE_A_CHOICE_LEAVES_THE_PROMPT_UNCHANGED()
    {
        var (_, outcome) = Run(NoDefault(),
            Special(ConsoleKey.Enter),
            Special(ConsoleKey.DownArrow), Special(ConsoleKey.DownArrow),
            Special(ConsoleKey.Enter));

        Assert.Equal(1, Chosen(outcome));
    }

    //enter before a choice stays inert even with a free-text row, otherwise it resolves as Chosen(-1)
    [Fact]
    public void ENTER_BEFORE_A_CHOICE_IS_INERT_EVEN_WHEN_A_FREE_TEXT_ROW_EXISTS()
    {
        var spec = NoDefault() with { FreeTextLabel = "Something else" };

        var (_, outcome) = Run(spec,
            Special(ConsoleKey.Enter),
            Special(ConsoleKey.DownArrow),
            Special(ConsoleKey.Enter));

        Assert.Equal(0, Chosen(outcome));
    }

    [Fact]
    public void DOWN_FROM_NO_CURSOR_LANDS_ON_THE_FIRST_ROW()
    {
        var (_, outcome) = Run(NoDefault(), Special(ConsoleKey.DownArrow), Special(ConsoleKey.Enter));
        Assert.Equal(0, Chosen(outcome));
    }

    //up from no cursor lands on the last row, scanning upward from -2 fails its bounds check and does nothing
    [Fact]
    public void UP_FROM_NO_CURSOR_LANDS_ON_THE_LAST_ROW()
    {
        var (_, outcome) = Run(NoDefault(), Special(ConsoleKey.UpArrow), Special(ConsoleKey.Enter));
        Assert.Equal(2, Chosen(outcome));
    }

    //space on a no-cursor multi-select would index position -1, so the guard must keep it inert
    [Fact]
    public void SPACE_WITH_NO_CURSOR_IS_INERT_rather_than_indexing_minus_one()
    {
        var spec = NoDefault() with { MultiSelect = true };

        var (_, outcome) = Run(spec, Special(ConsoleKey.Spacebar), Special(ConsoleKey.Escape));

        Assert.IsType<SelectOutcome.Cancelled>(outcome);
    }

    //the flag lives in a widget every wizard and permission prompt uses, so its default must stay untouched
    [Fact]
    public void THE_DEFAULT_PROMPT_IS_UNCHANGED_cursor_on_row_zero_and_enter_confirms()
    {
        var spec = NoDefault() with { NoInitialCursor = false };

        var (surface, outcome) = Run(spec, Special(ConsoleKey.Enter));

        Assert.Equal(0, Chosen(outcome));
        Assert.Contains(RowsOf(surface.Text), r => r.Contains('❯'));
    }
}
