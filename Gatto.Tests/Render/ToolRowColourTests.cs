using Gatto.Repl.Input;
using Gatto.Repl.Render;
using Gatto.Repl.Term;
using Gatto.Terminal;
using Gatto.Tests.Fakes;
using Xunit;

namespace Gatto.Tests.Render;

//a tool call row paints its name in Theme.ToolName and its argument preview in Theme.ToolArgs, live and committed
public sealed class ToolRowColourTests
{
    private static readonly Theme T = new(new TermCaps(true, true));

    [Fact]
    public void A_committed_tool_block_paints_the_name_and_the_arguments_in_their_own_colours()
    {
        var header = ItemRender.ToolRows("read_file", @"~\notes\a.txt", "ok", true, T, "generalist", glyphs: GlyphSet.Unicode)[0];

        Assert.Contains(T.Paint("read_file", Theme.ToolName) + " " + T.Paint(@"~\notes\a.txt", Theme.ToolArgs), header, StringComparison.Ordinal);
    }

    [Fact]
    public void The_live_tool_row_paints_the_name_and_the_arguments_in_the_same_colours()
    {
        var s = new VtScreenSurface(80, 20);
        var status = new StatusInfo(@"C:\Users\user\projects\gatto", "model", "generalist", new CtxState(), @"C:\Users\user");
        var painter = new ChromePainter(s, T, new object())
        {
            Frame = new InputFrame(s, T, "generalist", status, glyphs: GlyphSet.Unicode),
        };
        painter.State.Composer = new EditorView(new List<string> { "" }, 0, 0);
        painter.State.Tool = ("read_file", @"~\notes\a.txt");

        var row = painter.ComposeChromeBlock(80, 20).Rows
            .Single(r => r.Region == ChromeRegion.Tool && r.Visible.Contains("read_file", StringComparison.Ordinal));

        Assert.Contains(T.Paint("read_file", Theme.ToolName) + " " + T.Paint(@"~\notes\a.txt", Theme.ToolArgs), row.Rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void The_light_theme_maps_both_tool_colours_to_their_light_pairs()
    {
        var light = new Theme(new TermCaps(true, true), ThemeMode.Light);

        Assert.Equal(Theme.ToolNameLight, light.Map(Theme.ToolName));
        Assert.Equal(Theme.ToolArgsLight, light.Map(Theme.ToolArgs));
    }
}
