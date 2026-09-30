using System.Text.RegularExpressions;
using Gatto.Repl;
using Gatto.Repl.Input;
using Gatto.Repl.Render;
using Gatto.Repl.Term;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests;

//the picker is a thin binding onto SelectPrompt, so every test here asserts through the live render path
public class ListPickerTests
{
    private static readonly PickerItem[] Items =
    [
        new("gemma-4-26b",  "gemma-4-26b   ● 128k", Marked: true,  Current: false),
        new("qwen3.6-35b",  "qwen3.6-35b     256k", Marked: false, Current: true),
        new("ornith-35b",   "ornith-35b      256k", Marked: false, Current: false),
    ];

    //keys are held as ConsoleKeyInfo, since a digit is only a digit by its KeyChar. an empty queue throws, so a picker wanting an extra key fails visibly
    private sealed class ScriptedKeys(IEnumerable<ConsoleKeyInfo> keys) : IKeySource
    {
        private readonly Queue<ConsoleKeyInfo> _q = new(keys);
        public bool KeyAvailable => false;
        public ConsoleKeyInfo ReadKey() => _q.Count > 0
            ? _q.Dequeue()
            : throw new InvalidOperationException("test scripted too few keys");
    }

    //runs the callback with a read index before each key, the only window a test has to change the world during a synchronous Pick
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

    private static ConsoleKeyInfo Special(ConsoleKey k) => new('\0', k, false, false, false);
    private static ConsoleKeyInfo Digit(char d) => new(d, (ConsoleKey)((int)ConsoleKey.D0 + (d - '0')), false, false, false);
    private static ConsoleKeyInfo Letter(char c) => new(c, (ConsoleKey)((int)ConsoleKey.A + (char.ToUpperInvariant(c) - 'A')), false, false, false);

    private static IKeySource Script(params ConsoleKey[] keys) => new ScriptedKeys(keys.Select(Special));

    //matches the cursor-up escape the inline render path writes
    private const string CursorUp = "\\u001b\\[(\\d+)A";

    private static readonly Theme T = new(new TermCaps(true, true));

    private const string Title = "select a model";

    private static RichListPicker RichPickerOver(params ConsoleKey[] keys) =>
        new(new RecordingSurface(), T, Script(keys));

    private static (RecordingSurface Surface, RichListPicker Picker) RichPickerWithSurface(params ConsoleKey[] keys)
    {
        var surface = new RecordingSurface();
        return (surface, new RichListPicker(surface, T, Script(keys)));
    }

