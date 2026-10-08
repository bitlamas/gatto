using System.Web;
using Gatto.Cli.Setup;
using Gatto.Core.Acquire;
using Gatto.Core.Hardware;
using Gatto.Terminal;

namespace Gatto.Tests.Setup;

//the probe hands the lit families, the lift and a typed search to the engine unchanged, over one Hub client per session
public sealed class LiveProbeSearchTests : IDisposable
{
    private const long B = 1_000_000_000L;
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-probe-search-").FullName;

    public void Dispose() => Directory.Delete(_home, recursive: true);

    private LiveSetupProbes Probes(FakeShelfHub hub) =>
        new(_home, GlyphSet.Unicode, TextWriter.Null,
            readHardware: () => new ProbeOutcome(new HardwareSnapshot(32UL << 30, 32UL << 30, GpuKind.Discrete, 8UL << 30, "0x1002"), "fixture"),
            hubHandler: hub);

    private static FakeShelfHub Ladder()
    {
        var hub = new FakeShelfHub();
        foreach (var (name, total, gb) in new[] { ("Qwen3.8-27B", 27 * B, 12.0), ("Qwen3.6-35B-A3B", 35 * B, 20.0), ("Qwen3.5-9B", 9 * B, 5.5) })
            hub.Source("Qwen/" + name).Conversion("Qwen/" + name, "unsloth/" + name + "-GGUF", total, 10, "qwen35",
                files: (name + "-Q4_K_M.gguf", gb));
        hub.Source("google/gemma-4-E4B-it").Conversion("google/gemma-4-E4B-it", "unsloth/gemma-4-E4B-it-GGUF", 8 * B, 10, "gemma4",
            files: ("e-Q4_K_M.gguf", 5));
        return hub;
    }

    private static List<string> Terms(FakeShelfHub hub) =>
        [.. hub.Urls.Select(u => HttpUtility.ParseQueryString(new Uri(u).Query)["search"]).OfType<string>()];

    [Fact]
    public void THE_PROBE_PASSES_THE_LIT_SET_AND_THE_LIFT()
    {
        var hub = Ladder();
        using (var probes = Probes(hub))
            probes.SearchModels(new ModelSearchRequest(new HashSet<string> { "qwen" }), null, CancellationToken.None);
        Assert.DoesNotContain(Terms(hub), t => t.StartsWith("gemma", StringComparison.Ordinal));

        var lifted = Ladder();
        using (var probes = Probes(lifted))
            probes.SearchModels(new ModelSearchRequest(new HashSet<string> { "gemma" }, Lifted: true), null, CancellationToken.None);
        Assert.Equal(["gemma-4", "gemma-4", "gemma-3n", "gemma-3n", "gemma-3", "gemma-3"], Terms(lifted));
    }

    [Fact]
    public void A_TYPED_SEARCH_GOES_TO_THE_TYPED_PATH()
    {
        var hub = Ladder();
        using (var probes = Probes(hub))
            probes.SearchModels(new ModelSearchRequest(new HashSet<string> { "qwen" }, Search: "heretic"), null, CancellationToken.None);
        Assert.Equal(UploaderAllowlist.Load().Orgs.Count, Terms(hub).Count(t => t == "heretic"));
        Assert.All(Terms(hub), t => Assert.Equal("heretic", t));
    }

    //a lifts from the session's last landing of the same lit families, so a window spent by the landing takes none of its rows away
    [Fact]
    public void A_LIFTS_FROM_THE_LANDING()
    {
        var hub = Ladder();
        using var probes = Probes(hub);
        HashSet<string> both = ["gemma", "qwen"];
        var landing = probes.SearchModels(new ModelSearchRequest(both), null, CancellationToken.None);
        Assert.NotEmpty(landing.Rows);

        hub.RateRemaining = ShelfSearch.Reserve + 1;
        var lifted = probes.SearchModels(new ModelSearchRequest(both, Lifted: true), null, CancellationToken.None);
        Assert.True(lifted.Cut);
        Assert.All(landing.Rows, l => Assert.Contains(lifted.Rows, r => r.Model == l.Model));

        //another lit set takes no landing rows, so the gemma row stays on the shelf it was found for
        var other = probes.SearchModels(new ModelSearchRequest(new HashSet<string> { "qwen" }, Lifted: true), null, CancellationToken.None);
        Assert.DoesNotContain(other.Rows, r => r.Family == "gemma");
    }

    //the window the Hub reported in one search still binds the next one in the same session
    [Fact]
    public void THE_WINDOW_OUTLIVES_ONE_SEARCH()
    {
        var hub = Ladder();
        hub.RateRemaining = 24;
        using var probes = Probes(hub);
        var first = probes.SearchModels(new ModelSearchRequest(new HashSet<string> { "qwen" }), null, CancellationToken.None);
        Assert.True(first.Cut);
        var asked = hub.Urls.Count;
        var second = probes.SearchModels(new ModelSearchRequest(new HashSet<string> { "gemma" }), null, CancellationToken.None);
        Assert.Equal(asked, hub.Urls.Count);
        Assert.Equal(120, second.RateLimitedFor);
    }
}
