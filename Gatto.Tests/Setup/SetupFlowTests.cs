using Gatto.Tests.Fakes;
using Gatto.Cli.Setup;
using Gatto.Core.Hardware;
using Gatto.Terminal;

namespace Gatto.Tests.Setup;

//the flow is a pure state machine, so these tests drive it with screens in and out. assertions name screen keys, since copy changes and the flow does not
public class SetupFlowTests
{
    //the only faked seam. it records what was asked, so a test can prove a probe never ran.
    private sealed class FakeProbes : ISetupProbes
    {
        public HardwareSnapshot? Snapshot { get; init; } =
            new(InstalledBytes: 34359738368, OsVisibleBytes: 34093496320, GraphicsKind: GpuKind.Discrete,
                GraphicsMemoryBytes: 8589934592);
        public string? Llama { get; init; }
        public bool Model { get; init; }
        public Gatto.Core.Acquire.ConnectProbe? Server { get; init; }
        //settable, so a walk can add found models while it runs.
        public IReadOnlyList<Gatto.Core.Acquire.FoundModel> Found { get; set; } = [];
        public List<string?> ScanRoots { get; } = [];
        public IReadOnlyList<Gatto.Core.Acquire.ShelfRow> Rows { get; init; } = [];
        public TypedIdOutcome TypedId { get; init; } = new TypedIdOutcome.Unreachable(false);
        public List<string> TypedIds { get; } = [];
        public AuditionOutcome Audition { get; init; } = AuditionOutcome.Passed;
        public Gatto.Roles.LlamaAsset? Asset { get; init; } =
            new("llama-b11071-bin-win-cuda-12.4-x64.zip", "cudart-llama-bin-win-cu12.4-x64.zip",
                "an NVIDIA adapter", "CUDA");
        public Gatto.Core.Tools.ProbeShape Shape { get; init; } = Gatto.Core.Tools.ProbeShape.ClassicServer;
        //the build the engine reports, when its shape reports one. null falls back to LlamaAssetSteering.PinnedRelease, the commonest real machine
        public string? Build { get; init; }

        //the swept engine's shape, when it differs from what a typed path gets. null means the two are the same
        public Gatto.Core.Tools.ProbeShape? EngineShape { get; init; }

        //what a fetch of the pinned release would be. null means the fixture says nothing about fetching, so the walk reaches the steering screen
        public EngineFetchOffer? Offer { get; init; }

        public EngineFetchOffer? EngineOffer() => Offer;
        public int Context { get; init; } = 8192;
        public Gatto.Core.Acquire.ProveOutcome Proof { get; init; } =
            new(true, "hello", TimeSpan.FromSeconds(3));
        public List<string> Auditioned { get; } = [];

        public List<string> Asked { get; } = [];

        public HardwareSnapshot? Hardware() { Asked.Add(nameof(Hardware)); return Snapshot; }

        //display-only, and the default invents nothing. a name here would show hardware that no probe reported.
        public HardwareNames HardwareNames() => default;
        public string? LlamaServerPath() { Asked.Add(nameof(LlamaServerPath)); return Llama; }
        public bool HasResolvableModel() { Asked.Add(nameof(HasResolvableModel)); return Model; }
        //the skip requests the flow made, in order. recording them is the only evidence of what was asked, since a fake would pass every screen assertion anyway
        public List<IReadOnlyList<int>> ProbeSkips { get; } = [];

        //what the second probe request answers. null keeps the shipped behaviour.
        public Gatto.Core.Acquire.ConnectProbe? ServerAfterSkip { get; init; }

        //the port and model of the server gatto owns. null means nothing of ours is up, which is also what a dead process answers at the live probe.
        public (int Port, string Model)? Own { get; init; }

        public Gatto.Core.Acquire.ConnectProbe? ProbeServer(IReadOnlyList<int>? skip = null)
        {
            Asked.Add(nameof(ProbeServer));
            ProbeSkips.Add([.. skip ?? []]);
            return skip is { Count: > 0 } ? ServerAfterSkip : Server;
        }

        public string? GattoServingOn(int port) =>
            Own is { } own && own.Port == port ? own.Model : null;

        //records every address the flow asked about, which is the only proof it probed what the user typed
        public List<string> ProbedAt { get; } = [];

        public Gatto.Core.Acquire.ConnectProbe? AtAddress { get; init; }

        public Gatto.Core.Acquire.ConnectProbe? ProbeAt(string baseUrl)
        {
            ProbedAt.Add(baseUrl);
            return AtAddress;
        }

        //the folders the sweep reports it looked in, where ScanRoots holds the arguments the fake was called with
        public IReadOnlyList<string> SweptRoots { get; init; } = [];

        public ScanResult Scan(string? extraRoot)
        {
            Asked.Add(nameof(Scan));
            ScanRoots.Add(extraRoot);
            return new ScanResult(Found, SweptRoots);
        }

        //the cause an empty result reports, when the fixture has one. null keeps the unevidenced branch that claims nothing
        public Gatto.Core.Acquire.HubSearchCause? SearchOutcome { get; init; }

        //which view the flow asked for, in order. the list is the oracle for the widen test, since wider copy alone cannot show the wider view was requested
        public List<Gatto.Core.Acquire.HubSearchView> Views { get; } = [];

        //the publisher a curated search narrowed to. null means the broadened view, which is also what a missing publisher reports
        public string? CuratedPublisher { get; init; } = "somepublisher";

        //the search terms the flow asked for, in order. a term belongs to the request, so the returned rows cannot show it.
        public readonly List<string?> Searches = [];

        public Gatto.Core.Acquire.HubSearchOutcome Search(Gatto.Core.Acquire.HubSearchRequest request)
        {
            Asked.Add(nameof(Search));
            Views.Add(request.View);
            Searches.Add(request.Search);
            return new Gatto.Core.Acquire.HubSearchOutcome(
                Rows, Rows.Count > 0 ? null : SearchOutcome,
                request.View == Gatto.Core.Acquire.HubSearchView.Curated ? CuratedPublisher : null,
                HiddenOlder: HiddenOlder, HiddenNewer: HiddenNewer, HiddenByFamily: HiddenByFamily);
        }

        //rows the tier filter set aside as older, so a test can drive the count line's buckets through the flow rather than the renderer
        public int HiddenOlder { get; init; }
        public int HiddenNewer { get; init; }
        //rows the family filter narrowed away, so a test can assert the count reaches the drawn shelf rather than the search outcome
        public int HiddenByFamily { get; init; }

        //the projector file beside the chosen model, when the fixture sets one.
        public (string Path, long Bytes)? Projector { get; init; }

        //the architecture the model file reports. null is the default and means unreadable, so a test that wants the custom-build branch must set it
        public string? Architecture { get; init; }

        //the offer to move a model out of Downloads. null is the default, since most models are not there, so a test that wants this screen must set it
        public MoveOffer? Move { get; init; }
        //the fit regimes for the model alone and with its projector. null means the machine could not be read, where the screen says nothing about fit
        public (Gatto.Core.Models.FitRegime Alone, Gatto.Core.Models.FitRegime WithProjector)? Pair { get; init; }

        public (string Path, long Bytes)? ProjectorFor(string ggufPath) { Asked.Add(nameof(ProjectorFor)); return Projector; }
        public string? ArchitectureOf(string ggufPath) { Asked.Add(nameof(ArchitectureOf)); return Architecture; }
        public MoveOffer? MoveOfferFor(string ggufPath) { Asked.Add(nameof(MoveOfferFor)); return Move; }
        public (Gatto.Core.Models.FitRegime Alone, Gatto.Core.Models.FitRegime WithProjector)? PairFit(string ggufPath, long projectorBytes) => Pair;

        public TypedIdOutcome EvaluateTypedId(string repoId)
        {
            Asked.Add(nameof(EvaluateTypedId));
            TypedIds.Add(repoId);
            return TypedId;
        }

        public Gatto.Roles.LlamaAsset? ChooseLlamaAsset() { Asked.Add(nameof(ChooseLlamaAsset)); return Asset; }

        //whether the Visual C++ runtime is missing, so a test can drive that case. false is the default, since a machine that has it is the ordinary one
        public bool VcRuntimeAbsent { get; init; }

        public Gatto.Core.Tools.ProbeResult VerifyLlamaServer(string exePath)
        {
            Asked.Add(nameof(VerifyLlamaServer));
            //derive the build from the shape, since production reads both from one banner. the swept engine and a typed path get separate shapes
            var shape = exePath == Llama && Llama is not null ? EngineShape ?? Shape : Shape;
            return new Gatto.Core.Tools.ProbeResult(shape, "detail", VcRuntimeAbsent: VcRuntimeAbsent,
                Build: shape == Gatto.Core.Tools.ProbeShape.ClassicServer
                    ? Build ?? Gatto.Roles.LlamaAssetSteering.PinnedRelease
                    : null);
        }

        public string ConfigPath() => @"C:\home\gatto.json";

        public Gatto.Core.Acquire.ProveOutcome ProveIt(string? modelId,
            IProgress<Gatto.Roles.Audition.AuditionProgress>? progress = null) { Asked.Add(nameof(ProveIt)); return Proof; }

        public int ContextFor(string ggufPath) { Asked.Add(nameof(ContextFor)); return Context; }

        //the measurements from the check. null is a real state, an audition that ran but reported nothing, and the completion screen must survive it.
        public AuditionFacts? Facts { get; init; }

        //the model id already present on the machine, and the endpoint already set to a URL. null on both means a fresh machine, which every older test here assumes.
        public string? ExistingModel { get; init; }
        //the id that a different model file already holds. null means no collision.
        public string? Colliding { get; init; }
        public string? ExistingEndpoint { get; init; }
        public List<string> AskedModelFor { get; } = [];

        public string? ExistingModelFor(string ggufPath) { AskedModelFor.Add(ggufPath); return ExistingModel; }
        //defaults to DifferentModel, so a fixture that sets nothing still means two models to choose between
        public Gatto.Roles.IdClash ClashKind { get; init; } = Gatto.Roles.IdClash.DifferentModel;

        //the id of the model whose weights are loaded, or null for none. null is the default, since a fresh machine serves nothing.
        public string? Loaded { get; init; }

        public string? LoadedModelId() => Loaded;

        public (Gatto.Roles.IdClash Kind, string? Id) ClashFor(string ggufPath, string? incomingRepoId) =>
            Colliding is { Length: > 0 }
                ? (ClashKind, Colliding)
                : (Gatto.Roles.IdClash.Free, null);
        //the fakes assume gatto is already installed, so each walk starts after the install segment.

        //installed by default, so a test that sets nothing starts after the install segment
        public Gatto.Cli.InstallState Install { get; init; } = Gatto.Cli.InstallState.Installed;
        //the running location the screen reports. a fixed path keeps the render the same, where the real executable would change it
        public string RunningFrom() => @"C:\Users\you\Downloads\gatto\gatto.exe";

        public (Gatto.Cli.InstallState State, string Dir) InstallStatus() => (Install, @"C:\Programs\gatto");

        public string? Installed;
        public string? InstalledVersion() => Installed;

        public string RunningVersion() => Gatto.Core.GattoVersion.String;
        public string? ExistingEndpointFor(string baseUrl) => ExistingEndpoint;
        //reports the update question as already answered, so these walks never raise it.
        public bool? UpdateConsent() => UpdateAnswered;
        public bool? UpdateAnswered { get; init; } = false;

        //records the repository the flow held at each request. null is valid for discovery and adoption, so a test must name the case it drives
        public List<string?> AuditionedRepo { get; } = [];

        public AuditionCheck RunAudition(string modelId, string? repoId, IProgress<Gatto.Roles.Audition.AuditionProgress>? progress, CancellationToken ct)
        {
            Asked.Add(nameof(RunAudition));
            Auditioned.Add(modelId);
            AuditionedRepo.Add(repoId);
            return new AuditionCheck(Audition, Facts);
        }
    }

    private static Gatto.Core.Acquire.ShelfRow Row(
        string repoId, Gatto.Core.Models.FitRegime fit = Gatto.Core.Models.FitRegime.FitsGpu,
        bool vision = false, Gatto.Core.Acquire.Badge? badge = null, long downloads = 100,
        string? structure = null) =>
        new(repoId, repoId.Split('/')[0],
            new Gatto.Core.Acquire.HubQuant("model-Q4_K_M.gguf", 4_000_000_000, null),
            fit, NativeCtx: 32768, Vision: vision, Badge: badge, Downloads: downloads, Gated: false,
            Structure: structure);

    private static SetupFlow AtTypedDoor(FakeProbes probes)
    {
        var flow = AtSearch(probes);
        flow.Answer(SetupFlow.TypeAnId);
        return flow;
    }

    //typed words are a search by default, since most of what a person types is one. the term must reach the request
    [Fact]
    public void WORDS_TYPED_INTO_THE_DOOR_BECOME_A_SEARCH()
    {
        var probes = new FakeProbes
        {
            Llama = @"C:\llama\llama-server.exe",
            Rows = [Row("org/a")],
        };
        AtTypedDoor(probes).Answer("gemma 4");

        //the walk already searched on the way in, so assert on the last entry.
        Assert.Equal("gemma 4", probes.Searches[^1]);
        //a search is not a lookup, so the typed words must never reach the id request.
        Assert.DoesNotContain("gemma 4", probes.TypedIds);
    }

    //a repo id still goes to the id lookup. a new grammar must not take that away
    [Fact]
    public void A_REPO_ID_STILL_GOES_TO_THE_ID_DOOR()
    {
        var probes = new FakeProbes
        {
            Llama = @"C:\llama\llama-server.exe",
            TypedId = new TypedIdOutcome.NoUsableQuant(),
        };
        AtTypedDoor(probes).Answer("unsloth/gemma-4-e4b-it-GGUF");

        Assert.Contains("unsloth/gemma-4-e4b-it-GGUF", probes.TypedIds);
    }

    //a pasted link resolves the repository it names. the lookup receives the parsed id, so the user need not retype it as org/model
    [Theory]
    [InlineData("https://huggingface.co/unsloth/gemma-4-e4b-it-GGUF")]
    [InlineData("https://huggingface.co/unsloth/gemma-4-e4b-it-GGUF/tree/main")]
    [InlineData("https://huggingface.co/unsloth/gemma-4-e4b-it-GGUF/blob/main/m-Q4_K_M.gguf")]
    public void A_PASTED_LINK_RESOLVES_THE_REPO_IT_NAMES(string pasted)
    {
        var probes = new FakeProbes
        {
            Llama = @"C:\llama\llama-server.exe",
            TypedId = new TypedIdOutcome.NoUsableQuant(),
        };
        AtTypedDoor(probes).Answer(pasted);

        Assert.Contains("unsloth/gemma-4-e4b-it-GGUF", probes.TypedIds);
        //the URL itself must never reach the lookup, since the lookup would refuse it as malformed.
        Assert.DoesNotContain(pasted, probes.TypedIds);
    }

    //a collection names many models and gatto cannot pick one, so the answer says what the link is. it must never blame the user for pasting it
    [Fact]
    public void A_COLLECTION_LINK_IS_ANSWERED_WITHOUT_BLAME()
    {
        var probes = new FakeProbes { Llama = @"C:\llama\llama-server.exe" };
        var again = Assert.IsType<WizardScreen.Ask>(
            AtTypedDoor(probes).Answer("https://huggingface.co/collections/unsloth/gemma-4-68a1"));

        Assert.Equal(SetupFlow.TypedIdKey, again.Key);
        Assert.Contains("collection", again.Label!, StringComparison.OrdinalIgnoreCase);
        //gatto knows a collection is unanswerable without asking, so no request goes out.
        Assert.Empty(probes.TypedIds);
        //the link is valid, so the sentence must not blame the user.
        Assert.DoesNotContain("doesn't look like", again.Label!, StringComparison.OrdinalIgnoreCase);
    }

