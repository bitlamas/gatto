using Gatto.Cli.Setup;
using Gatto.Core.Acquire;
using Gatto.Core.Hardware;

namespace Gatto.Tests.Setup;

//these tests run the whole wizard flow end to end. name a property another test already pins, don't assert it again, since two copies can disagree
public class AcceptanceTests
{
    private sealed class Probes : ISetupProbes
    {
        public HardwareSnapshot? Snap { get; init; } = new(34359738368, 34093496320, GpuKind.Discrete, 8589934592);
        public string? Llama { get; init; } = @"C:\llama\llama-server.exe";
        public IReadOnlyList<FoundModel> Found { get; init; } = [];
        public IReadOnlyList<ShelfRow> Rows { get; init; } = [];
        public TypedIdOutcome Typed { get; init; } = new TypedIdOutcome.Unreachable(false);
        public ConnectProbe? Server { get; init; }
        public int Ctx { get; init; } = 8192;

        public int Searches;

        public HardwareSnapshot? Hardware() => Snap;

        //names are display-only, so report none. a fake that invents a name shows hardware no probe reported
        public HardwareNames HardwareNames() => default;
        public string? ServeOnly { get; init; }
        public string? ServeOnlyGpu() => ServeOnly;
        public string? LlamaServerPath() => Llama;
        public bool HasResolvableModel() => false;
        public ConnectProbe? ProbeServer(IReadOnlyList<int>? skip = null) => Server;
        public string? GattoServingOn(int port) => null;

        public ConnectProbe? ProbeAt(string baseUrl) => null;
        public IReadOnlyList<string> Roots { get; init; } = [];
        public ScanResult Scan(string? r) => new(Found, Roots);
        public HubSearchOutcome Search(HubSearchRequest request) { Searches++; return new(Rows, null); }
        public (string Path, long Bytes)? ProjectorFor(string p) => null;
        public string? ArchitectureOf(string p) => null;
        public MoveOffer? MoveOfferFor(string p) => null;
        public (Gatto.Core.Models.FitRegime Alone, Gatto.Core.Models.FitRegime WithProjector)? PairFit(string p, long b) => null;
        public TypedIdOutcome EvaluateTypedId(string id) => Typed;
        public Gatto.Roles.LlamaAsset? ChooseLlamaAsset() => new("z.zip", null, "a graphics card", "Vulkan");
        public Gatto.Core.Tools.ProbeResult VerifyLlamaServer(string p) =>
            new(Gatto.Core.Tools.ProbeShape.ClassicServer, "d",
                //a ClassicServer answer names its build too, read from the same banner as the shape, or the flow skips the engine step
                Build: Gatto.Roles.LlamaAssetSteering.PinnedRelease);
        public int ContextFor(string p) => Ctx;
        public AuditionCheck RunAudition(string p, string? repoId, IProgress<Gatto.Roles.Audition.AuditionProgress>? progress, CancellationToken ct) =>
            new(AuditionOutcome.Passed);
        public string? ExistingModel { get; init; }
        //the id another file already holds, or null for no collision.
        public string? Colliding { get; init; }
        public string? ExistingEndpoint { get; init; }
        public string? ExistingModelFor(string ggufPath) => ExistingModel;
        public (Gatto.Roles.IdClash Kind, string? Id) ClashFor(string ggufPath, string? incomingRepoId) =>
            Colliding is { Length: > 0 }
                ? (Gatto.Roles.IdClash.DifferentModel, Colliding)
                : (Gatto.Roles.IdClash.Free, null);
        //the fake is an already-installed machine, so the flow runs after the install segment. the fixed path keeps the screen at a chosen state
        public string RunningFrom() => @"C:\Users\you\Downloads\gatto\gatto.exe";

        public (Gatto.Cli.InstallState State, string Dir) InstallStatus() =>
            (Gatto.Cli.InstallState.Installed, @"C:\Programs\gatto");

        public string? InstalledVersion() => null;


        public string RunningVersion() => Gatto.Core.GattoVersion.String;
        //consent reads as already answered, so these runs never raise the update question.
        public bool? UpdateConsent() => UpdateAnswered;
        public bool? UpdateAnswered { get; init; } = false;
        public string? ExistingEndpointFor(string baseUrl) => ExistingEndpoint;
        public string ConfigPath() => @"C:\home\gatto.json";
        public ProveOutcome ProveIt(string? modelId,
            IProgress<Gatto.Roles.Audition.AuditionProgress>? progress = null) => new(true, "hi", TimeSpan.FromSeconds(2));
    }

    private static FoundModel Local(string p) => new(p, 4_000_000_000, null);

