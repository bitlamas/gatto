using Gatto.Cli.Setup;
using Gatto.Cli.Setup.Shelves;
using Gatto.Core.Acquire;
using Gatto.Tests.Fakes;
using Gatto.Tests.Setup.Tui;

namespace Gatto.Tests.Setup;

//a file the wizard adopts is read on a watched screen first, so its profile entry records the bytes, and nothing reads behind a still screen
public class ReadingStepTests
{
    private const string Downloaded = @"C:\Users\me\Downloads\model-Q4_K_M.gguf";
    private static readonly string Sha = new('f', 64);

    private static ModelRow Row(string? published) =>
        ShelfRows.Of("org/a", "org", new HubQuant("model-Q4_K_M.gguf", 4_000_000_000, published),
            Gatto.Core.Models.FitRegime.FitsGpu, 32768, false, null, 100, false);

    //the hash answers only when the test lets it, so the screen between the arrival and the verdict can be seen
    private sealed class HeldHash(string? answer)
    {
        public readonly ManualResetEventSlim Release = new(false);
        public int Calls;
        public string? Read(string path)
        {
            Interlocked.Increment(ref Calls);
            Release.Wait(TimeSpan.FromSeconds(10));
            return answer;
        }
    }

    private static (SetupFlow Flow, FakeSetupProbes Probes) Watching(string? published, HeldHash hash)
    {
        var probes = new FakeSetupProbes { Rows = [Row(published)], Found = [], Hash = hash.Read };
        var flow = new SetupFlow(probes);
        flow.StartPastEngine();
        flow.Answer("0");
        probes.Found = [new FoundModel(Downloaded, 4_000_000_000, null)];
        Assert.True(flow.PollForDownload());
        return (flow, probes);
    }

    private static SetupScreen.Choice ReadingScreen(SetupScreen screen)
    {
        var reading = Assert.IsType<SetupScreen.Choice>(screen);
        Assert.Equal(SetupFlow.ReadingKey, reading.Key);
        Assert.True(reading.Watching);
        Assert.True(reading.FullPurr);   //the purr carries the live timer, and the row carries no number
        Assert.Equal("reading model-Q4_K_M.gguf", Assert.Single(reading.BodyRows!).Text);
        return reading;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void THE_WATCH_READS_ON_A_ROW_AND_RECORDS_THE_HASH_WITH_OR_WITHOUT_A_PUBLISHED_ONE(bool published)
    {
        var hash = new HeldHash(Sha);
        var (flow, _) = Watching(published ? Sha : null, hash);

        ReadingScreen(flow.Answer(SetupFlow.Landed));
        Assert.False(flow.PollWatch());
        Assert.Null(flow.Writes.CreateModel);

        hash.Release.Set();
        Assert.True(SpinWait.SpinUntil(flow.PollWatch, TimeSpan.FromSeconds(10)));
        flow.Answer(SetupFlow.Landed);

        Assert.Equal(Sha, flow.Writes.CreateModel!.Sha256);
        Assert.Equal(1, hash.Calls);
    }

    //the screen as the face draws it, the purr with its timer above the row naming the file
    [Fact]
    public void THE_READING_SCREEN_DRAWS_THE_FILE_UNDER_THE_PURR()
    {
        var hash = new HeldHash(Sha);
        var (flow, _) = Watching(null, hash);
        var reading = ReadingScreen(flow.Answer(SetupFlow.Landed));
        var opened = false;
        var rows = WalkRender.Watching(reading, 100, tick: null,
            nowMs: () => { if (!opened) { opened = true; return 0; } return 72_800; }).Rows;
        hash.Release.Set();

        var joined = string.Join("\n", rows);
        Assert.Contains("reading model-Q4_K_M.gguf", joined, StringComparison.Ordinal);
        Assert.Contains("1m 12s", joined, StringComparison.Ordinal);
    }

    //a published fingerprint the bytes do not match is the wrong-file screen, after the read and never before it
    [Fact]
    public void A_READ_THAT_DISAGREES_WITH_THE_PUBLISHED_HASH_IS_THE_WRONG_FILE()
    {
        var hash = new HeldHash(new string('0', 64));
        var (flow, _) = Watching(Sha, hash);
        ReadingScreen(flow.Answer(SetupFlow.Landed));

        hash.Release.Set();
        Assert.True(SpinWait.SpinUntil(flow.PollWatch, TimeSpan.FromSeconds(10)));
        var after = flow.Answer(SetupFlow.Landed);

        Assert.NotEqual(SetupFlow.ReadingKey, ScreenKey.Of(after));
        Assert.Null(flow.Writes.CreateModel);
    }

    [Fact]
    public void A_FOUND_FILE_IS_READ_ON_A_ROW_BEFORE_ITS_MODEL_IS_WRITTEN()
    {
        var hash = new HeldHash(Sha);
        var flow = new SetupFlow(new FakeSetupProbes
        {
            Found = [new FoundModel(@"D:\m\model-Q4_K_M.gguf", 4_000_000_000, null)],
            Hash = hash.Read,
        });
        flow.StartPastEngine();

        ReadingScreen(flow.Answer("0"));
        hash.Release.Set();
        Assert.True(SpinWait.SpinUntil(flow.PollWatch, TimeSpan.FromSeconds(10)));
        flow.Answer(SetupFlow.Landed);

        Assert.Equal(Sha, flow.Writes.CreateModel!.Sha256);
    }

    //a stop cancels the read, writes nothing and goes back to where the read began
    [Fact]
    public void A_STOP_DURING_THE_READ_LEAVES_NO_KEY_AND_NO_MODEL()
    {
        var hash = new HeldHash(Sha);
        var (flow, _) = Watching(null, hash);
        ReadingScreen(flow.Answer(SetupFlow.Landed));

        hash.Release.Set();
        var back = flow.Answer(SetupFlow.ReadingStop);

        Assert.Equal(SetupFlow.DownloadKey, ScreenKey.Of(back));
        Assert.Null(flow.Writes.CreateModel);
    }

    //the watch read the file to judge it, so the adoption after it takes that hash and reads nothing
    [Fact]
    public void THE_WATCHS_READ_IS_THE_ADOPTIONS_HASH_AND_NOTHING_READS_TWICE()
    {
        var hash = new HeldHash(Sha);
        hash.Release.Set();
        var (flow, _) = Watching(Sha, hash);
        flow.Settle(flow.Answer(SetupFlow.Landed));
        Assert.Equal(1, hash.Calls);
        Assert.Equal(Sha, flow.Writes.CreateModel!.Sha256);
    }
}