    //an empty search must speak about the search, in the user's own words, and name the provider. the browse sentences would both be false here
    [Fact]
    public void AN_EMPTY_SEARCH_SAYS_SO_ABOUT_THE_SEARCH()
    {
        var probes = new FakeProbes { Llama = @"C:\llama\llama-server.exe", Rows = [] };
        var screen = Assert.IsType<WizardScreen.Choice>(
            AtTypedDoor(probes).Answer("wharrgarbl"));

        //this walk never reached a shelf, so the sentence is the screen's heading, since no frame holds the block
        var said = screen.Question!;

        Assert.Contains("wharrgarbl", said, StringComparison.Ordinal);
        Assert.Contains("Hugging Face", said, StringComparison.Ordinal);

        //and neither browse sentence may appear.
        Assert.DoesNotContain("fit this machine", said, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("couldn't reach", said, StringComparison.OrdinalIgnoreCase);
    }

    //an empty browse keeps the browse sentence. without this, the guard above would pass on a screen that always quotes a search.
    [Fact]
    public void AN_EMPTY_BROWSE_KEEPS_THE_BROWSE_SENTENCE()
    {
        var probes = new FakeProbes { Llama = @"C:\llama\llama-server.exe", Rows = [] };
        var screen = Assert.IsType<WizardScreen.Choice>(AtSearch(probes).Emitted[^1]);

        Assert.DoesNotContain("Nothing on Hugging Face answers", screen.Question!, StringComparison.Ordinal);
    }

    //the lookups' two refusals map to two outcomes, and the mapping is pure on the outcome type, so a test can reach it. a priced row wins over both flags
    [Fact]
    public void THE_LOOKUPS_TWO_REFUSALS_EARN_TWO_DIFFERENT_OUTCOMES()
    {
        var row = Row("o/m");

        Assert.IsType<TypedIdOutcome.Ok>(TypedIdOutcome.For(new Gatto.Core.Acquire.HubLookup(row, false)));
        Assert.IsType<TypedIdOutcome.NoWeights>(TypedIdOutcome.For(new Gatto.Core.Acquire.HubLookup(null, true)));
        Assert.IsType<TypedIdOutcome.NoUsableQuant>(TypedIdOutcome.For(new Gatto.Core.Acquire.HubLookup(null, false)));

        //a priced row is never a refusal, even when a flag says otherwise.
        Assert.IsType<TypedIdOutcome.Ok>(TypedIdOutcome.For(new Gatto.Core.Acquire.HubLookup(row, true)));
    }

    //the screen names what the repo is and must accept another typed answer, since a missing dispatch arm throws here while every render check passes
    [Fact]
    public void THE_NO_WEIGHTS_DOOR_SAYS_WHAT_THE_REPO_IS_AND_TAKES_ANOTHER_TRY()
    {
        var flow = AtSearch(new FakeProbes
        {
            Llama = @"C:\llama\llama-server.exe",
            TypedId = new TypedIdOutcome.NoWeights(),
        });
        flow.Answer(SetupFlow.TypeAnId);
        var again = Assert.IsType<WizardScreen.Ask>(flow.Answer("ggml-org/vocabs"));

        Assert.Equal(SetupFlow.TypedIdKey, again.Key);
        Assert.Contains("publishes no weights", again.Label!, StringComparison.OrdinalIgnoreCase);

        //a repo that publishes no weights must not borrow another state's words, since the smaller-model line gives no next step here
        Assert.DoesNotContain("smaller model", again.Label!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("look that one up", again.Label!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("doesn't look like", again.Label!, StringComparison.OrdinalIgnoreCase);

        //typing again is the point of a re-prompt, so this answers the screen, since a state with no dispatch arm throws here
        Assert.NotNull(flow.Answer("unsloth/gemma-4-e4b-it-GGUF"));
    }

    //a real search must pass both generation counts into the view, since every count-line test builds its ShelfView by hand
    [Fact]
    public void THE_LIVE_SHELF_CARRIES_BOTH_GENERATION_COUNTS()
    {
        var probes = new FakeProbes
        {
            Llama = @"C:\llama\llama-server.exe",
            Rows = [Row("org/a"), Row("org/b")],
            HiddenOlder = 4,
            HiddenNewer = 2,
        };
        var screen = Assert.IsType<WizardScreen.Choice>(new SetupFlow(probes).StartPastEngine());

        var line = Gatto.Cli.Setup.Tui.Shelf.CountLine(screen.Shelf!, glyphs: GlyphSet.Unicode);

        Assert.Contains("2 newer", line, StringComparison.Ordinal);
        Assert.Contains("4 older", line, StringComparison.Ordinal);
    }

    //the family count crosses three hops from the search to the shelf. it asserts the composed line, since reading a number back out proves nothing
    [Fact]
    public void THE_LIVE_SHELF_CARRIES_THE_FAMILY_COUNT()
    {
        var probes = new FakeProbes
        {
            Llama = @"C:\llama\llama-server.exe",
            Rows = [Row("org/a"), Row("org/b")],
            HiddenByFamily = 7,
        };
        var screen = Assert.IsType<WizardScreen.Choice>(new SetupFlow(probes).StartPastEngine());

        Assert.Contains("7 in other families", Gatto.Cli.Setup.Tui.Shelf.CountLine(screen.Shelf!, glyphs: GlyphSet.Unicode),
            StringComparison.Ordinal);
    }

    //the live flow must hand the row's facts along, since every structure test drove the renderer with its own. assert the composed row
    [Fact]
    public void THE_LIVE_SHELF_RENDERS_A_ROWS_STRUCTURE()
    {
        var probes = new FakeProbes
        {
            Llama = @"C:\llama\llama-server.exe",
            Rows = [Row("org/a", structure: "MoE A4B"), Row("org/b", structure: "dense")],
        };
        var screen = Assert.IsType<WizardScreen.Choice>(new SetupFlow(probes).StartPastEngine());

        //render through the TUI widget, since the structure cell exists only there. the plain face's labels show no such cell
        var table = string.Join("\n", Gatto.Cli.Setup.Tui.Shelf
            .Table(screen.Shelf!, row: -1, focused: false, glyphs: GlyphSet.Unicode).Select(r => r.Text));

        Assert.Contains("MoE A4B", table, StringComparison.Ordinal);
        Assert.Contains("dense", table, StringComparison.Ordinal);
    }

    private static T Screen<T>(SetupFlow f, string key) where T : WizardScreen =>
        Assert.IsType<T>(f.Emitted.Single(s => Key(s) == key));

    private static string Key(WizardScreen s) => ScreenKey.Of(s);

    [Fact]
    public void THE_WELCOME_IS_FIRST_AND_IT_CARRIES_THE_HARDWARE_LINE()
    {
        //the user learns gatto's reading of the machine before choosing a path, so the line is a body row of the first screen
        var flow = new SetupFlow(new FakeProbes());

        var welcome = Assert.IsType<WizardScreen.Choice>(flow.Start());
        Assert.Equal(SetupFlow.WelcomeKey, welcome.Key);
        Assert.Contains(welcome.BodyRows!, r => r.Text.Contains("This machine", StringComparison.Ordinal));

        //the machine numbers have a screen of their own, so check both screens. the fork stays the first screen that asks the user to choose
        var machine = flow.Answer(SetupFlow.WelcomeGo);
        Assert.Equal(SetupFlow.MachineKey, Key(machine));
        Assert.Equal(PastTheOpening, Key(flow.Answer(SetupFlow.MachineNext)));
    }

    [Fact]
    public void THE_API_ROW_IS_GONE_AND_NO_ROW_IS_UNPICKABLE()
    {
        //no production caller may construct a disabled option. the widget keeps the flag, so the matcher looks for a construction
        static bool Constructs(string text)
        {
            for (var at = text.IndexOf(Named, StringComparison.Ordinal); at >= 0;
                 at = text.IndexOf(Named, at + 1, StringComparison.Ordinal))
                //a pattern match writes '{ ' before the name, a named argument doesn't
                if (at < 2 || text[(at - 2)..at] != "{ ") return true;
            return false;
        }

        //check a known match and a known miss before scanning the tree, so a green shows the matcher can tell them apart
        Assert.True(Constructs("new ChoiceOption(k, l, " + Named + ")"), "the matcher misses a caller");
        Assert.False(Constructs("opt is { " + Named + " }"), "the matcher fires on a pattern match");

        var offenders = Gatto.Tests.Census.SourceTree.ProductionFiles()
            .Where(f => Constructs(File.ReadAllText(f)))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(offenders.Count == 0,
            "a row in an option list is a promise that it IS a choice, and a disabled row breaks it "
            + "then needs chrome to un-break it. Found a production caller in:\n  "
            + string.Join("\n  ", offenders));
    }

    [Fact]
    public void THE_STEERING_SCREEN_ENDS_WITH_WHAT_TO_DO_not_with_a_spare_link()
    {
        //the last row a person reads must be what to do next (the all-builds link isn't it). assert the rule rather than a stored position
        var flow = new SetupFlow(new FakeProbes { Llama = null, Model = true });
        var steer = Assert.IsType<WizardScreen.Choice>(flow.StartPastOpening());
        var rows = steer.BodyRows!.Select(r => r.Text).ToList();

        Assert.StartsWith("Once you're done", rows[^1], StringComparison.Ordinal);
        Assert.Contains(rows, r => r.StartsWith("All builds", StringComparison.Ordinal));
        Assert.True(
            rows.FindIndex(r => r.StartsWith("All builds", StringComparison.Ordinal)) < rows.Count - 1,
            "the fallback link must not be the last thing read");

        //the screen must not send the user away to come back later
        Assert.DoesNotContain(rows, r => r.StartsWith("Come back", StringComparison.Ordinal));
    }

    //the screen asks for the path itself, so assert the title, the typed area and the body together
    [Fact]
    public void THE_STEERING_SCREEN_ASKS_FOR_THE_PATH_ITSELF_rather_than_for_a_keystroke()
    {
        var flow = AtSteering(new FakeProbes { Llama = null, Shape = Gatto.Core.Tools.ProbeShape.ClassicServer });
        var steer = Assert.IsType<WizardScreen.Choice>(flow.Emitted[^1]);

        //the title names the step, the body asks for the path, and the connect row shares the screen with the typed area
        Assert.Equal(SetupFlow.EngineTitle, steer.Question);
        Assert.Equal(SetupFlow.LlamaDoorPlaceholderOf(Gatto.Terminal.GlyphSet.Unicode), steer.Door);
        Assert.Contains("type the path below", string.Join(" ", steer.BodyRows!), StringComparison.Ordinal);

        //check for the removed step by name, so it cannot come back unnoticed
        var body = string.Join(" ", steer.BodyRows!);
        Assert.DoesNotContain("gatto needs llama.cpp to run models", body, StringComparison.Ordinal);
        Assert.DoesNotContain("let me point at it", body, StringComparison.Ordinal);

        //the path must reach verification, so assert the narration and the write (which screen follows depends on the rest of the machine)
        flow.Answer(ShelfControls.TypedAnswer(@"C:\llama\llama-server.exe"));
        //the narration holds the verification result, and the screen doesn't say it again
        Assert.Contains(flow.TakeNarration(), i => i.Key == "llama.ok");
        Assert.Equal(@"C:\llama\llama-server.exe", flow.Writes.LlamaServer);
    }

    //the question holds the ask and the body the explanation, so this checks the facts and leaves the drawn order to a render test
    [Fact]
    public void THE_AUDITION_OFFER_ASKS_BEFORE_IT_EXPLAINS_and_recommends_the_check()
    {
        var flow = AtAuditionOffer();
        var offer = Assert.IsType<WizardScreen.Choice>(flow.Emitted[^1]);
        var rows = offer.BodyRows!.Select(r => r.Text).ToList();

        Assert.Equal("Can it run commands and edit files?", offer.Question);
        Assert.Contains(rows, r => r.Contains("five short tasks", StringComparison.Ordinal));
        //the body must not repeat the ask, since one fact gets one voice.
        Assert.DoesNotContain(rows, r => r.Contains("Would you like", StringComparison.Ordinal));

        //gatto recommends the yes answer, since a neutral pair hides the answer from the user least able to judge
        Assert.True(offer.Options.Single(o => o.Key == SetupFlow.Yes).Recommended);
        Assert.False(offer.Options.Single(o => o.Key == SetupFlow.Skip).Recommended);

        //the duration reads in words, so no screen quotes a fixed number
        Assert.Contains("a minute or two", string.Join(" ", rows), StringComparison.Ordinal);
    }

    //this screen reports the verdict, and the measured values stay on the earlier screen
    [Fact]
    public void THE_COMPLETION_SCREEN_SHOWS_WHAT_WAS_MEASURED()
    {
        var flow = AtAuditionOffer(new AuditionFacts("Q6_K", "defaults", "template default", 49.4));
        var done = Assert.IsType<WizardScreen.Choice>(PastThePass(flow));
        var body = string.Join("\n", done.BodyRows!.Select(r => r.Text));

        Assert.Equal(SetupFlow.SummaryKey, done.Key);
        //the verdict row states the outcome plainly, and no headline prose states it again
        Assert.Contains("check", body, StringComparison.Ordinal);
        Assert.Contains("passed, it ran commands and edited files", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Congratulations", body, StringComparison.Ordinal);

        //check both halves of the model stamp, since one half alone would pass
        Assert.DoesNotContain("quant", body, StringComparison.Ordinal);
        Assert.DoesNotContain("tok/s", body, StringComparison.Ordinal);

        //the config path is the accented fact this screen owns, so read it from Lift, the member a fact row writes it to
        Assert.Contains(done.BodyRows!, r => r.Lift is { } h && h.Contains(@"C:\home\gatto.json"));
    }

    //a passing audition gets a screen with no key press, and the task counts come from the verdict, which can be four of five
    [Fact]
    public void A_PASSING_AUDITION_GETS_A_SCREEN_AT_ALL_carrying_what_it_measured()
    {
        var flow = AtAuditionOffer(
            new AuditionFacts("Q6_K", "defaults", "template default", 49.4, TasksPassed: 4, TasksTotal: 5));
        //the pass screen is emitted and answerable, and its own dwell keeps it up
        var said = Assert.IsType<WizardScreen.Choice>(CheckRan(flow));
        Assert.Equal(SetupFlow.AuditionPassedKey, said.Key);
        var body = string.Join("\n", said.BodyRows!.Select(r => r.Text));

        Assert.Contains("ran commands and edited files", body, StringComparison.Ordinal);
        Assert.Contains("4 of 5 tasks", body, StringComparison.Ordinal);

        //assert the whole stamp, from the same composer the completion screen uses, so the two can't drift
        Assert.Contains("Q6_K", body, StringComparison.Ordinal);
        Assert.Contains("template default", body, StringComparison.Ordinal);
        Assert.Contains($"~49.4 tok/s, {Gatto.Roles.Audition.AuditionReport.SpeedNote(49.4)}",
            body, StringComparison.Ordinal);
    }

    //the failed screen shows the same measured facts above the mitigation, since a fast model can still fail and the user needs both
    [Fact]
    public void A_FAILED_AUDITION_SHOWS_WHAT_IT_MEASURED_above_the_mitigation()
    {
        var flow = new SetupFlow(new FakeProbes
        {
            Llama = @"C:\llama\llama-server.exe",
            Found = [new Gatto.Core.Acquire.FoundModel(@"D:\m\qwen.gguf", 4_000_000_000, null)],
            Audition = AuditionOutcome.Failed,
            Facts = new AuditionFacts("Q4_K_M", "defaults", "template default", 61.2, 2, 5),
        });
        flow.StartPastEngine();
        flow.Answer("0");
        Resumed(flow, "qwen");

        var failed = Assert.IsType<WizardScreen.Choice>(Answered(flow, SetupFlow.Yes));
        var rows = failed.BodyRows!.Select(r => r.Text).ToList();

        Assert.Equal(SetupFlow.AuditionFailedKey, failed.Key);
        var speed = rows.FindIndex(r => r.Contains("~61.2 tok/s", StringComparison.Ordinal));
        var mitigation = rows.FindIndex(r => r.Contains("still use it", StringComparison.Ordinal));
        Assert.True(speed >= 0, "a failed check still measured the speed and must say so");
        Assert.True(speed < mitigation, "CW-20: evidence above the mitigation, never under it");
        Assert.Contains("Q4_K_M", string.Join("\n", rows), StringComparison.Ordinal);
    }

    //both screens must show the identical stamp, since a second spelling passes every other test here
    [Fact]
    public void THE_PASS_AND_FAIL_SCREENS_RENDER_ONE_STAMP_not_two_spellings_of_it()
    {
        var facts = new AuditionFacts("Q5_K_M", "temp 0.7", "template default", 33.3, 4, 5);

        SetupFlow At(AuditionOutcome outcome)
        {
            var flow = new SetupFlow(new FakeProbes
            {
                Llama = @"C:\llama\llama-server.exe",
                Found = [new Gatto.Core.Acquire.FoundModel(@"D:\m\qwen.gguf", 4_000_000_000, null)],
                Audition = outcome,
                Facts = facts,
            });
            flow.StartPastEngine();
            flow.Answer("0");
            Resumed(flow, "qwen");
            return flow;
        }

        var passFlow = At(AuditionOutcome.Passed);
        var passRows = Assert.IsType<WizardScreen.Choice>(CheckRan(passFlow))
            .BodyRows!.Select(r => r.Text).ToList();

        var failed = Assert.IsType<WizardScreen.Choice>(CheckRan(At(AuditionOutcome.Failed)));
        var failRows = failed.BodyRows!.Select(r => r.Text).ToList();

        //the stamp is the block of key and value rows, which each screen wraps in its own words.
        static List<string> Stamp(List<string> rows) =>
            [.. rows.Where(r => r.StartsWith("quant", StringComparison.Ordinal)
                || r.StartsWith("sampling", StringComparison.Ordinal)
                || r.StartsWith("thinking", StringComparison.Ordinal)
                || r.StartsWith("speed", StringComparison.Ordinal))];

        var onPass = Stamp(passRows);
        Assert.Equal(4, onPass.Count);            //four, one per supplied fact, or the filter could be matching nothing
        Assert.Equal(onPass, Stamp(failRows));
    }

    [Fact]
    public void A_SKIPPED_AUDITION_REPORTS_NO_STAMP_AT_ALL_rather_than_a_table_of_defaults()
    {
        //skipping the check measures nothing, so a stamp row here would be an invention (every line must be something gatto watched happen)
        var flow = AtAuditionOffer();
        PastTheSkip(flow);
        var done = Assert.IsType<WizardScreen.Choice>(flow.Emitted[^1]);
        var body = string.Join("\n", done.BodyRows!.Select(r => r.Text));

        Assert.DoesNotContain("quant", body, StringComparison.Ordinal);
        Assert.DoesNotContain("sampling", body, StringComparison.Ordinal);
        Assert.DoesNotContain("tok/s", body, StringComparison.Ordinal);
        //the skipped check gets a verdict row about one load that answered, and no timing number, since the row is a sentence
        Assert.Contains("it loads and answers, the five tasks were skipped", body,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, "1 second")]
    [InlineData(2, "2 seconds")]
    [InlineData(37, "37 seconds")]
    public void ONE_SECOND_IS_SINGULAR(int shown, string expected)
    {
        //test Plural.Of itself, rather than a screen whose wording may change
        Assert.Equal(expected, Gatto.Core.Plural.Of(shown, "second"));
    }

    //reach the private formatter through a screen that uses it
    private static string SecondsOf(TimeSpan t)
    {
        var flow = new SetupFlow(new FakeProbes
        {
            Llama = @"C:\llama\llama-server.exe",
            Found = [new Gatto.Core.Acquire.FoundModel(@"D:\m\qwen.gguf", 4_000_000_000, null)],
            Proof = new Gatto.Core.Acquire.ProveOutcome(true, "hi", t),
        });
        flow.StartPastEngine();
        flow.Answer("0");
        Resumed(flow, "qwen");
        PastTheSkip(flow);

        var line = Assert.IsType<WizardScreen.Choice>(flow.Emitted[^1])
            .BodyRows!.Select(r => r.Text)
            .First(r => r.Contains("first reply", StringComparison.Ordinal));
        return line[(line.IndexOf("took ", StringComparison.Ordinal) + 5)..].TrimEnd('.');
    }

    [Fact]
    public void THE_CLOSING_LINE_SENDS_THE_USER_OFF_rather_than_restating_that_setup_finished()
    {
        //the earlier screen already said setup finished, so this one must not say it again.
        var flow = AtAuditionOffer();
        PastTheSkip(flow);
        var end = Assert.IsType<WizardScreen.Terminal>(Answered(flow, SetupFlow.Finish));

        Assert.DoesNotContain(end.Rows, r => r.Text.Contains("gatto is set up", StringComparison.Ordinal));
        //the closing line is optional in the model, so assert it exists (sending the user off is the point of the screen)
        Assert.NotNull(end.NextStep);
        Assert.Contains("Have fun with your local model", end.NextStep!.Text, StringComparison.Ordinal);
        Assert.Equal(["gatto"], end.NextStep.Highlight);

        //the one empty row is deliberate, a line of space above the closing sentence, and only this screen has it
        Assert.Equal([""], end.Rows.Select(r => r.Text));
    }

    [Fact]
    public void STARTING_GATTO_GETS_NO_PARTING_SENTENCE_because_it_is_not_parting()
    {
        //the blank row belongs to the branch that leaves the user in a shell, so starting gatto has none
        var flow = AtAuditionOffer();
        PastTheSkip(flow);
        var end = Assert.IsType<WizardScreen.Terminal>(Answered(flow, SetupFlow.OpenRepl));

        Assert.Empty(end.Rows);
        Assert.True(flow.StartReplWhenDone);
    }

    //answer the offer, wait on the poll rather than on a duration, and stop at the outcome. the write pause stays undrained, since a caller still reads an intent
    private static WizardScreen CheckRan(SetupFlow flow)
    {
        var running = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.Yes));
        Assert.Equal(SetupFlow.AuditionRunningKey, running.Key);
        Assert.True(SpinWait.SpinUntil(flow.PollWatch, TimeSpan.FromSeconds(10)),
            "the check never finished, so this walk never reached its own subject");
        return flow.Answer(SetupFlow.Landed);
    }

    //answering Skip returns the wait screen, since the skip runs one load. let the load finish, return the arrival, and leave the pause undrained
    private static WizardScreen SkipRan(SetupFlow flow)
    {
        var waiting = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.Skip));
        Assert.Equal(SetupFlow.AuditionWaitingKey, waiting.Key);
        Assert.True(SpinWait.SpinUntil(flow.PollWatch, TimeSpan.FromSeconds(10)),
            "the one load never finished, so this walk never reached its own subject");
        var landed = flow.Answer(SetupFlow.Landed);

        //answer the arrival so the helper ends past the check step with the pause armed. a load that never answered has no arrival, so don't require one
        return landed is WizardScreen.Choice { Key: SetupFlow.AuditionAnswersKey }
            ? flow.Answer(SetupFlow.AnswersNext)
            : landed;
    }

    //the skip step, with the write pause drained. it stays apart from SkipRan, which leaves the pause armed for callers that read an intent
    private static WizardScreen PastTheSkip(SetupFlow flow)
    {
        var screen = SkipRan(flow);
        while (flow.NeedsWritesApplied) screen = flow.ResumeAfterWrites(null);
        return screen;
    }

    //a walk must answer the pass screen to reach what follows. this stays separate from Answered, since draining there would hide the frame
    private static WizardScreen PastThePass(SetupFlow flow)
    {
        var passed = Assert.IsType<WizardScreen.Choice>(CheckRan(flow));
        Assert.Equal(SetupFlow.AuditionPassedKey, passed.Key);
        return Answered(flow, SetupFlow.AuditionPassedNext);
    }

    //answer a screen as the runner does. use it only where a test reads what follows, since draining wipes the armed state another test pins
    private static WizardScreen Answered(SetupFlow flow, string key)
    {
        var screen = flow.Answer(key);
        if (screen is WizardScreen.Choice { OnlyTheWatchAdvances: true })
        {
            Assert.True(SpinWait.SpinUntil(flow.PollWatch, TimeSpan.FromSeconds(10)),
                "the watched work never finished, so this walk never reached its own subject");
            screen = flow.Answer(SetupFlow.Landed);
        }
        while (flow.NeedsWritesApplied) screen = flow.ResumeAfterWrites(null);
        return screen;
    }

    //the same drain, from the runner's side, since the connect path arms the next pause on its first resume
    private static WizardScreen Resumed(SetupFlow flow, string? modelId = null)
    {
        var screen = flow.ResumeAfterWrites(modelId);
        while (flow.NeedsWritesApplied) screen = flow.ResumeAfterWrites(null);

        //pass through the watched screen as the runner does when the poll resolves. guard on Watching, since the other path's prove is not watched
        if (screen is WizardScreen.Choice { Watching: true, Key: SetupFlow.ProveKey })
            screen = flow.Answer(SetupFlow.OpenRepl);
        return screen;
    }

    //a failed check must not print a passing headline, and both halves are asserted, since either one alone would pass
    [Fact]
    public void A_FAILED_AUDITION_DOES_NOT_GET_A_PASSING_HEADLINE()
    {
        //supply facts that measure well on a failing outcome, so the outcome alone decides the headline
        var flow = AtChosenModel(new FakeProbes
        {
            Llama = @"C:\llama\llama-server.exe",
            Found = [Model(@"D:\models\qwen.gguf")],
            Audition = AuditionOutcome.Failed,
            Facts = new AuditionFacts("Q4_K_M", "defaults", "template default", 12.5),
        });
        CheckRan(flow);                                   //let the check fail here
        var prove = Assert.IsType<WizardScreen.Choice>(Answered(flow, SetupFlow.Anyway));

        var body = string.Join("\n", prove.BodyRows!.Select(r => r.Text));

        Assert.DoesNotContain("answering properly", body, StringComparison.Ordinal);
        Assert.Contains("struggled, you chose to use it anyway", body, StringComparison.Ordinal);

        //the numbers render once, on the screen where the choice was made, so this screen repeats no stamp
        Assert.DoesNotContain("quant", body, StringComparison.Ordinal);
        //the verdict row names the outcome, and the numbers stay on the check screen
        Assert.Contains("struggled, you chose to use it anyway", body, StringComparison.Ordinal);

        //a screen that opens on a failed check must not close with congratulations, since the closing line branches on the same outcome
        Assert.DoesNotContain("Congratulations", body, StringComparison.Ordinal);
        //the verdict row keeps the two facts apart, setup completed and the model falling short
        Assert.Contains("struggled, you chose to use it anyway", body, StringComparison.Ordinal);
        //the sentence must not claim the setup failed, since the setup did complete, so assert it in the title
        Assert.Equal("gatto is set up.", prove.Question);
    }

    //the passing branch needs no third test, since the completion screen already covers the non-failed paths

    //a passed audition keeps its sentence, since the guard above would pass on a screen that says it to nobody
    [Fact]
    public void A_PASSED_AUDITION_STILL_SAYS_THE_MODEL_ANSWERS_PROPERLY()
    {
        //supply facts here, since the stamp block is what renders the headline, and a factless fixture would pass without it
        var flow = AtChosenModel(new FakeProbes
        {
            Llama = @"C:\llama\llama-server.exe",
            Found = [Model(@"D:\models\qwen.gguf")],
            Facts = new AuditionFacts("Q4_K_M", "defaults", "template default", 60.0),
        });
        var prove = Assert.IsType<WizardScreen.Choice>(PastThePass(flow));

        var body = string.Join("\n", prove.BodyRows!.Select(r => r.Text));
        Assert.Contains("passed, it ran commands and edited files", body, StringComparison.Ordinal);
        Assert.DoesNotContain("found problems", body, StringComparison.Ordinal);
    }

    //the third outcome measured nothing, so the rows claim neither verdict and report only what the load watched
    [Fact]
    public void AN_AUDITION_THAT_COULD_NOT_RUN_CLAIMS_NEITHER_VERDICT()
    {
        var flow = AtChosenModel(WithModel(audition: AuditionOutcome.CouldNotRun));
        CheckRan(flow);
        var prove = Assert.IsType<WizardScreen.Choice>(Answered(flow, SetupFlow.Anyway));

        var body = string.Join("\n", prove.BodyRows!.Select(r => r.Text));
        Assert.DoesNotContain("answering properly", body, StringComparison.Ordinal);
        Assert.DoesNotContain("found problems", body, StringComparison.Ordinal);
        //a check that could not run proved nothing, so its row must not read like the skip row's
        Assert.Contains("not run", body, StringComparison.Ordinal);
    }

    //returns a run standing at the audition offer, with its writes applied.
    private static SetupFlow AtAuditionOffer(AuditionFacts? facts = null)
    {
        var flow = new SetupFlow(new FakeProbes
        {
            Llama = @"C:\llama\llama-server.exe",
            Found = [new Gatto.Core.Acquire.FoundModel(@"D:\m\qwen.gguf", 4_000_000_000, null)],
            Facts = facts,
        });
        flow.StartPastEngine();
        flow.Answer("0");
        Resumed(flow, "qwen");
        return flow;
    }

    //the lookup reads every model, since checking only the default hides the rest and scaffolds a duplicate
    [Fact]
    public void A_MODEL_THAT_ALREADY_HAS_A_PACK_REUSES_IT_rather_than_scaffolding_a_second()
    {
        var flow = new SetupFlow(new FakeProbes
        {
            Llama = @"C:\llama\llama-server.exe",
            Found = [new Gatto.Core.Acquire.FoundModel(@"D:\m\qwen.gguf", 4_000_000_000, null)],
            ExistingModel = "qwen3.6-35b-a3b",
        });
        flow.StartPastEngine();
        flow.Answer("0");

        //assert the absence of the create intent, since a run that writes a duplicate still passes an id check
        Assert.Null(flow.Writes.CreateModel);

        //the pause must be armed, since it is the only thing that writes the default model on the reuse path
        Assert.True(flow.NeedsWritesApplied);

        Assert.Equal("qwen3.6-35b-a3b", flow.Writes.DefaultModel);
        //the reuse is reported in the narration, so the user learns it happened
        Assert.Contains(flow.TakeNarration(), i => i.Key == "model.reused");
    }

    [Fact]
    public void A_MODEL_WITH_NO_PACK_STILL_SCAFFOLDS_ONE()
    {
        //reuse that fired for every file would leave no way to adopt a new model, and the guard above can't see that alone
        var probes = new FakeProbes
        {
            Llama = @"C:\llama\llama-server.exe",
            Found = [new Gatto.Core.Acquire.FoundModel(@"D:\m\new.gguf", 4_000_000_000, null)],
            ExistingModel = null,
        };
        var flow = new SetupFlow(probes);
        flow.StartPastEngine();
        flow.Answer("0");

        Assert.Equal(@"D:\m\new.gguf", flow.Writes.CreateModel!.GgufPath);
        Assert.True(flow.NeedsWritesApplied);
        //the lookup must be asked about the file being adopted
        Assert.Contains(@"D:\m\new.gguf", probes.AskedModelFor);
    }

    //an endpoint already pointing at this URL is updated. the name local goes to the collision screen, since the launch branch is chosen by that name
    [Fact]
    public void CONFIRMING_A_SERVER_YOU_ALREADY_HAVE_UPDATES_THAT_ENDPOINT()
    {
        var flow = ConnectTo("http://127.0.0.1:1235", existingEndpoint: "their-box");

        var e = flow.Writes.UpsertEndpoint!;
        Assert.Equal("their-box", e.Name);
        Assert.Equal("http://127.0.0.1:1235", e.BaseUrl);
    }

    [Fact]
    public void A_SERVER_NOTHING_IS_CONFIGURED_FOR_STILL_GETS_THE_NEW_ENDPOINT()
    {
        var e = ConnectTo("http://127.0.0.1:9999", existingEndpoint: null).Writes.UpsertEndpoint!;
        Assert.Equal("server", e.Name);
    }

    //a second run leaves the endpoint set unchanged, asserted by nothing written, against a real home whose config is read back
    [Fact]
    public void A_SECOND_RUN_ADDS_NO_ENDPOINT_TO_A_HOME_THAT_ALREADY_HAS_ONE()
    {
        using var home = new TempHome();
        Gatto.Core.Home.GattoHome.EnsureInitialized(home.Path);
        //use a name other than local, since that one opens the collision screen. use an address no other endpoint has, or the look-up answers by ordering
        Gatto.Core.Home.GattoConfigWriter.UpsertEndpoint(home.Path, "their-box", "http://127.0.0.1:9999", 4096);

        var before = Gatto.Core.Home.GattoConfig.Load(home.Path).Endpoints.Keys.Order().ToList();

        //run the whole connect path against that home, through the live probe and the real writer.
        var probes = new LiveSetupProbes(home.Path, glyphs: GlyphSet.Unicode);
        var flow = new SetupFlow(new FakeProbes
        {
            Server = new Gatto.Core.Acquire.ConnectProbe("http://127.0.0.1:9999", ["m"], 65536),
            ExistingEndpoint = probes.ExistingEndpointFor("http://127.0.0.1:9999"),
        });
        flow.StartPastOpening();
        flow.Answer(SetupFlow.ForkConnect);
        flow.Answer(SetupFlow.Yes);
        Assert.Null(WriteSetApply.Apply(home.Path, flow.Writes, out _));

        var after = Gatto.Core.Home.GattoConfig.Load(home.Path).Endpoints;
        Assert.Equal(before, after.Keys.Order());
        Assert.Equal(65536, after["their-box"].Context);
        Assert.Equal("http://127.0.0.1:9999", after["their-box"].BaseUrl);
    }

    //the name local can't be reused, since the launch branch is chosen by it. the run writes a second endpoint and leaves the first alone, so assert the whole set
    [Fact]
    public void THE_CONNECT_WALK_ADDS_SERVER_BESIDE_LOCAL_AND_MAKES_IT_THE_DEFAULT()
    {
        using var home = new TempHome();
        Gatto.Core.Home.GattoHome.EnsureInitialized(home.Path);
        Gatto.Core.Home.GattoConfigWriter.UpsertEndpoint(home.Path, "local", "http://127.0.0.1:1235", 4096);
        Gatto.Core.Home.GattoConfigWriter.SetDefaultEndpoint(home.Path, "local");

        var probes = new LiveSetupProbes(home.Path, glyphs: GlyphSet.Unicode);
        var flow = new SetupFlow(new FakeProbes
        {
            Server = new Gatto.Core.Acquire.ConnectProbe("http://127.0.0.1:1235", ["their-model"], 65536),
            ExistingEndpoint = probes.ExistingEndpointFor("http://127.0.0.1:1235"),
        });
        flow.StartPastOpening();
        flow.Answer(SetupFlow.ForkConnect);
        flow.Answer(SetupFlow.Yes);

        Assert.Null(WriteSetApply.Apply(home.Path, flow.Writes, out _));

        var config = Gatto.Core.Home.GattoConfig.Load(home.Path);
        Assert.Equal(["local", "server"], config.Endpoints.Keys.Order());
        Assert.Equal("server", config.DefaultEndpoint);
        Assert.Equal("their-model", config.DefaultModel);
        Assert.Equal(4096, config.Endpoints["local"].Context);
    }