    //the GPU the fit counted rides the model intent to the writer, which pins the profile to it
    [Theory]
    [InlineData("AMD Radeon(TM) 8060S Graphics")]
    [InlineData(null)]
    public void THE_MODEL_INTENT_CARRIES_THE_GPU_THE_FIT_COUNTED(string? gpu)
    {
        var flow = new SetupFlow(new Probes { Rows = [], Found = [Local(@"D:\m\qwen.gguf")], ServeOnly = gpu });
        flow.StartPastEngine();
        flow.Answer("0");

        Assert.Equal(gpu, flow.Writes.CreateModel!.PinGpu);
    }

    [Fact]
    public void ACCEPTANCE_the_offline_wizard_FINISHES()
    {
        //with every hub call returning nothing, the run still completes through discovery and writes a usable config
        var flow = new SetupFlow(new Probes { Rows = [], Found = [Local(@"D:\m\qwen.gguf")] });
        flow.StartPastEngine();
        flow.Answer("0");

        //assert the pending intent before ResumeAfterWrites consumes it (this test stands in for the runner at each pause)
        Assert.NotNull(flow.Writes.CreateModel);
        flow.ResumeAfterWrites("qwen");        //the id the writer produced, so the flow resumes at the audition offer

        //skip starts the one load and the step ends at its arrival. the test waits for the load and answers the arrival, then the default is an intent
        Assert.Equal(SetupFlow.AuditionWaitingKey,
            Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.Skip)).Key);
        Assert.True(SpinWait.SpinUntil(flow.PollWatch, TimeSpan.FromSeconds(10)), "the load never finished");
        flow.Answer(SetupFlow.Landed);
        flow.Answer(SetupFlow.AnswersNext);
        Assert.NotNull(flow.Writes.DefaultModel);
        flow.ResumeAfterWrites(null);          //the default write has landed, so the flow resumes at the prove-it check

        //prove-it runs on into the done step and ends at the consent pause, where the summary draws from what the install question did
        flow.ResumeAfterWrites(null);
        var end = Assert.IsType<WizardScreen.Terminal>(flow.Answer(SetupFlow.Finish));

        Assert.True(end.Success);
        Assert.True(flow.Writes.IsEmpty);      //nothing is left pending, because every intent reached a writer.
    }

    [Fact]
    public void ACCEPTANCE_an_empty_allowlist_still_evaluates_a_TYPED_id_end_to_end()
    {
        //the allowlist shapes browsing only, so an empty one must still evaluate a typed id
        var flow = new SetupFlow(new Probes
        {
            Rows = [],
            Typed = new TypedIdOutcome.Ok(new ShelfRow(
                "someone/model", "someone", new HubQuant("m.gguf", 4_000_000_000, null),
                Gatto.Core.Models.FitRegime.FitsGpu, 32768, false, null, 1, false)),
        });
        flow.StartPastEngine();
        flow.Answer(SetupFlow.TypeAnId);
        flow.Answer("someone/model");

        Assert.Equal("someone/model", flow.Picked!.RepoId);
    }

    [Fact]
    public void ACCEPTANCE_nothing_to_offer_is_reachable_and_carries_THIS_MACHINES_numbers()
    {
        //don't assert that nothing fits (zero rows also means every org failed). the screen states the machine's numbers and two ways forward with no cause named
        var flow = new SetupFlow(new Probes { Rows = [], Found = [] });
        //the fake machine has an engine, so the flow crosses the engine step before the shelf.
        var screen = Assert.IsType<WizardScreen.Choice>(flow.StartPastEngine());

        //assert the presence of the machine's numbers rather than their values (a figure that reads as os-visible memory would be the defect)
        Assert.Contains("This machine:", string.Join(" ", screen.BodyRows!), StringComparison.Ordinal);
        Assert.Contains(screen.Options, o => o.Key == SetupFlow.Elsewhere);
        Assert.Contains(screen.Options, o => o.Key == SetupFlow.TypeAnId);
    }

    [Fact]
    public void ACCEPTANCE_the_wizard_CANNOT_MANUFACTURE_A_NULL_BUDGET()
    {
        //the Context field is non-nullable, so the risk is a manufactured zero. the connect fork has its own case, and this one is the model write
        var flow = new SetupFlow(new Probes { Found = [Local(@"D:\m\qwen.gguf")], Ctx = 16384 });
        flow.StartPastEngine();
        flow.Answer("0");

        Assert.True(flow.Writes.CreateModel!.Context > 0);
        Assert.Equal(16384, flow.Writes.CreateModel.Context);
    }

    //the /model add entry re-enters the model segment of the same wizard, opening there with no welcome screen or install question
    [Fact]
    public void ACCEPTANCE_a_re_run_LANDS_ON_THE_MODEL_SEGMENT_not_at_the_fork()
    {
        var flow = new SetupFlow(new Probes());

        var first = flow.StartAtModelSegment();

        //the entry screen depends on what discovery found, so assert one of the model segment's own keys (the picker or the search)
        var screen = Assert.IsType<WizardScreen.Choice>(first);
        Assert.Contains(screen.Key, new[] { SetupFlow.DiscoveredKey, SetupFlow.SearchKey });

        //a user who typed /model add asked for one thing, so the wizard must not open with a welcome screen or the install question
        Assert.DoesNotContain(flow.Emitted, s => s is WizardScreen.Choice c
            && (c.Key == SetupFlow.WelcomeKey || c.Key == SetupFlow.InstallKey));
        //the fork screen must not be emitted at all, so this reads the emitted screens rather than the flow's decisions
        Assert.DoesNotContain(flow.Emitted, s => ScreenKey.Of(s) == "fork");
    }

    [Fact]
    public void ACCEPTANCE_a_re_run_with_a_working_config_walks_the_ticks()
    {
        //assert that satisfied segments were skipped, since the screens have their own tests
        var probes = new ProbesWithModel();
        var flow = new SetupFlow(probes);
        flow.StartPastOpening();

        //a satisfied segment asks nothing, so assert the questions' absence (a confirmation line would only say what the next screen shows)
        Assert.DoesNotContain(flow.Emitted, s => s is WizardScreen.Ask a && a.Key == SetupFlow.SteerKey);
        Assert.DoesNotContain(flow.TakeNarration(), i => i.Key is "segment.llama" or "segment.model");
    }

    private sealed class ProbesWithModel : ISetupProbes
    {
        //consent reads as already answered, so these runs never raise the update question.
        public bool? UpdateConsent() => false;
        //the fake reports a fresh home, so no model id collides. the fixed exe path keeps the screen at a chosen state
        public string RunningFrom() => @"C:\Users\you\Downloads\gatto\gatto.exe";

        public (Gatto.Cli.InstallState State, string Dir) InstallStatus() =>
            (Gatto.Cli.InstallState.Installed, @"C:\Programs\gatto");

        public string? InstalledVersion() => null;


        public string RunningVersion() => Gatto.Core.GattoVersion.String;
        public (Gatto.Roles.IdClash Kind, string? Id) ClashFor(string ggufPath, string? incomingRepoId) => (Gatto.Roles.IdClash.Free, null);
        public string ConfigPath() => @"C:\home\gatto.json";
        public HardwareSnapshot? Hardware() => new(34359738368, 34093496320, GpuKind.Discrete, 8589934592);

        //names are display-only, so report none. a fake that invents a name shows hardware no probe reported
        public HardwareNames HardwareNames() => default;
        public string? LlamaServerPath() => @"C:\llama\llama-server.exe";
        public bool HasResolvableModel() => true;
        public ConnectProbe? ProbeServer(IReadOnlyList<int>? skip = null) => null;
        public string? GattoServingOn(int port) => null;

        public ConnectProbe? ProbeAt(string baseUrl) => null;
        public IReadOnlyList<string> Roots { get; init; } = [];
        public ScanResult Scan(string? r) => new([new(@"D:\m\a.gguf", 1, null)], Roots);
        public HubSearchOutcome Search(HubSearchRequest request) => new([], null);
        public (string Path, long Bytes)? ProjectorFor(string p) => null;
        public string? ArchitectureOf(string p) => null;
        public MoveOffer? MoveOfferFor(string p) => null;
        public (Gatto.Core.Models.FitRegime Alone, Gatto.Core.Models.FitRegime WithProjector)? PairFit(string p, long b) => null;
        public TypedIdOutcome EvaluateTypedId(string id) => new TypedIdOutcome.Malformed();
        public Gatto.Roles.LlamaAsset? ChooseLlamaAsset() => null;
        public Gatto.Core.Tools.ProbeResult VerifyLlamaServer(string p) =>
            new(Gatto.Core.Tools.ProbeShape.ClassicServer, "d",
                //a ClassicServer answer names its build too, read from the same banner as the shape, or the flow skips the engine step
                Build: Gatto.Roles.LlamaAssetSteering.PinnedRelease);
        public int ContextFor(string p) => 4096;
        public AuditionCheck RunAudition(string p, string? repoId, IProgress<Gatto.Roles.Audition.AuditionProgress>? progress, CancellationToken ct) =>
            new(AuditionOutcome.Passed);
        public string? ExistingModel { get; init; }
        //the id another file already holds, or null for no collision.
        public string? Colliding { get; init; }
        public string? ExistingEndpoint { get; init; }
        public string? ExistingModelFor(string ggufPath) => ExistingModel;
        public string? ExistingEndpointFor(string baseUrl) => ExistingEndpoint;
        public ProveOutcome ProveIt(string? modelId,
            IProgress<Gatto.Roles.Audition.AuditionProgress>? progress = null) => new(true, "hi", TimeSpan.Zero);
    }
}
