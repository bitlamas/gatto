using System.Text.Json;
using Gatto.Core.Client;
using Gatto.Core.Loop;
using Gatto.Core.Tools;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Repl;

//the committed /context screen is drawn again at the width the transcript repaints at, never cut from the rows of the old width
public class ContextResizeTests : IDisposable
{
    private readonly string _cwd = Directory.CreateTempSubdirectory("gatto-context-resize-").FullName;
    public void Dispose() { try { Directory.Delete(_cwd, recursive: true); } catch { } }

    private static ContextInputs Inputs(ContextUsageState _)
    {
        var spec = new ToolSpec("read_file", "reads", JsonDocument.Parse("{\"type\":\"object\"}").RootElement);
        return new ContextInputs("m", "sys", [new ContextPartText("system prompt", ContextGroup.Prefix, new string('s', 4000))],
            [spec], [], new RequestShape([spec], null, null, ReasoningHistory.All), 65_536, 0.8, 1.0, null, null);
    }

    //a bar row is the gutter and nothing but bar cells
    private static int BarCells(string row) => row.Trim().Length > 0 && row.Trim().All(c => c is '\u2588' or '\u2591') ? row.Trim().Length : -1;

    [Fact]
    public async Task Committed_at_120_and_repainted_at_90_the_bar_is_86_cells_and_nothing_is_cut()
    {
        var h = new RichReplHarness(_cwd, width: 120, height: 40, contextInputs: Inputs);
        h.Keys.Line("/context");
        var resized = false;

        await h.RunUntilAsync(() =>
        {
            if (!resized && h.Saw("cache  no request yet")) { h.Surface.Resize(90, 40); resized = true; }
            return resized && h.FinalFrame().Any(r => BarCells(r) == 86);
        });

        var frame = h.FinalFrame();
        //at 90 the label no longer fits after the mark, so it is turned back to end on the arrow
        Assert.Contains(frame, r => r.TrimEnd().EndsWith("auto-compacts at 80% \u2191", StringComparison.Ordinal));
        Assert.DoesNotContain(frame, r => (BarCells(r.Replace("\u2026", "")) > 0 || r.Contains("auto-compacts", StringComparison.Ordinal)) && r.Contains('\u2026'));
    }
}
