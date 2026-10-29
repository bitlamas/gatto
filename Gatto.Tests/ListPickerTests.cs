using Gatto.Repl.Input;
using Gatto.Repl.Prompts;
using Gatto.Repl.Render;
using Gatto.Repl.Render.Chrome;
using Gatto.Terminal;
using Gatto.Terminal.Tests.Fakes;
using Gatto.Tests.Fakes;

namespace Gatto.Tests;

//the picker binds a panel spec onto SelectPrompt, and the plain picker prints the list a plain terminal cannot navigate
public class ListPickerTests
{
    private static readonly PickerItem[] Items =
    [
        new("gemma-4-26b", "gemma-4-26b   128k", Marked: true, Current: false, Columns: ["128k", "Gemma-Q4"]),
        new("qwen3.6-35b", "qwen3.6-35b   256k", Marked: false, Current: true, Default: true, Columns: ["256k", "Qwen-Q5"]),
        new("ornith-35b", "ornith-35b    256k", Marked: false, Current: false, Columns: ["256k", "Ornith-Q4"]),
    ];

    private static readonly Theme T = new(new TermCaps(true, true));

    private static readonly PanelFields Fields = new("model", [new("Enter", "switch"), new("Esc", "close")], Legend: "loaded");

    private sealed class Keys(params ConsoleKeyInfo[] keys) : IKeySource
    {
        private readonly Queue<ConsoleKeyInfo> _q = new(keys);
        public ConsoleKeyInfo ReadKey() => _q.Count > 0 ? _q.Dequeue() : throw new InvalidOperationException("test scripted too few keys");
        public bool KeyAvailable => _q.Count > 0;
    }

    private static ConsoleKeyInfo K(ConsoleKey k) => new('\0', k, false, false, false);

    //the options keep the item order, the cursor opens on the current item, and the marks reach the widget as runs, not as a reserved column
    [Fact]
    public void The_spec_carries_the_rows_the_cursor_and_the_panels_words()
    {
        SelectSpec? seen = null;
        var picker = new RichListPicker(new RecordingSurface(), T, new Keys())
        {
            ShowPanelForTest = s => { seen = s; return new SelectPrompt.PanelResult(new SelectOutcome.Cancelled(), null, false); },
        };

        picker.ShowPanel(new PanelSpec(Items, Fields, ['d']));

        Assert.Equal(["gemma-4-26b", "qwen3.6-35b", "ornith-35b"], seen!.Options.Select(o => o.Label));
        Assert.Equal(1, seen.InitialCursor);
        Assert.Same(Fields, seen.Panel);
        Assert.Equal(['d'], seen.ControlKeys!);
        Assert.Equal(["128k", "Gemma-Q4"], seen.Options[0].Columns!);
        Assert.Contains(seen.Options[0].Tail!, r => r.Text == GlyphSet.Unicode.Loaded);
        Assert.Contains(seen.Options[1].Trail!, r => r.Text == "default");
        Assert.All(seen.Options, o => Assert.False(o.Marked));
    }

    [Fact]
    public void With_no_current_item_the_cursor_opens_at_the_top_and_openAt_wins_over_both()
    {
        SelectSpec? seen = null;
        var picker = new RichListPicker(new RecordingSurface(), T, new Keys())
        {
            ShowPanelForTest = s => { seen = s; return new SelectPrompt.PanelResult(new SelectOutcome.Cancelled(), null, false); },
        };

        picker.ShowPanel(new PanelSpec([.. Items.Select(i => i with { Current = false })], Fields));
        Assert.Equal(0, seen!.InitialCursor);

        picker.ShowPanel(new PanelSpec(Items, Fields, OpenAt: 2));
        Assert.Equal(2, seen!.InitialCursor);
    }

