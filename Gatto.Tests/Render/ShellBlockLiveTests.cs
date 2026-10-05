using System.Text.Json;
using Gatto.Core.Client;
using Gatto.Core.Tools;
using Gatto.Repl;
using Gatto.Repl.Input;
using Gatto.Repl.Render;
using Gatto.Repl.Term;
using Gatto.Terminal;
using Gatto.Tests.Fakes;
using Xunit;

namespace Gatto.Tests.Render;

public sealed class ShellBlockLiveTests
{
    private static readonly Theme T = new(new TermCaps(true, true));

    private static (StreamRenderer R, ChromePainter P, VtScreenSurface S) Wire(int width, int height)
    {
        var s = new VtScreenSurface(width, height);
        var gate = new object();
        var model = new TranscriptModel("coder");
        var painter = new ChromePainter(s, T, gate, model)
        {
            Frame = new InputFrame(s, T, "coder", new StatusInfo(@"C:\proj", "qwen", "coder", new CtxState(), @"C:\Users\x"), glyphs: GlyphSet.Unicode),
            RoleForTint = "coder",
        };
        painter.State.Composer = new EditorView(new List<string> { "" }, 0, 0);
        var ticker = new ChromeTicker(painter, gate);
        var renderer = new StreamRenderer(painter, s, T, "coder", ticker, gate, model: model, convoTail: () => null);
        painter.AltScreen.Enter();
        painter.Repaint();
        return (renderer, painter, s);
    }

    private static List<string> ToolRows(ChromePainter p, int width, int height) =>
        p.ComposeChromeBlock(width, height).Rows.Where(r => r.Region == ChromeRegion.Tool).Select(r => r.Visible.TrimEnd()).Where(v => v.Length > 0).ToList();

    private static IEnumerable<string> Deltas(string json, int step)
    {
        for (var n = step; n < json.Length + step; n += step) yield return json[..Math.Min(n, json.Length)];
    }

    //each line carries a counter, so a window pinned at the top cannot pass
    [Fact]
    public void The_window_follows_the_newest_row_at_every_delta()
    {
        var command = string.Join("\n", Enumerable.Range(1, 12).Select(i => $"echo line{i:00}"));
        var json = JsonSerializer.Serialize(new { command });
        var (r, p, _) = Wire(120, 30);
        r.BeginTurn();
        foreach (var partial in Deltas(json, 7))
        {
            r.OnToolCallDelta("shell", partial);
            var decoded = PartialJson.StringValue(partial, "command") ?? "";
            var newest = decoded.Split('\n')[^1];
            if (newest.Length == 0) continue;
            var rows = ToolRows(p, 120, 30);
            Assert.EndsWith(newest.Trim(), rows[^1].Trim(), StringComparison.Ordinal);
            Assert.True(rows.Count <= 4, $"the live block holds the header and at most three command rows: {rows.Count}");
        }
    }

    //the rows on screen after the last delta are the committed rows, so nothing snaps when the call closes
    [Fact]
    public void Nothing_snaps_when_the_call_commits()
    {
        var command = "$s = @'\necho one\necho two\necho three\n'@; $s | ssh host \"bash -s\"";
        var json = JsonSerializer.Serialize(new { command });
        var (r, p, s) = Wire(120, 30);
        r.BeginTurn();
        foreach (var partial in Deltas(json, 5)) r.OnToolCallDelta("shell", partial);
        var live = ToolRows(p, 120, 30).Skip(1).ToList();
        var call = new ToolCall("c1", "shell", json);
        r.OnToolCallStart(call);
        r.OnToolResult(call, new ToolResult("done", Gloss: "exit 0"));
        p.Repaint();
        var screen = s.Viewport.Select(v => v.TrimEnd()).ToList();
        var at = screen.FindIndex(v => v.Contains(live[0].Trim(), StringComparison.Ordinal));
        Assert.True(at >= 0, "the committed block does not show the live rows");
        Assert.Equal(live.Select(v => v.Trim()), screen.Skip(at).Take(live.Count).Select(v => v.Trim()));
    }

