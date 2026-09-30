using Gatto.Cli.Setup;
using Gatto.Core.Hardware;
using Gatto.Core.Models;
using Gatto.Terminal;

namespace Gatto.Tests.Setup;

//the PairFit and ContextFor sites judge fit through FitArithmetic. a set read as its first shard fits on the card, so the ladder picks a budget it cannot load
public class ShardFitTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gatto-shardfit-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (Exception) { }
    }

    //the injected machine has a few hundred MB spare, so a 10 MB file and a 210 MB file get different answers. a real machine would call both small and agree
    private static ProbeOutcome SmallMachine() => new(
        new HardwareSnapshot(InstalledBytes: 4_000_000_000, OsVisibleBytes: 1_600_000_000,
            GraphicsKind: GpuKind.Discrete, GraphicsMemoryBytes: 2_000_000_000, GraphicsVendorId: "1002"),
        "ok", CpuName: "test cpu", GpuName: "test gpu");

    //writes a real GGUF header, then pads the file to bytes. the parser reads from the start, so the padding is invisible to it and shows up in every size read
    private string Gguf(string name, long bytes)
    {
        var path = Path.Combine(_dir, name);
        var header = GgufTestBytes.SyntheticHeader(kv =>
        {
            kv.Str("general.architecture", "qwen3");
            kv.Str("general.name", "shardy");
            kv.U32("qwen3.context_length", 32768);
            kv.U32("qwen3.block_count", 8);
            kv.U32("qwen3.embedding_length", 512);
            kv.U32("qwen3.attention.head_count", 8);
            kv.U32("qwen3.attention.head_count_kv", 2);
            kv.U32("qwen3.attention.key_length", 64);
            kv.U32("qwen3.attention.value_length", 64);
        });
        using var fs = File.Create(path);
        fs.Write(header);
        if (bytes > header.Length) fs.SetLength(bytes);
        return path;
    }

    private LiveSetupProbes Probes() =>
        new(Path.Combine(_dir, "home"), GlyphSet.Unicode, TextWriter.Null,
            readHardware: SmallMachine);

    //the first shard holds metadata only and the set totals each twice. the regime gets a set the machine cannot run, and the ladder one that still fits
    private string ShardSet(string stem, long each)
    {
        var one = Gguf($"{stem}-00001-of-00003.gguf", 10_000_000);
        Gguf($"{stem}-00002-of-00003.gguf", each);
        Gguf($"{stem}-00003-of-00003.gguf", each);
        return one;
    }

    //builds a 1.5 GB set, past every budget the injected machine has.
    private string UnrunnableSet() => ShardSet("big", 745_000_000);

    //builds a 0.7 GB set, inside the RAM budget, which leaves the context ladder rungs to compare
    private string RunnableSet() => ShardSet("mid", 345_000_000);

    //the test drives the PairFit site of a size rule with two sites. the injected machine needs no download to make a set larger than the card
    [Fact]
    public void PAIR_FIT_JUDGES_THE_WHOLE_SET_AGAINST_THE_MACHINE()
    {
        using var probes = Probes();
        var one = UnrunnableSet();

        var set = probes.PairFit(one, projectorBytes: 0);

        Assert.NotNull(set);
        Assert.NotEqual(FitRegime.FitsGpu, set!.Value.Alone);
    }

    //the lone file of the first shard's size stops the test passing on a machine where nothing fits. the claim is the disagreement alone
    [Fact]
    public void AND_A_LONE_FILE_OF_THE_FIRST_SHARDS_SIZE_GETS_A_DIFFERENT_REGIME()
    {
        using var probes = Probes();
        var one = UnrunnableSet();
        var lone = Gguf("lone.gguf", 10_000_000);

        var set = probes.PairFit(one, projectorBytes: 0);
        var alone = probes.PairFit(lone, projectorBytes: 0);

        Assert.NotNull(set);
        Assert.NotNull(alone);
        Assert.NotEqual(set!.Value.Alone, alone!.Value.Alone);
    }

    //this test drives the ContextFor site, where the ladder searches down from the header's ceiling to the largest budget that fits
    [Fact]
    public void THE_CONTEXT_LADDER_PRICES_THE_WHOLE_SET()
    {
        using var probes = Probes();
        var one = RunnableSet();
        var lone = Gguf("lone.gguf", 10_000_000);

        var forTheSet = probes.ContextFor(one);
        var forOneFile = probes.ContextFor(lone);

        Assert.True(forTheSet < forOneFile,
            $"the set took {forTheSet} and a single file of shard one's size took {forOneFile}; "
            + "the ladder is not reading the set");
    }
}