    [Theory]
    [InlineData("http://127.0.0.1:1235/")]   //the user may type a trailing slash.
    [InlineData("HTTP://127.0.0.1:1235")]    //and the scheme may be typed in upper case.
    public void THE_SAME_SERVER_WRITTEN_SLIGHTLY_DIFFERENTLY_IS_STILL_THE_SAME_ENDPOINT(string configured)
    {
        //the comparison trims a trailing slash and ignores case. it runs through the live probe, since a fake would agree with any rule.
        using var home = new TempHome();
        Gatto.Core.Home.GattoHome.EnsureInitialized(home.Path);
        Gatto.Core.Home.GattoConfigWriter.UpsertEndpoint(home.Path, "local", configured, 4096);

        Assert.Equal("local", new LiveSetupProbes(home.Path, glyphs: GlyphSet.Unicode).ExistingEndpointFor("http://127.0.0.1:1235"));
    }

    [Fact]
    public void A_DIFFERENT_SERVER_IS_NOT_TREATED_AS_THE_SAME_ONE()
    {
        //a port forward and its target are two endpoints, so no rule may treat one host as enough
        using var home = new TempHome();
        Gatto.Core.Home.GattoHome.EnsureInitialized(home.Path);
        Gatto.Core.Home.GattoConfigWriter.UpsertEndpoint(home.Path, "local", "http://127.0.0.1:1235", 4096);

        Assert.Null(new LiveSetupProbes(home.Path, glyphs: GlyphSet.Unicode).ExistingEndpointFor("http://127.0.0.1:8080"));
    }