    [Fact]
    public void A_short_command_stays_on_the_header_row_through_every_delta()
    {
        var json = JsonSerializer.Serialize(new { command = "git status" });
        var (r, p, _) = Wire(120, 30);
        r.BeginTurn();
        foreach (var partial in Deltas(json, 3))
        {
            r.OnToolCallDelta("shell", partial);
            Assert.Single(ToolRows(p, 120, 30));
        }
        //today's chrome also paints one row while arguments stream, a bare shell, so the command text is the proof
        Assert.Contains("git status", ToolRows(p, 120, 30).Single(), StringComparison.Ordinal);
    }

    //the chrome cuts any row to the width on its own, so the width oracle reads the layout, and the painted oracle reads the tail
    [Fact]
    public void A_resize_between_deltas_keeps_the_tail_in_view_and_every_layout_row_inside_the_window()
    {
        var command = string.Join("\n", Enumerable.Range(1, 8).Select(i => $"Get-ChildItem -Path C:\\some\\long\\path\\number{i} -Recurse -Filter *.cs"));
        var json = JsonSerializer.Serialize(new { command });
        var (r, p, s) = Wire(120, 30);
        r.BeginTurn();
        var widths = new[] { 120, 80, 47, 120 };
        var k = 0;
        foreach (var partial in Deltas(json, 9))
        {
            var w = widths[k++ % widths.Length];
            s.Resize(w, 30);
            r.OnToolCallDelta("shell", partial);
            p.Repaint();
            var decoded = PartialJson.StringValue(partial, "command") ?? "";
            var layout = ShellBlockRender.Layout(ShellBlockInput.Live(ItemRender.ShellCommandText(decoded), "coder", null), ShellBlockState.Closed, w, T, GlyphSet.Unicode);
            Assert.All(layout.Rows, row => Assert.True(UnicodeWidth.Of(TermText.StripAnsiForWidth(row.Text)) <= w, $"width {w}: {row.Text}"));
            var tail = decoded.Split('\n')[^1].Trim();
            if (tail.Length == 0) continue;
            Assert.EndsWith(tail.Split(' ')[^1], ToolRows(p, w, 30)[^1].Trim(), StringComparison.Ordinal);
        }
    }

    //a prompt's wait text is a row of its own, so it never pushes a short command off the header row
    [Fact]
    public void The_wait_text_is_its_own_row_under_the_window()
    {
        var (r, p, _) = Wire(120, 30);
        r.BeginTurn();
        r.OnToolCallDelta("shell", JsonSerializer.Serialize(new { command = "git status" }));
        p.State.ToolWait = "waiting for your input";
        var rows = ToolRows(p, 120, 30);
        Assert.Equal(2, rows.Count);
        Assert.Contains("git status", rows[0], StringComparison.Ordinal);
        Assert.Contains("waiting for your input", rows[1], StringComparison.Ordinal);
    }

    //the live rows cost the panel their own height, so a panel up mid-call cannot cover the command window
    [Fact]
    public void The_live_rows_take_their_room_from_the_panel_allowance()
    {
        var command = string.Join("\n", Enumerable.Range(1, 12).Select(i => $"Write-Output {i}"));
        foreach (var (width, height) in new[] { (80, 36), (50, 44) })
        {
            var (r, p, _) = Wire(width, height);
            r.BeginTurn();
            var bare = p.RoomRowsAvailable();
            r.OnToolCallDelta("shell", JsonSerializer.Serialize(new { command }));
            var painted = p.ComposeChromeBlock(width, height).Rows.Count(rr => rr.Region == ChromeRegion.Tool);
            Assert.Equal(5, painted);   //the blank, the header and the three command rows resting on the tail
            Assert.Equal(bare - painted, p.RoomRowsAvailable());
            p.State.ToolWait = "waiting for your input";
            Assert.Equal(bare - painted - 1, p.RoomRowsAvailable());
        }
    }
}
