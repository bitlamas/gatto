using Gatto.Cli;
using Gatto.Cli.Setup;
using Gatto.Core.Acquire;
using Gatto.Core.Hardware;
using Gatto.Core.Home;
using Gatto.Tests.Setup;

namespace Gatto.Tests;

//the setup offer fires only when gatto is unconfigured, and nothing is created before consent. the predicate is separate so a test process can drive it
public class FirstRunDoorTests : IDisposable
{
    private readonly string _home =
        Path.Combine(Directory.CreateTempSubdirectory("gatto-door-").FullName, ".gatto");

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_home)!, true); } catch (Exception) { }
    }

    //an existing but empty home is the state setup exists for, so a directory alone must never read as configured.
    [Fact]
    public void AN_EMPTY_BUT_PRESENT_HOME_STILL_NEEDS_SETUP()
    {
        Directory.CreateDirectory(_home);

        Assert.True(FirstRunDoor.NotConfigured(_home));
    }

    //the starter gatto.json is written with no default_model on purpose, so a scaffolded home still reads as unconfigured
    [Fact]
    public void A_SCAFFOLDED_BUT_UNCONFIGURED_HOME_STILL_NEEDS_SETUP()
    {
        GattoHome.EnsureInitialized(_home);

        Assert.True(FirstRunDoor.NotConfigured(_home));
    }

    [Fact]
    public void A_HOME_THAT_DOES_NOT_EXIST_NEEDS_SETUP()
    {
        Assert.True(FirstRunDoor.NotConfigured(_home));
    }

    //the predicate ANDs two clauses, so each needs a fixture that satisfies only that clause. a fixture that satisfies both would pass whichever clause is broken
    [Fact]
    public void A_DEFAULT_MODEL_MEANS_CONFIGURED_even_with_no_models()
    {
        GattoHome.EnsureInitialized(_home);
        GattoConfigWriter.SetEndpointDefaultModel(_home, "local", "some-model");

        Assert.False(FirstRunDoor.NotConfigured(_home));
    }

    //a default endpoint that lists models launches on its first entry with nothing saved, so the home needs no setup
    [Fact]
    public void A_LISTED_MODEL_ON_THE_DEFAULT_ENDPOINT_MEANS_CONFIGURED()
    {
        Directory.CreateDirectory(_home);
        File.WriteAllText(Path.Combine(_home, "gatto.json"),
            """{"endpoints":{"cloudy":{"base_url":"https://c.test","models":["cloudy-max"]}},"default_endpoint":"cloudy"}""");

        Assert.False(FirstRunDoor.NotConfigured(_home));
    }

    //a model on disk means the machine has used gatto before, so setup stays closed with no default set
    [Fact]
    public void A_PACK_ON_DISK_MEANS_CONFIGURED_even_with_no_default_model()
    {
        GattoHome.EnsureInitialized(_home);
        var model = Directory.CreateDirectory(Path.Combine(_home, "models", "mine")).FullName;
        File.WriteAllText(Path.Combine(model, "profile.json"), "{}");

        Assert.False(FirstRunDoor.NotConfigured(_home));
    }

    //unreadable config still counts as configured, so the config error surfaces instead of setup
    [Fact]
    public void A_MALFORMED_CONFIG_IS_NOT_AN_INVITATION_TO_RUN_SETUP()
    {
        Directory.CreateDirectory(_home);
        File.WriteAllText(Path.Combine(_home, "gatto.json"), "{ this is not json");

        Assert.False(FirstRunDoor.NotConfigured(_home));
    }

    //the predicate only reads and never creates the home, since a check that scaffolds while deciding can fire only once
    [Fact]
    public void ASKING_THE_QUESTION_WRITES_NOTHING()
    {
        Assert.False(Directory.Exists(_home));

        FirstRunDoor.NotConfigured(_home);

        Assert.False(Directory.Exists(_home));
    }

    //leaving setup at once must create no home, no config and no roles folder. the test drives SetupRunner, since the outer composition refuses redirected stdio
    [Fact]
    public void LEAVING_THE_WIZARD_IMMEDIATELY_CREATES_NO_HOME()
    {
        Assert.False(Directory.Exists(_home));

        //the empty answer script makes the surface back out of the first screen.
        SetupRunner.Run(new SetupFlow(new SilentProbes()), new LeaveAtOnce(), _home);

        Assert.False(Directory.Exists(_home));
    }

    //a run that reaches a real write must still create the home. without this pair the guard above passes against an initialization that never writes.
    [Fact]
    public void A_WALK_THAT_WRITES_CREATES_THE_HOME_AT_THE_WRITE()
    {
        Assert.False(Directory.Exists(_home));

        var probes = new SilentProbes { Found = [Model()] };
        SetupRunner.Run(new SetupFlow(probes), new Answers([.. WalkOpening.PastEngine, "0"]), _home);

        Assert.True(Directory.Exists(_home));
        Assert.NotEmpty(Gatto.Roles.Model.ListIds(Path.Combine(_home, "models")));
    }

    private FoundModel Model()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetDirectoryName(_home)!, "models")).FullName;
        var dst = Path.Combine(dir, "tiny.gguf");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "tiny.gguf"), dst, overwrite: true);
        return new FoundModel(dst, 4_000_000_000, null);
    }

    private sealed class LeaveAtOnce : IWizardSurface
    {
        public string? Choose(WizardScreen.Choice c, Func<bool>? watch = null,
            Func<FetchTick?>? tick = null, Func<CheckTick?>? check = null) => null;
        public string? Ask(WizardScreen.Ask a) => null;
        public void Show(WizardScreen.Info i) { }
        public void End(WizardScreen.Terminal t) { }
    }

    private sealed class Answers(params string?[] answers) : IWizardSurface
    {
        private int _i;
        public string? Choose(WizardScreen.Choice c, Func<bool>? watch = null,
            Func<FetchTick?>? tick = null, Func<CheckTick?>? check = null) => Next();
        public string? Ask(WizardScreen.Ask a) => Next();
        public void Show(WizardScreen.Info i) { }
        public void End(WizardScreen.Terminal t) { }
        private string? Next() => _i < answers.Length ? answers[_i++] : null;
    }

    private sealed class SilentProbes : ISetupProbes
    {
        public IReadOnlyList<FoundModel> Found { get; init; } = [];

        public HardwareSnapshot? Hardware() => new(34359738368, 34093496320, GpuKind.Discrete, 8589934592);

        //this is display-only, so the fake returns nothing. an invented name would show hardware that no probe reported.
        public HardwareNames HardwareNames() => default;
        public string? LlamaServerPath() => @"C:\llama\llama-server.exe";
        public bool HasResolvableModel() => false;
        public ConnectProbe? ProbeServer(IReadOnlyList<int>? skip = null) => null;
        public string? GattoServingOn(int port) => null;

        public ConnectProbe? ProbeAt(string baseUrl) => null;
        public IReadOnlyList<string> Roots { get; init; } = [];
        public ScanResult Scan(string? r) => new(Found, Roots);
        public (string Path, long Bytes)? ProjectorFor(string p) => null;
        public string? ArchitectureOf(string p) => null;
        public MoveOffer? MoveOfferFor(string p) => null;
        public (Gatto.Core.Models.FitRegime Alone, Gatto.Core.Models.FitRegime WithProjector)? PairFit(string p, long b) => null;
        public TypedIdOutcome EvaluateTypedId(string id) => new TypedIdOutcome.Unreachable(false);
        public Gatto.Roles.LlamaAsset? ChooseLlamaAsset() => new("z.zip", null, "a graphics card", "Vulkan");
        //report a build here, since the real probe reads shape and build from one banner and the flow skips the engine step without it
        public Gatto.Core.Tools.ProbeResult VerifyLlamaServer(string p) =>
            new(Gatto.Core.Tools.ProbeShape.ClassicServer, p,
                Build: Gatto.Roles.LlamaAssetSteering.PinnedRelease);
        public int ContextFor(string p) => 8192;
        public AuditionCheck RunAudition(string p, string? repoId, IProgress<Gatto.Roles.Audition.AuditionProgress>? progress, CancellationToken ct) =>
            new(AuditionOutcome.Passed);
        public string? ExistingModelFor(string ggufPath) => null;
        public (Gatto.Roles.IdClash Kind, string? Id) ClashFor(string ggufPath, string? incomingRepoId) => (Gatto.Roles.IdClash.Free, null);
        //return a fixed path, so the screen shows a chosen state rather than the gatto.exe that happens to run the tests
        public string RunningFrom() => @"C:\Users\you\Downloads\gatto\gatto.exe";

        public (InstallState State, string Dir) InstallStatus() => (InstallState.Installed, @"C:\Programs\gatto");

        public string? InstalledVersion() => null;


        public string RunningVersion() => Gatto.Core.GattoVersion.String;
        public bool? UpdateConsent() => false;
        public string? ExistingEndpointFor(string baseUrl) => null;
        public string ConfigPath() => Path.Combine("home", "gatto.json");
        public ProveOutcome ProveIt(string? modelId,
            IProgress<Gatto.Roles.Audition.AuditionProgress>? progress = null) => new(true, "hi", TimeSpan.FromSeconds(2));
    }
}
