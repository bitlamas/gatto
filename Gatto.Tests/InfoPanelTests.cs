using Gatto.Repl;
using Gatto.Repl.Input;
using Gatto.Repl.Render;
using Gatto.Repl.Term;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests;

//the queue bypass and the info panel a bypassing command answers in mid-turn
public class InfoPanelTests
{
    private static readonly Theme T = new(new TermCaps(true, true));

    [Theory]
    [InlineData("/context", true)]
    [InlineData("/help", true)]
    [InlineData("/tools", true)]
    [InlineData("/permissions", true)]
    [InlineData("/permissions all", true)]
    [InlineData("/permissions revoke 1", false)]
    [InlineData("/model", false)]
    [InlineData("/effort high", false)]
    [InlineData("/compact", false)]
    [InlineData("/new", false)]
    [InlineData("/wild", false)]
    [InlineData("/remember a fact", false)]
    [InlineData("what is in the context", false)]
    public void Only_a_command_that_reads_and_changes_nothing_bypasses_the_queue(string input, bool bypasses) =>
        Assert.Equal(bypasses, SlashCommands.BypassesQueue(input));

    [Fact]
    public void An_open_info_panel_takes_the_first_Esc_above_every_other_rung()
    {
        Assert.Equal(Gatto.Repl.Repl.CancelAction.CloseInfo,
            Gatto.Repl.Repl.DecideCancel(ConsoleKey.Escape, hasSelection: true, turnInFlight: true, composerEmpty: true, infoUp: true));
        //with the panel closed the ladder is as before, the next Esc stops the turn
        Assert.Equal(Gatto.Repl.Repl.CancelAction.AbortTurn,
            Gatto.Repl.Repl.DecideCancel(ConsoleKey.Escape, hasSelection: false, turnInFlight: true, composerEmpty: true, infoUp: false));
        //the copy chord is not the panel's
        Assert.Equal(Gatto.Repl.Repl.CancelAction.AbortTurn,
            Gatto.Repl.Repl.DecideCancel(ConsoleKey.C, hasSelection: false, turnInFlight: true, composerEmpty: true, infoUp: true));
    }

    private static ChromePainter Painter()
    {
        var surface = new RecordingSurface { Width = 80, Height = 30 };
        var status = new StatusInfo(@"C:\proj", "qwen", "coder", new CtxState(), @"C:\Users\x");
        return new ChromePainter(surface, T, new object())
        {
            Frame = new InputFrame(surface, T, "coder", status, glyphs: GlyphSet.Unicode),
            RoleForTint = "coder",
        };
    }

    private static ConsoleKeyInfo K(char c, ConsoleKey key = ConsoleKey.NoName, bool ctrl = false) => new(c, key, false, false, ctrl);

    [Fact]
    public void Enter_closes_the_panel_and_is_consumed_so_nothing_is_submitted()
    {
        var p = Painter();
        var sink = Gatto.Repl.Repl.BuildInfoPanelSink(p, new object());
        p.SetInfoPanel((_, _) => ["  context"]);

        Assert.Equal(KeyDisposition.Consumed, sink(K('\r', ConsoleKey.Enter)));
        Assert.False(p.State.PanelUp);
    }

    [Fact]
    public void A_typed_character_closes_the_panel_and_goes_on_to_the_composer()
    {
        var p = Painter();
        var sink = Gatto.Repl.Repl.BuildInfoPanelSink(p, new object());
        p.SetInfoPanel((_, _) => ["  context"]);

        Assert.Equal(KeyDisposition.PassThrough, sink(K('a', ConsoleKey.A)));
        Assert.False(p.State.PanelUp);
    }

    [Fact]
    public void Page_keys_scroll_the_panel_and_Ctrl_C_is_left_to_the_cancel_ladder()
    {
        var p = Painter();
        var sink = Gatto.Repl.Repl.BuildInfoPanelSink(p, new object());
        p.SetInfoPanel((_, _) => [.. Enumerable.Range(0, 60).Select(i => $"  row {i}")]);

        Assert.Equal(KeyDisposition.Consumed, sink(K('\0', ConsoleKey.PageDown)));
        Assert.True(p.State.PanelInfo);
        Assert.Equal(KeyDisposition.PassThrough, sink(K('\u0003', ConsoleKey.C, ctrl: true)));
        Assert.True(p.State.PanelInfo);
    }

    [Fact]
    public void With_no_info_panel_every_key_passes_untouched()
    {
        var p = Painter();
        p.SetPanel(["a prompt row"]);
        var sink = Gatto.Repl.Repl.BuildInfoPanelSink(p, new object());

        Assert.Equal(KeyDisposition.PassThrough, sink(K('\r', ConsoleKey.Enter)));
        Assert.True(p.State.PanelUp);
    }

    //the counts are awaited, so the answer can arrive while a prompt waits: it is dropped, and Esc stays the prompt's
    [Fact]
    public void An_answer_that_arrives_over_a_prompt_is_dropped()
    {
        var p = Painter();
        p.SetPanel(["Allow write_file?"]);

        p.SetInfoPanel((_, _) => ["  context"]);

        Assert.Equal(["Allow write_file?"], p.State.PanelRows);
        Assert.False(p.State.PanelInfo);
        Assert.False(p.CloseInfoPanel());
        Assert.Equal(["Allow write_file?"], p.State.PanelRows);
        Assert.NotEqual(Gatto.Repl.Repl.CancelAction.CloseInfo,
            Gatto.Repl.Repl.DecideCancel(ConsoleKey.Escape, false, true, true, infoUp: p.State.PanelInfo));
    }

    //a permission or ask_user prompt sets its own panel, which replaces the answer, and closing the prompt brings nothing back
    [Fact]
    public void A_prompt_replaces_an_open_info_panel()
    {
        var p = Painter();
        p.SetInfoPanel((_, _) => ["  context"]);

        p.SetPanelFactory((_, _) => (IReadOnlyList<string>)["Allow write_file?"]);
        Assert.False(p.State.PanelInfo);
        Assert.Equal(["Allow write_file?"], p.State.PanelRows);

        p.SetPanel(null);
        Assert.False(p.State.PanelUp);
    }

    //the answer opens at its top, so a panel taller than the room shows its header first
    [Fact]
    public void A_tall_info_panel_opens_at_its_top()
    {
        var p = Painter();
        p.SetInfoPanel((_, _) => [.. Enumerable.Range(0, 60).Select(i => $"  row {i}")]);

        var rows = p.ComposeChromeBlock(80, 30).Rows.Where(r => r.Region == ChromeRegion.Prompt).ToList();
        Assert.Equal("  row 0", rows[0].Visible.TrimEnd());
    }
}
