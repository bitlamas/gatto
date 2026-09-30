    //the census keeps a row for every constructed screen, and a key written into _emitted with nothing draining it fails NO_SCREEN_IS_NARRATION_ONLY_ANY_MORE

using Gatto.Cli.Setup;
using Gatto.Core.Acquire;
using Gatto.Core.Hardware;
using Gatto.Core.Home;

namespace Gatto.Tests.Setup;

//six reachable screens that no test named, typed at the construction site so a reflection-based audit misses them
public class UnmappedScreenTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-unmapped-").FullName;
    public void Dispose() { try { Directory.Delete(_home, true); } catch (Exception) { } }

    private static string Fixture(string n) => Path.Combine(AppContext.BaseDirectory, "Fixtures", n);

    private sealed class Surface(params string?[] answers) : IWizardSurface
    {
        private int _i;
        public List<WizardScreen> Seen { get; } = [];
        public string? Choose(WizardScreen.Choice c, Func<bool>? watch = null,
            Func<FetchTick?>? tick = null, Func<CheckTick?>? check = null) { Seen.Add(c); return Next(); }
        public string? Ask(WizardScreen.Ask a) { Seen.Add(a); return Next(); }
        public void Show(WizardScreen.Info i) => Seen.Add(i);
        public void End(WizardScreen.Terminal t) => Seen.Add(t);
        private string? Next() => _i < answers.Length ? answers[_i++] : null;

        public IEnumerable<string> Keys => Seen.Select(ScreenKey.Of);

        public string Text(string key) => string.Join(" ",
            Seen.OfType<WizardScreen.Info>().Where(i => i.Key == key).SelectMany(i => i.Rows.Select(r => r.Text)));
    }

    private sealed class Probes : ISetupProbes
    {
        public string? Llama { get; init; } = @"C:\llama\llama-server.exe";
        public IReadOnlyList<FoundModel> Found { get; set; } = [];
        public MoveOffer? Move { get; init; }
        public ConnectProbe? Connect { get; init; }

        public HardwareSnapshot? Hardware() => new(34359738368, 34093496320, GpuKind.Discrete, 8589934592);

        //display only, and null is honest here, a name this fake invented would put hardware on a screen no probe reported
        public HardwareNames HardwareNames() => default;
        public string? LlamaServerPath() => Llama;
        public bool HasResolvableModel() => false;
        public ConnectProbe? ProbeServer(IReadOnlyList<int>? skip = null) => Connect;
        public string? GattoServingOn(int port) => null;

        public ConnectProbe? ProbeAt(string baseUrl) => null;
        public IReadOnlyList<string> Roots { get; init; } = [];
        public ScanResult Scan(string? r) => new(Found, Roots);
        public HubSearchOutcome Search(HubSearchRequest request) => new([], null);
        public (string Path, long Bytes)? ProjectorFor(string p) => null;
        public string? ArchitectureOf(string p) => null;
        public MoveOffer? MoveOfferFor(string p) => Move;
        public (Gatto.Core.Models.FitRegime Alone, Gatto.Core.Models.FitRegime WithProjector)? PairFit(string p, long b) => null;
        public TypedIdOutcome EvaluateTypedId(string id) => new TypedIdOutcome.Unreachable(false);
        public Gatto.Roles.LlamaAsset? ChooseLlamaAsset() => new("z.zip", null, "a graphics card", "Vulkan");
        public Gatto.Core.Tools.ProbeResult VerifyLlamaServer(string p) =>
            new(Gatto.Core.Tools.ProbeShape.ClassicServer, "d",
                //a ClassicServer names its build in the same banner the probe reads the shape from. faking one without the other makes the run skip the engine step
                Build: Gatto.Roles.LlamaAssetSteering.PinnedRelease);
        public int ContextFor(string p) => 8192;
        public AuditionCheck RunAudition(string p, string? repoId, IProgress<Gatto.Roles.Audition.AuditionProgress>? progress, CancellationToken ct) =>
            new(AuditionOutcome.Passed);
        public string? ExistingModelFor(string ggufPath) => null;
        public (Gatto.Roles.IdClash Kind, string? Id) ClashFor(string ggufPath, string? incomingRepoId) => (Gatto.Roles.IdClash.Free, null);
        //a fixed fake path, so the screen renders the state the test chose rather than the exe running the tests
        public string RunningFrom() => @"C:\Users\you\Downloads\gatto\gatto.exe";

        public (Gatto.Cli.InstallState State, string Dir) InstallStatus() =>
            (Gatto.Cli.InstallState.Installed, @"C:\Programs\gatto");

        public string? InstalledVersion() => null;


        public string RunningVersion() => Gatto.Core.GattoVersion.String;
        public bool? UpdateConsent() => false;
        public string? ExistingEndpointFor(string baseUrl) => null;
        public string ConfigPath() => @"C:\home\gatto.json";
        public ProveOutcome ProveIt(string? modelId,
            IProgress<Gatto.Roles.Audition.AuditionProgress>? progress = null) => new(true, "hi", TimeSpan.FromSeconds(2));
    }

    private string Gguf(string name = "tiny.gguf")
    {
        var dst = Path.Combine(_home, name);
        File.Copy(Fixture("tiny.gguf"), dst, overwrite: true);
        return dst;
    }

    //narration and the face

    //narration now reaches the face, and the model.chosen confirmation is gone from the flow entirely
    [Fact]
    public void NARRATION_NOW_REACHES_THE_FACE_AND_model_chosen_IS_GONE()
    {
        //narration now goes to the channel SetupRunner drains, so the pin is inverted and a line no face reads fails again
        var probes = new Probes { Found = [new FoundModel(Gguf("qwen.gguf"), 4_000_000_000, null)] };
        var flow = new SetupFlow(probes);
        var face = new Surface([.. WalkOpening.PastEngine, "0"]);

        SetupRunner.Run(flow, face, homePath: null);

        //the confirmation is gone from Emitted as well as from the face, so it is not merely unrendered
        Assert.DoesNotContain(flow.Emitted, s => s is WizardScreen.Info i && i.Key == "model.chosen");
        Assert.DoesNotContain("model.chosen", face.Keys);
        //this run finds a model already, so none of the five survivors fires here and the kept-rows claim sits in SetupRunnerWalkTests
    }

    //the four that were only missing a guard




    [Fact]
    public void A_FAILED_WRITE_SAYS_WHAT_FAILED_and_stops()
    {
        //the model's header is really not a gguf, so the reader throws for real and nothing on the failure path is faked
        GattoHome.EnsureInitialized(_home);
        var notAModel = Path.Combine(_home, "broken.gguf");
        File.WriteAllText(notAModel, "this is not a gguf");

        var probes = new Probes { Found = [new FoundModel(notAModel, 100, null)] };
        var face = new Surface([.. WalkOpening.PastEngine, "0"]);

        var code = SetupRunner.Run(new SetupFlow(probes), face, _home);

        Assert.Equal(1, code);
        Assert.Contains("write.failed", face.Keys);
        Assert.Contains("couldn't create the model", face.Text("write.failed"), StringComparison.Ordinal);
    }

    //the two, one of which was a real defect


    //a move that did not happen gets a line on the screen, and the model is still written so the run continues
    [Fact]
    public void A_MOVE_THAT_DID_NOT_HAPPEN_IS_SHOWN_TO_THE_USER()
    {
        GattoHome.EnsureInitialized(_home);
        var src = Gguf();

        //the destination already holds someone else's file, since ModelAdoption refuses to overwrite it
        var dest = Directory.CreateDirectory(Path.Combine(_home, "blocked")).FullName;
        //the block goes in the model's own folder, where the apply writes now, and the id comes from the same Derive it uses
        var blockedDir = Directory.CreateDirectory(Path.Combine(dest,
            Gatto.Core.Models.ModelId.Derive(Gatto.Core.Models.GgufReader.Read(src), src))).FullName;
        File.WriteAllText(Path.Combine(blockedDir, "tiny.gguf"), "someone else's file");

        var probes = new Probes
        {
            Found = [new FoundModel(src, 4_000_000_000, null)],
            Move = new MoveOffer(dest, false, 4_000_000_000, null),
        };
        var face = new Surface([.. WalkOpening.PastEngine, "0", SetupFlow.Yes]);

        SetupRunner.Run(new SetupFlow(probes), face, _home);

        Assert.Contains("move.incomplete", face.Keys);
        Assert.Contains("stays where it is", face.Text("move.incomplete"), StringComparison.Ordinal);
        //the model is in place anyway, which is the point of degrading rather than aborting
        Assert.True(File.Exists(src));
        Assert.NotEmpty(Gatto.Roles.Model.ListIds(Path.Combine(_home, "models")));
    }

    //the architecture is bailingmoe3, a real one the pinned build cannot load, so the custom-build offer fires. the fixture agrees with the shipped architecture set
    private sealed class ArchProbes : ISetupProbes
    {
        private readonly Probes _inner = new();
        public IReadOnlyList<FoundModel> Found { get; init; } = [];

        public string? ArchitectureOf(string p) => "bailingmoe3";
        public IReadOnlyList<string> Roots { get; init; } = [];
        public ScanResult Scan(string? r) => new(Found, Roots);

        public HardwareSnapshot? Hardware() => _inner.Hardware();

        //display only, and null is honest here, a name this fake invented would put hardware on a screen no probe reported
        public HardwareNames HardwareNames() => default;
        public string? LlamaServerPath() => _inner.LlamaServerPath();
        public bool HasResolvableModel() => _inner.HasResolvableModel();
        public ConnectProbe? ProbeServer(IReadOnlyList<int>? skip = null) => _inner.ProbeServer(skip);
        public string? GattoServingOn(int port) => _inner.GattoServingOn(port);

        public ConnectProbe? ProbeAt(string baseUrl) => null;
        public HubSearchOutcome Search(HubSearchRequest request) => _inner.Search(request);
        public (string Path, long Bytes)? ProjectorFor(string p) => _inner.ProjectorFor(p);
        public MoveOffer? MoveOfferFor(string p) => _inner.MoveOfferFor(p);
        public (Gatto.Core.Models.FitRegime Alone, Gatto.Core.Models.FitRegime WithProjector)? PairFit(string p, long b) => _inner.PairFit(p, b);
        public TypedIdOutcome EvaluateTypedId(string id) => _inner.EvaluateTypedId(id);
        public Gatto.Roles.LlamaAsset? ChooseLlamaAsset() => _inner.ChooseLlamaAsset();
        public Gatto.Core.Tools.ProbeResult VerifyLlamaServer(string p) => _inner.VerifyLlamaServer(p);
        public int ContextFor(string p) => _inner.ContextFor(p);
        public AuditionCheck RunAudition(string p, string? repoId, IProgress<Gatto.Roles.Audition.AuditionProgress>? progress, CancellationToken ct) =>
        _inner.RunAudition(p, repoId, progress, ct);
        public string? ExistingModelFor(string g) => _inner.ExistingModelFor(g);
        public (Gatto.Roles.IdClash Kind, string? Id) ClashFor(string ggufPath, string? incomingRepoId) =>
            _inner.ClashFor(ggufPath, incomingRepoId);
        //a fixed fake path, so the screen renders the state the test chose rather than the exe running the tests
        public string RunningFrom() => @"C:\Users\you\Downloads\gatto\gatto.exe";

        public (Gatto.Cli.InstallState State, string Dir) InstallStatus() => _inner.InstallStatus();

        public string? InstalledVersion() => null;


        public string RunningVersion() => Gatto.Core.GattoVersion.String;
        public bool? UpdateConsent() => _inner.UpdateConsent();
        public string? ExistingEndpointFor(string b) => _inner.ExistingEndpointFor(b);
        public string ConfigPath() => _inner.ConfigPath();
        public ProveOutcome ProveIt(string? modelId,
        IProgress<Gatto.Roles.Audition.AuditionProgress>? progress = null) =>
        _inner.ProveIt(modelId, progress);
    }
}