    private sealed class TempHome : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("gatto-endpoint-").FullName;
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }

    private static SetupFlow ConnectTo(string baseUrl, string? existingEndpoint)
    {
        var flow = new SetupFlow(new FakeProbes
        {
            //the connect entry sits on the first engine screen, so the fixture supplies an engine, since the test is about taking the entry
            Llama = @"C:\llama\llama-server.exe",
            Server = new Gatto.Core.Acquire.ConnectProbe(baseUrl, ["m"], 65536),
            ExistingEndpoint = existingEndpoint,
        });
        flow.StartPastOpening();
        flow.Answer(SetupFlow.ForkConnect);
        flow.Answer(SetupFlow.Yes);
        return flow;
    }

    private static string ConnectProofBody(Gatto.Core.Acquire.ProveOutcome proof)
    {
        var flow = new SetupFlow(new FakeProbes
        {
            //one model only, since a server with several shows a picker first, and the subject here is the prove-it sentence
            Server = new Gatto.Core.Acquire.ConnectProbe("http://127.0.0.1:1234", ["m"], 65536),
            Proof = proof,
        });
        flow.StartPastOpening();
        flow.Answer(SetupFlow.ForkConnect);
        var screen = flow.Answer(SetupFlow.Yes);
        //this path arms more than one write pause, so loop like the runner. pass null each time, since no model exists on this path
        while (flow.NeedsWritesApplied) screen = flow.ResumeAfterWrites(null);

        //the connect check is watched, so pass through its screen as the runner does, since any answer but the stop key means it finished
        if (screen is WizardScreen.Choice { Watching: true })
            screen = flow.Answer(SetupFlow.OpenRepl);

        var prove = Assert.IsType<WizardScreen.Choice>(screen);
        return string.Join(" ", prove.BodyRows!.Select(r => r.Text));
    }

    private const string CapabilitySentence = "cannot talk to this server at all";

    //a status in the reply proves the protocol, so the capability sentence must not render. read the status from the outcome, since the display text drifts
    [Fact]
    public void A_SERVER_THAT_ANSWERED_IS_NOT_ACCUSED_OF_LACKING_THE_PROTOCOL()
    {
        var lmStudio = "endpoint m returned 400: {\"error\":{\"message\":\"No models loaded. Please "
                     + "load a model in the developer page or use the 'lms load' command.\"}}";

        var body = ConnectProofBody(new Gatto.Core.Acquire.ProveOutcome(
            false, lmStudio, TimeSpan.Zero, ServerAnswered: 400));

        Assert.DoesNotContain(CapabilitySentence, body, StringComparison.Ordinal);
        //the server's own reply stays on screen, since it holds the next step
        Assert.Contains("No models loaded", body, StringComparison.Ordinal);
    }

    //with the chat route genuinely absent, the capability sentence is right and must render. a guard on one arm alone passes on a build that deleted it
    [Theory]
    [InlineData(404)]    //404 means the chat route is missing.
    [InlineData(405)]
    public void WITHOUT_A_DISCRIMINATOR_THE_CAPABILITY_SENTENCE_STILL_RENDERS(int? status)
    {
        var body = ConnectProofBody(new Gatto.Core.Acquire.ProveOutcome(
            false, "connection refused", TimeSpan.Zero, ServerAnswered: status));

        Assert.Contains(CapabilitySentence, body, StringComparison.Ordinal);
    }

    //prove-it never renders the fourth capability's sentence, since only an overflow shows it. a known match proves the matcher works
    [Fact]
    public void PROVE_IT_NEVER_SPEAKS_THE_FOURTH_CAPABILITY()
    {
        var overflow = Gatto.Core.Acquire.ServerConnect.Capabilities[3].IfAbsent;

        //prove the matcher first, on a body that does contain the sentence.
        Assert.Contains(overflow, $"a screen that said: {overflow}", StringComparison.Ordinal);

        foreach (int? status in new int?[] { null, 400, 404, 405, 500 })
        {
            var body = ConnectProofBody(new Gatto.Core.Acquire.ProveOutcome(
                false, "nothing came back", TimeSpan.Zero, ServerAnswered: status));

            Assert.DoesNotContain(overflow, body, StringComparison.Ordinal);
        }
    }

    //a transport failure must not claim the protocol is missing, since the model list answered moments before
    [Fact]
    public void A_TRANSPORT_FAILURE_IS_NOT_A_MISSING_PROTOCOL()
    {
        var body = ConnectProofBody(new Gatto.Core.Acquire.ProveOutcome(
            false, "connection refused", TimeSpan.Zero, ServerAnswered: null));

        Assert.DoesNotContain(CapabilitySentence, body, StringComparison.Ordinal);

        //a transport arm states the fact in gatto's words, since the exception's message is diagnostic text. where the server answered, its own words show whole
        Assert.Contains("nothing, the connection was refused", body, StringComparison.Ordinal);
        Assert.Contains("It answered a moment ago", body, StringComparison.Ordinal);
    }

    [Fact]
    public void EVERY_HIGHLIGHTED_SPAN_IS_ACTUALLY_PRESENT_IN_ITS_OWN_SENTENCE()
    {
        //a highlight span is literal text, so dropping it leaves markup pointing at nothing. assert every highlighted row, since the miss only looks like a plain screen
        var flow = new SetupFlow(new FakeProbes
        {
            Llama = null, Model = true,
            Asset = new Gatto.Roles.LlamaAsset(
                "llama-b11071-bin-win-vulkan-x64.zip", null, "an AMD discrete card", "Vulkan"),
            Offer = new EngineFetchOffer(@"C:\Users\you\.gatto\llama\b11071\", new EnginePair(
                new EngineAsset("llama-b11071-bin-win-vulkan-x64.zip", "https://example.invalid/z",
                    new string('a', 64), 1), null)),
        });
        var steer = Assert.IsType<WizardScreen.Choice>(flow.StartPastOpening());

        var highlighted = steer.BodyRows!.Where(r => r.Highlight is { Count: > 0 }).ToList();
        Assert.NotEmpty(highlighted);
        foreach (var row in highlighted)
            foreach (var span in row.Highlight!)
                Assert.Contains(span, row.Text, StringComparison.Ordinal);

        //the highlighted span is the build, inside the sentence that says why this machine gets it
        Assert.Contains(highlighted, r => r.Highlight!.Contains("Vulkan"));
    }

    [Fact]
    public void NO_SCREEN_TALKS_ABOUT_WRITING_BEFORE_ANYTHING_IS_WRITTEN()
    {
        //no screen says nothing is written before the writes, since the promise goes false mid-run
        var flow = new SetupFlow(new FakeProbes { Llama = null, Model = true });
        flow.StartPastOpening();

        var text = string.Join("\n", flow.Emitted.Select(Rendered));

        Assert.DoesNotContain("nothing is written", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("nothing was written", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void THE_CONNECT_DOOR_GIVES_TWO_VENDOR_EXAMPLES_AND_CLAIMS_NO_LIST()
    {
        //the door names two vendors as examples, and a third would make it an enumeration someone must keep current
        var named = new[] { "ollama", "lm studio", "openai", "jan", "koboldcpp", "vllm", "text-generation" }
            .Where(v => SetupFlow.ConnectDoorLabel.Contains(v, StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.True(named.Count == 2,
            "the door gives two vendors as EXAMPLES; a third makes it an enumeration somebody has "
            + "to keep current, and a list of vendors on a screen goes stale. Named: "
            + string.Join(", ", named));

        //the label must not claim who is supported, and the deciding word is start, since the distinction is who keeps the server running
        Assert.Contains("you start yourself", SetupFlow.ConnectDoorLabel, StringComparison.Ordinal);
    }

    [Fact]
    public void The_wizard_NEVER_ADVERTISES_WHICH_PORTS_IT_PROBES()
    {
        //the wizard must never recite the ports it probes, and a screen may echo the address it found, so this run finds nothing
        var flow = new SetupFlow(new FakeProbes { Server = null });
        flow.StartPastOpening();
        flow.Answer(SetupFlow.ForkConnect);

        foreach (var s in flow.Emitted)
            foreach (var port in new[] { "1234", "1235", "8080" })
                Assert.DoesNotContain(port, Rendered(s), StringComparison.Ordinal);
    }

    [Fact]
    public void The_confirmation_ECHOES_THE_FOUND_SERVER_because_silent_adoption_is_the_defect()
    {
        //adoption must never be silent, so the screen shows which server, by its own address
        var flow = new SetupFlow(new FakeProbes
        {
            Server = new Gatto.Core.Acquire.ConnectProbe("http://127.0.0.1:1234", ["some-model"], 8192),
        });
        flow.StartPastOpening();
        var confirm = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.ForkConnect));

        Assert.Equal(SetupFlow.ConfirmServerKey, confirm.Key);
        //the address is a body row, so that is where the echo is asserted
        Assert.Contains(confirm.BodyRows!, r => r.Text.Contains("http://127.0.0.1:1234", StringComparison.Ordinal));
        Assert.Contains(confirm.BodyRows!, r => r.Text.Contains("some-model", StringComparison.Ordinal));
    }

    [Fact]
    public void A_MULTI_MODEL_SERVER_IS_NOT_DESCRIBED_AS_SERVING_THEM_ALL()
    {
        //a server that lists many models has loaded none yet, so it must not read as serving them. both halves are asserted, since one model must still say serving
        var many = new SetupFlow(new FakeProbes
        {
            Server = new Gatto.Core.Acquire.ConnectProbe("http://host:9", ["a", "b", "c"], 8192),
        });
        many.StartPastOpening();
        var manyRows = Assert.IsType<WizardScreen.Choice>(many.Answer(SetupFlow.ForkConnect)).BodyRows!;

        Assert.DoesNotContain(manyRows, r => r.Text.Contains("Serving", StringComparison.Ordinal));
        //the catalogue renders as one fact with a count, and the model pick is what names the models
        Assert.Contains(manyRows, r => r.Text.Contains("models it can load", StringComparison.Ordinal));
        Assert.Contains(manyRows, r => r.Text.Contains("3 models it can load", StringComparison.Ordinal));
        //assert the model ids absent here, since this screen states a count and the model pick states the list
        foreach (var id in new[] { "a", "b", "c" })
            Assert.DoesNotContain(manyRows, r => r.Text.EndsWith(id, StringComparison.Ordinal));

        var one = new SetupFlow(new FakeProbes
        {
            Server = new Gatto.Core.Acquire.ConnectProbe("http://host:9", ["only-one"], 8192),
        });
        one.StartPastOpening();
        var oneRows = Assert.IsType<WizardScreen.Choice>(one.Answer(SetupFlow.ForkConnect)).BodyRows!;

        //assert the row exactly, since a single-model server must still name what it serves
        Assert.Contains(oneRows, r => r.Text == "serving   only-one");
    }

    private static SetupFlow AdoptingWithCollision(string? colliding, string? existing = null,
        Gatto.Roles.IdClash kind = Gatto.Roles.IdClash.DifferentModel)
    {
        var flow = new SetupFlow(new FakeProbes
        {
            ClashKind = kind,
            //the fixture has llama.cpp already, so the walk reaches discovery, and the subject is the model name
            Llama = @"C:\llama\llama-server.exe",
            Found = [new Gatto.Core.Acquire.FoundModel(@"D:\m
ew.gguf", 4_000_000_000, null)],
            ExistingModel = existing,
            Colliding = colliding,
            Context = 8192,
        });
        flow.StartPastEngine();
        return flow;
    }

    [Fact]
    public void A_TAKEN_PACK_NAME_ASKS_INSTEAD_OF_SENDING_THE_USER_AWAY_TO_DELETE_SOMETHING()
    {
        //a taken model name must ask here, since a late throw sends the user off to delete a directory by hand
        var flow = AdoptingWithCollision(colliding: "qwen3.6-35b-a3b");

        var screen = Assert.IsType<WizardScreen.Choice>(flow.Answer("0"));

        Assert.Equal(SetupFlow.ModelCollisionKey, screen.Key);
        //two options only, since this fixture has two files that differ, which rules out joining them
        Assert.Equal([SetupFlow.UseExistingModel, SetupFlow.ReplaceModel], screen.Options.Select(o => o.Key));
        Assert.False(flow.NeedsWritesApplied);            //no write is staged while both answers on this screen remain open.
        //the body must name the cost of replacing, since an overwrite needs confirmation here
        var body = string.Join(" ", (screen.BodyRows ?? []).Select(r => r.Text));
        Assert.Contains("is gone", body, StringComparison.Ordinal);
    }

    //assert the collision screen is absent, since both names give the same repo and the file joins a model gatto already has
    [Fact]
    public void A_PROVEN_SECOND_QUANT_JOINS_WITHOUT_ASKING_ANYTHING()
    {
        var flow = AdoptingWithCollision("gemma-4-e4b-it", kind: Gatto.Roles.IdClash.SameModel);

        flow.Answer("0");

        Assert.DoesNotContain(flow.Emitted, s => Key(s) == SetupFlow.ModelCollisionKey);
        Assert.True(flow.NeedsWritesApplied);
        var write = Assert.IsType<WriteSet.Model>(flow.Writes.CreateModel);
        Assert.Equal("gemma-4-e4b-it", write.AddToModelId);
        //the Id field holds the same value, so the mover puts the file in that model's folder
        Assert.Equal("gemma-4-e4b-it", write.Id);
        Assert.False(write.Replace);
    }

    //gatto offers the join rather than guessing, since a local file has no repo and a header match is weak. the join sits above replace, which throws work away
    [Fact]
    public void WHEN_GATTO_CANNOT_TELL_THE_SCREEN_OFFERS_JOINING()
    {
        var flow = AdoptingWithCollision("gemma-4-e4b-it", kind: Gatto.Roles.IdClash.CannotTell);

        var screen = Assert.IsType<WizardScreen.Choice>(flow.Answer("0"));

        Assert.Equal([SetupFlow.UseExistingModel, SetupFlow.AddAsFile, SetupFlow.ReplaceModel],
            screen.Options.Select(o => o.Key));
        Assert.Contains(screen.Options,
            o => o.Label == "Add it as another file of gemma-4-e4b-it");
    }

    //the join is absent, since the holder's model may not load, and adding a file to it always fails
    [Fact]
    public void AN_UNREADABLE_HOLDER_IS_NOT_OFFERED_A_JOIN()
    {
        var flow = AdoptingWithCollision("gemma-4-e4b-it", kind: Gatto.Roles.IdClash.HolderUnreadable);

        var screen = Assert.IsType<WizardScreen.Choice>(flow.Answer("0"));

        Assert.Equal([SetupFlow.UseExistingModel, SetupFlow.ReplaceModel], screen.Options.Select(o => o.Key));
    }

    //answering the join must record what the proven case records, so the screen's answer and the repo's proof can't drift
    [Fact]
    public void ANSWERING_THE_JOIN_RECORDS_THE_SAME_INTENT_AS_THE_PROVEN_CASE()
    {
        var flow = AdoptingWithCollision("gemma-4-e4b-it", kind: Gatto.Roles.IdClash.CannotTell);
        flow.Answer("0");

        flow.Answer(SetupFlow.AddAsFile);

        Assert.True(flow.NeedsWritesApplied);
        var write = Assert.IsType<WriteSet.Model>(flow.Writes.CreateModel);
        Assert.Equal("gemma-4-e4b-it", write.AddToModelId);
        Assert.False(write.Replace);
    }

    //the fixture file name holds a real quant token, since the done-step question is about the label
    private static SetupFlow Joining(string? loaded = null)
    {
        var flow = new SetupFlow(new FakeProbes
        {
            Llama = @"C:\llama\llama-server.exe",
            Found = [new Gatto.Core.Acquire.FoundModel(@"D:\w\gemma-Q6_K.gguf", 4_000_000_000, null)],
            Colliding = "gemma-4-e4b-it",
            ClashKind = Gatto.Roles.IdClash.SameModel,
            Loaded = loaded,
            Context = 8192,
        });
        flow.StartPastEngine();
        flow.Answer("0");                       //answering 0 picks the found file, which joins with no screen
        return flow;
    }

    //checks the whole second-quant sequence as one run, since separate steps prove no order. a second quant adds the file, says so, then asks which file wins
    [Fact]
    public void THE_SECOND_QUANT_WALK()
    {
        var flow = Joining();

        //the collision question has one real case, and a second quant of the same model isn't it, so nothing is asked
        Assert.DoesNotContain(flow.Emitted, s => Key(s) == SetupFlow.ModelCollisionKey);
        Assert.True(flow.NeedsWritesApplied);

        //the narration must state the added file before the flow asks anything about it.
        var screen = Assert.IsType<WizardScreen.Choice>(flow.ResumeAfterWrites("gemma-4-e4b-it"));
        var said = string.Join(" ",
            flow.TakeNarration().SelectMany(i => i.Rows).Select(r => r.Text));
        Assert.Contains("Q6_K is added to gemma-4-e4b-it", said, StringComparison.Ordinal);

        //the question about which file to use comes only after that report.
        Assert.Equal(SetupFlow.QuantFromNowKey, screen.Key);

        //answering yes records the switch, and the flow ends on the model that was joined.
        flow.Answer(SetupFlow.Yes);
        Assert.Equal(("gemma-4-e4b-it", "Q6_K"), flow.Writes.ActivateFile);
    }

    //the model got its check when it was created. after a join only the new file is undecided, so the resume asks about the quant
    [Fact]
    public void A_JOIN_RESUMES_ON_THE_QUANT_QUESTION()
    {
        var flow = Joining();

        var screen = Assert.IsType<WizardScreen.Choice>(flow.ResumeAfterWrites("gemma-4-e4b-it"));

        Assert.Equal(SetupFlow.QuantFromNowKey, screen.Key);
        //the screen and the in-session confirm read the sentence from Gatto.Cli.SwapConfirm, so the two can't drift apart
        Assert.Equal(Gatto.Cli.SwapConfirm.QuantQuestion("gemma-4-e4b-it", "Q6_K"), screen.Question);
    }

    //both branches ask the question, and the serving one adds that a running server keeps its file until restart
    [Fact]
    public void THE_SERVING_BRANCH_SAYS_THE_RUNNING_SERVER_KEEPS_ITS_FILE()
    {
        var flow = Joining(loaded: "gemma-4-e4b-it");

        var screen = Assert.IsType<WizardScreen.Choice>(flow.ResumeAfterWrites("gemma-4-e4b-it"));
        var body = string.Join(" ", (screen.BodyRows ?? []).Select(r => r.Text));

        Assert.Contains("until it restarts", body, StringComparison.Ordinal);
    }

    //the known miss for the assertion above, so it cannot pass on both branches
    [Fact]
    public void AND_THE_IDLE_BRANCH_DOES_NOT_MENTION_A_RESTART()
    {
        var flow = Joining(loaded: null);

        var screen = Assert.IsType<WizardScreen.Choice>(flow.ResumeAfterWrites("gemma-4-e4b-it"));
        var body = string.Join(" ", (screen.BodyRows ?? []).Select(r => r.Text));

        Assert.DoesNotContain("restart", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("on gemma-4-e4b-it's list either way", body, StringComparison.Ordinal);
    }

    //answering yes records the activation as an intent, applied at the one apply point, so the sentence and the write can't part
    [Fact]
    public void YES_RECORDS_THE_ACTIVATION_AS_AN_INTENT()
    {
        var flow = Joining();
        flow.ResumeAfterWrites("gemma-4-e4b-it");

        flow.Answer(SetupFlow.Yes);

        Assert.Equal(("gemma-4-e4b-it", "Q6_K"), flow.Writes.ActivateFile);
    }

    //declining only chooses which file is used, so no flow may remove the file
    [Fact]
    public void NO_KEEPS_THE_FILE_AND_WRITES_NOTHING()
    {
        var flow = Joining();
        flow.ResumeAfterWrites("gemma-4-e4b-it");

        flow.Answer(SetupFlow.No);

        Assert.Null(flow.Writes.ActivateFile);
        //no removal is recorded either, so the model keeps the file the join added.
        Assert.Null(flow.Writes.CreateModel);
    }

    //replace must record a replacing intent here, or a set AddToModelId would be true of every answer
    [Fact]
    public void AND_REPLACE_STILL_REPLACES_RATHER_THAN_JOINING()
    {
        var flow = AdoptingWithCollision("gemma-4-e4b-it", kind: Gatto.Roles.IdClash.CannotTell);
        flow.Answer("0");

        flow.Answer(SetupFlow.ReplaceModel);

        //assert on the writes, since a predicate over Emitted that ignores its argument can never fail
        Assert.Null(flow.Writes.CreateModel?.AddToModelId);
    }

    [Fact]
    public void USE_THE_ONE_I_HAVE_scaffolds_nothing_and_completes_on_the_existing_model()
    {
        var flow = AdoptingWithCollision(colliding: "qwen3.6-35b-a3b");
        flow.Answer("0");

        flow.Answer(SetupFlow.UseExistingModel);

        Assert.Null(flow.Writes.CreateModel);              //this answer adopts no new file, since the existing model stays.
        //the kept model must appear as a discovery in the narration, which is what renders
        Assert.Contains(flow.TakeNarration(), i => i.Key == "model.reused");
    }

    [Fact]
    public void REPLACE_IT_carries_the_answer_to_the_apply_point_as_INTENT()
    {
        //a destructive answer is no different, since the flow only records that the user said yes and one place acts on it
        var flow = AdoptingWithCollision(colliding: "qwen3.6-35b-a3b");
        flow.Answer("0");

        flow.Answer(SetupFlow.ReplaceModel);

        var model = Assert.IsType<WriteSet.Model>(flow.Writes.CreateModel);
        Assert.True(model.Replace);
        Assert.Equal(@"D:\m
ew.gguf", model.GgufPath);
        Assert.Equal(8192, model.Context);                 //the recorded context is the budget the flow probed, and the header's maximum is a different number
        Assert.True(flow.NeedsWritesApplied);
    }

    [Fact]
    public void THE_SAME_FILE_IS_STILL_RULING_BS_REUSE_and_never_reaches_the_collision_screen()
    {
        //the same file means reuse and the same name means a question, and neither may absorb the other, so this fixture answers both
        var flow = AdoptingWithCollision(colliding: "qwen3.6-35b-a3b", existing: "qwen3.6-35b-a3b");

        flow.Answer("0");

        Assert.DoesNotContain(flow.Emitted, s => Key(s) == SetupFlow.ModelCollisionKey);
        Assert.Null(flow.Writes.CreateModel);
    }

    [Fact]
    public void A_FREE_PACK_NAME_IS_NOT_A_COLLISION()
    {
        var flow = AdoptingWithCollision(colliding: null);

        flow.Answer("0");

        Assert.DoesNotContain(flow.Emitted, s => Key(s) == SetupFlow.ModelCollisionKey);
        Assert.False(Assert.IsType<WriteSet.Model>(flow.Writes.CreateModel).Replace);
    }


    [Fact]
    public void THE_UPDATE_QUESTION_IS_ASKED_ONCE_at_the_very_end()
    {
        //the update question belongs after the setup works, so it is asked last. the answer comes from the probe, so a run that has one skips the screen
        var flow = ConnectingTo(["m"], probes: new FakeProbes
        {
            Server = new Gatto.Core.Acquire.ConnectProbe("http://host:9", ["m"], 8192),
            UpdateAnswered = null,
        });
        flow.Answer(SetupFlow.Yes);
        Resumed(flow, null);

        var ask = Assert.IsType<WizardScreen.Choice>(Answered(flow, SetupFlow.Finish));

        Assert.Equal(SetupFlow.UpdateKey, ask.Key);
        //no option is pre-selected, since a highlighted default on a network question nudges the answer.
        Assert.All(ask.Options, o => Assert.False(o.Recommended));
        var body = string.Join(" ", (ask.BodyRows ?? []).Select(r => r.Text));
        Assert.Contains("Nothing about you or your machine is sent", body, StringComparison.Ordinal);
        Assert.Contains("update_check", body, StringComparison.Ordinal);   //the body names the update_check setting, so the user can change the answer later
    }

    [Fact]
    public void AN_ALREADY_ANSWERED_MACHINE_IS_NOT_ASKED_AGAIN()
    {
        var flow = ConnectingTo(["m"], probes: new FakeProbes
        {
            Server = new Gatto.Core.Acquire.ConnectProbe("http://host:9", ["m"], 8192),
            UpdateAnswered = true,
        });
        flow.Answer(SetupFlow.Yes);
        Resumed(flow, null);

        //naming the summary screen is a stronger claim than its type, since reaching it at all proves the consent screen was skipped
        var done = Assert.IsType<WizardScreen.Choice>(Answered(flow, SetupFlow.Finish));
        Assert.Equal(SetupFlow.SummaryKey, done.Key);
        Assert.IsType<WizardScreen.Terminal>(flow.Answer(SetupFlow.Finish));
        Assert.Null(flow.Writes.UpdateCheck);   //an already answered machine records no consent write again.
    }

    [Theory]
    [InlineData(SetupFlow.UpdateYes, true)]
    [InlineData(SetupFlow.UpdateNo, false)]
    public void THE_ANSWER_IS_CARRIED_AS_INTENT_like_every_other_write(string key, bool expected)
    {
        var flow = ConnectingTo(["m"], probes: new FakeProbes
        {
            Server = new Gatto.Core.Acquire.ConnectProbe("http://host:9", ["m"], 8192),
            UpdateAnswered = null,
        });
        flow.Answer(SetupFlow.Yes);
        Resumed(flow, null);
        Answered(flow, SetupFlow.Finish);

        //the consent flush consumes the intent on its way to disk, so read it before the pause runs
        flow.Answer(key);
        Assert.Equal(expected, flow.Writes.UpdateCheck);

        //the summary screen sits between the consent question and the final screen, so the intent above is read first
        var done = Assert.IsType<WizardScreen.Choice>(Resumed(flow));
        Assert.Equal(SetupFlow.SummaryKey, done.Key);
        Assert.IsType<WizardScreen.Terminal>(flow.Answer(SetupFlow.Finish));
    }

    [Fact]
    public void THE_GATE_ACCIDENT_picked_2_meant_1_and_it_is_not_written_yet()
    {
        //back must return the same pre-write screen, so a mis-pick is undone. the engine choice writes nothing, so there is a real state change to undo
        var flow = new SetupFlow(new FakeProbes
        {
            Llama = @"C:\llama\llama-server.exe",
            Server = new Gatto.Core.Acquire.ConnectProbe("http://host:9", ["alpha"], 8192),
        });

        var found = Assert.IsType<WizardScreen.Choice>(flow.StartPastOpening());
        Assert.True(found.AllowBack);                     //the screen itself offers back, so a mis-pick is visibly undoable.

        flow.Answer(SetupFlow.ForkConnect);               //the wrong turn, taken instead of the found model
        var again = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.BackKey));

        Assert.Equal(SetupFlow.FoundKey, again.Key);
        Assert.Contains(SetupFlow.FoundUse, again.Options.Select(o => o.Key));
        flow.Answer(SetupFlow.FoundUse);

        //only the second choice survives, since the flow follows the llama path
        Assert.Equal(SetupPath.Llama, flow.Path);
        Assert.Null(flow.Writes.UpsertEndpoint);
    }

    [Fact]
    public void BACK_RESTORES_THE_STATE_THE_SCREEN_WAS_ANSWERED_UNDER_not_just_the_screen()
    {
        //the screen choice also switches SetupPath, so back must restore that too, or the re-offered screen belongs to a path gatto already left
        var flow = new SetupFlow(new FakeProbes
        {
            Llama = @"C:\llama\llama-server.exe",
            Server = new Gatto.Core.Acquire.ConnectProbe("http://host:9", ["their-model"], 8192),
        });
        flow.StartPastOpening();
        flow.Answer(SetupFlow.ForkConnect);               //the wrong turn, which switches the flow to the connect path.
        Assert.Equal(SetupPath.Connect, flow.Path);

        var back = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.BackKey));

        Assert.Equal(SetupFlow.FoundKey, back.Key);
        Assert.Equal(SetupPath.Llama, flow.Path);
    }

    [Fact]
    public void BACK_IS_REFUSED_ONCE_ANYTHING_HAS_BEEN_WRITTEN()
    {
        //the boundary is any written change, so back is refused as soon as ResumeAfterWrites runs
        var flow = ConnectingTo(["their-model"]);
        flow.Answer(SetupFlow.Yes);
        Resumed(flow, null);                     //from this call on, the writes are on disk.

        var refused = Assert.IsType<WizardScreen.Info>(flow.Answer(SetupFlow.BackKey));

        Assert.Equal("back.refused", refused.Key);        //the key proves this is the refusal screen itself
        Assert.Contains("can't go back", refused.Rows[0].Text, StringComparison.Ordinal);
        Assert.Contains("re-run gatto setup", refused.Rows[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void NO_SCREEN_PAST_THE_WRITE_BOUNDARY_ADVERTISES_BACK()
    {
        //past the write boundary the back flag goes false, so the screen never offers the key, since offering one gatto then refuses is worse
        var flow = ConnectingTo(["their-model"]);
        flow.Answer(SetupFlow.Yes);
        var prove = Resumed(flow, null);

        Assert.False(prove.AllowBack);
    }

    [Fact]
    public void THE_FIRST_SCREEN_HAS_NOWHERE_TO_GO_and_says_so_by_not_offering()
    {
        //both halves are asserted, since a check on the first screen alone also passes when back is offered nowhere.
        var flow = new SetupFlow(new FakeProbes());

        var welcome = flow.Start();
        Assert.False(welcome.AllowBack);

        Assert.True(flow.Answer(SetupFlow.WelcomeGo).AllowBack,
            "the screen after the welcome must offer back, there is now somewhere to go");
    }

    //the argument that makes an option unpickable, kept as one constant so the census and its known match spell it the same
    private const string Named = "Disabled: true";

    //where a run sits once the opening and the install step are behind it, named once so those tests share one key
    private static readonly string PastTheOpening = SetupFlow.SteerKey;

    //the screen after the install question, the first question of the done step. the fixture answers the consent, so these flows reach the write pause
    private const string AfterTheInstallQuestion = "consent.ready";

    [Fact]
    public void BACK_WALKS_MORE_THAN_ONE_STEP_and_the_stack_empties_honestly()
    {
        //back returns each screen in turn, and stops being offered once nothing is behind the first one.
        var flow = ConnectingTo(["alpha", "beta"]);
        flow.Answer(SetupFlow.Yes);                       //yes answers the model pick.

        var toConfirm = flow.Answer(SetupFlow.BackKey);
        Assert.Equal(SetupFlow.ConfirmServerKey, Key(toConfirm));
        var toEngine = flow.Answer(SetupFlow.BackKey);

        //the found screen, since this machine reports an engine, which is what makes the connect choice reachable
        Assert.Equal(SetupFlow.FoundKey, Key(toEngine));
        //visit every screen on the way back, since a stack that lost one would still claim to reach the welcome
        Assert.True(toEngine.AllowBack);
        var toMachine = flow.Answer(SetupFlow.BackKey);
        Assert.Equal(SetupFlow.MachineKey, Key(toMachine));
        Assert.True(toMachine.AllowBack);
        var toWelcome = flow.Answer(SetupFlow.BackKey);
        Assert.Equal(SetupFlow.WelcomeKey, Key(toWelcome));
        Assert.False(toWelcome.AllowBack);                //the welcome is the first screen, so there is nothing to go back to
    }

    private static SetupFlow FlowWithInstall(Gatto.Cli.InstallState state) =>
        FlowWithInstall(state, null);

    //drive the flow to the install question without asserting where it sits, since each test asserts the screen and what an answer records
    private static WizardScreen AtTheInstallQuestion(SetupFlow flow)
    {
        flow.StartPastEngine();
        flow.Answer("0");
        Resumed(flow, "qwen");
        return PastThePass(flow);
    }

    //the running version is GattoVersion.String, so a fixture sets the installed version from that value
    private static SetupFlow FlowWithInstall(Gatto.Cli.InstallState state, string? installed) =>
        new(new FakeProbes { Install = state, Installed = installed });

    //an engine, a model and a settled audition reach the install question, so keep this fixture beside the bare one that skips it
    private static SetupFlow WalkableFlowWithInstall(
        Gatto.Cli.InstallState state, string? installed = null) =>
        new(new FakeProbes
        {
            Install = state,
            Installed = installed,
            Llama = @"C:\llama\llama-server.exe",
            Found = [new Gatto.Core.Acquire.FoundModel(@"D:\m\qwen.gguf", 4_000_000_000, null)],
            Facts = new AuditionFacts("Q6_K", "defaults", "template default", 49.4),
        });

    //a newer running copy beside an older install must offer the update, which needs no download since the bytes are already here
    [Fact]
    public void A_NEWER_RUNNING_GATTO_OFFERS_TO_UPDATE_THE_INSTALLED_COPY()
    {
        var flow = WalkableFlowWithInstall(Gatto.Cli.InstallState.AlreadyInstalledElsewhere, installed: "0.3.9");

        var first = Assert.IsType<WizardScreen.Choice>(AtTheInstallQuestion(flow));

        Assert.Equal(SetupFlow.UpdateInstalledKey, first.Key);
        //check NoDefault on this screen, since the rendered guards build their own choice and only prove the face obeys it
        Assert.True(first.NoDefault);
        var body = string.Join(" ", (first.BodyRows ?? []).Select(r => r.Text));
        Assert.Contains("0.3.9", body, StringComparison.Ordinal);            //0.3.9 is the installed version
        //read the running version from GattoVersion.String, since a literal fails at the next version bump instead of at a defect
        Assert.Contains(Gatto.Core.GattoVersion.String, body, StringComparison.Ordinal);
        //a replacement offer must not arrive with a pre-selected answer.
        Assert.All(first.Options, o => Assert.False(o.Recommended));
    }

    //a shape check passes even when answering throws, so this test answers the offer and asserts the recorded intent and the next screen
    [Fact]
    public void THE_UPDATE_OFFER_CAN_ACTUALLY_BE_ANSWERED_and_records_the_copy()
    {
        var flow = WalkableFlowWithInstall(Gatto.Cli.InstallState.AlreadyInstalledElsewhere, installed: "0.3.9");
        Assert.IsType<WizardScreen.Choice>(AtTheInstallQuestion(flow));

        var next = flow.Answer(SetupFlow.InstallYes);

        Assert.Equal(AfterTheInstallQuestion, Key(next));
        Assert.Equal(@"C:\Programs\gatto", flow.Writes.InstallTo);
        //an update records a copy, so PathOnly stays false
        Assert.False(flow.Writes.PathOnly);
    }

    //the write assertions are negative, so the next-screen check is what catches a throw
    [Fact]
    public void LEAVING_THE_INSTALLED_COPY_ALONE_WALKS_ON_AND_WRITES_NOTHING()
    {
        var flow = WalkableFlowWithInstall(Gatto.Cli.InstallState.AlreadyInstalledElsewhere, installed: "0.3.9");
        Assert.IsType<WizardScreen.Choice>(AtTheInstallQuestion(flow));

        var next = flow.Answer(SetupFlow.InstallNo);

        Assert.Equal(AfterTheInstallQuestion, Key(next));
        Assert.Null(flow.Writes.InstallTo);
        Assert.False(flow.Writes.PathOnly);
    }

    //an older, identical or unreadable installed copy gets no offer and no screen

    //the identical version must come from GattoVersion.String, since a literal stops meaning identical at the next bump
    [Fact]
    public void NO_OFFER_WHEN_THE_INSTALLED_COPY_IS_THE_SAME_VERSION()
    {
        var flow = FlowWithInstall(Gatto.Cli.InstallState.AlreadyInstalledElsewhere,
            installed: Gatto.Core.GattoVersion.String);

        Assert.Equal(PastTheOpening, Key(flow.StartPastOpening()));
        Assert.DoesNotContain(flow.Emitted, s => Key(s) == SetupFlow.UpdateInstalledKey);
    }

    [Theory]
    [InlineData("0.9.9")]   //the installed copy is newer, so an offer would downgrade it
    [InlineData(null)]      //a null installed version means gatto cannot tell, so it offers nothing
    [InlineData("nonsense")]
    public void NO_OFFER_WHEN_THE_RUNNING_IMAGE_IS_NOT_STRICTLY_NEWER(string? installed)
    {
        var flow = WalkableFlowWithInstall(Gatto.Cli.InstallState.AlreadyInstalledElsewhere, installed);

        var first = AtTheInstallQuestion(flow);

        Assert.Equal(SetupFlow.SummaryKey, Key(first));
        Assert.DoesNotContain(flow.Emitted, s => Key(s) == SetupFlow.UpdateInstalledKey);
        Assert.Null(flow.Writes.InstallTo);
    }

    //with a newer image the copy is worth making, so yes copies the binary instead of only repairing the path
    [Fact]
    public void SITE_TWO_WITH_A_NEWER_IMAGE_STILL_COPIES()
    {
        var flow = WalkableFlowWithInstall(Gatto.Cli.InstallState.InstalledButNotOnPath, installed: "0.3.9");
        AtTheInstallQuestion(flow);

        flow.Answer(SetupFlow.InstallYes);

        Assert.Equal(@"C:\Programs\gatto", flow.Writes.InstallTo);
        Assert.False(flow.Writes.PathOnly);
    }

    //site two compares versions too, so an older image can't silently downgrade a newer install (the label stays "Fix that for me")
    [Theory]
    [InlineData("0.9.9")]
    [InlineData(null)]
    public void SITE_TWO_REPAIRS_WITHOUT_COPYING_WHEN_THE_IMAGE_IS_NOT_NEWER(string? installed)
    {
        var flow = WalkableFlowWithInstall(Gatto.Cli.InstallState.InstalledButNotOnPath, installed);
        var first = Assert.IsType<WizardScreen.Choice>(AtTheInstallQuestion(flow));

        Assert.Equal("Fix that for me", first.Options[0].Label);
        var everything = first.Question + " "
            + string.Join(" ", (first.BodyRows ?? []).Select(r => r.Text)) + " "
            + string.Join(" ", first.Options.Select(o => o.Label));
        Assert.DoesNotContain("PATH", everything, StringComparison.Ordinal);

        flow.Answer(SetupFlow.InstallYes);

        Assert.True(flow.Writes.PathOnly);
        Assert.Null(flow.Writes.InstallTo);   //no copy here, since a copy would install the older image
    }

    //repair-only covers equal versions, so the body says the same version, since naming one version twice reads as a fault
    [Fact]
    public void SITE_TWO_SAYS_SAME_VERSION_RATHER_THAN_NAMING_ONE_VERSION_TWICE()
    {
        //the fixture derives the version, since a literal here fails at the next bump instead of at a defect
        var flow = WalkableFlowWithInstall(Gatto.Cli.InstallState.InstalledButNotOnPath,
            installed: Gatto.Core.GattoVersion.String);
        var first = Assert.IsType<WizardScreen.Choice>(AtTheInstallQuestion(flow));

        var body = string.Join(" ", (first.BodyRows ?? []).Select(r => r.Text));
        Assert.Contains($"the same version ({Gatto.Core.GattoVersion.String})", body, StringComparison.Ordinal);
        Assert.DoesNotContain(
            $"is {Gatto.Core.GattoVersion.String} and this one is {Gatto.Core.GattoVersion.String}",
            body, StringComparison.Ordinal);
    }

    //the opposite case: with different versions the body names both, so a body that always said the same version fails
    [Fact]
    public void SITE_TWO_STILL_NAMES_BOTH_VERSIONS_WHEN_THEY_DIFFER()
    {
        var flow = WalkableFlowWithInstall(Gatto.Cli.InstallState.InstalledButNotOnPath, installed: "0.9.9");
        var first = Assert.IsType<WizardScreen.Choice>(AtTheInstallQuestion(flow));

        var body = string.Join(" ", (first.BodyRows ?? []).Select(r => r.Text));
        Assert.Contains("0.9.9", body, StringComparison.Ordinal);
        Assert.Contains(Gatto.Core.GattoVersion.String, body, StringComparison.Ordinal);
        Assert.DoesNotContain("the same version", body, StringComparison.Ordinal);
    }

    [Fact]
    public void SITE_ONE_EMITS_NO_INSTALL_HEADING_because_the_question_sits_inside_the_done_step()
    {
        var flow = WalkableFlowWithInstall(Gatto.Cli.InstallState.AlreadyInstalledElsewhere, installed: "0.3.9");

        var first = Assert.IsType<WizardScreen.Choice>(AtTheInstallQuestion(flow));

        Assert.Equal(SetupFlow.UpdateInstalledKey, first.Key);
        //no step heading here, since the question sits inside the done step and the strip has no such section
        Assert.DoesNotContain(flow.TakeNarration(), i => i.Key == "step.install");
    }

    //no heading is hoisted above the fork, since a dev build gets its own sentence instead
    [Fact]
    public void A_DEV_BUILD_GETS_ITS_SENTENCE_AND_NOT_THE_INSTALL_HEADING()
    {
        var flow = WalkableFlowWithInstall(Gatto.Cli.InstallState.DevBuild);

        AtTheInstallQuestion(flow);

        var narration = flow.TakeNarration();
        Assert.Contains(narration, i => i.Key == "segment.install");
        Assert.DoesNotContain(narration, i => i.Key == "step.install");
    }

    //anything the segment does not name falls through into the install offer, so a new InstallState would ship a gatto that does not start
    [Fact]
    public void A_DEV_BUILD_IS_TOLD_WHY_THE_INSTALL_STEP_IS_SKIPPED()
    {
        var flow = WalkableFlowWithInstall(Gatto.Cli.InstallState.DevBuild);

        var first = AtTheInstallQuestion(flow);

        Assert.Equal(SetupFlow.SummaryKey, Key(first));
        Assert.DoesNotContain(flow.Emitted, s => Key(s) == SetupFlow.InstallKey);

        //gatto says why the step is skipped, instead of passing over a promised step in silence.
        var line = Assert.Single(flow.TakeNarration(), i => i.Key == "segment.install");
        Assert.Contains("development build",
            string.Join(" ", line.Rows.Select(r => r.Text)), StringComparison.Ordinal);

        Assert.Null(flow.Writes.InstallTo);
    }

    [Fact]
    public void AN_INSTALLED_GATTO_WALKS_THE_SEGMENT_GREEN_and_goes_straight_to_the_fork()
    {
        var flow = WalkableFlowWithInstall(Gatto.Cli.InstallState.Installed);

        var first = AtTheInstallQuestion(flow);

        Assert.Equal(SetupFlow.SummaryKey, Key(first));
        //an installed machine narrates nothing here, since restating the state repeats a skipped question (this key belongs to the dev-build branch)
        Assert.DoesNotContain(flow.TakeNarration(), i => i.Key == "segment.install");
        Assert.Null(flow.Writes.InstallTo);
    }

    [Fact]
    public void A_MACHINE_WITH_NO_GATTO_INSTALLED_IS_OFFERED_ONE()
    {
        var flow = WalkableFlowWithInstall(Gatto.Cli.InstallState.NotInstalled);

        var first = Assert.IsType<WizardScreen.Choice>(AtTheInstallQuestion(flow));

        Assert.Equal(SetupFlow.InstallKey, first.Key);
        var body = string.Join(" ", (first.BodyRows ?? []).Select(r => r.Text));
        //the body names the account-only scope and the administrator permission, which is what makes this screen a consent
        Assert.Contains("account only", body, StringComparison.Ordinal);
        Assert.Contains("administrator permission", body, StringComparison.Ordinal);
    }

    [Fact]
    public void INSTALLED_BUT_UNREACHABLE_OFFERS_TO_FINISH_not_to_install_again()
    {
        //the half-done state needs its own wording, since the copy exists and the path entry does not and asking to install would misstate both
        var flow = WalkableFlowWithInstall(Gatto.Cli.InstallState.InstalledButNotOnPath);

        var first = Assert.IsType<WizardScreen.Choice>(AtTheInstallQuestion(flow));

        Assert.Contains("only this window can find it", first.Question!, StringComparison.Ordinal);
        Assert.DoesNotContain("Install gatto", first.Question!, StringComparison.Ordinal);
        Assert.Equal("Fix that for me", first.Options[0].Label);

        //PATH is jargon at the moment a user is lost, so it may not appear in the question, the body or an option label
        var everything = first.Question + " "
            + string.Join(" ", (first.BodyRows ?? []).Select(r => r.Text)) + " "
            + string.Join(" ", first.Options.Select(o => o.Label));
        Assert.DoesNotContain("PATH", everything, StringComparison.Ordinal);
    }

    [Fact]
    public void SAYING_YES_ACCUMULATES_INTENT_and_installs_nothing_yet()
    {
        //a machine change follows write-at-the-end too, so leaving after this screen installs nothing
        var flow = WalkableFlowWithInstall(Gatto.Cli.InstallState.NotInstalled);
        AtTheInstallQuestion(flow);

        var next = flow.Answer(SetupFlow.InstallYes);

        Assert.Equal(AfterTheInstallQuestion, Key(next));
        Assert.Equal(@"C:\Programs\gatto", flow.Writes.InstallTo);
    }

    [Fact]
    public void SAYING_NO_CARRIES_ON_TO_THE_FORK_with_nothing_recorded()
    {
        var flow = WalkableFlowWithInstall(Gatto.Cli.InstallState.NotInstalled);
        AtTheInstallQuestion(flow);

        var next = flow.Answer(SetupFlow.InstallNo);

        Assert.Equal(AfterTheInstallQuestion, Key(next));
        Assert.Null(flow.Writes.InstallTo);
    }

    private static SetupFlow ConnectingTo(
        IReadOnlyList<string> models, int? nctx = 8192, string? existingEndpoint = null,
        FakeProbes? probes = null)
    {
        var flow = new SetupFlow(probes ?? new FakeProbes
        {
            //the connect choice needs an engine, so the fixture states one rather than trusting a default
            Llama = @"C:\llama\llama-server.exe",
            Server = new Gatto.Core.Acquire.ConnectProbe("http://host:9", models, nctx),
            ExistingEndpoint = existingEndpoint,
        });
        flow.StartPastOpening();
        flow.Answer(SetupFlow.ForkConnect);
        return flow;
    }

    [Fact]
    public void A_CONNECT_WALK_WRITES_THE_ENDPOINT_ITS_DEFAULT_AND_THE_SERVED_MODEL()
    {
        //an endpoint with no default model resolves at nothing, so the endpoint, its default and the served model are written together
        var flow = ConnectingTo(["their-model"]);

        flow.Answer(SetupFlow.Yes);

        Assert.True(flow.NeedsWritesApplied);
        var e = Assert.IsType<WriteSet.Endpoint>(flow.Writes.UpsertEndpoint);
        Assert.Equal("server", e.Name);
        Assert.Equal("server", flow.Writes.DefaultEndpoint);
        Assert.Equal("their-model", flow.Writes.DefaultModel);
    }

    [Fact]
    public void ONE_SERVED_MODEL_IS_NOT_A_CHOICE_TO_PUT_TO_THE_USER()
    {
        var flow = ConnectingTo(["only-one"]);

        flow.Answer(SetupFlow.Yes);

        Assert.Equal("only-one", flow.Writes.DefaultModel);
        Assert.DoesNotContain(flow.Emitted, s => Key(s) == SetupFlow.ModelPickKey);
    }

    [Fact]
    public void SEVERAL_LISTED_MODELS_ARE_PICKED_BY_THE_USER_never_chosen_for_them()
    {
        //a server lists everything it could load, so the choice of model is the user's and the labels are the server's own strings
        var flow = ConnectingTo(["alpha", "beta", "gamma"]);

        var pick = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.Yes));

        Assert.Equal(SetupFlow.ModelPickKey, pick.Key);
        Assert.Equal(["alpha", "beta", "gamma"], pick.Options.Select(o => o.Key));
        Assert.False(flow.NeedsWritesApplied);

        flow.Answer("beta");

        Assert.True(flow.NeedsWritesApplied);
        Assert.Equal("beta", flow.Writes.DefaultModel);
    }

    [Fact]
    public void A_LONG_SERVER_LIST_IS_NEVER_SILENTLY_CUT_the_widget_owns_the_nine()
    {
        //the flow must not truncate the list, since SelectPrompt.NumberedRows owns the numbering limit and a dropped model would be unreachable
        var many = Enumerable.Range(1, 12).Select(i => $"model-{i}").ToList();
        var flow = ConnectingTo(many);

        var pick = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.Yes));

        Assert.Equal(SetupFlow.ModelPickKey, pick.Key);
        Assert.Equal(many, pick.Options.Select(o => o.Key));      //every model, in the order the server gave.

        //the twelfth row takes an answer, since a drawn but dead row would still pass the list check
        flow.Answer("model-12");
        Assert.Equal("model-12", flow.Writes.DefaultModel);
    }

    [Fact]
    public void A_SERVER_THAT_LISTS_NOTHING_IS_ASKED_WHICH_MODEL_TO_SEND()
    {
        //a server that lists nothing leaves gatto without an id, so it asks for the name
        var flow = ConnectingTo([]);

        var ask = Assert.IsType<WizardScreen.Ask>(flow.Answer(SetupFlow.Yes));

        Assert.Equal(SetupFlow.ModelNameKey, ask.Key);
        Assert.NotNull(ask.Validate("   "));
        Assert.Null(ask.Validate("their-model"));

        flow.Answer("  their-model  ");

        Assert.Equal("their-model", flow.Writes.DefaultModel);
    }

    //the name "local" decides the launch branch, so reusing it for a server gatto did not start would overwrite the user's own endpoint
    [Fact]
    public void THE_CONNECT_WALK_LEAVES_LOCAL_ALONE_and_goes_in_beside_it()
    {
        var flow = ConnectingTo(["their-model"], existingEndpoint: "local");

        flow.Answer(SetupFlow.Yes);

        Assert.Equal("server", flow.Writes.UpsertEndpoint!.Name);
        Assert.Equal("server", flow.Writes.DefaultEndpoint);
    }

    [Fact]
    public void AN_ENDPOINT_UNDER_ANY_OTHER_NAME_IS_STILL_REUSED_per_ruling_C()
    {
        //every other existing endpoint name is reused, since only "local" collides
        var flow = ConnectingTo(["their-model"], existingEndpoint: "their-box");

        flow.Answer(SetupFlow.Yes);

        Assert.Equal("their-box", flow.Writes.UpsertEndpoint!.Name);
        Assert.Equal("their-box", flow.Writes.DefaultEndpoint);
    }

    [Fact]
    public void RESUMING_A_CONNECT_WALK_PROVES_IT_and_never_offers_an_audition()
    {
        //the audition needs a model and a connect walk has none, so a later edit must not offer it on that walk
        var flow = ConnectingTo(["their-model"]);
        flow.Answer(SetupFlow.Yes);
        Assert.True(flow.NeedsWritesApplied);

        Resumed(flow, null);

        Assert.False(flow.NeedsWritesApplied);
        Assert.DoesNotContain(flow.Emitted, s => Key(s) == SetupFlow.AuditionOfferKey);
        Assert.Contains(flow.Emitted, s => Key(s) == SetupFlow.ProveKey);
    }

    [Fact]
    public void THE_CONTEXT_ASK_SAYS_THE_SERVERS_OWN_NUMBER_WILL_OVERRIDE_THIS_ANSWER()
    {
        //the copy may promise the override only because ConnectContext performs the check (screen text must not describe behaviour the code lacks)
        var flow = new SetupFlow(new FakeProbes
        {
            Server = new Gatto.Core.Acquire.ConnectProbe("http://host:9", ["m"], NCtx: null),
        });
        flow.StartPastOpening();
        flow.Answer(SetupFlow.ForkConnect);

        var ask = Assert.IsType<WizardScreen.Ask>(flow.Answer(SetupFlow.Yes));
        //the key check is what ties the body assertion below to this screen
        Assert.Equal(SetupFlow.ContextKey, ask.Key);
        var body = string.Join("\n", (ask.BodyRows ?? []).Select(r => r.Text));
        Assert.Contains("overrides", body, StringComparison.Ordinal);
    }

    [Fact]
    public void A_SERVER_THAT_WONT_SAY_ITS_CONTEXT_gets_asked_and_the_offer_is_never_a_model_ceiling()
    {
        var flow = new SetupFlow(new FakeProbes
        {
            Server = new Gatto.Core.Acquire.ConnectProbe("http://host:9", ["m"], NCtx: null),
        });
        flow.StartPastOpening();
        flow.Answer(SetupFlow.ForkConnect);

        var ask = Assert.IsType<WizardScreen.Ask>(flow.Answer(SetupFlow.Yes));
        Assert.Equal(SetupFlow.ContextKey, ask.Key);
        //the offer is a pickable row holding the flat default, and its label must not show 8192
        var offer = Assert.IsType<AskOffer>(ask.Offer);
        //the label shows the thousands comma and the value does not, since one is read and the other is parsed from the same constant
        Assert.Contains(
            SetupFlow.OfferedServerContext.ToString("N0", System.Globalization.CultureInfo.InvariantCulture),
            offer.Label, StringComparison.Ordinal);
        Assert.Equal(
            SetupFlow.OfferedServerContext.ToString(System.Globalization.CultureInfo.InvariantCulture),
            offer.Value);
        Assert.DoesNotContain("8192", offer.Label, StringComparison.Ordinal);
        //the written budget is the number the label advertised, since both come from one constant
        flow.Answer(offer.Value);
        var written = Assert.IsType<WriteSet.Endpoint>(flow.Writes.UpsertEndpoint);
        Assert.Equal(SetupFlow.OfferedServerContext, written.Context);
    }

    [Theory]
    [InlineData(null, 8192, 8192)]    //the server reported its own context, so that number is written
    [InlineData("", null, 65536)]     //an empty answer takes the offer, which is the connect path's own default
    [InlineData("32768", null, 32768)]//the typed number is written, whether the offer arrived or not
    public void EVERY_TERMINATING_PATH_WRITES_A_POSITIVE_CONTEXT(string? typed, int? reported, int expected)
    {
        //the WriteSet.Endpoint.Context field is non-nullable, so this proves the flow never reaches the write without resolving a number
        var flow = new SetupFlow(new FakeProbes
        {
            Server = new Gatto.Core.Acquire.ConnectProbe("http://host:9", ["m"], reported),
        });
        flow.StartPastOpening();
        flow.Answer(SetupFlow.ForkConnect);
        flow.Answer(SetupFlow.Yes);
        if (typed is not null) flow.Answer(typed);   //the answer goes to an Ask, which takes free text

        var ep = Assert.IsType<WriteSet.Endpoint>(flow.Writes.UpsertEndpoint);
        Assert.Equal(expected, ep.Context);
        Assert.True(ep.Context > 0);
    }

    [Fact]
    public void NOTHING_ANSWERED_is_a_RETRY_state_and_never_a_dead_exit()
    {
        //the commonest cause is a server the user has not started, so the screen offers a retry rather than an exit
        var flow = new SetupFlow(new FakeProbes { Server = null });
        flow.StartPastOpening();

        var retry = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.ForkConnect));

        Assert.Equal(SetupFlow.RetryKey, retry.Key);
        Assert.Contains(retry.Options, o => o.Key == SetupFlow.Retry);
    }

    [Fact]
    public void LEAVING_writes_nothing_and_still_says_what_to_do_next()
    {
        //leaving never closes the console, and write-at-the-end means it costs nothing
        var flow = new SetupFlow(new FakeProbes { Server = new Gatto.Core.Acquire.ConnectProbe("http://host:9", ["their-model"], 8192) });
        flow.StartPastOpening();
        flow.Answer(SetupFlow.ForkConnect);
        var end = Assert.IsType<WizardScreen.Terminal>(flow.Answer(SetupFlow.No));

        //only the model write is asserted, since choosing the engine records a write of its own
        Assert.Null(flow.Writes.CreateModel);
        Assert.NotEqual("", end.NextStep);
    }

    private static Gatto.Core.Acquire.FoundModel Model(string path, long bytes = 4_000_000_000) =>
        new(path, bytes, null);

    [Fact]
    public void MODELS_ALREADY_ON_THE_MACHINE_are_offered_before_any_download()
    {
        //models already on the machine come first, since re-downloading a file the user has is wasteful and slow.
        var flow = new SetupFlow(new FakeProbes
        {
            Llama = @"C:\llama\llama-server.exe",
            Found = [Model(@"D:\models\qwen.gguf"), Model(@"D:\models\gemma.gguf")],
        });
        var choice = Assert.IsType<WizardScreen.Choice>(flow.StartPastEngine());

        Assert.Equal(SetupFlow.DiscoveredKey, choice.Key);
        Assert.Contains(choice.Options, o => o.Label == "qwen.gguf");
        Assert.Contains(choice.Options, o => o.Label == "gemma.gguf");
    }

    [Fact]
    public void The_discovery_copy_names_SIZES_AND_PATHS_and_never_a_vendor()
    {
        //the row names the file, since a vendor label would state an opinion about another publisher's weights
        var flow = new SetupFlow(new FakeProbes
        {
            Llama = @"C:\llama\llama-server.exe",
            Found = [Model(@"D:\models\qwen.gguf", 8_589_934_592)],
        });
        var choice = Assert.IsType<WizardScreen.Choice>(flow.StartPastEngine());

        var row = choice.Options[0];
        Assert.Contains("GB", row.Description!, StringComparison.Ordinal);
        Assert.Contains(@"D:\models", row.Description!, StringComparison.Ordinal);
        foreach (var vendor in new[] { "qwen ", "alibaba", "google", "meta", "mistral ai" })
            Assert.DoesNotContain(vendor, choice.Question, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AN_EMPTY_SCAN_is_normal_and_gets_no_guilt_screen()
    {
        //an empty scan is the normal state, so it goes straight to search with no remark
        var flow = new SetupFlow(new FakeProbes { Llama = @"C:\llama\llama-server.exe", Found = [] });
        var screen = flow.StartPastEngine();

        //the flow goes to search, and no screen on the way scolds the user for an empty machine
        Assert.Equal(SetupFlow.SearchKey, Key(screen));
        var everything = string.Join(" ", flow.Emitted.Select(Rendered));
        //match "you should" rather than a bare "should", since a neutral question may use the latter
        foreach (var scold in new[] { "you should", "need to", "must", "unfortunately", "sorry" })
            Assert.DoesNotContain(scold, everything, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_CHOSEN_FILE_STAYS_WHERE_IT_IS_because_adoption_is_for_steered_arrivals()
    {
        //a chosen file stays where it is, since moving a large file just because the user picked it would surprise them.
        var flow = new SetupFlow(new FakeProbes
        {
            Llama = @"C:\llama\llama-server.exe",
            Found = [Model(@"D:\models\qwen.gguf")],
        });
        flow.StartPastEngine();

        flow.Answer("0");

        Assert.Equal(@"D:\models\qwen.gguf", flow.Selected!.Path);

        //the recorded intent must point at the file's original path, since copying or relocating belongs to adoption
        Assert.Equal(@"D:\models\qwen.gguf", flow.Writes.CreateModel!.GgufPath);
    }

    [Fact]
    public void ANOTHER_FOLDER_re_scans_with_the_typed_root_normalised()
    {
        //a pasted path may have quotes or forward slashes, so both are normalised away (the fixture keeps a found model to reach a choice screen)
        var probes = new FakeProbes
        {
            Llama = @"C:\llama\llama-server.exe",
            Found = [Model(@"D:\models\qwen.gguf")],
        };
        var flow = new SetupFlow(probes);
        flow.StartPastEngine();
        flow.Answer(SetupFlow.Elsewhere);

        flow.Answer("  \"D:/weights\"  ");

        Assert.Equal(@"D:\weights", probes.ScanRoots[^1]);
    }

    [Fact]
    public void The_ORDERING_IS_LABELLED_and_says_among_these_rather_than_implying_a_hub_wide_rank()
    {
        //the sort is a hidden recommendation, so the screen names it, since an unqualified "most downloaded" would claim a Hub-wide ranking
        var probes = new FakeProbes
        {
            Llama = @"C:\llama\llama-server.exe",
            Rows = [Row("org/a"), Row("org/b")],
            CuratedPublisher = "unsloth",
        };
        var flow = new SetupFlow(probes);
        var curated = Assert.IsType<WizardScreen.Choice>(flow.StartPastEngine());

        Assert.Equal(SetupFlow.SearchKey, curated.Key);

        //the ordering words are literals here, since reading the same helper the screen builds from would pass by construction
        Assert.Equal(SetupFlow.ModelTitleFor(inSession: false), curated.Question);
        foreach (var ordering in new[] { "downloaded", "newest", "fits", "sorted" })
            Assert.DoesNotContain(ordering, curated.Question!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(curated.BodyRows!);
        var onCurated = ShelfBinding.StateSentence(curated.Shelf!, Gatto.Terminal.GlyphSet.Unicode);

        //the curated shelf names its publisher, which keeps "most downloaded" a claim about one shelf
        Assert.Contains("unsloth", onCurated, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("most downloaded", onCurated, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("newest", onCurated, StringComparison.OrdinalIgnoreCase);
        //the fit wording sits in the tier headings, since lifting the filter makes a shelf-wide "these fit this machine" false
        Assert.Contains(
            ShelfBinding.For(curated.Shelf!, [], new Gatto.Terminal.Theme(Gatto.Terminal.TermCaps.Plain), glyphs: GlyphSet.Unicode).HeadingsAt(100),
            h => h.Text.Contains("fit", StringComparison.OrdinalIgnoreCase));
        //the publisher half is what tells the two shelves apart, so the curated sentence must not name every approved publisher
        Assert.DoesNotContain("every approved publisher", onCurated, StringComparison.OrdinalIgnoreCase);

        //widening is a control rather than a row, and it opens a picker, so the wide shelf comes on the answer after the control
        flow.Answer(SetupFlow.CtlPublisher);
        var broad = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.PickEveryPublisher));
        var onBroad = ShelfBinding.StateSentence(broad.Shelf!, Gatto.Terminal.GlyphSet.Unicode);

        //assert the view the engine was asked for, since wording alone would pass when the key changes nothing.
        Assert.Equal(
            [Gatto.Core.Acquire.HubSearchView.Curated, Gatto.Core.Acquire.HubSearchView.Broadened],
            probes.Views);

        Assert.Contains("downloaded", onBroad, StringComparison.OrdinalIgnoreCase);
        //the label states the mechanism and never grades, so the fit half sits in the tier headings where a shelf-wide claim stays true
        Assert.Contains(
            ShelfBinding.For(broad.Shelf!, [], new Gatto.Terminal.Theme(Gatto.Terminal.TermCaps.Plain), glyphs: GlyphSet.Unicode).HeadingsAt(100),
            h => h.Text.Contains("fit", StringComparison.OrdinalIgnoreCase));
        //the header names the set that was searched, which keeps it from reading as a Hub-wide ranking
        Assert.StartsWith("every approved publisher · ", onBroad, StringComparison.Ordinal);
        Assert.DoesNotContain("newest", onBroad, StringComparison.OrdinalIgnoreCase);

        //neither shelf's sentence may contain the word "best"
        foreach (var said in new[] { onCurated, onBroad })
            Assert.DoesNotContain("best", said, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void THE_WAY_OUT_OF_THE_CURATED_SHELF_IS_ON_THE_SCREEN_and_a_wide_shelf_does_not_offer_it()
    {
        //curation narrows a default, so the way out is a control. the wide shelf draws no publisher slot, since it has no curated publisher to name
        var flow = new SetupFlow(new FakeProbes
        {
            Llama = @"C:\llama\llama-server.exe",
            Rows = [Row("org/a")],
            CuratedPublisher = "unsloth",
        });
        var curated = Assert.IsType<WizardScreen.Choice>(flow.StartPastEngine());

        //the slot is drawn, is a Tab stop and picks on Enter, all from one predicate
        Assert.True(curated.Shelf!.HasPublisherSlot);
        Assert.Contains(Gatto.Terminal.Region.Publisher,
            Gatto.Cli.Setup.Tui.Shelf.Regions(curated.Shelf!, 100, 0, curated.Door is not null));
        //widening must not exist as a row and as a control at once
        Assert.DoesNotContain(curated.Options, o => o.Key == SetupFlow.Broaden);

        flow.Answer(SetupFlow.CtlPublisher);
        var broad = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.PickEveryPublisher));
        Assert.Contains("every approved publisher",
            ShelfBinding.StateSentence(broad.Shelf!, Gatto.Terminal.GlyphSet.Unicode), StringComparison.Ordinal);
        //the wide shelf names no curated publisher, so it draws no slot and reaches the picker by answering the control
        Assert.False(broad.Shelf!.HasPublisherSlot);

        //the numbered-row promise is about the screen's total, so a shelf keeps at most nine rows and controls take the keys.
        Assert.True(curated.Options.Count <= 9, $"{curated.Options.Count} rows");
        Assert.True(broad.Options.Count <= 9, $"{broad.Options.Count} rows");
    }

    [Fact]
    public void A_CURATED_SEARCH_THAT_SEARCHED_EVERYONE_does_not_offer_to_widen_itself()
    {
        //with no default publisher slug the engine searched every org, so the screen must say so
        var flow = new SetupFlow(new FakeProbes
        {
            Llama = @"C:\llama\llama-server.exe",
            Rows = [Row("org/a")],
            CuratedPublisher = null,
        });
        var search = Assert.IsType<WizardScreen.Choice>(flow.StartPastEngine());

        Assert.DoesNotContain(search.Options, o => o.Key == SetupFlow.Broaden);

        //the key opens a picker of every approved publisher, so the row stays away (one deed gets one affordance)
        Assert.Contains('f', ShelfControls.Keys(search.Shelf!));
        Assert.Contains("f publisher", ShelfControls.Strip(search.Shelf!, Gatto.Terminal.GlyphSet.Unicode), StringComparison.Ordinal);

        //the sentence is computed from the axis the rows are in, so a label never describes the other view's sort
        var said = ShelfBinding.StateSentence(search.Shelf!, Gatto.Terminal.GlyphSet.Unicode);
        Assert.Contains("every approved publisher", said, StringComparison.Ordinal);
        //both views use the same downloads axis, so the order half reads alike, and each half stands on its own
        Assert.Contains("most downloaded", said, StringComparison.Ordinal);
        //the header names the set that was searched, which keeps it from reading as a Hub-wide ranking
        Assert.StartsWith("every approved publisher · ", said, StringComparison.Ordinal);
    }

    [Fact]
    public void AN_UNREACHABLE_HUB_degrades_to_the_paths_that_still_work()
    {
        //zero rows means the Hub is unreachable, and the wizard continues through a typed folder path
        var flow = new SetupFlow(new FakeProbes { Llama = @"C:\llama\llama-server.exe", Rows = [] });
        var search = Assert.IsType<WizardScreen.Choice>(flow.StartPastEngine());

        Assert.Contains(search.Options, o => o.Key == SetupFlow.Elsewhere);
        foreach (var word in new[] { "error", "failed", "sorry" })
            Assert.DoesNotContain(word, search.Question, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void THE_ESCAPE_HATCH_IS_ON_THE_SEARCH_SCREEN_so_an_empty_scan_never_strands_anyone()
    {
        //a user with models on an unswept drive reaches search with nothing offered, and would otherwise download a file they own
        var flow = new SetupFlow(new FakeProbes
        {
            Llama = @"C:\llama\llama-server.exe",
            Found = [],
            Rows = [Row("org/a")],
        });
        var search = Assert.IsType<WizardScreen.Choice>(flow.StartPastEngine());

        //the row stays in the flow list, since the plain face binds no "d" and this is its only folder route
        Assert.Contains(search.Options, o => o.Key == SetupFlow.Elsewhere);

        //taking it opens the folder ask
        var ask = Assert.IsType<WizardScreen.Ask>(flow.Answer(SetupFlow.Elsewhere));
        Assert.Equal(SetupFlow.ScanPathKey, ask.Key);
    }

    [Fact]
    public void PICKING_A_SEARCH_ROW_records_it_and_still_writes_nothing()
    {
        var flow = new SetupFlow(new FakeProbes
        {
            Llama = @"C:\llama\llama-server.exe",
            Rows = [Row("org/a"), Row("org/b")],
        });
        flow.StartPastEngine();

        flow.Answer("1");

        Assert.Equal("org/b", flow.Picked!.RepoId);
        //only the model write is asserted, since choosing the engine records a write of its own
        Assert.Null(flow.Writes.CreateModel);   //picking a row records no download, since the browser does that later
    }

    private static SetupFlow AtSearch(FakeProbes probes)
    {
        var flow = new SetupFlow(probes);
        flow.StartPastEngine();
        return flow;
    }

    [Fact]
    public void A_USER_MAY_NAME_ANY_MODEL_because_the_allowlist_shapes_BROWSING_only()
    {
        var probes = new FakeProbes
        {
            Llama = @"C:\llama\llama-server.exe",
            TypedId = new TypedIdOutcome.Ok(Row("someone/not-on-the-list")),
        };
        var flow = AtSearch(probes);

        flow.Answer(SetupFlow.TypeAnId);
        flow.Answer("someone/not-on-the-list");

        Assert.Equal("someone/not-on-the-list", flow.Picked!.RepoId);
        Assert.Contains("someone/not-on-the-list", probes.TypedIds);
    }

    [Fact]
    public void A_MALFORMED_ID_IS_A_TYPO_and_never_reported_as_the_hub_being_down()
    {
        //the guard rejects the id before any request, so the input is at fault and an outage message would be wrong
        var flow = AtSearch(new FakeProbes
        {
            Llama = @"C:\llama\llama-server.exe",
            TypedId = new TypedIdOutcome.Malformed(),
        });
        flow.Answer(SetupFlow.TypeAnId);

        var again = Assert.IsType<WizardScreen.Ask>(flow.Answer("https://huggingface.co/org/model"));

        Assert.Contains("org/model", again.Label, StringComparison.Ordinal);   //org/model is the shape the label must show
        foreach (var wrong in new[] { "unreachable", "offline", "connection", "network" })
            Assert.DoesNotContain(wrong, again.Label, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_GATED_REPO_says_what_to_DO_rather_than_that_it_failed()
    {
        var flow = AtSearch(new FakeProbes
        {
            Llama = @"C:\llama\llama-server.exe",
            TypedId = new TypedIdOutcome.Unreachable(Gated: true),
        });
        flow.Answer(SetupFlow.TypeAnId);

        var again = Assert.IsType<WizardScreen.Ask>(flow.Answer("org/gated"));
        Assert.Contains("licence", again.Label, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AN_UNREACHABLE_ID_STAYS_ON_THE_ASK_so_a_retry_costs_one_keystroke()
    {
        //a wrong name is the likeliest cause, so the ask stays open and a retry costs one keystroke
        var flow = AtSearch(new FakeProbes
        {
            Llama = @"C:\llama\llama-server.exe",
            TypedId = new TypedIdOutcome.Unreachable(false),
        });
        flow.Answer(SetupFlow.TypeAnId);

        var again = flow.Answer("org/typo");
        Assert.Equal(SetupFlow.TypedIdKey, Key(again));
    }

    //a repo that answers with no usable quant is its own outcome, so its sentence must not read as an outage
    [Fact]
    public void A_MODEL_THAT_DOES_NOT_FIT_IS_NOT_REPORTED_AS_AN_OUTAGE()
    {
        var flow = AtSearch(new FakeProbes
        {
            Llama = @"C:\llama\llama-server.exe",
            TypedId = new TypedIdOutcome.NoUsableQuant(),
        });
        flow.Answer(SetupFlow.TypeAnId);
        var again = Assert.IsType<WizardScreen.Ask>(flow.Answer("someone/enormous"));

        Assert.Equal(SetupFlow.TypedIdKey, again.Key);
        Assert.Contains("Found it", again.Label!, StringComparison.Ordinal);
        //the reply must not claim that gatto could not look, or judge the model
        Assert.DoesNotContain("look that one up", again.Label!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("bad", again.Label!, StringComparison.OrdinalIgnoreCase);
    }

    private static FakeProbes WithProjector(Gatto.Core.Models.FitRegime alone = Gatto.Core.Models.FitRegime.FitsGpu,
        Gatto.Core.Models.FitRegime paired = Gatto.Core.Models.FitRegime.FitsGpu) => new()
    {
        Llama = @"C:\llama\llama-server.exe",
        Found = [new Gatto.Core.Acquire.FoundModel(@"D:\m\qwen.gguf", 4_000_000_000, null)],
        Projector = (@"D:\m\mmproj-qwen-f16.gguf", 1_100_000_000),
        Pair = (alone, paired),
    };

    //yes leads the option list, yet the projector is still asked for rather than enabled by itself
    [Fact]
    public void THE_PROJECTOR_ASK_LEADS_WITH_YES_and_consent_is_never_auto_armed()
    {
        //vision stays an addition the screen asks for, so the projector is never armed on its own
        var flow = new SetupFlow(WithProjector());
        flow.StartPastEngine();
        var ask = Assert.IsType<WizardScreen.Choice>(flow.Answer("0"));

        Assert.Equal(SetupFlow.ProjectorKey, ask.Key);
        Assert.Equal(SetupFlow.Yes, ask.Options[0].Key);
        Assert.Contains("mmproj-qwen-f16.gguf", string.Join(" ", ask.BodyRows!), StringComparison.Ordinal);

        flow.Answer(SetupFlow.No);
        Assert.Null(flow.Writes.Projector);
        Assert.NotNull(flow.Writes.CreateModel);
    }

    [Fact]
    public void SAYING_YES_RECORDS_THE_PROJECTOR_as_an_intent_like_every_other_write()
    {
        var flow = new SetupFlow(WithProjector());
        flow.StartPastEngine();
        flow.Answer("0");
        flow.Answer(SetupFlow.Yes);

        Assert.Equal(@"D:\m\mmproj-qwen-f16.gguf", flow.Writes.Projector);
    }

    [Fact]
    public void A_MODEL_WITH_NO_PROJECTOR_BESIDE_IT_IS_NEVER_ASKED()
    {
        //a null probe result means no projector was found, so the screen asks nothing
        var flow = new SetupFlow(new FakeProbes
        {
            Llama = @"C:\llama\llama-server.exe",
            Found = [new Gatto.Core.Acquire.FoundModel(@"D:\m\qwen.gguf", 4_000_000_000, null)],
        });
        flow.StartPastEngine();
        flow.Answer("0");

        Assert.DoesNotContain(flow.Emitted, s => s is WizardScreen.Choice c && c.Key == SetupFlow.ProjectorKey);
        Assert.NotNull(flow.Writes.CreateModel);
    }

    [Fact]
    public void THE_ASK_PRICES_THE_REGIME_and_says_nothing_when_the_answer_does_not_change()
    {
        //a projector costs about a fixed size, so the screen shows the pair's regime and warns only when the answer changes it
        var costs = new SetupFlow(WithProjector(Gatto.Core.Models.FitRegime.FitsGpu, Gatto.Core.Models.FitRegime.FitsRamOnly));
        costs.StartPastEngine();
        var warned = Assert.IsType<WizardScreen.Choice>(costs.Answer("0"));
        var warnedBody = string.Join(" ", warned.BodyRows!);

        Assert.Contains(SearchRow.FitWords(Gatto.Core.Models.FitRegime.FitsRamOnly, Gatto.Core.Hardware.MachineShape.Discrete, Gatto.Terminal.GlyphSet.Unicode), warnedBody, StringComparison.Ordinal);
        Assert.Contains(SearchRow.FitWords(Gatto.Core.Models.FitRegime.FitsGpu, Gatto.Core.Hardware.MachineShape.Discrete, Gatto.Terminal.GlyphSet.Unicode), warnedBody, StringComparison.Ordinal);

        var free = new SetupFlow(WithProjector(Gatto.Core.Models.FitRegime.FitsGpu, Gatto.Core.Models.FitRegime.FitsGpu));
        free.StartPastEngine();
        var quiet = Assert.IsType<WizardScreen.Choice>(free.Answer("0"));

        Assert.Single(quiet.BodyRows!);   //the single row is the file itself, with no fit sentence
    }

    [Fact]
    public void THE_ASK_NEVER_PRICES_A_CONVERSATION_WITH_PICTURES_IN_IT()
    {
        //the screen prices the two resident files, so it must not imply a cost per image
        var flow = new SetupFlow(WithProjector(Gatto.Core.Models.FitRegime.FitsGpu, Gatto.Core.Models.FitRegime.FitsRamOnly));
        flow.StartPastEngine();
        var ask = Assert.IsType<WizardScreen.Choice>(flow.Answer("0"));
        var body = string.Join(" ", ask.BodyRows!);

        foreach (var banned in new[] { "per image", "each image", "tokens", "slower per" })
            Assert.DoesNotContain(banned, body, StringComparison.OrdinalIgnoreCase);
    }

    //the flow pauses for the writer right after this ask, so no screen follows it
    [Fact]
    public void BACKING_OUT_OF_THE_PROJECTOR_ASK_ARMS_NOTHING()
    {
        var flow = new SetupFlow(WithProjector());
        flow.StartPastEngine();
        var ask = Assert.IsType<WizardScreen.Choice>(flow.Answer("0"));
        Assert.Equal(SetupFlow.ProjectorKey, ask.Key);

        flow.Answer(SetupFlow.BackKey);

        //a screen shown is not a screen answered, so nothing about vision may remain after the user backs out.
        Assert.Null(flow.Writes.Projector);
    }

    private static FakeProbes InDownloads(bool crossVolume = false, string? storageSense = null) => new()
    {
        Llama = @"C:\llama\llama-server.exe",
        Found = [new Gatto.Core.Acquire.FoundModel(@"C:\Users\me\Downloads\qwen.gguf", 4_000_000_000, null)],
        Move = new MoveOffer(@"C:\Users\me\.gatto\models", crossVolume, 30_000_000_000, storageSense),
    };

    //the byte count is a variable here, so a price of zero and one below the resolution are both expressible
    private static FakeProbes InDownloadsSized(bool crossVolume, long bytes) => new()
    {
        Llama = @"C:\llama\llama-server.exe",
        Found = [new Gatto.Core.Acquire.FoundModel(@"C:\Users\me\Downloads\qwen.gguf", 4_000_000_000, null)],
        Move = new MoveOffer(@"C:\Users\me\.gatto\models", crossVolume, bytes, null),
    };

    private static (SetupFlow Flow, WizardScreen Landed) AtMove(FakeProbes probes)
    {
        var flow = new SetupFlow(probes);
        flow.StartPastEngine();
        return (flow, flow.Answer("0"));
    }

    [Fact]
    public void THE_MOVE_OFFER_DEFAULTS_TO_KEEPING_IT_WHERE_IT_IS()
    {
        //a multi-gigabyte copy is a choice, and the first option is the default on this widget
        var (flow, landed) = AtMove(InDownloads());
        var ask = Assert.IsType<WizardScreen.Choice>(landed);

        Assert.Equal(SetupFlow.MoveKey, ask.Key);
        Assert.Equal(SetupFlow.No, ask.Options[0].Key);
        Assert.Equal(3, ask.Options.Count);

        flow.Answer(SetupFlow.No);
        Assert.Null(flow.Writes.MoveTo);
        Assert.NotNull(flow.Writes.CreateModel);
    }

    [Fact]
    public void A_MODEL_OUTSIDE_DOWNLOADS_IS_NEVER_ASKED()
    {
        //gatto mandates no folder, and the offer exists only because Windows can clean Downloads.
        var (flow, _) = AtMove(new FakeProbes
        {
            Llama = @"C:\llama\llama-server.exe",
            Found = [new Gatto.Core.Acquire.FoundModel(@"D:\models\qwen.gguf", 4_000_000_000, null)],
        });

        Assert.DoesNotContain(flow.Emitted, s => s is WizardScreen.Choice c && c.Key == SetupFlow.MoveKey);
        Assert.NotNull(flow.Writes.CreateModel);
    }

    [Fact]
    public void A_CROSS_VOLUME_MOVE_SAYS_COPY_AND_THE_SIZE_BEFORE_STARTING()
    {
        var (_, landed) = AtMove(InDownloads(crossVolume: true));
        var body = string.Join(" ", Assert.IsType<WizardScreen.Choice>(landed).BodyRows!);

        Assert.Contains("COPY", body, StringComparison.Ordinal);
        //30e9 bytes converts to 27.94 GB through Gb(), so the expected text is 27.9 GB
        Assert.Contains("27.9 GB", body, StringComparison.Ordinal);
    }

    [Fact]
    public void A_SAME_VOLUME_MOVE_SAYS_NOTHING_ABOUT_COST()
    {
        //a same-volume rename costs nothing, so a cost warning would misstate it
        var (_, landed) = AtMove(InDownloads(crossVolume: false));
        var body = string.Join(" ", Assert.IsType<WizardScreen.Choice>(landed).BodyRows!);

        Assert.DoesNotContain("COPY", body, StringComparison.Ordinal);
        Assert.DoesNotContain("GB", body, StringComparison.Ordinal);
    }

    [Fact]
    public void THE_COPY_PRICE_IS_A_FIGURE_OR_NOTHING_never_zero()
    {
        //a pricing screen must never show 0 GB, since zero reads as free. the zero comes from the byte count rather than the formatter
        var priced = string.Join(" ", Assert.IsType<WizardScreen.Choice>(
            AtMove(InDownloads(crossVolume: true)).Landed).BodyRows!);
        Assert.Contains("COPY 27.9 GB", priced, StringComparison.Ordinal);

        var unmeasured = string.Join(" ", Assert.IsType<WizardScreen.Choice>(
            AtMove(InDownloadsSized(crossVolume: true, bytes: 0)).Landed).BodyRows!);
        Assert.Contains("COPY the file rather than rename it", unmeasured, StringComparison.Ordinal);
        //no size that reads as zero may appear anywhere on the row.
        Assert.DoesNotContain("0 GB", unmeasured, StringComparison.Ordinal);
        //the row still says the move is a copy, even without a number.
        Assert.Contains("another drive", unmeasured, StringComparison.Ordinal);
    }

    [Fact]
    public void A_TINY_MODEL_IS_PRICED_BELOW_THE_RESOLUTION_not_at_zero()
    {
        //one size rule serves every screen, so a tiny file reads "<0.1 GB" here as it does elsewhere
        var tiny = string.Join(" ", Assert.IsType<WizardScreen.Choice>(
            AtMove(InDownloadsSized(crossVolume: true, bytes: 40L * 1024 * 1024)).Landed).BodyRows!);

        Assert.Contains("<0.1 GB", tiny, StringComparison.Ordinal);
        Assert.DoesNotContain("0 GB", tiny.Replace("<0.1 GB", "", StringComparison.Ordinal),
            StringComparison.Ordinal);
    }

    [Fact]
    public void STORAGE_SENSE_NAMES_THE_CASE_WHEN_READ_AND_IS_SILENT_WHEN_NOT()
    {
        //the policy read informs and never gates, so an unreadable policy keeps the generic sentence
        var armed = string.Join(" ", Assert.IsType<WizardScreen.Choice>(
            AtMove(InDownloads(storageSense: "armed")).Landed).BodyRows!);
        Assert.Contains("currently armed", armed, StringComparison.Ordinal);

        var unknown = string.Join(" ", Assert.IsType<WizardScreen.Choice>(
            AtMove(InDownloads()).Landed).BodyRows!);
        Assert.DoesNotContain("armed", unknown, StringComparison.Ordinal);
        //the generic line stands either way, so the read never gates it
        Assert.Contains("Downloads folder", unknown, StringComparison.Ordinal);
    }

    [Fact]
    public void ACCEPTING_THE_SUGGESTION_RECORDS_THE_FOLDER_THAT_WAS_SHOWN()
    {
        //the recorded folder is the one shown on the screen, since a second derivation would let the two part company
        var (flow, _) = AtMove(InDownloads());
        flow.Answer(SetupFlow.Yes);

        Assert.Equal(@"C:\Users\me\.gatto\models", flow.Writes.MoveTo);
    }

    [Fact]
    public void CHOOSING_A_FOLDER_ASKS_FOR_IT_AND_RECORDS_WHAT_WAS_TYPED()
    {
        var (flow, _) = AtMove(InDownloads());
        var ask = Assert.IsType<WizardScreen.Ask>(flow.Answer(SetupFlow.Elsewhere));
        Assert.Equal(SetupFlow.MovePathKey, ask.Key);

        //a folder that does not exist yet is legal, since adoption creates it, so only an empty answer is rejected.
        Assert.Null(ask.Validate(@"E:\not-yet"));
        Assert.NotNull(ask.Validate("   "));

        flow.Answer(@"E:\weights");
        Assert.Equal(@"E:\weights", flow.Writes.MoveTo);
    }

    //the fixture needs a projector, since the move answer would otherwise run to the write pause with nothing to go back from
    [Fact]
    public void BACKING_OUT_OF_THE_MOVE_ASK_MOVES_NOTHING()
    {
        var probes = new FakeProbes
        {
            Llama = @"C:\llama\llama-server.exe",
            Found = [new Gatto.Core.Acquire.FoundModel(@"C:\Users\me\Downloads\qwen.gguf", 4_000_000_000, null)],
            Move = new MoveOffer(@"C:\Users\me\.gatto\models", false, 30_000_000_000, null),
            Projector = (@"C:\Users\me\Downloads\mmproj-qwen-f16.gguf", 1_100_000_000),
            Pair = (Gatto.Core.Models.FitRegime.FitsGpu, Gatto.Core.Models.FitRegime.FitsGpu),
        };

        var (flow, _) = AtMove(probes);
        flow.Answer(SetupFlow.Yes);
        Assert.Equal(@"C:\Users\me\.gatto\models", flow.Writes.MoveTo);

        //the projector ask sits between this intent and the write pause, so backing out is possible.
        var projector = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.BackKey));
        Assert.Equal(SetupFlow.MoveKey, projector.Key);

        flow.Answer(SetupFlow.No);
        Assert.Null(flow.Writes.MoveTo);
    }

    //the name is a real architecture the pinned build cannot load, so the fixture matches the shipped set
    private static FakeProbes WithCustomArch(string? arch = "futurearch") => new()
    {
        Llama = @"C:\llama\llama-server.exe",
        Found = [new Gatto.Core.Acquire.FoundModel(@"D:\m\ling.gguf", 4_000_000_000, null)],
        Architecture = arch,
    };

    //drive to where the build offer appears and return the flow with the screen that came back (the flow exposes no reader for it)
    private static (SetupFlow Flow, WizardScreen Landed) AtCustomBuild(FakeProbes probes)
    {
        var flow = new SetupFlow(probes);
        flow.StartPastEngine();
        return (flow, flow.Answer("0"));
    }

    [Fact]
    public void A_MODEL_THE_BUILD_CANNOT_LOAD_IS_OFFERED_A_BUILD_FOR_THIS_PACK()
    {
        var (flow, landed) = AtCustomBuild(WithCustomArch());
        var ask = Assert.IsType<WizardScreen.Choice>(landed);

        Assert.Equal(SetupFlow.CustomBuildKey, ask.Key);
        Assert.Equal(SetupFlow.No, ask.Options[0].Key);

        var body = string.Join(" ", ask.BodyRows!);
        Assert.Contains("futurearch", body, StringComparison.Ordinal);   //the architecture string is the actionable fact, so the message must name it.
        Assert.Contains("b11071", body, StringComparison.Ordinal);        //the row names the pinned release, since the claim is about that build
        //the screen must say the change covers this model only, since the wording otherwise reads like a whole-install change.
        Assert.Contains("this model only", body, StringComparison.Ordinal);
        //the screen must not call the model unsupported.
        Assert.DoesNotContain("unsupported", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SAYING_NO_SCAFFOLDS_THE_PACK_ANYWAY_because_inform_never_refuse()
    {
        //declining the build must still write the model, since refusing to record a real model decides for the user
        var (flow, _) = AtCustomBuild(WithCustomArch());
        flow.Answer(SetupFlow.No);

        Assert.NotNull(flow.Writes.CreateModel);
        Assert.Null(flow.Writes.ModelLlamaServer);
    }

    [Fact]
    public void SAYING_YES_RECORDS_THE_BUILD_AGAINST_THE_PACK_and_never_the_machine()
    {
        var (flow, _) = AtCustomBuild(WithCustomArch());
        var ask = Assert.IsType<WizardScreen.Ask>(flow.Answer(SetupFlow.Yes));
        Assert.Equal(SetupFlow.CustomBuildPathKey, ask.Key);

        flow.Answer(@"C:\forks\bailing\llama-server.exe");

        Assert.Equal(@"C:\forks\bailing\llama-server.exe", flow.Writes.ModelLlamaServer);
        //the per-model build never touches the machine's own binary, so the two paths live in separate members
        Assert.Equal(@"C:\llama\llama-server.exe", flow.Writes.LlamaServer);
        Assert.NotNull(flow.Writes.CreateModel);
    }

    [Fact]
    public void A_MODEL_THE_BUILD_CAN_LOAD_IS_NEVER_ASKED()
    {
        //qwen3 is in the shipped set, so nothing is asked. the screen never promises that a listed model loads fine
        var (flow, _) = AtCustomBuild(WithCustomArch("qwen3"));

        Assert.DoesNotContain(flow.Emitted,
            s => s is WizardScreen.Choice c && c.Key == SetupFlow.CustomBuildKey);
        Assert.NotNull(flow.Writes.CreateModel);
    }

    [Fact]
    public void AN_UNREADABLE_HEADER_ASKS_NOTHING()
    {
        //a null read is gatto's own failure, so a warning would turn a parse error into a claim about the user's file
        var (flow, _) = AtCustomBuild(WithCustomArch(arch: null));

        Assert.DoesNotContain(flow.Emitted,
            s => s is WizardScreen.Choice c && c.Key == SetupFlow.CustomBuildKey);
        Assert.NotNull(flow.Writes.CreateModel);
    }

    [Fact]
    public void A_BUILD_OF_THE_WRONG_SHAPE_RE_ASKS_WITH_THE_GLOBAL_PATHS_OWN_SENTENCE()
    {
        //a wrong build shape is wrong under either key, so one rule serves both paths and this asserts they agree
        var (flow, _) = AtCustomBuild(new FakeProbes
        {
            Llama = @"C:\llama\llama-server.exe",
            //the machine's own engine is fine and only the typed per-model path is wrong, which is why the two shapes differ
            EngineShape = Gatto.Core.Tools.ProbeShape.ClassicServer,
            Found = [new Gatto.Core.Acquire.FoundModel(@"D:\m\ling.gguf", 4_000_000_000, null)],
            Architecture = "futurearch",
            Shape = Gatto.Core.Tools.ProbeShape.NotClassic,
        });
        flow.Answer(SetupFlow.Yes);

        var again = Assert.IsType<WizardScreen.Ask>(flow.Answer(@"C:\wrong\llama.exe"));
        Assert.Equal(SetupFlow.CustomBuildPathKey, again.Key);
        Assert.Contains("single combined program", again.Label!, StringComparison.Ordinal);
        Assert.Null(flow.Writes.ModelLlamaServer);
    }

    //the assertion is on the applied intent, since an abandoned choice must not reach disk
    [Fact]
    public void GOING_BACK_ACROSS_THE_PROJECTOR_ASK_UNARMS_IT_now_that_a_screen_sits_after_it()
    {
        var flow = new SetupFlow(new FakeProbes
        {
            Llama = @"C:\llama\llama-server.exe",
            Found = [new Gatto.Core.Acquire.FoundModel(@"D:\m\qwen.gguf", 4_000_000_000, null)],
            Projector = (@"D:\m\mmproj-qwen-f16.gguf", 1_100_000_000),
            Pair = (Gatto.Core.Models.FitRegime.FitsGpu, Gatto.Core.Models.FitRegime.FitsGpu),
            Architecture = "futurearch",
        });
        flow.StartPastEngine();
        flow.Answer("0");

        var offer = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.Yes));
        Assert.Equal(@"D:\m\mmproj-qwen-f16.gguf", flow.Writes.Projector);

        //the build offer is the screen that sits between the intent and the pause.
        Assert.Equal(SetupFlow.CustomBuildKey, offer.Key);

        var back = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.BackKey));
        Assert.Equal(SetupFlow.ProjectorKey, back.Key);
        flow.Answer(SetupFlow.No);

        //the abandoned answer must not survive, since the projector would otherwise reach disk for a choice the user reversed
        Assert.Null(flow.Writes.Projector);
    }

    [Fact]
    public void A_MISSING_REPO_DOES_NOT_CLAIM_TO_KNOW_IT_IS_MISSING()
    {
        //a 401 covers an absent repo and a private one, so the reply may not name only one cause.
        var flow = AtSearch(new FakeProbes
        {
            Llama = @"C:\llama\llama-server.exe",
            TypedId = new TypedIdOutcome.Unreachable(false),
        });
        flow.Answer(SetupFlow.TypeAnId);
        var again = Assert.IsType<WizardScreen.Ask>(flow.Answer("someone/nope"));

        Assert.Contains("private", again.Label!, StringComparison.OrdinalIgnoreCase);
    }

    //zero rows has two causes, and this test asserts the screen tells them apart
    [Fact]
    public void NOTHING_FITS_AND_THE_HUB_IS_UNREACHABLE_ARE_DIFFERENT_SCREENS()
    {
        //nothing fits on a small machine, and its network is fine, so the screen must not blame the connection.
        var small = AtSearch(new FakeProbes
        {
            Llama = @"C:\llama\llama-server.exe",
            Rows = [],
            SearchOutcome = Gatto.Core.Acquire.HubSearchCause.NothingFits,
        });
        var smallBody = string.Join(" ", Assert.IsType<WizardScreen.Choice>(small.Emitted[^1]).BodyRows!);

        //here every publisher request failed, so the Hub is the cause and the machine is fine.
        var offline = AtSearch(new FakeProbes
        {
            Llama = @"C:\llama\llama-server.exe",
            Rows = [],
            SearchOutcome = Gatto.Core.Acquire.HubSearchCause.HubFailed,
        });
        var offlineBody = string.Join(" ", Assert.IsType<WizardScreen.Choice>(offline.Emitted[^1]).BodyRows!);

        Assert.NotEqual(smallBody, offlineBody);
        Assert.Contains("this machine", smallBody, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("couldn't reach", offlineBody, StringComparison.OrdinalIgnoreCase);
        //the cause changed and the exits did not, so both screens still offer a folder and a typed id.
        foreach (var flow in new[] { small, offline })
        {
            var screen = Assert.IsType<WizardScreen.Choice>(flow.Emitted[^1]);
            Assert.Contains(screen.Options, o => o.Key == SetupFlow.Elsewhere);
            Assert.Contains(screen.Options, o => o.Key == SetupFlow.TypeAnId);
        }
    }

    //the model is intended and nothing applied yet, so a test that reads CreateModel must do it before the writer runs
    private static SetupFlow AtScaffoldPause(FakeProbes probes)
    {
        var flow = new SetupFlow(probes);
        flow.StartPastEngine();
        flow.Answer("0");                      //0 picks the discovered model
        Assert.True(flow.NeedsWritesApplied);  //the write must be due before the offer appears
        return flow;
    }

    //walk through the write point to where the offer appears, resuming under the id the writer would produce
    private static SetupFlow AtChosenModel(FakeProbes probes)
    {
        var flow = AtScaffoldPause(probes);
        Resumed(flow, "qwen");
        return flow;
    }

    private static FakeProbes WithModel(AuditionOutcome audition = AuditionOutcome.Passed) => new()
    {
        Llama = @"C:\llama\llama-server.exe",
        Found = [Model(@"D:\models\qwen.gguf")],
        Audition = audition,
    };

    [Fact]
    public void THE_PICKED_REPO_ID_SURVIVES_THE_WRITE_PAUSE_and_reaches_the_audition()
    {
        //the badge needs the repo id the search recorded, since a write pause that lost Picked would write a badge with none
        var probes = new FakeProbes { Llama = @"C:\llama\llama-server.exe", Rows = [Row("org/m")] };
        var flow = new SetupFlow(probes);
        flow.StartPastEngine();       //nothing was discovered, so this lands on the search shelf.
        flow.Answer("0");                       //0 is the Hub row pick, which opens the download screen
        probes.Found = [Model(@"D:\Downloads\model-Q4_K_M.gguf")];
        flow.Answer(SetupFlow.Landed);      //the file arrived, so the flow adopts it and reaches the scaffold pause
        Resumed(flow, "m");
        CheckRan(flow);                         //accepting the offer is what lets the check run

        Assert.Equal("org/m", Assert.Single(probes.AuditionedRepo));
    }

    [Fact]
    public void A_DISCOVERED_MODEL_CARRIES_NO_REPO_ID_because_there_is_no_provenance_to_claim()
    {
        //a model found on disk has no listing, so null is honest here (a name taken from the file would be invented)
        var probes = WithModel();
        var flow = AtChosenModel(probes);
        CheckRan(flow);

        Assert.Null(Assert.Single(probes.AuditionedRepo));
    }

    [Fact]
    public void The_OFFER_is_made_before_the_model_becomes_the_daily_driver()
    {
        var flow = AtChosenModel(WithModel());

        var offer = Assert.IsType<WizardScreen.Choice>(flow.Emitted[^1]);
        Assert.Equal(SetupFlow.AuditionOfferKey, offer.Key);
        Assert.Contains(offer.Options, o => o.Key == SetupFlow.Skip);   //the check is never compulsory, so the offer has a skip
    }

    [Fact]
    public void The_offer_quotes_a_RANGE_and_never_a_single_number()
    {
        //measured waits ranged from 72 to 128 seconds, so the offer gives a range
        var flow = AtChosenModel(WithModel());
        var offer = Assert.IsType<WizardScreen.Choice>(flow.Emitted[^1]);
        var body = string.Join(" ", offer.BodyRows!);

        Assert.Contains("a minute or two", body, StringComparison.OrdinalIgnoreCase);
        foreach (var promise in new[] { "about a minute", "one minute", "30 seconds", "takes a minute." })
            Assert.DoesNotContain(promise, body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_FAILED_CHECK_WARNS_AND_RECOMMENDS_AND_STILL_PROCEEDS_ON_INSISTENCE()
    {
        //a failed check warns and recommends another model, then the user can keep their choice
        var flow = AtChosenModel(WithModel(audition: AuditionOutcome.Failed));

        var failed = Assert.IsType<WizardScreen.Choice>(CheckRan(flow));
        Assert.Equal(SetupFlow.AuditionFailedKey, failed.Key);
        Assert.Equal(SetupFlow.PickAnother, failed.Options[0].Key);   //the recommendation is the first option.
        Assert.Contains(failed.Options, o => o.Key == SetupFlow.Anyway);

        //insisting still reaches the proof screen and the run ends in success
        Assert.Equal(SetupFlow.SummaryKey, Key(Answered(flow, SetupFlow.Anyway)));
        var end = Assert.IsType<WizardScreen.Terminal>(Answered(flow, SetupFlow.Finish));
        Assert.True(end.Success);
    }

    [Fact]
    public void DECLINING_THE_CHECK_changes_nothing_about_what_gets_written()
    {
        //the offer only changes what the user knows, so both answers write the same default model
        var skipped = AtChosenModel(WithModel());
        var passed = AtChosenModel(WithModel());

        //read the intent here, the done pause applies the writes and clears them
        SkipRan(skipped);
        CheckRan(passed);
        Assert.Equal(skipped.Writes.DefaultModel, passed.Writes.DefaultModel);
        //both write sets must be set, or the comparison above would pass on two empty ones.
        Assert.NotNull(skipped.Writes.DefaultModel);

        //both paths reach the same screen
        Assert.Equal(SetupFlow.SummaryKey, Key(Resumed(skipped)));
        Assert.Equal(SetupFlow.SummaryKey, Key(Resumed(passed)));

        var a = Assert.IsType<WizardScreen.Terminal>(Answered(skipped, SetupFlow.Finish));
        var b = Assert.IsType<WizardScreen.Terminal>(Answered(passed, SetupFlow.Finish));

        Assert.Equal(a.Key, b.Key);
        Assert.Equal(a.Success, b.Success);
    }

    [Fact]
    public void THE_AUDITION_IS_NEVER_OFFERED_ON_THE_CONNECT_PATH()
    {
        //the runner AuditionRunner.RunAsync takes a model id and loads a model, and a server the user runs has neither
        var probes = new FakeProbes
        {
            Server = new Gatto.Core.Acquire.ConnectProbe("http://host:9", ["m"], 8192),
        };
        var flow = new SetupFlow(probes);
        flow.StartPastOpening();
        flow.Answer(SetupFlow.ForkConnect);
        flow.Answer(SetupFlow.Yes);

        Assert.DoesNotContain(flow.Emitted, s => Key(s) == SetupFlow.AuditionOfferKey);
        Assert.DoesNotContain(nameof(ISetupProbes.RunAudition), probes.Asked);
    }

    private static SetupFlow AtSteering(FakeProbes probes)
    {
        var flow = new SetupFlow(probes);
        flow.StartPastOpening();   //no engine found, so the flow steers.
        return flow;
    }

    [Fact]
    public void The_CUDART_COMPANION_is_part_of_the_SAME_UNIT_not_a_footnote()
    {
        //the two files arrive as one unit, so the consent names both
        var flow = new SetupFlow(new FakeProbes
        {
            Llama = null,
            Offer = new EngineFetchOffer(@"C:\Users\you\.gatto\llama\b11071\", new EnginePair(
                new EngineAsset("llama-b11071-bin-win-cuda-12.4-x64.zip", "https://example.invalid/s",
                    new string('a', 64), 1),
                new EngineAsset("cudart-llama-bin-win-cuda-12.4-x64.zip", "https://example.invalid/c",
                    new string('b', 64), 66_060_288))),
        });
        var consent = Assert.IsType<WizardScreen.Choice>(flow.StartPastOpening());
        var body = string.Join(" ", consent.BodyRows!)
            + " " + string.Join(" ", EngineFetchView.Rows(consent.Engine!.Value, 100, glyphs: GlyphSet.Unicode).Select(r => r.Text));

        Assert.Contains("cudart", body, StringComparison.OrdinalIgnoreCase);
        //the screen must also say why it picked this build.
        Assert.Contains("According to your hardware", body, StringComparison.Ordinal);

        //both files share one destination, so the view names it once
        Assert.Single(EngineFetchView.Rows(consent.Engine!.Value, 100, glyphs: GlyphSet.Unicode),
            r => r.Text.Contains("into", StringComparison.Ordinal));
    }

    [Fact]
    public void A_SINGLE_DOWNLOAD_VENDOR_STILL_SAYS_EXTRACT_IT()
    {
        //a vendor with no companion gets the single-file wording, which the pair test above would not cover
        var flow = new SetupFlow(new FakeProbes
        {
            Llama = null,
            Asset = new Gatto.Roles.LlamaAsset(
                "llama-b11071-bin-win-vulkan-x64.zip", null, "an AMD discrete card", "Vulkan"),
            Offer = new EngineFetchOffer(@"C:\Users\you\.gatto\llama\b11071\", null),
        });
        var view = Assert.IsType<WizardScreen.Choice>(flow.StartPastOpening()).Engine!.Value;
        var body = string.Join(" ", EngineFetchView.Rows(view, 100, glyphs: GlyphSet.Unicode).Select(r => r.Text));

        Assert.Contains("extract to", body, StringComparison.Ordinal);
        Assert.DoesNotContain("with ", body, StringComparison.Ordinal);
        Assert.DoesNotContain("cudart", body, StringComparison.OrdinalIgnoreCase);
        //a vendor with one file gets no both-files warning
        Assert.DoesNotContain("Extract BOTH", body, StringComparison.Ordinal);
    }

    [Fact]
    public void AN_UNREADABLE_MACHINE_does_not_get_steered_to_one_of_thirteen_builds()
    {
        //steering to one CUDA build on a machine gatto could not read is a guess that costs a large download when wrong.
        var flow = AtSteering(new FakeProbes { Llama = null, Asset = null });
        var steer = Assert.IsType<WizardScreen.Choice>(flow.Emitted[^1]);

        Assert.DoesNotContain("cuda", string.Join(" ", steer.BodyRows!), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("point gatto at", string.Join(" ", steer.BodyRows!), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData((int)Gatto.Core.Tools.ProbeShape.DllNotFound, "cuda runtime")]
    [InlineData((int)Gatto.Core.Tools.ProbeShape.NotClassic, "combined program")]
    [InlineData((int)Gatto.Core.Tools.ProbeShape.TimedOut, "answer in time")]
    [InlineData((int)Gatto.Core.Tools.ProbeShape.Failed, "path points at")]
    public void EACH_FAILURE_SHAPE_RENDERS_ITS_OWN_SENTENCE_driven_off_the_probe(int shape, string expected)
    {
        //each case is a ProbeShape value, and one generic sentence would send four shapes to the same dead end
        var flow = AtSteering(new FakeProbes
        {
            Llama = null,
            Shape = (Gatto.Core.Tools.ProbeShape)shape,
        });

        //read the sentence through FailureScreen, the retry case is a Choice and the typed case is an Ask with a null Label
        Assert.Contains(expected, FailureScreen.Sentence(flow.Answer(ShelfControls.TypedAnswer(@"C:\llama\llama-server.exe"))),
            StringComparison.OrdinalIgnoreCase);
    }

    //the code 0xC0000135 means a missing dll. the steered download can name cudart since its verified file is the steered asset, a typed path gets only a hint
    [Theory]
    [InlineData(true, true, "cudart zip into the SAME folder", "If this is a CUDA build")]
    [InlineData(true, false, "a file it needs is missing", "cudart zip into the SAME folder")]
    [InlineData(false, true, "If this is a CUDA build", "cudart zip into the SAME folder")]
    [InlineData(false, false, "a file it needs is missing", "CUDA")]
    public void DLL_NOT_FOUND_NAMES_CUDART_ONLY_WHERE_THE_STEER_IS_A_FACT(
        bool steeredDoor, bool cudaSteer, string mustSay, string mustNotSay)
    {
        var asset = cudaSteer
            ? new Gatto.Roles.LlamaAsset("llama-cuda.zip", "cudart-cu12.zip", "an NVIDIA adapter", "CUDA")
            : new Gatto.Roles.LlamaAsset("llama-vulkan.zip", null, "an AMD adapter", "Vulkan");

        string label;
        if (steeredDoor)
        {
            //on the download path the typed path is the arrived file, so the verified exe is the steered asset
            var flow = AtSteering(new FakeProbes
            {
                Llama = null, Shape = Gatto.Core.Tools.ProbeShape.DllNotFound, Asset = asset,
            });
            label = FailureScreen.Sentence(flow.Answer(ShelfControls.TypedAnswer(@"C:\llama\llama-server.exe")));
        }
        else
        {
            //on the typed path nothing links the user's file to the steered asset
            var (flow, _) = AtCustomBuild(new FakeProbes
            {
                Llama = @"C:\llama\llama-server.exe",
                Found = [new Gatto.Core.Acquire.FoundModel(@"D:\m\ling.gguf", 4_000_000_000, null)],
                Architecture = "futurearch",
                //the machine's own engine is fine, the missing dll belongs to the path the user types
                EngineShape = Gatto.Core.Tools.ProbeShape.ClassicServer,
                Shape = Gatto.Core.Tools.ProbeShape.DllNotFound,
                Asset = asset,
            });
            flow.Answer(SetupFlow.Yes);   //yes opens the path ask.
            label = Assert.IsType<WizardScreen.Ask>(flow.Answer(@"D:\mine\llama-server.exe")).Label!;
        }

        Assert.Contains(mustSay, label, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(mustNotSay, label, StringComparison.OrdinalIgnoreCase);
    }

    //the CRT sentence appears only when vcruntime140.dll is really absent, and outranks cudart. with the runtime present the sentence stays neutral
    [Theory]
    [InlineData(true, false, "Visual C++ Redistributable", "extracted into the same folder")]
    [InlineData(true, true, "Visual C++ Redistributable", "cudart zip into the SAME folder")]
    [InlineData(false, false, "extracted into the same folder", "Visual C++")]
    [InlineData(false, true, "cudart zip into the SAME folder", "Visual C++")]
    public void B0_1_THE_MISSING_VC_RUNTIME_IS_NAMED_and_only_when_the_file_is_really_absent(
        bool vcRuntimeAbsent, bool cudaSteer, string mustSay, string mustNotSay)
    {
        var asset = cudaSteer
            ? new Gatto.Roles.LlamaAsset("llama-cuda.zip", "cudart-cu12.zip", "an NVIDIA adapter", "CUDA")
            : new Gatto.Roles.LlamaAsset("llama-vulkan.zip", null, "an AMD adapter", "Vulkan");

        //the steered path is the common case, gatto proposed the build and it will not start
        var flow = AtSteering(new FakeProbes
        {
            Llama = null,
            Shape = Gatto.Core.Tools.ProbeShape.DllNotFound,
            Asset = asset,
            VcRuntimeAbsent = vcRuntimeAbsent,
        });

        var label = FailureScreen.Sentence(flow.Answer(ShelfControls.TypedAnswer(@"C:\llama\llama-server.exe")));

        Assert.Contains(mustSay, label, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(mustNotSay, label, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void THE_CLASSIC_BANNER_moves_on_to_the_model_segment()
    {
        var flow = AtSteering(new FakeProbes
        {
            Llama = null,
            Shape = Gatto.Core.Tools.ProbeShape.ClassicServer,
            Found = [Model(@"D:\models\qwen.gguf")],
        });

        var next = flow.Answer(ShelfControls.TypedAnswer(@"C:\llama\llama-server.exe"));

        Assert.Contains(flow.TakeNarration(), i => i.Key == "llama.ok");
        Assert.Equal(SetupFlow.DiscoveredKey, Key(next));
    }

    [Fact]
    public void FINDING_LLAMA_CPP_COMPLETES_NOTHING_even_though_the_segment_succeeded()
    {
        //a segment that succeeds still completes nothing, so CreateModel and DefaultModel stay null
        var flow = AtSteering(new FakeProbes { Llama = null, Shape = Gatto.Core.Tools.ProbeShape.ClassicServer });
        flow.Answer(ShelfControls.TypedAnswer(@"C:\llama\llama-server.exe"));

        Assert.Null(flow.Writes.CreateModel);
        Assert.Null(flow.Writes.DefaultModel);
    }

    [Fact]
    public void NOTHING_IS_WRITTEN_UNTIL_THE_END_on_every_non_terminal_exit()
    {
        //the write set must stay empty until the completion point, so a user who closes the terminal leaves nothing behind
        var probes = WithModel();
        var flow = new SetupFlow(probes);

        //the engine is already found, so this screen is discovery
        flow.StartPastOpening();
        //only the model write is in question, taking the engine records a write of its own
        Assert.Null(flow.Writes.CreateModel);
    }

    [Fact]
    public void The_PACK_intent_carries_the_COMPUTED_context_and_never_the_GGUFs_ceiling()
    {
        //the wizard writes the computed budget, a trained maximum would not load. read the intent at the scaffold pause, applying consumes it
        var flow = AtScaffoldPause(WithModel());

        var model = Assert.IsType<WriteSet.Model>(flow.Writes.CreateModel);
        Assert.Equal(8192, model.Context);          //the budget the probe computed.
        Assert.Equal(SetupFlow.DefaultPort, model.Port);
    }

    [Fact]
    public void DEFAULT_MODEL_IS_THE_LAST_INTENT_and_only_after_the_offer_resolves()
    {
        //a written model with no default yet is a legal intermediate, so the audition offer can sit between them
        var flow = AtScaffoldPause(WithModel());

        //before the writer runs the model is intended and the default is not.
        Assert.NotNull(flow.Writes.CreateModel);
        Assert.Null(flow.Writes.DefaultModel);

        Resumed(flow, "qwen");

        //applying consumed the model intent, and the default is still unset while the offer is on screen
        Assert.Null(flow.Writes.CreateModel);
        Assert.Null(flow.Writes.DefaultModel);

        //read the write set before the done pause drains it, the subject is the order of the intents
        SkipRan(flow);
        Assert.NotNull(flow.Writes.DefaultModel);  //the default appears only now.
    }

    [Fact]
    public void A_FAILED_CHECK_STILL_SETS_THE_DEFAULT_when_the_user_insists()
    {
        //the offer only changes what the user knows, the default is written either way
        var flow = AtChosenModel(WithModel(audition: AuditionOutcome.Failed));
        CheckRan(flow);

        //read the write set before the done pause consumes the intent, insisting still records the default
        flow.Answer(SetupFlow.Anyway);
        Assert.NotNull(flow.Writes.DefaultModel);

        Assert.Equal(SetupFlow.SummaryKey, Key(Resumed(flow)));
    }

    [Fact]
    public void THE_VERIFIED_LLAMA_PATH_becomes_an_intent_rather_than_an_immediate_write()
    {
        var flow = AtSteering(new FakeProbes { Llama = null, Shape = Gatto.Core.Tools.ProbeShape.ClassicServer });
        flow.Answer(ShelfControls.TypedAnswer(@"C:\llama\llama-server.exe"));

        Assert.Equal(@"C:\llama\llama-server.exe", flow.Writes.LlamaServer);
    }

    [Fact]
    public void The_RUN_ENDS_BY_ASKING_THE_MODEL_TO_SAY_SOMETHING()
    {
        //a config that never ran a model is as unproven as a fresh install, so the run asks the model to say something
        var probes = WithModel();
        var flow = AtChosenModel(probes);
        PastTheSkip(flow);

        Assert.Contains(nameof(ISetupProbes.ProveIt), probes.Asked);
        Assert.Equal(SetupFlow.SummaryKey, Key(flow.Emitted[^1]));
    }

    [Fact]
    public void A_SUCCESSFUL_PROOF_offers_to_LAND_SOMEWHERE_rather_than_vanishing()
    {
        //the run never ends by closing the console, gatto opens or the screen shows the next command
        var flow = AtChosenModel(WithModel());
        var prove = Assert.IsType<WizardScreen.Choice>(PastTheSkip(flow));

        Assert.Contains(prove.Options, o => o.Key == SetupFlow.OpenRepl);
        Assert.Contains(prove.Options, o => o.Key == SetupFlow.Finish);

        var end = Assert.IsType<WizardScreen.Terminal>(Answered(flow, SetupFlow.OpenRepl));
        Assert.True(flow.StartReplWhenDone);
        Assert.NotEqual("", end.NextStep);
    }

    [Fact]
    public void DECLINING_THE_LAUNCH_still_prints_the_exact_command()
    {
        var flow = AtChosenModel(WithModel());
        PastTheSkip(flow);
        var end = Assert.IsType<WizardScreen.Terminal>(Answered(flow, SetupFlow.Finish));

        Assert.False(flow.StartReplWhenDone);
        //the NextStep field is nullable, this screen must set it to place the user somewhere
        Assert.NotNull(end.NextStep);
        Assert.Contains("gatto", end.NextStep!.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_FAILED_PROOF_IS_HONEST_and_keeps_the_setup_it_wrote()
    {
        //only the conversation failed, so the screen must not send the user through setup again
        var flow = AtChosenModel(new FakeProbes
        {
            Llama = @"C:\llama\llama-server.exe",
            Found = [Model(@"D:\models\qwen.gguf")],
            Proof = new Gatto.Core.Acquire.ProveOutcome(false, "connection refused", TimeSpan.Zero),
        });

        var prove = Assert.IsType<WizardScreen.Choice>(PastTheSkip(flow));
        var body = string.Join(" ", prove.BodyRows!);

        Assert.Contains("connection refused", body, StringComparison.Ordinal);   //the screen quotes the real failure detail.
        Assert.Contains("gatto doctor", body, StringComparison.Ordinal);         //the command that inspects further
        //the claim that the setup stands is read from the leaving sentence, the intent is gone once the done pause applies
        Assert.Contains("gatto is set up and pointed at", flow.LeaveStep!.Text,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_FAILED_PROOF_ON_THE_PACK_PATH_does_not_blame_the_server_s_protocol()
    {
        //a failure of gatto's own server must not borrow the connect path's guess about a stranger's server
        var flow = AtChosenModel(new FakeProbes
        {
            Llama = @"C:\llama\llama-server.exe",
            Found = [Model(@"D:\models\qwen.gguf")],
            Proof = new Gatto.Core.Acquire.ProveOutcome(
                false, "model is corrupted or incomplete", TimeSpan.Zero),
        });

        var prove = Assert.IsType<WizardScreen.Choice>(PastTheSkip(flow));
        var body = string.Join(" ", prove.BodyRows!);

        Assert.DoesNotContain(
            Gatto.Core.Acquire.ServerConnect.Capabilities[0].IfAbsent, body, StringComparison.Ordinal);
        Assert.Contains("model is corrupted or incomplete", body, StringComparison.Ordinal);
    }

    [Fact]
    public void THE_FAILED_PROOF_HEADLINE_follows_the_cause_it_was_given()
    {
        //both headlines are asserted in one test, so a headline that does not move with the cause shows up
        static string Headline(bool startFailed)
        {
            var flow = AtChosenModel(new FakeProbes
            {
                Llama = @"C:\llama\llama-server.exe",
                Found = [Model(@"D:\models\qwen.gguf")],
                Proof = new Gatto.Core.Acquire.ProveOutcome(
                    false, "some detail", TimeSpan.Zero, StartFailed: startFailed),
            });
            return Assert.IsType<WizardScreen.Choice>(PastTheSkip(flow)).Question!;
        }

        Assert.Contains("the server didn't start", Headline(true), StringComparison.Ordinal);
        Assert.Contains("the model didn't answer", Headline(false), StringComparison.Ordinal);

        //both headlines keep the claim that the setup stands, neither cause changes it
        Assert.All([Headline(true), Headline(false)],
            h => Assert.StartsWith("The setup is written", h, StringComparison.Ordinal));
    }

    [Fact]
    public void THE_THREE_SCREENS_THE_BACKTICK_SWEEP_CANNOT_REACH_are_clean_too()
    {
        //these screens are not on the sweep's path, so drive them directly, and each command must be a real highlight span
        static IReadOnlyList<WizardRow> Rows(WizardScreen s) => s switch
        {
            WizardScreen.Choice c => c.BodyRows ?? [],
            WizardScreen.Ask a => a.BodyRows ?? [],
            _ => [],
        };

        var f1 = AtChosenModel(new FakeProbes
        {
            Llama = @"C:\llama\llama-server.exe",
            Found = [Model(@"D:\models\qwen.gguf")],
            Audition = AuditionOutcome.CouldNotRun,
        });
        var couldNotRun = Rows(Answered(f1, SetupFlow.Yes));

        var f2 = AtChosenModel(new FakeProbes
        {
            Llama = @"C:\llama\llama-server.exe",
            Found = [Model(@"D:\models\qwen.gguf")],
            Proof = new Gatto.Core.Acquire.ProveOutcome(false, "nothing answered", TimeSpan.Zero),
        });
        var failedProof = Rows(Answered(f2, SetupFlow.Skip));

        //the hardware snapshot is missing, so no build can be chosen.
        var flow = new SetupFlow(new FakeProbes { Snapshot = null, Asset = null });
        var unreadable = Rows(flow.StartPastOpening());

        foreach (var rows in new[] { couldNotRun, failedProof, unreadable })
            Assert.DoesNotContain(rows, r => r.Text.Contains('`'));

        Assert.Contains(couldNotRun, r => r.Highlight?.Contains("gatto audition") == true);
        Assert.Contains(failedProof, r => r.Highlight?.Contains("gatto doctor") == true);

        //this branch must say where to get the build, and the url is the steering module's own constant, a retyped copy would drift
        Assert.Contains(unreadable,
            r => r.Text == Gatto.Roles.LlamaAssetSteering.ReleasePage);
    }

    [Fact]
    public void LEAVING_DISCARDS_EVERY_INTENT_the_run_had_accumulated()
    {
        //the runner applies the write set at any terminal screen, and leaving is one, so the intent must be cleared here
        var flow = new SetupFlow(new FakeProbes
        {
            Server = new Gatto.Core.Acquire.ConnectProbe("http://host:9", ["m"], 8192),
        });
        flow.StartPastOpening();
        flow.Answer(SetupFlow.ForkConnect);
        flow.Answer(SetupFlow.Yes);          //this answer records the endpoint intent.
        Assert.False(flow.Writes.IsEmpty);

        //the second run backs out at the last screen.
        var flow2 = new SetupFlow(new FakeProbes { Server = new Gatto.Core.Acquire.ConnectProbe("http://host:9", ["their-model"], 8192) });
        flow2.StartPastOpening();
        flow2.Answer(SetupFlow.ForkConnect);
        flow2.Answer(SetupFlow.No);

        Assert.True(flow2.Writes.IsEmpty);
    }

    //the rendered sentence is the subject, drive LlamaAssetSteering.Choose itself, a fragment can pass a substring check while the sentence reads wrong
    [Theory]
    [InlineData(8_000_000_000UL, "10DE", false)]          //an NVIDIA adapter gives CUDA and the companion.
    [InlineData(6_962_544_640UL, "1002", false)]          //an AMD discrete card gives Vulkan.
    [InlineData(94_832_877_896UL, "1002", true)]          //an AMD APU with unified memory gives Vulkan.
    [InlineData(6_000_000_000UL, null, false)]            //an unreadable vendor with a real budget gives Vulkan.
    [InlineData(0UL, "1002", true)]                       //no budget on an AMD APU gives CPU.
    [InlineData(0UL, null, false)]                        //no budget and an unreadable vendor gives CPU.
    public void THE_STEERING_SENTENCE_NAMES_THE_BUILD_EXACTLY_ONCE(ulong budget, string? vendor, bool unified)
    {
        var hw = new Gatto.Core.Hardware.HardwareClass(
            unified ? Gatto.Core.Hardware.MemoryTopology.Unified : Gatto.Core.Hardware.MemoryTopology.Discrete,
            unified ? Gatto.Core.Hardware.ShareKind.CarvedOut : Gatto.Core.Hardware.ShareKind.None,
            budget, 16_000_000_000,
            new Gatto.Core.Hardware.HardwareSnapshot(34_359_738_368, 17_179_869_184,
                unified ? Gatto.Core.Hardware.GpuKind.Integrated : Gatto.Core.Hardware.GpuKind.Discrete,
                8_589_934_592, vendor), 0, Gatto.Core.Hardware.BudgetBound.None);
        var asset = Gatto.Roles.LlamaAssetSteering.Choose(hw);

        //the consent sentence has one home and three screens, and this is the screen a readable machine reaches.
        var flow = new SetupFlow(new FakeProbes
        {
            Llama = null,
            Asset = asset,
            Offer = new EngineFetchOffer(@"C:\Users\you\.gatto\llama\b11071\", new EnginePair(
                new EngineAsset(asset.ZipName, "https://example.invalid/z", new string('a', 64), 1),
                asset.CudartZipName is { Length: > 0 } c
                    ? new EngineAsset(c, "https://example.invalid/c", new string('b', 64), 1)
                    : null)),
        });
        var rows = Assert.IsType<WizardScreen.Choice>(flow.StartPastOpening()).BodyRows!;
        var row = Assert.Single(rows, r => r.Text.StartsWith("According to your hardware", StringComparison.Ordinal));
        var sentence = row.Text;

        //the build name must appear exactly once, a second copy is the collision
        Assert.Equal(1, Occurrences(sentence, asset.BuildName));
        Assert.EndsWith($"you need the {asset.BuildName} build.", sentence, StringComparison.Ordinal);

        //the hardware clause must not bring its own connectives, the sentence around it already has them
        Assert.DoesNotContain(", so the", sentence, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("because", sentence, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" build build", sentence, StringComparison.OrdinalIgnoreCase);

        //both asserted values are the accented spans, a highlight over text the sentence lacks renders with no symptom
        Assert.Equal([asset.HardwareFact, asset.BuildName], row.Highlight);
    }

    private static int Occurrences(string haystack, string needle)
    {
        int n = 0, at = 0;
        while ((at = haystack.IndexOf(needle, at, StringComparison.Ordinal)) >= 0) { n++; at += needle.Length; }
        return n;
    }

    [Fact]
    public void TWO_NOTHING_MESSAGES_never_land_in_a_row()
    {
        //the scan line is shown only when rows follow it, it explains the move to downloads
        var empty = AtSearch(new FakeProbes { Llama = @"C:\llama\llama-server.exe", Found = [], Rows = [] });
        Assert.DoesNotContain(empty.TakeNarration(), i => i.Key == "model.none");

        //with rows present, the line is context and stays.
        var withRows = AtSearch(new FakeProbes
        {
            Llama = @"C:\llama\llama-server.exe", Found = [], Rows = [Row("org/a")],
        });
        Assert.Contains(withRows.TakeNarration(), i => i.Key == "model.none");
    }

    [Fact]
    public void A_SATISFIED_segment_renders_a_tick_and_is_skipped_on_a_second_run()
    {
        //a machine that already has llama-server and a model gets a tick instead of a question
        var flow = new SetupFlow(new FakeProbes { Llama = @"C:\llama\llama-server.exe", Model = true });
        var next = flow.StartPastEngine();

        //a satisfied segment narrates nothing, the next screen already shows that state
        Assert.DoesNotContain(flow.TakeNarration(), i => i.Key is "segment.llama" or "segment.model");
        //a working config does not end the wizard, it continues to the model segment so another can be added
        Assert.Equal(SetupFlow.SearchKey, Key(next));
    }

    [Fact]
    public void An_UNSATISFIED_segment_stops_the_walk_there_rather_than_running_ahead()
    {
        var flow = new SetupFlow(new FakeProbes { Llama = null, Model = true });
        var next = flow.StartPastOpening();

        //the gap is llama.cpp, so the flow stops at the steering screen.
        Assert.Equal(SetupFlow.SteerKey, Key(next));
        //the flow stops at the first gap and asks nothing about the model.
        Assert.DoesNotContain(flow.Emitted, s => Key(s).StartsWith("model.", StringComparison.Ordinal));
    }

    [Fact]
    public void The_CONNECT_fork_never_probes_for_a_local_llama_server()
    {
        //asking for the local llama-server path on the connect path implies gatto wants to run something
        var probes = new FakeProbes { Llama = null };
        var flow = new SetupFlow(probes);
        flow.StartPastOpening();

        //the probe log is cleared after the welcome, the welcome reads gatto's own config before any path is chosen
        probes.Asked.Clear();
        var beforeFork = flow.Emitted.Count;
        flow.Answer(SetupFlow.ForkConnect);

        Assert.Equal(SetupPath.Connect, flow.Path);
        Assert.DoesNotContain(nameof(ISetupProbes.LlamaServerPath), probes.Asked);

        //after the fork llama.cpp may appear only in the sentence about the ports it usually owns, and the llama path is where it belongs
        var connectRows = flow.Emitted.Skip(beforeFork).SelectMany(Words).ToList();
        var mentions = connectRows
            .Where(r => r.Contains("llama.cpp", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.All(mentions, r => Assert.Contains("usually use", r, StringComparison.OrdinalIgnoreCase));

        var llama = new SetupFlow(new FakeProbes { Llama = null });
        //the boundary count is taken before StartPastOpening, which emits the engine screens itself
        var llamaBeforeFork = llama.Emitted.Count;
        llama.StartPastOpening();
        Assert.Contains("llama.cpp", string.Join("\n", llama.Emitted.Skip(llamaBeforeFork).SelectMany(Words)),
            StringComparison.OrdinalIgnoreCase);

        static IEnumerable<string> Words(WizardScreen s) => s switch
        {
            WizardScreen.Choice c => [c.Question ?? "", .. (c.BodyRows ?? []).Select(r => r.Text),
                                      .. c.Options.Select(o => o.Label + " " + (o.Description ?? ""))],
            WizardScreen.Ask a => [a.Label ?? "", .. (a.BodyRows ?? []).Select(r => r.Text)],
            WizardScreen.Info i => [.. i.Rows.Select(r => r.Text)],
            WizardScreen.Terminal t => [.. t.Rows.Select(r => r.Text)],
            _ => [],
        };
    }

    [Fact]
    public void A_MACHINE_WE_COULD_NOT_READ_says_so_instead_of_inventing_a_number()
    {
        //a wizard that prints 0 GB for a failed probe states something false about the machine
        var flow = new SetupFlow(new FakeProbes { Snapshot = null });

        //the unreadable-memory fact sits on the welcome map, so the assertion reads the welcome row
        var welcome = Assert.IsType<WizardScreen.Choice>(flow.Start());
        var line = welcome.BodyRows!.Single(r => r.Text.Contains("machine's memory", StringComparison.Ordinal)).Text;
        Assert.DoesNotContain("0 GB", line, StringComparison.Ordinal);
        Assert.Contains("couldn't read", line, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Answering_a_screen_that_is_not_waiting_THROWS_rather_than_silently_doing_nothing()
    {
        //a terminal screen waits for nothing, so an answer there must throw
        var flow = new SetupFlow(new FakeProbes { Server = new Gatto.Core.Acquire.ConnectProbe("http://host:9", ["their-model"], 8192) });
        flow.StartPastOpening();
        flow.Answer(SetupFlow.ForkConnect);
        flow.Answer(SetupFlow.No);            //leaving ends on the terminal screen.

        Assert.Throws<InvalidOperationException>(() => flow.Answer("whatever"));
    }

    private static string Rendered(WizardScreen s) => s switch
    {
        WizardScreen.Choice c => c.Question + " " + string.Join(" ", c.Options.Select(o => o.Label + " " + o.Description))
                                + " " + string.Join(" ", c.BodyRows ?? []),
        WizardScreen.Info i => string.Join(" ", i.Rows),
        WizardScreen.Progress p => p.Label,
        //an Ask has body rows, so the renderer must include them. an assertion without them reads an empty screen and passes
        WizardScreen.Ask a => a.Label + " " + a.Placeholder
                              + " " + string.Join(" ", a.BodyRows ?? []),
        WizardScreen.Terminal t => string.Join(" ", t.Rows) + " " + t.NextStep,
        _ => "",
    };

    //naming the file is half the job, the screen must say where to get it. the check reads the url off the row the user sees
    [Fact]
    public void THE_OFFLINE_ARM_SAYS_WHERE_TO_GET_IT_not_just_what_it_is_called()
    {
        //the row names the release page, a direct asset link would go stale and leave the user nothing to act on
        var flow = new SetupFlow(new FakeProbes
        {
            //the model already exists, so the only gap is llama.cpp and the flow stops at the engine step.
            Llama = null, Model = true,
            Asset = new Gatto.Roles.LlamaAsset(
                "llama-b11071-bin-win-vulkan-x64.zip", null, "an AMD discrete card", "Vulkan"),
            Offer = new EngineFetchOffer(@"C:\Users\you\.gatto\llama\b11071\", null),
        });
        var arm = Assert.IsType<WizardScreen.Choice>(flow.StartPastOpening());
        var text = string.Join("\n", flow.Emitted.Select(Rendered))
            + "\n" + string.Join("\n", EngineFetchView.Rows(arm.Engine!.Value, 100, glyphs: GlyphSet.Unicode).Select(r => r.Text));

        Assert.Contains("llama-b11071-bin-win-vulkan-x64.zip", text, StringComparison.Ordinal);
        Assert.Contains("github.com/ggml-org/llama.cpp/releases/tag/b11071", text, StringComparison.Ordinal);
        //the screen also says what to do once the download arrives, and the return checks the folder gatto named
        Assert.Contains("Done, check that folder", string.Join(" ", arm.Options.Select(o => o.Label)),
            StringComparison.Ordinal);
        Assert.DoesNotContain("nothing is written", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void THE_CUDART_COMPANION_IS_NAMED_ON_THE_ARM_WHERE_THE_USER_FETCHES_IT()
    {
        //gatto fetches the companion itself, so only the arm where the release read failed names it
        var flow = new SetupFlow(new FakeProbes
        {
            Llama = null, Model = true,
            Asset = new Gatto.Roles.LlamaAsset(
                "llama-b11071-bin-win-cuda-12.4-x64.zip", "cudart-llama-bin-win-cuda-12.4-x64.zip",
                "an NVIDIA adapter", "CUDA"),
            Offer = new EngineFetchOffer(@"C:\Users\you\.gatto\llama\b11071\", null),
        });
        var arm = Assert.IsType<WizardScreen.Choice>(flow.StartPastOpening());
        var text = string.Join("\n", EngineFetchView.Rows(arm.Engine!.Value, 100, glyphs: GlyphSet.Unicode).Select(r => r.Text));

        //the screen names the companion file rather than linking it, a stale link strands the user
        Assert.Contains("cudart-llama-bin-win-cuda-12.4-x64.zip", text, StringComparison.Ordinal);
        Assert.Contains("the CUDA runtime it needs", text, StringComparison.Ordinal);
        //one folder is named once, and both files go there.
        Assert.Single(EngineFetchView.Rows(arm.Engine!.Value, 100, glyphs: GlyphSet.Unicode),
            r => r.Text.StartsWith("extract to", StringComparison.Ordinal));
    }

    //the Placeholder field labels the free-text row, so a hint there must read as an instruction, a bare value would look pickable
    [Theory]
    [InlineData("nothing")]        //the llama path's first ask.
    [InlineData("server")]         //the connect context ask.
    public void NO_ASK_SCREEN_OFFERS_A_BARE_VALUE_AS_IF_IT_WERE_PICKABLE(string scenario)
    {
        var flow = scenario == "server"
            ? new SetupFlow(new FakeProbes
            {
                Server = new Gatto.Core.Acquire.ConnectProbe("http://127.0.0.1:1235", [], null),
            })
            //the engine is not the classic server, so this scenario reaches the typed re-ask
            : new SetupFlow(new FakeProbes
            {
                Llama = @"C:\llama\llama-server.exe",
                Shape = Gatto.Core.Tools.ProbeShape.NotClassic,
                Model = true,
            });

        flow.StartPastOpening();
        if (scenario == "server") { flow.Answer(SetupFlow.ForkConnect); flow.Answer(SetupFlow.Yes); }
        //the steering screen takes a typed path, so the answer must be wrapped as typed text.
        else { flow.Answer(ShelfControls.TypedAnswer(@"C:\llama\llama-server.exe")); }

        var asks = flow.Emitted.OfType<WizardScreen.Ask>().ToList();
        Assert.NotEmpty(asks);
        foreach (var a in asks)
        {
            Assert.NotNull(a.Placeholder);
            //the hint must read as an instruction rather than a value to pick, so no exact prefix is pinned
            Assert.Contains("type", a.Placeholder, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(" ", a.Placeholder.Trim(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void THE_SEARCH_SCREEN_NEVER_OFFERS_MORE_THAN_NINE_NUMBERED_ROWS()
    {
        //a digit picks and confirms at once, so a numbered list stays at nine rows including the escapes
        var probes = new FakeProbes
        {
            Llama = @"C:\llama\llama-server.exe",
            Rows = [.. Enumerable.Range(1, 50).Select(n => Row($"org/model{n}"))],
        };
        var flow = new SetupFlow(probes);
        var search = Assert.IsType<WizardScreen.Choice>(flow.StartPastEngine());

        Assert.True(search.Options.Count <= 9,
            $"the search screen offers {search.Options.Count} numbered rows; a digit cannot press past nine");
    }

    [Fact]
    public void THE_WELCOMES_QUESTION_IS_WRITTEN_FOR_ITS_COMMITTED_FORM()
    {
        //the transcript commits the question on its own, so the question must read well without the body
        var welcome = Assert.IsType<WizardScreen.Choice>(new SetupFlow(new FakeProbes()).Start());

        Assert.Equal("Ready to set gatto up?", welcome.Question);
    }

    [Fact]
    public void THE_PRIVACY_LINE_SAYS_HAS_TO_BECAUSE_THE_ABSOLUTE_BREAKS_WHEN_CLOUD_SHIPS()
    {
        //an absolute privacy claim becomes false the day cloud ships, so the line says the model has to run somewhere the reader chooses
        var welcome = Assert.IsType<WizardScreen.Choice>(new SetupFlow(new FakeProbes()).Start());
        var body = string.Join(" ", welcome.BodyRows!.Select(r => r.Text));

        //the wording has to stay, the absolute form turns false the day cloud ships
        Assert.Contains("never have to leave it", body, StringComparison.Ordinal);
        Assert.DoesNotContain("never leaves it", body);
    }

    [Fact]
    public void THE_MAP_MARKS_INSTALL_ALREADY_DONE_ONLY_ON_A_MACHINE_THAT_HAS_IT()
    {
        //the map states a fact about this run, so both states are asserted. a line that appears means nothing unless it is absent when false
        static string MapWithLlama(string? llama) => string.Join("\n",
            Assert.IsType<WizardScreen.Choice>(new SetupFlow(new FakeProbes { Llama = llama }).Start())
                .BodyRows!.Select(r => r.Text));

        //the fact sits on the engine row of the map, so both states of that row are asserted
        Assert.Contains("already on this machine", MapWithLlama(@"C:\llama\llama-server.exe"),
            StringComparison.Ordinal);
        Assert.DoesNotContain("already on this machine", MapWithLlama(null));

        //no already done line here, the install segment is off the map
        Assert.DoesNotContain("already done", MapWithLlama(@"C:\llama\llama-server.exe"));
    }

    [Fact]
    public void THE_INSTALL_SCREEN_SHOWS_FROM_TO_AND_HAS_HANDED_THE_PATH_SENTENCE_ON()
    {
        //both halves of the move are asserted, a line that leaves one screen and never arrives is worse than one that never moved
        var install = Assert.IsType<WizardScreen.Choice>(
            AtTheInstallQuestion(WalkableFlowWithInstall(Gatto.Cli.InstallState.NotInstalled)));
        var body = string.Join(" ", install.BodyRows!.Select(r => r.Text));

        Assert.Contains("from", body, StringComparison.Ordinal);
        Assert.Contains("gatto.exe", body, StringComparison.Ordinal);
        Assert.DoesNotContain("A new terminal will find it", body, StringComparison.Ordinal);
    }

    [Fact]
    public void THE_QUESTION_THE_FORK_ASKED_IS_ANSWERED_BY_THE_ENGINE_STEP_ITSELF()
    {
        //the engine step answers what the fork asked, and an engine is stated on purpose so no screen stands before it
        var probes = new FakeProbes { Llama = @"C:\llama\llama-server.exe" };
        var first = Assert.IsType<WizardScreen.Choice>(new SetupFlow(probes).StartPastOpening());

        //the flow lands on the engine step itself, with no question before it
        Assert.Equal(SetupFlow.FoundKey, first.Key);
        Assert.Equal(SetupFlow.EngineTitle, first.Question);

        //arriving sets the path, a flow that never picks the connect option follows the llama path
        var walked = new SetupFlow(new FakeProbes { Llama = @"C:\llama\llama-server.exe" });
        walked.StartPastOpening();
        Assert.Equal(SetupPath.Llama, walked.Path);

        //the last option leads to the connect path
        Assert.Equal(SetupFlow.ForkConnect, first.Options[^1].Key);
        Assert.Equal(SetupFlow.ConnectDoorLabel, first.Options[^1].Label);
    }

    [Fact]
    public void THE_THREE_STEP_MARKERS_ARE_QUESTIONLESS_SO_THEY_COMMIT_AND_STAY()
    {
        //step markers are named rather than counted, and a marker with no question is never collapsed so the heading stays in the scrollback
        var flow = FlowWithInstall(Gatto.Cli.InstallState.NotInstalled);
        flow.StartPastOpening();

        var marker = Assert.Single(flow.TakeNarration());
        //the install question sits inside the done step, which already has a heading, so its marker is retired
        Assert.Equal("step.model", marker.Key);
        Assert.Equal("Model", marker.Rows[0].Text);

        //reading the narration once empties the channel, so a marker cannot render twice.
        Assert.Empty(flow.TakeNarration());

        var installed = FlowWithInstall(Gatto.Cli.InstallState.Installed);
        installed.StartPastOpening();
        Assert.Equal("step.model", Assert.Single(installed.TakeNarration()).Key);
    }


    [Theory]
    //each fixture is a verbatim probe report from a real machine, so all three shapes are real
    [InlineData("unified-128gb-96gb-carveout.txt",
        "128 GB of memory shared with the graphics chip", "about 88 GB of it")]
    [InlineData("unified-32gb-1gb-carveout.txt",
        "almost none of its own", "about 15 GB of it")]
    [InlineData("discrete-32gb-8gb-vram.txt",
        "a graphics card with 8 GB", "about 16 GB more slowly in memory")]
    public void THE_HARDWARE_LINE_QUOTES_THE_BUDGET_FOR_EACH_REAL_MACHINE(
        string fixture, string shapePhrase, string budgetPhrase)
    {
        //the hardware line must quote the budget a model can use, since the inventory understates a unified machine by about 3x
        var flow = new SetupFlow(new FakeProbes { Snapshot = HwFixture.Fixture(fixture) });
        var body = string.Join(" ",
            Assert.IsType<WizardScreen.Choice>(flow.Start()).BodyRows!.Select(r => r.Text));

        Assert.Contains(shapePhrase, body, StringComparison.Ordinal);
        Assert.Contains(budgetPhrase, body, StringComparison.Ordinal);
    }

    [Fact]
    public void THE_ZERO_BUDGET_MACHINE_IS_NEVER_TOLD_IT_HAS_ABOUT_0_GB()
    {
        //a carve-out can leave the graphics budget at zero, so the wording must never say about 0 GB
        var flow = new SetupFlow(new FakeProbes
        {
            Snapshot = HwFixture.Fixture("unified-32gb-1gb-carveout.txt"),
        });
        var body = string.Join(" ",
            Assert.IsType<WizardScreen.Choice>(flow.Start()).BodyRows!.Select(r => r.Text));

        Assert.DoesNotContain("0 GB", body, StringComparison.Ordinal);
        Assert.DoesNotContain("graphics chip, a model can use", body, StringComparison.Ordinal);
    }

    [Fact]
    public void THE_FIT_WORDS_AND_THE_HARDWARE_LINE_ARE_ONE_VOCABULARY()
    {
        //the fit words and the hardware line read the same shape, derived once on HardwareClass
        var noShare = Gatto.Core.Hardware.HardwareClassifier.Classify(
            HwFixture.Fixture("unified-32gb-1gb-carveout.txt")).Shape;
        var withShare = Gatto.Core.Hardware.HardwareClassifier.Classify(
            HwFixture.Fixture("unified-128gb-96gb-carveout.txt")).Shape;
        var discrete = Gatto.Core.Hardware.HardwareClassifier.Classify(
            HwFixture.Fixture("discrete-32gb-8gb-vram.txt")).Shape;

        Assert.Equal(Gatto.Core.Hardware.MachineShape.UnifiedNoShare, noShare);
        Assert.Equal(Gatto.Core.Hardware.MachineShape.UnifiedWithShare, withShare);
        Assert.Equal(Gatto.Core.Hardware.MachineShape.Discrete, discrete);

        //a machine with no faster tier offers no comparison.
        Assert.DoesNotContain("slower",
            SearchRow.FitWords(Gatto.Core.Models.FitRegime.FitsRamOnly, noShare, Gatto.Terminal.GlyphSet.Unicode), StringComparison.Ordinal);
        //the two shapes with a faster tier do compare.
        Assert.Contains("slower",
            SearchRow.FitWords(Gatto.Core.Models.FitRegime.FitsRamOnly, withShare, Gatto.Terminal.GlyphSet.Unicode), StringComparison.Ordinal);
        Assert.Contains("slower",
            SearchRow.FitWords(Gatto.Core.Models.FitRegime.FitsRamOnly, discrete, Gatto.Terminal.GlyphSet.Unicode), StringComparison.Ordinal);

        //the words name where the weights sit, and gatto measures speed rather than predicting it
        foreach (var shape in new[] { noShare, withShare, discrete })
            foreach (var fit in new[] { Gatto.Core.Models.FitRegime.FitsGpu, Gatto.Core.Models.FitRegime.FitsRamOnly })
                Assert.DoesNotContain("full speed",
                    SearchRow.FitWords(fit, shape, Gatto.Terminal.GlyphSet.Unicode), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void THE_CPU_ONLY_LINE_IS_WITNESSED_AND_PROMISES_NOTHING()
    {
        //a device-less machine is a witnessed shape, the line reports that models run in system memory
        var flow = new SetupFlow(new FakeProbes
        {
            Snapshot = new Gatto.Core.Hardware.HardwareSnapshot(16_000_000_000, 15_000_000_000,
                Gatto.Core.Hardware.GpuKind.None, null),
        });
        var body = string.Join(" ",
            Assert.IsType<WizardScreen.Choice>(flow.Start()).BodyRows!.Select(r => r.Text));

        Assert.Contains("no graphics driver gatto can use", body, StringComparison.Ordinal);
        Assert.Contains("models run in system memory", body, StringComparison.Ordinal);
        //the line promises nothing gatto cannot do
        Assert.DoesNotContain("ask rather than guess", body, StringComparison.Ordinal);
        Assert.DoesNotContain("couldn't work out", body, StringComparison.Ordinal);
    }

    private static WizardScreen ConnectRoad(FakeProbes probes)
    {
        var flow = new SetupFlow(probes);
        flow.StartPastOpening();
        return flow.Answer(SetupFlow.ForkConnect);
    }

    //say gatto's own server and step past it, and assert that the skip was asked for rather than stumbled on
    [Fact]
    public void AN_OWN_SERVER_IS_SAID_AND_WALKED_PAST_TO_THE_NEXT_RUNG()
    {
        var probes = new FakeProbes
        {
            Server = new Gatto.Core.Acquire.ConnectProbe("http://127.0.0.1:1235", ["gemma-4-26B-A4B-it"], 8192),
            ServerAfterSkip = new Gatto.Core.Acquire.ConnectProbe("http://127.0.0.1:8080", ["a-stranger"], 4096),
            Own = (1235, "gemma-4-26B-A4B-it"),
        };

        var screen = Assert.IsType<WizardScreen.Choice>(ConnectRoad(probes));
        var body = string.Join("\n", screen.BodyRows!);

        Assert.Equal(SetupFlow.ConfirmServerKey, screen.Key);
        //the offered address sits in a fact row, and the title asks the connect question.
        Assert.Contains(screen.BodyRows!, r => r.Text.Contains("http://127.0.0.1:8080",
            StringComparison.Ordinal));
        Assert.DoesNotContain(screen.BodyRows!, r => r.Text.Contains("server    http://127.0.0.1:1235",
            StringComparison.Ordinal));

        Assert.Contains("http://127.0.0.1:1235 is gatto's own", body, StringComparison.Ordinal);
        Assert.Contains("serving gemma-4-26B-A4B-it", body, StringComparison.Ordinal);
        //no wizard screen may show an em dash in this sentence
        Assert.DoesNotContain('—', body);

        //the flow must request the skip of that port, or a lucky second answer would pass
        Assert.Contains(probes.ProbeSkips, s => s.Contains(1235));
    }

    //the retry screen must say that no other server answered, since gatto's own server did
    [Fact]
    public void AN_OWN_SERVER_WITH_NOTHING_BEHIND_IT_LANDS_ON_THE_RETRY_SCREEN()
    {
        var probes = new FakeProbes
        {
            Server = new Gatto.Core.Acquire.ConnectProbe("http://127.0.0.1:1235", ["gemma-4-26B-A4B-it"], 8192),
            ServerAfterSkip = null,
            Own = (1235, "gemma-4-26B-A4B-it"),
        };

        var screen = Assert.IsType<WizardScreen.Choice>(ConnectRoad(probes));
        var body = string.Join("\n", screen.BodyRows!);

        Assert.Equal(SetupFlow.RetryKey, screen.Key);
        Assert.Contains("is gatto's own", body, StringComparison.Ordinal);
        Assert.Contains("No other server answered.", body, StringComparison.Ordinal);
    }

    //a foreign server on gatto's usual port is a stranger while gatto does not run, and it is offered like one
    [Fact]
    public void A_DEAD_PID_DOES_NOT_CLAIM_THE_PORT()
    {
        var probes = new FakeProbes
        {
            Server = new Gatto.Core.Acquire.ConnectProbe("http://127.0.0.1:1235", ["a-stranger"], 8192),
            Own = null,   //a null Own also answers what a dead pid gives, so one fixture covers both states
        };

        var screen = Assert.IsType<WizardScreen.Choice>(ConnectRoad(probes));

        Assert.Equal(SetupFlow.ConfirmServerKey, screen.Key);
        Assert.Contains(screen.BodyRows!, r => r.Text.Contains("http://127.0.0.1:1235",
            StringComparison.Ordinal));
        Assert.DoesNotContain("gatto's own", string.Join("\n", screen.BodyRows!),
            StringComparison.Ordinal);
        Assert.DoesNotContain(probes.ProbeSkips, s => s.Count > 0);
    }

    //an ordinary connect asks for no skips at all, so the own-server catch costs nothing on the common path.
    [Fact]
    public void AN_ORDINARY_CONNECT_WALK_SKIPS_NOTHING()
    {
        var probes = new FakeProbes
        {
            Server = new Gatto.Core.Acquire.ConnectProbe("http://127.0.0.1:8080", ["a-stranger"], 4096),
        };

        ConnectRoad(probes);

        Assert.All(probes.ProbeSkips, s => Assert.Empty(s));
    }
}