    //a row opens with \r and erase-to-EOL, so the shared strip rule removes that opener as chrome
    private static string[] RowsOf(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(Gatto.Tests.Fakes.TerminalReplay.StripRowOpener).ToArray();

    //everything after the last cursor-up, with ANSI stripped. the block repaints on every keystroke, so order claims hold for one frame
    private static string[] LastFrame(string text)
    {
        var ups = Regex.Matches(text, CursorUp);
        var from = ups.Count == 0 ? 0 : ups[^1].Index + ups[^1].Length;
        return RowsOf(text[from..].TrimStart('\r')).Select(TermText.StripAnsiForWidth).ToArray();
    }

    //these rows mirror the golden frame s10-picker-100.txt, one loaded and default, one neither, one the add row
    private static readonly PickerItem[] Drawn =
    [
        new("qwen-qwen3.6-35b-a3b", "qwen-qwen3.6-35b-a3b", Marked: true, Current: true, Default: true),
        new("gemma-4-e4b-it", "gemma-4-e4b-it", Marked: false, Current: false),
        new(" new", "+ add a model…", Marked: false, Current: false),
    ];

    private const string DrawnLegend = "weights loaded   d set as default";

    //slice the picker rows by content, the frame also holds the transcript above and the status line below, which belong to the repl
    private static string[] DrawnBlock()
    {
        var lines = File.ReadAllLines(Path.Combine(Gatto.Tests.Setup.Tui.Golden.Dir, "s10-picker-100.txt"));
        var from = Array.FindIndex(lines, l => l.Contains("select a model", StringComparison.Ordinal));
        var to = Array.FindIndex(lines, from, l => l.StartsWith("─", StringComparison.Ordinal));
        Assert.True(from >= 0 && to > from, "the drawn frame no longer has a title row above a rule");
        return lines[from..to];
    }

    //assert whole rows against the drawn frame, so a different mark form or a stray parenthesis fails here
    [Fact]
    public void Rich_TheRowFormAndTheLegendMatchTheDrawnFrame()
    {
        var (surface, picker) = RichPickerWithSurface(ConsoleKey.Enter);

        picker.Pick(Title, Drawn, markLegend: DrawnLegend);

        Assert.Equal(DrawnBlock(), LastFrame(surface.Text));
    }

    //the two marks are separate facts, and the frame only ever shows them together, so each is asserted alone
    [Theory]
    [InlineData(true, false, "  ●")]
    [InlineData(false, true, "  default")]
    [InlineData(true, true, "  ●  default")]
    [InlineData(false, false, "")]
    public void Rich_EachMarkRidesItsOwnFact(bool marked, bool isDefault, string tail)
    {
        var (surface, picker) = RichPickerWithSurface(ConsoleKey.Enter);

        picker.Pick(Title, [new PickerItem("m", "a-model", marked, Current: true, Default: isDefault)]);

        var row = LastFrame(surface.Text).Single(r => r.Contains("a-model", StringComparison.Ordinal));
        Assert.Equal("❯ 1. a-model" + tail, row);
    }

    //rows truncate from the right, where the marks sit, so a long name must give way or the row loses them
    [Fact]
    public void Rich_ALongNameGivesWayAndTheMarksSurvive()
    {
        var surface = new RecordingSurface { Width = 30 };
        var picker = new RichListPicker(surface, T, Script(ConsoleKey.Enter));

        picker.Pick(Title, [new PickerItem("m", new string('x', 60), Marked: true, Current: true, Default: true)]);

        var row = LastFrame(surface.Text).Single(r => r.Contains('x'));
        Assert.EndsWith("  ●  default", row, StringComparison.Ordinal);
        Assert.True(Gatto.Terminal.UnicodeWidth.Of(row) <= 30, $"row is {row.Length} cells: {row}");
    }

    [Fact]
    public void Rich_EnterOnStart_SelectsTheCurrentItem()
    {
        //the cursor starts on the Current item, so a bare enter returns it
        var picker = RichPickerOver(ConsoleKey.Enter);
        Assert.Equal(1, picker.Pick(Title, Items).PickedRow);
    }

    [Fact]
    public void Rich_DownThenEnter_SelectsTheNext()
    {
        var picker = RichPickerOver(ConsoleKey.DownArrow, ConsoleKey.Enter);
        Assert.Equal(2, picker.Pick(Title, Items).PickedRow);
    }

    [Fact]
    public void Rich_UpFromFirst_ClampsAndDoesNotWrap()
    {
        //the second up press proves the clamp: at row 0 it must not wrap around.
        var picker = RichPickerOver(ConsoleKey.UpArrow, ConsoleKey.UpArrow, ConsoleKey.Enter);
        Assert.Equal(0, picker.Pick(Title, Items).PickedRow);
    }

    [Fact]
    public void Rich_DownFromLast_ClampsAndDoesNotWrap()
    {
        //the second down press proves the clamp: at the last row it must not wrap around.
        var picker = RichPickerOver(ConsoleKey.DownArrow, ConsoleKey.DownArrow, ConsoleKey.Enter);
        Assert.Equal(2, picker.Pick(Title, Items).PickedRow);
    }

    [Fact]
    public void Rich_Escape_Cancels()
    {
        var picker = RichPickerOver(ConsoleKey.Escape);
        Assert.IsType<PickOutcome.Cancelled>(picker.Pick(Title, Items));
    }

    [Fact]
    public void Rich_NoCurrentItem_StartsAtZero()
    {
        var none = Items.Select(i => i with { Current = false }).ToList();
        var picker = RichPickerOver(ConsoleKey.Enter);
        Assert.Equal(0, picker.Pick(Title, none).PickedRow);
    }

    [Fact]
    public void Rich_MultipleCurrentItems_StartsOnTheFirst()
    {
        //the first of two current items must win, and real data never holds two, so this test builds the pair
        var two = new[]
        {
            Items[0] with { Current = true },
            Items[1],                            //already Current in the Items fixture, so this row completes the two-current case
            Items[2],
        };
        var picker = RichPickerOver(ConsoleKey.Enter);
        Assert.Equal(0, picker.Pick(Title, two).PickedRow);
    }

    [Fact]
    public void Rich_UnrecognizedKey_IsIgnored_PromptContinues()
    {
        var picker = new RichListPicker(new RecordingSurface(), T,
            new ScriptedKeys([Letter('a'), Special(ConsoleKey.Enter)]));
        Assert.Equal(1, picker.Pick(Title, Items).PickedRow);
    }

    [Fact]
    public void Rich_EmptyList_ReturnsNull_WithoutReadingAKey()
    {
        //an empty list must cancel, and the surface must stay empty, since a throwing key source only proves no key was read
        var surface = new RecordingSurface();
        var picker = new RichListPicker(surface, T, new ThrowingKeys());
        Assert.IsType<PickOutcome.Cancelled>(picker.Pick(Title, Array.Empty<PickerItem>()));
        Assert.Equal("", surface.Text);   //nothing must render before the cancel
    }

    [Fact]
    public void Rich_CursorRow_UsesTheComposerGlyph_NotTheReasoningChevron()
    {
        var (surface, picker) = RichPickerWithSurface(ConsoleKey.Enter);
        Assert.Equal(1, picker.Pick(Title, Items).PickedRow);

        //the cursor uses the composer's prompt glyph ❯, and a picker never shows the reasoning chevron ▸
        var frame = LastFrame(surface.Text);
        Assert.Contains(frame, r => r.StartsWith("❯ ", StringComparison.Ordinal));
        Assert.DoesNotContain("▸", surface.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Rich_RowsAreNumbered_InOrder()
    {
        var (surface, picker) = RichPickerWithSurface(ConsoleKey.Enter);
        picker.Pick(Title, Items);

        var frame = LastFrame(surface.Text);
        Assert.Contains(frame, r => r.Contains("1. ", StringComparison.Ordinal) && r.Contains("gemma-4-26b", StringComparison.Ordinal));
        Assert.Contains(frame, r => r.Contains("2. ", StringComparison.Ordinal) && r.Contains("qwen3.6-35b", StringComparison.Ordinal));
        Assert.Contains(frame, r => r.Contains("3. ", StringComparison.Ordinal) && r.Contains("ornith-35b", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData('1', 0)]
    [InlineData('3', 2)]
    public void Rich_DigitConfirms_TheNumberedRow(char digit, int expected)
    {
        //the cursor opens on row 1, so each digit answer differs from the bare-enter result and can't pass by accident
        var picker = new RichListPicker(new RecordingSurface(), T, new ScriptedKeys([Digit(digit)]));
        Assert.Equal(expected, picker.Pick(Title, Items).PickedRow);
    }

    [Fact]
    public void Rich_TitleIsTheFirstRow_PaintedAsATitle()
    {
        var (surface, picker) = RichPickerWithSurface(ConsoleKey.Enter);
        picker.Pick(Title, Items);

        Assert.Equal(Title, LastFrame(surface.Text)[0].Trim());
        //the widget paints the title row bright and bold, so a caller can't dim it
        Assert.Contains(T.Paint(Title, Theme.Bright, bold: true), surface.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Rich_NoFooterRow()
    {
        //the spec defaults the footer hint to "Esc to cancel", so this check catches the picker leaving it alone
        var (surface, picker) = RichPickerWithSurface(ConsoleKey.Enter);
        picker.Pick(Title, Items);

        Assert.DoesNotContain("Esc to cancel", TermText.StripAnsiForWidth(surface.Text), StringComparison.Ordinal);
    }

    [Fact]
    public void Rich_MarkedNotCurrent_DotIsThemePainted()
    {
        var (surface, picker) = RichPickerWithSurface(ConsoleKey.Enter);
        picker.Pick(Title, Items);

        //the dot must be painted with the theme accent, or it stops reading as a status indicator
        Assert.Contains(T.Paint("●", Theme.Accent), surface.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Rich_NoPainter_RepaintsInPlaceAfterEachMove()
    {
        var (surface, picker) = RichPickerWithSurface(ConsoleKey.DownArrow, ConsoleKey.Enter);
        Assert.Equal(2, picker.Pick(Title, Items).PickedRow);

        //the cursor-up escape only appears when a repaint really fired, otherwise the down move shifts an invisible cursor
        Assert.Contains(Ansi.Up(5), surface.Text, StringComparison.Ordinal);

        //after the repaint the last ❯ must sit on the newly selected row
        var lastCaret = surface.Text.LastIndexOf('❯');
        Assert.True(lastCaret >= 0, "no cursor was ever rendered");
        Assert.Contains("ornith-35b", surface.Text[lastCaret..], StringComparison.Ordinal);
    }

    [Fact]
    public void Rich_NoPainter_LongLabels_TruncateToSurfaceWidthToPreventWrapping()
    {
        //40 cells, narrow enough to force truncation, so don't widen the surface
        var surface = new RecordingSurface { Width = 40 };

        //these labels exceed 40 cells and would wrap without truncation.
        var items = new[]
        {
            new PickerItem("id1", "gemma-4-26B-A4B-it-Q6_K.gguf-extra-long-suffix", Marked: false, Current: true),
            new PickerItem("id2", "Llama-3.3-70B-Instruct-FP8-dynamic-quant-gguf.gguf", Marked: false, Current: false),
        };

        var picker = new RichListPicker(surface, T, Script(ConsoleKey.DownArrow, ConsoleKey.Enter));

        Assert.Equal(1, picker.Pick(Title, items).PickedRow);

        //every row must fit the surface width, since a wrapped row breaks the row count the next cursor-up depends on
        foreach (var line in RowsOf(surface.Text))
        {
            var visibleWidth = UnicodeWidth.Of(TermText.StripAnsiForWidth(line));
            Assert.True(visibleWidth <= surface.Width,
                $"Line exceeds surface width: width={visibleWidth}, max={surface.Width}");
        }
    }

    [Fact]
    public void Rich_NoPainter_ShrinkMidPicker_RowsFitTheNewWidth()
    {
        //width must be re-read on every render, since a cached one leaves soft-wrapped rows that desynchronise the next cursor-up
        var surface = new RecordingSurface { Width = 80 };
        var keys = new ProbingKeys(
            n => { if (n == 0) surface.Width = 24; },
            [Special(ConsoleKey.DownArrow), Special(ConsoleKey.Enter)]);
        var picker = new RichListPicker(surface, T, keys);

        Assert.Equal(2, picker.Pick(Title, Items).PickedRow);

        var ups = Regex.Matches(surface.Text, CursorUp);
        Assert.True(ups.Count > 0, "no in-place repaint fired");
        var afterResize = surface.Text[(ups[^1].Index + ups[^1].Length)..].TrimStart('\r');
        foreach (var row in RowsOf(afterResize))
        {
            var width = UnicodeWidth.Of(TermText.StripAnsiForWidth(row));
            Assert.True(width <= 24, $"row overflows the resized surface: width={width}, max=24 — '{row}'");
        }
    }

    //the default and the cursor sit on different rows, otherwise a tail on the default looks like one on the cursor
    private static readonly PickerItem[] WithDefault =
    [
        new("gemma-4-26b",  "gemma-4-26b   ● 128k", Marked: true,  Current: false, Default: true),
        new("qwen3.6-35b",  "qwen3.6-35b     256k", Marked: false, Current: true),
        new("ornith-35b",   "ornith-35b      256k", Marked: false, Current: false),
    ];

    //the tail shares the width budget, so a shrink can cut the word default. the test fails if the tail is dropped to make room
    [Fact]
    public void Rich_NoPainter_ShrinkMidPicker_KeepsTheDefaultTail()
    {
        var surface = new RecordingSurface { Width = 80 };
        var keys = new ProbingKeys(
            n => { if (n == 0) surface.Width = 24; },
            [Special(ConsoleKey.DownArrow), Special(ConsoleKey.Enter)]);
        var picker = new RichListPicker(surface, T, keys);

        Assert.Equal(2, picker.Pick(Title, WithDefault).PickedRow);

        var ups = Regex.Matches(surface.Text, CursorUp);
        Assert.True(ups.Count > 0, "no in-place repaint fired");
        var afterResize = surface.Text[(ups[^1].Index + ups[^1].Length)..].TrimStart('\r');
        var rows = RowsOf(afterResize).Select(TermText.StripAnsiForWidth).ToArray();

        Assert.All(rows, r => Assert.True(UnicodeWidth.Of(r) <= 24,
            $"row overflows the resized surface: width={UnicodeWidth.Of(r)}, max=24 — '{r}'"));
        Assert.Contains(rows, r => r.Contains("default", StringComparison.Ordinal));
    }

    [Fact]
    public void Plain_PrintsEveryItemAndReturnsNull()
    {
        var output = new StringWriter();
        Assert.IsType<PickOutcome.Cancelled>(new PlainListPicker(output, glyphs: GlyphSet.Unicode).Pick(Title, Items));
        var text = output.ToString();
        Assert.Contains("gemma-4-26b", text, StringComparison.Ordinal);
        Assert.Contains("qwen3.6-35b", text, StringComparison.Ordinal);
        Assert.Contains("ornith-35b", text, StringComparison.Ordinal);
    }

    //the plain dump has no cursor, so it marks the session's model with ❯ itself, and only that row
    [Fact]
    public void Plain_MarksTheSessionsModelWithTheCursorGlyph()
    {
        var output = new StringWriter();

        new PlainListPicker(output, glyphs: GlyphSet.Unicode).Pick(Title, Items);

        var rows = output.ToString().Split('\n').Where(r => r.Contains("35b", StringComparison.Ordinal)
                                                            || r.Contains("26b", StringComparison.Ordinal)).ToArray();
        var marked = rows.Where(r => r.TrimStart().StartsWith('❯')).ToArray();
        var row = Assert.Single(marked);
        Assert.Contains("qwen3.6-35b", row, StringComparison.Ordinal);   //the fixture marks this row current, so the glyph must be here
    }

    //with nothing current no glyph may be drawn, or a picker that always marked row two would pass the test above
    [Fact]
    public void Plain_MarksNothingWhenNoModelIsArmed()
    {
        var output = new StringWriter();
        PickerItem[] none = [.. Items.Select(i => i with { Current = false })];

        new PlainListPicker(output, glyphs: GlyphSet.Unicode).Pick(Title, none);

        Assert.DoesNotContain("❯", output.ToString(), StringComparison.Ordinal);
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

    //snapshots the panel rows just before each key, the only window into the live chrome state while Pick runs
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

    private sealed class ThrowingKeys : IKeySource
    {
        public bool KeyAvailable => false;
        public ConsoleKeyInfo ReadKey() => throw new InvalidOperationException("pump dead");
    }

    [Fact]
    public void Armed_RowsLiveInPanelRows_AndUpdateOnEachMove()
    {
        var (_, painter, handle) = Armed();
        var keys = new SnapshotKeys(painter, [Special(ConsoleKey.DownArrow), Special(ConsoleKey.Enter)]);
        var picker = new RichListPicker(new RecordingSurface(), T, keys, pump: null, chrome: handle);

        Assert.Equal(2, picker.Pick(Title, Items).PickedRow);

        Assert.Equal(2, keys.Snapshots.Count);
        var beforeDown = keys.Snapshots[0];
        var afterDown = keys.Snapshots[1];
        Assert.NotNull(beforeDown);
        Assert.NotNull(afterDown);
        //the cursor must move in the painted rows, from qwen to ornith, since the returned row alone doesn't prove the render followed
        Assert.Contains(beforeDown!, r => r.Contains("qwen3.6-35b", StringComparison.Ordinal) && r.Contains('❯'));
        Assert.Contains(afterDown!, r => r.Contains("ornith-35b", StringComparison.Ordinal) && r.Contains('❯'));
        Assert.DoesNotContain(afterDown!, r => r.Contains("qwen3.6-35b", StringComparison.Ordinal) && r.Contains('❯'));
        Assert.Null(painter.State.PanelRows);   //an answered pick must clear the panel
    }

    [Fact]
    public void Armed_RowsRenderInsideTheComposerFrame()
    {
        //a filled panel field doesn't prove the rows reached the frame, so compose the block and check the composer text is gone
        var (_, painter, handle) = Armed();
        painter.State.Composer = new EditorView(new List<string> { "half-typed" }, 0, 10);
        IReadOnlyList<ChromeRow>? rows = null;
        var keys = new ProbingKeys(_ => rows = painter.ComposeChromeBlock(80, 24).Rows, [Special(ConsoleKey.Enter)]);
        var picker = new RichListPicker(new RecordingSurface(), T, keys, pump: null, chrome: handle);

        Assert.Equal(1, picker.Pick(Title, Items).PickedRow);

        Assert.NotNull(rows);
        var visible = rows!.Select(r => r.Visible).ToList();
        Assert.Contains(visible, r => r.Contains("gatto · coder", StringComparison.Ordinal));
        Assert.Contains(visible, r => r.Contains("qwen3.6-35b", StringComparison.Ordinal));
        Assert.DoesNotContain(visible, r => r.Contains("half-typed", StringComparison.Ordinal));

        Assert.Contains(painter.ComposeChromeBlock(80, 24).Rows,
            r => r.Visible.Contains("half-typed", StringComparison.Ordinal));
    }

    [Fact]
    public void Armed_AbortedKeyRead_ClearsThePanel()
    {
        var (_, painter, handle) = Armed();
        var picker = new RichListPicker(new RecordingSurface(), T, new ThrowingKeys(), pump: null, chrome: handle);

        Assert.Throws<InvalidOperationException>(() => picker.Pick(Title, Items));

        Assert.Null(painter.State.PanelRows);
        Assert.False(painter.State.PanelUp);   //panel flag down, so the frame draws the editor again
    }

    [Fact]
    public void ArmedButTornDownPainter_FallsBackToInline()
    {
        //after teardown SetPanel does nothing, so a torn-down painter counts as unarmed, or the picker would render nowhere and still take the keys
        var (_, painter, handle) = Armed();
        painter.Teardown();
        var surface = new RecordingSurface();
        var picker = new RichListPicker(surface, T, Script(ConsoleKey.Enter), pump: null, chrome: handle);

        Assert.Equal(1, picker.Pick(Title, Items).PickedRow);

        Assert.Contains(Title, TermText.StripAnsiForWidth(surface.Text), StringComparison.Ordinal);
        Assert.Null(painter.State.PanelRows);
    }

    //these outcomes can't happen through the widget, so the test injects them. the picker must cancel and warn rather than guess a row

    private static readonly SelectOutcome[] UnexpectedOutcomes =
    {
        new SelectOutcome.Moved(1, 0, Array.Empty<int>()),
        new SelectOutcome.FreeText("whatever"),
        new SelectOutcome.Multi(Array.Empty<int>()),
    };

    public static TheoryData<int> UnexpectedOutcomeIndices() => new() { 0, 1, 2 };

    [Theory]
    [MemberData(nameof(UnexpectedOutcomeIndices))]
    public void UnrecognisedOutcome_FailsSafe_AndSaysSoThroughTheWarningSink(int which)
    {
        var warnings = new List<string>();
        var picker = new RichListPicker(new RecordingSurface(), T, new ThrowingKeys(), pump: null, chrome: null,
            warn: new Gatto.Core.WarningSink(warnings.Add))
        {
            ShowForTest = _ => UnexpectedOutcomes[which],
        };

        //an unrecognised outcome must behave like a cancel and can't pick a row the user never chose
        Assert.IsType<PickOutcome.Cancelled>(picker.Pick(Title, Items));
        //a silent cancel is indistinguishable from esc, so the warning must name the outcome type.
        var message = Assert.Single(warnings);
        Assert.Contains(UnexpectedOutcomes[which].GetType().Name, message, StringComparison.Ordinal);
        Assert.Contains("cancelled", message, StringComparison.Ordinal);
    }

    //the ordinary outcomes, each row and esc, none of which may raise a warning
    private static readonly SelectOutcome[] RecognisedOutcomes =
    {
        new SelectOutcome.Chosen(0), new SelectOutcome.Chosen(1), new SelectOutcome.Chosen(2),
        new SelectOutcome.Cancelled(),
    };

    public static TheoryData<int> RecognisedOutcomeIndices() => new() { 0, 1, 2, 3 };

    [Theory]
    [MemberData(nameof(RecognisedOutcomeIndices))]
    public void RecognisedOutcomes_AreTranslated_AndNeverWarn(int which)
    {
        //a spurious warning here would make every pick warn and hide the real one. the cases are a theory, so a failure names the outcome behind it
        var warnings = new List<string>();
        var picker = new RichListPicker(new RecordingSurface(), T, new ThrowingKeys(), pump: null, chrome: null,
            warn: new Gatto.Core.WarningSink(warnings.Add))
        {
            ShowForTest = _ => RecognisedOutcomes[which],
        };

        var expected = RecognisedOutcomes[which] is SelectOutcome.Chosen chosen ? chosen.Index : (int?)null;
        Assert.Equal(expected, picker.Pick(Title, Items).PickedRow);
        Assert.Empty(warnings);
    }

    [Fact]
    public void Spec_IsSingleSelect_WithNoFreeTextAndNoHorizontal()
    {
        //these settings make an unexpected outcome unreachable, so assert them at the seam, or a real answer ends in the fail-safe cancel
        SelectSpec? seen = null;
        var picker = new RichListPicker(new RecordingSurface(), T, new ThrowingKeys())
        {
            ShowForTest = s => { seen = s; return new SelectOutcome.Cancelled(); },
        };
        picker.Pick(Title, Items);

        Assert.NotNull(seen);
        Assert.False(seen!.MultiSelect);
        Assert.Null(seen.FreeTextLabel);
        Assert.False(seen.Horizontal);
        Assert.Null(seen.FooterHint);
        Assert.Null(seen.Question);
        Assert.Null(seen.DetailRows);
        Assert.Equal([Title], seen.TitleRows);
        //the options must keep the item order, and the cursor must open on the Current item
        Assert.Equal(Items.Select(i => i.Label), seen.Options.Select(o => o.Label));
        //the mark must reach the widget through the tail. the Marked flag would reserve a dot column before every label and shift the names on unmarked rows
        Assert.All(seen.Options, o => Assert.False(o.Marked, "no picker row reserves the prefix column"));
        Assert.Equal(Items.Select(i => i.Marked),
            seen.Options.Select(o => o.Tail is { } t && t.Any(r => r.Text == "●")));
        Assert.Equal(1, seen.InitialCursor);
    }

    [Fact]
    public void Spec_NoCurrentItem_OpensTheCursorAtTheTop()
    {
        //pin the clamp to zero at the seam where the spec is built. the widget clamps on its own, so an end-to-end test passes even when this normalization is gone.
        SelectSpec? seen = null;
        var picker = new RichListPicker(new RecordingSurface(), T, new ThrowingKeys())
        {
            ShowForTest = s => { seen = s; return new SelectOutcome.Cancelled(); },
        };
        picker.Pick(Title, Items.Select(i => i with { Current = false }).ToList());

        Assert.NotNull(seen);
        Assert.Equal(0, seen!.InitialCursor);   //the spec that reaches the widget must never hold -1.
    }

    //local copies of the fakes in RichPermissionPrompterTests, which are private to that class
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

    private static int? WaitForPick(Task<PickOutcome> pick, int ms = 30000) =>
        pick.Wait(ms) ? pick.Result.PickedRow : null;

    //unwraps one ComposerInput.Key with a bounded wait, so a stuck pump fails the test rather than hanging it
    private static ConsoleKeyInfo? ReadKeyBounded(IComposerSource source, int ms = 30000)
    {
        var read = Task.Run(source.Read);
        if (!read.Wait(ms)) return null;
        return Assert.IsType<ComposerInput.Key>(read.Result).K;
    }

    [Fact]
    public void Rich_WithPump_ReadsKeysViaFocus_TypeAheadSurvivesInComposer()
    {
        var src = new System.Collections.Concurrent.BlockingCollection<ConsoleKeyInfo>();
        var pump = new InputPump(new KeyInputSource(new FeedKeys(src)));
        pump.Start();
        src.Add(new ConsoleKeyInfo('t', ConsoleKey.T, false, false, false));   //a key typed before the picker exists.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        //the channel's KeyAvailable also reports the source peek, so wait for the source to drain first, or the focus scope steals the key
        while ((src.Count > 0 || !pump.Composer.KeyAvailable) && sw.ElapsedMilliseconds < 30000) Thread.Sleep(5);
        Assert.Empty(src);
        Assert.True(pump.Composer.KeyAvailable);

        var surface = new SyncSurface();
        //the constructor's key source throws when read, so this proves the picker reads through the focus scope.
        var picker = new RichListPicker(surface, T, new NeverKeys(), pump);
        var pick = Task.Run(() => picker.Pick(Title, Items));

        var renderSw = System.Diagnostics.Stopwatch.StartNew();
        while (!surface.Text.Contains(Title, StringComparison.Ordinal) && renderSw.ElapsedMilliseconds < 30000) Thread.Sleep(5);
        Assert.Contains(Title, surface.Text, StringComparison.Ordinal);

        src.Add(new ConsoleKeyInfo('\0', ConsoleKey.DownArrow, false, false, false));
        src.Add(new ConsoleKeyInfo('\0', ConsoleKey.Enter, false, false, false));
        var result = WaitForPick(pick);
        Assert.True(result.HasValue, "picker did not answer via the pump focus");
        Assert.Equal(2, result);

        //the earlier type-ahead must survive the pick and still sit in the composer channel
        Assert.True(pump.Composer.KeyAvailable);
        var leftover = ReadKeyBounded(pump.Composer);
        Assert.True(leftover.HasValue, "composer had no key to read despite KeyAvailable being true");
        Assert.Equal(ConsoleKey.T, leftover!.Value.Key);
        Assert.Equal('t', leftover.Value.KeyChar);
    }
}
