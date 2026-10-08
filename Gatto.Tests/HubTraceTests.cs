using Gatto.Core.Acquire;
using Gatto.Tests.Setup;

namespace Gatto.Tests;

//the trace is off unless a file is named, and then it gives each read its kind, its source, its bytes and its time
public class HubTraceTests
{
    private const string Model = "Qwen3.8-Traced-7B";

    private static Task<ShelfOutcome> Run(FakeShelfHub hub, HubTreeMemo memo) =>
        ShelfSearch.AssembleAsync(new HubClient(new HttpClient(hub) { Timeout = Timeout.InfiniteTimeSpan }),
            UploaderAllowlist.Load(), Families.Load(), new HashSet<string> { "qwen" }, ShelfMachines.Apu8060S, 8192,
            lifted: false, CancellationToken.None, memo: memo);

    [Fact]
    public async Task A_TRACED_SEARCH_NAMES_EACH_READ()
    {
        var file = Path.GetTempFileName();
        var hub = new FakeShelfHub().Source("Qwen/" + Model)
            .Conversion("Qwen/" + Model, "unsloth/" + Model + "-GGUF", 7_000_000_000, 1000, "qwen35",
                files: (Model + "-Q4_K_M.gguf", 4.5));
        var memo = new HubTreeMemo();
        HubTrace.Target = file;
        try
        {
            await Run(hub, memo);
            await Run(hub, memo);
        }
        finally { HubTrace.Target = null; }

        //other tests may write while the target is set, so only this model's lines are read
        var lines = File.ReadAllLines(file).Select(l => l.Split('\t')).Where(f => f[2].Contains(Model)).ToList();
        File.Delete(file);
        Assert.All(lines, f => Assert.Equal(6, f.Length));
        Assert.Contains(lines, f => f is ["conversions", "net", ..]);
        Assert.Single(lines, f => f is ["tree", "net", ..]);
        Assert.Contains(lines, f => f is ["tree", "memo", ..]);
        Assert.All(lines.Where(f => f[1] == "net"), f => Assert.True(long.Parse(f[4]) > 0));
    }

    [Fact]
    public void OFF_BY_DEFAULT()
    {
        Assert.Null(Environment.GetEnvironmentVariable(HubTrace.Variable));
        Assert.Null(HubTrace.Target);
    }
}
