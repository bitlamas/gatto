using Gatto.Repl.Render;
using Gatto.Terminal;
using Xunit;

namespace Gatto.Tests.Render;

public sealed class ShellBlockItemTests
{
    private static readonly Theme T = new(new TermCaps(true, true));
    private const string Command = "$s = @'\necho a\necho b\necho c\n'@; $s | ssh host \"bash -s\"";

    private static ToolBlockItem Block(string result, string gloss = "exit 0") =>
        new("shell", "", "painted", true, "coder") { Collapsed = true, FullResult = result, FullArgs = Command, RawGloss = gloss };

    //render and layout are one call, so the count of wraps against rows is the part that can drift
    [Fact]
    public void A_shell_block_has_one_wrap_for_every_row_it_renders()
    {
        var b = Block(string.Join("\r\n", Enumerable.Range(1, 9).Select(i => $"l{i}")));
        foreach (var collapsed in new[] { true, false })
        {
            b.Collapsed = collapsed;
            foreach (var width in new[] { 40, 80, 120 })
                Assert.Equal(b.Render(width, T, GlyphSet.Unicode).Count, b.RowWraps(width, T, GlyphSet.Unicode).Count);
        }
    }

    [Fact]
    public void Scrolling_a_window_bumps_the_revision_and_keeps_the_height()
    {
        var b = Block(string.Join("\r\n", Enumerable.Range(1, 9).Select(i => $"l{i}")));
        b.Collapsed = false;
        var height = b.Render(80, T, GlyphSet.Unicode).Count;
        var rev = b.Rev;
        b.OutputTop = 2;
        Assert.True(b.Rev > rev);
        Assert.Equal(height, b.Render(80, T, GlyphSet.Unicode).Count);
        rev = b.Rev;
        b.OutputTop = 2;
        Assert.Equal(rev, b.Rev);
    }

    [Fact]
    public void A_block_with_no_command_is_not_a_shell_block()
    {
        var b = new ToolBlockItem("shell", "", "painted", true, "coder") { FullResult = "x" };
        Assert.False(b.IsShell);
    }

    [Fact]
    public void Reset_returns_the_view_and_both_positions()
    {
        var b = Block("x");
        b.View = ShellView.All; b.OutputTop = 3; b.CommandFromTail = 1;
        b.ResetView();
        Assert.Equal((ShellView.Window, 0, 0), (b.View, b.OutputTop, b.CommandFromTail));
    }

    [Fact]
    public void The_lead_cells_travel_from_the_rows_to_the_wraps()
    {
        var b = Block("line one\r\nline two");
        b.Collapsed = false;
        var layout = b.ShellLayout(80, T, GlyphSet.Unicode);
        var wraps = b.RowWraps(80, T, GlyphSet.Unicode);
        Assert.Equal(layout.Rows[layout.OutputFirst].LeadCells, wraps[layout.OutputFirst].LeadCells);
        Assert.True(wraps[layout.OutputFirst].LeadCells > 0);
    }

    //a live call and the record it writes must draw the same rows, or a resumed session shows a different block
    [Fact]
    public void A_live_shell_block_and_its_replay_draw_the_same_rows()
    {
        var call = new Gatto.Core.Client.ToolCall("c1", "shell", System.Text.Json.JsonSerializer.Serialize(new { command = Command }));
        var result = new Gatto.Core.Tools.ToolResult("out 1\r\nout 2\r\n--- stderr ---\r\nbad\r\n(exit code 1)", IsError: true, Gloss: "exit 1");

        var live = new ToolBlockItem("shell", ItemRender.CompactArgs(call.ArgumentsJson), ItemRender.ToolGloss(result, T, GlyphSet.Unicode), true, "coder")
            { Collapsed = true, FullResult = result.Text, FullArgs = ItemRender.FullArgsOf("shell", call.ArgumentsJson), RawGloss = result.Gloss };

        var convo = new Gatto.Core.Loop.Conversation("sys");
        convo.AddUser("go");
        convo.SetGattoRole("coder");
        convo.AddAssistant("", new[] { call });
        convo.AddToolResult("c1", result);
        var (_, rebuilt) = TranscriptStore.Rebuild(TranscriptStore.BuildLines(new TranscriptModel("coder"), convo), T, "coder", glyphs: GlyphSet.Unicode);
        var replay = rebuilt.Items.OfType<ToolBlockItem>().Single();

        foreach (var width in new[] { 80, 120 })
            Assert.Equal(live.Render(width, T, GlyphSet.Unicode).Skip(live.LeadingBlank ? 1 : 0), replay.Render(width, T, GlyphSet.Unicode).Skip(replay.LeadingBlank ? 1 : 0));
    }
}