    //the widget's answers become the picker's, with the setting and whether it moved riding beside them
    [Fact]
    public void The_widgets_outcomes_are_translated()
    {
        var answers = new Queue<SelectPrompt.PanelResult>(
        [
            new(new SelectOutcome.Chosen(2), "high", true),
            new(new SelectOutcome.Control('d', 1), "low", false),
            new(new SelectOutcome.Cancelled(), null, false),
        ]);
        var picker = new RichListPicker(new RecordingSurface(), T, new Keys()) { ShowPanelForTest = _ => answers.Dequeue() };

        Assert.Equal(new PanelOutcome(new PickOutcome.Picked(2), "high", true), picker.ShowPanel(new PanelSpec(Items, Fields)));
        Assert.Equal(new PanelOutcome(new PickOutcome.Control('d', 1), "low", false), picker.ShowPanel(new PanelSpec(Items, Fields)));
        Assert.IsType<PickOutcome.Cancelled>(picker.ShowPanel(new PanelSpec(Items, Fields)).Pick);
    }

    //end to end through the widget with no painter: down then Enter picks the row under the cursor
    [Fact]
    public void Down_then_Enter_picks_the_next_row()
    {
        var picker = new RichListPicker(new RecordingSurface(), T, new Keys(K(ConsoleKey.DownArrow), new('\r', ConsoleKey.Enter, false, false, false)));

        Assert.Equal(2, picker.ShowPanel(new PanelSpec(Items, Fields)).Pick.PickedRow);
    }

    //an answered panel leaves the slot empty, so the frame draws the composer again
    [Fact]
    public void An_armed_panel_clears_its_slot_when_it_answers()
    {
        var surface = new RecordingSurface { Width = 80, Height = 24 };
        var gate = new object();
        var status = new StatusInfo(@"C:\proj", "qwen", "coder", new CtxState(), @"C:\Users\x");
        var painter = new ChromePainter(surface, T, gate)
        {
            Frame = new InputFrame(surface, T, "coder", status, glyphs: GlyphSet.Unicode),
            RoleForTint = "coder",
        };
        var renderer = new StreamRenderer(painter, surface, T, "coder", new ChromeTicker(painter, gate), gate);
        var picker = new RichListPicker(surface, T, new Keys(K(ConsoleKey.Escape)), chrome: new ChromeHandle { Painter = painter, Renderer = renderer });

        Assert.IsType<PickOutcome.Cancelled>(picker.ShowPanel(new PanelSpec(Items, Fields)).Pick);
        Assert.False(painter.State.PanelUp);
        Assert.Null(painter.State.PanelMouse);
    }

    [Fact]
    public void Plain_PrintsTheTitleTheLegendAndEveryItemAndCancels()
    {
        var output = new StringWriter();

        Assert.IsType<PickOutcome.Cancelled>(new PlainListPicker(output, glyphs: GlyphSet.Unicode).ShowPanel(new PanelSpec(Items, Fields)).Pick);

        var text = output.ToString();
        Assert.StartsWith("model   " + GlyphSet.Unicode.Loaded + " loaded", text, StringComparison.Ordinal);
        Assert.Contains("gemma-4-26b", text, StringComparison.Ordinal);
        Assert.Contains("qwen3.6-35b", text, StringComparison.Ordinal);
        Assert.Contains("ornith-35b", text, StringComparison.Ordinal);
    }

    //the plain dump has no cursor, so it marks the session's model with the prompt glyph itself, and only that row
    [Fact]
    public void Plain_MarksTheSessionsModelWithTheCursorGlyph()
    {
        var output = new StringWriter();

        new PlainListPicker(output, glyphs: GlyphSet.Unicode).ShowPanel(new PanelSpec(Items, Fields));

        var rows = output.ToString().Split('\n').Where(r => r.Contains("35b", StringComparison.Ordinal)
                                                            || r.Contains("26b", StringComparison.Ordinal)).ToArray();
        var row = Assert.Single(rows, r => r.TrimStart().StartsWith(GlyphSet.Unicode.Prompt, StringComparison.Ordinal));
        Assert.Contains("qwen3.6-35b", row, StringComparison.Ordinal);
    }

    //with nothing current no glyph may be drawn, or a picker that always marked row two would pass the test above
    [Fact]
    public void Plain_MarksNothingWhenNoModelIsArmed()
    {
        var output = new StringWriter();

        new PlainListPicker(output, glyphs: GlyphSet.Unicode).ShowPanel(new PanelSpec([.. Items.Select(i => i with { Current = false })], Fields));

        Assert.DoesNotContain(GlyphSet.Unicode.Prompt, output.ToString(), StringComparison.Ordinal);
    }
}
