using Gatto.Cli.Setup;
using Gatto.Core.Acquire;
using Gatto.Core.Hardware;

namespace Gatto.Tests.Fakes;

//one shared fake for the wizard's probe seam, so no two fakes disagree about a machine. the probe contract is read-only, so only the recording lists act.
internal sealed class WizardProbes : ISetupProbes
{
    public string? Llama { get; init; } = @"C:\llama\llama-server.exe";
    public IReadOnlyList<ModelRow> Rows { get; init; } = [];
    public IReadOnlyList<FoundModel> Found { get; set; } = [];

    //settable, since setup screens quote this machine in their text and a copy test must pick which machine
    public HardwareSnapshot? Snapshot { get; init; } = new(34359738368, 34093496320, GpuKind.Discrete, 8589934592);

    //count how many times the hardware was asked for. other screens ask too, so assert a count that does not grow with the rows, rather than an absolute
    public int HardwareReads { get; private set; }

    public HardwareSnapshot? Hardware()
    {
        HardwareReads++;
        OnHardware?.Invoke();
        return Snapshot;
    }

    //called inside every hardware read, so a test can hold the read the way a slow probe does
    public Action? OnHardware { get; init; }

    //settable so a test can name the machine parts in its copy. both null is the honest live state, and it exercises the fallback words.
    public HardwareNames Names { get; init; }

    public HardwareNames HardwareNames() => Names;
    public string? LlamaServerPath() => Llama;
    public bool HasResolvableModel() => false;

    //pretend to run on a legacy console or a modern terminal. false is the modern terminal, and every screen in the corpus was drawn there.
    public bool IsLegacyConsole() => LegacyConsole;

    public IReadOnlyList<string> Publishers { get; init; } = ["unsloth", "bartowski", "ggml-org"];

    public bool LegacyConsole { get; init; }
    //the connect probe this fixture answers. null means nothing answered, which is the other branch of that path.
    public ConnectProbe? Server { get; init; }
    //records the ports the flow asked to skip, in order, since a fake that ignored the argument would satisfy every screen assertion
    public List<IReadOnlyList<int>> ProbeSkips { get; } = [];

    //the answer for a repeat ask once a port has been skipped. null keeps answering the same probe every time.
    public ConnectProbe? ServerAfterSkip { get; init; }

    public ConnectProbe? ProbeServer(IReadOnlyList<int>? skip = null)
    {
        ProbeSkips.Add([.. skip ?? []]);
        return skip is { Count: > 0 } ? ServerAfterSkip : Server;
    }

    //the port and model gatto is serving on. null means nothing of ours is up, which is what a dead pid looks like to the live probe.
    public (int Port, string Model)? Own { get; init; }

    //answers the typed address and records every address asked about. the claim is about the argument, so a fake that ignored it would satisfy every assertion.
    public List<string> ProbedAt { get; } = [];

    public ConnectProbe? AtAddress { get; init; }

    public ConnectProbe? ProbeAt(string baseUrl)
    {
        ProbedAt.Add(baseUrl);
        return AtAddress;
    }

    public string? GattoServingOn(int port) =>
        Own is { } own && own.Port == port ? own.Model : null;
    public IReadOnlyList<string> Roots { get; init; } = [];
    //record the root arguments asked for, in order. the match counts only when the folder the user typed reaches the scan.
    public List<string?> ScanRoots { get; } = [];
    //called with the scan number before the result is built. discovery and the download watch share it, so a watched file must be absent at first and appear later
    public Action<int>? OnScan { get; set; }

    public ScanResult Scan(string? r)
    {
        ScanRoots.Add(r);
        OnScan?.Invoke(ScanRoots.Count);
        return new(Found, Roots);
    }
    //record the last search request. controls change what the flow asks, so a control test asserts the ask, and the engine's handling is tested elsewhere.
    public ModelSearchRequest? LastRequest { get; private set; }

    //every request the flow made, in order, so a test can say a control asked nothing
    public List<ModelSearchRequest> Requests { get; } = [];

    //script the search answer, so a fixture forces every shelf state. null answers the rows above, so no existing test changes meaning.
    public Func<ModelSearchRequest, ShelfOutcome>? Answer { get; init; }

    //a search that reports and waits like the Hub, for the loading shelf. null answers at once, so no other fixture changes meaning
    public Func<ModelSearchRequest, IProgress<SearchProgress>?, CancellationToken, ShelfOutcome>? Slow { get; init; }

    public ShelfOutcome SearchModels(ModelSearchRequest request, IProgress<SearchProgress>? progress, CancellationToken ct)
    {
        LastRequest = request;
        lock (Requests) Requests.Add(request);
        if (Slow is { } slow) return slow(request, progress, ct);
        return Answer?.Invoke(request) ?? Outcome(Rows);
    }

    //an outcome over the given rows, counted per regime the way the engine counts them
    public static ShelfOutcome Outcome(IReadOnlyList<ModelRow> rows, HubSearchCause? cause = null,
        bool moreBehindA = false, int hiddenByKind = 0) =>
        new(rows, rows.Count(r => r.Fit == Gatto.Core.Models.FitRegime.FitsGpu),
            rows.Count(r => r.Fit == Gatto.Core.Models.FitRegime.FitsRamOnly),
            rows.Count(r => r.Fit == Gatto.Core.Models.FitRegime.DoesNotFit),
            moreBehindA, hiddenByKind, [], rows.Count > 0 ? null : cause);
    public (string Path, long Bytes)? ProjectorFor(string p) => null;
    public string? ArchitectureOf(string p) => null;
    //a delegate answering where a found file would be offered a move to (a hard-coded null voids the assertion in every fixture)
    public Func<string, MoveOffer?>? Move { get; set; }

    public MoveOffer? MoveOfferFor(string p) => Move?.Invoke(p);
    public (Gatto.Core.Models.FitRegime Alone, Gatto.Core.Models.FitRegime WithProjector)? PairFit(string p, long b) => null;
    //a delegate that evaluates a typed model id (hard-coded to unreachable, only the failure branch is reachable)
    public Func<string, TypedIdOutcome>? Typed { get; init; }
    public TypedIdOutcome EvaluateTypedId(string id) =>
        Typed?.Invoke(id) ?? new TypedIdOutcome.Unreachable(false);
    //the asset a test can steer. the default has no cudart companion, so the complaint about it is hidden from every fake
    public Gatto.Roles.LlamaAsset? Asset { get; init; } = new("z.zip", null, "a graphics card", "Vulkan");

    public Gatto.Roles.LlamaAsset? ChooseLlamaAsset() => Asset;
    //the fetch offer this fake's machine would get. null means the browser-steered screen, so a test about the fetch sets it
    public EngineFetchOffer? Offer { get; init; }

    public EngineFetchOffer? EngineOffer() => Offer;

    //what a fetch actually produces. there's no default (an offer with no outcome is an unanswerable button, and the interface default throws)
    public Func<EnginePair, Gatto.Cli.EngineFetch>? Fetch { get; init; }

    //the pairs asked to be fetched, in order (the whole pair, so a test can prove the companion travelled with the server)
    public List<EnginePair> Fetched { get; } = [];

    //the ticks this fake reports while fetching, in order. empty means none, so a test of the progress bar must set them
    public List<FetchTick> Ticks { get; init; } = [];

    //when set, the fetch waits for cancellation, then a beat before finishing (returning at once would let a flow that never joined pass)
    public bool BlockUntilCancelled { get; init; }

    //true once the blocked fetch has finished unwinding (read it after the stop answer, where false means the flow never waited)
    public bool Unwound { get; private set; }

    //the free bytes the fake disk reports, or null for an unreadable drive. one figure for every path, since a per-path map would encode windows layout instead.
    public long? FreeSpace { get; init; }

    public long? FreeSpaceOn(string path) => FreeSpace;

    //the paused list and the resume offer are settable apart, since the row and the effect of a press are separate. deriving one from the other hides a disagreement.
    public IReadOnlyList<ISetupProbes.PausedFetch> Paused { get; init; } = [];

    public ModelFetchOffer? ResumeOfferResult { get; init; }

    public IReadOnlyList<ISetupProbes.PausedFetch> PausedFetches() => Paused;

    public ModelFetchOffer? ResumeOffer(ISetupProbes.PausedFetch paused) =>
        ResumeOfferResult ?? HubOffer;

    //what gatto would fetch for a picked row. null is the browser-watch branch, so a test that cares about the hub offer sets it
    public ModelFetchOffer? HubOffer { get; init; }

    public ModelFetchOffer? ModelOffer(Gatto.Core.Acquire.ModelRow row) => HubOffer;

    //how the model fetch ends. arrived by default, and tests set the failure arms.
    public Gatto.Core.Acquire.HubFetchResult ModelResult { get; set; } =
        new(Gatto.Core.Acquire.HubFetchOutcome.Arrived);

    //the offers asked to be fetched, in order. a rendering assertion cannot see whether the code really fetched
    public List<ModelFetchOffer> ModelFetched { get; } = [];

    //the progress ticks reported in order while a model fetch runs.
    public List<FetchTick> ModelTicks { get; init; } = [];

    //switch the result mid-test so a resume's second fetch can end differently from the first (an init-only result cannot express that)
    public void Arrive() => ModelResult = new(Gatto.Core.Acquire.HubFetchOutcome.Arrived);

    //a delegate deciding which downloaded files are still being written, so the answer can differ per path.
    public Func<string, bool>? Arriving { get; init; }

    public bool StillArriving(string ggufPath) => Arriving?.Invoke(ggufPath) ?? false;

    //what a downloaded file hashes to. null means unreadable, which the flow treats apart from a hash that disagrees
    public Func<string, string?>? Hash { get; init; }

    public string? Sha256Of(string ggufPath) => Hash?.Invoke(ggufPath);

    //when set, the model fetch holds until cancellation and finishes a beat later, so a flow that never joined fails
    public bool ModelBlockUntilCancelled { get; init; }

    //how many fetches block, every one by default. a guard needs a resumed fetch to finish, since while blocked the poll answers false either way
    public int ModelBlockingFetches { get; init; } = int.MaxValue;

    //true once the blocked model fetch has finished unwinding (read it after the stop answer, where false means the flow never waited)
    public bool ModelUnwound { get; private set; }

    //count the fetch starts. a resume is a second start, and the count tells a resume from a screen that only rendered
    public int ModelStarts { get; private set; }

    //the token that fired decides the outcome, same as the real fetch. pause is checked first, so a pause that arrives with a cancel keeps the partial
    public Gatto.Core.Acquire.HubFetchResult FetchModel(
        ModelFetchOffer offer, IProgress<FetchTick>? progress = null, CancellationToken ct = default,
        CancellationToken pause = default)
    {
        ModelStarts++;
        ModelFetched.Add(offer);
        foreach (var t in ModelTicks) progress?.Report(t);

        if (ModelBlockUntilCancelled && ModelStarts <= ModelBlockingFetches)
        {
            //bound the wait (a forgotten cancel would hang, and a hang names no screen and no cause)
            WaitHandle.WaitAny([ct.WaitHandle, pause.WaitHandle], TimeSpan.FromSeconds(5));
            Thread.Sleep(20);
            ModelUnwound = true;
            var file = ModelTicks.Count > 0 ? ModelTicks[^1].FileName : null;
            return new(pause.IsCancellationRequested
                ? Gatto.Core.Acquire.HubFetchOutcome.Paused
                : Gatto.Core.Acquire.HubFetchOutcome.Cancelled, file);
        }

        return ModelResult;
    }

    //the partials deleted, in order. this list is the only oracle for a promised delete, since screen text and an unchanged file have other causes
    public List<ModelFetchOffer> PartialsDeleted { get; } = [];

    public void DeletePartial(ModelFetchOffer offer) => PartialsDeleted.Add(offer);

    //keep the full signature, since the interface default throws and a drifted copy would still build without overriding
    public Gatto.Cli.EngineFetch FetchEngine(
        EnginePair assets, IProgress<FetchTick>? progress = null, CancellationToken ct = default)
    {
        Fetched.Add(assets);
        foreach (var t in Ticks) progress?.Report(t);
        if (BlockUntilCancelled)
        {
            ct.WaitHandle.WaitOne();
            //the beat makes a flow that never joined fail reliably, since it would read the flag at once and find false
            Thread.Sleep(50);
            Unwound = true;
        }
        ct.ThrowIfCancellationRequested();
        return Fetch is { } f
            ? f(assets)
            : throw new NotSupportedException(
                $"this fixture offered a fetch of {assets.Server.ZipName} and set no Fetch");
    }

    //the default verify answer names a pinned build (the real probe reads build and shape from one banner)
    public Func<string, Gatto.Core.Tools.ProbeResult>? Verify { get; init; }

    public Gatto.Core.Tools.ProbeResult VerifyLlamaServer(string p) =>
        Verify?.Invoke(p) ?? new(Gatto.Core.Tools.ProbeShape.ClassicServer, "d",
            Build: Gatto.Roles.LlamaAssetSteering.PinnedRelease);
    public int ContextFor(string p) => 8192;
    //the default check result reports its facts, a bare verdict renders without the stamp block. set it for a failure outcome
    public AuditionCheck Audition { get; init; } =
        new(AuditionOutcome.Passed, new AuditionFacts("Q4_K_M", "greedy", "off", 42.5, 5, 5));

    //the progress moments reported in order before the check returns. empty by default, which is a check that finishes instantly and says nothing
    public List<Gatto.Roles.Audition.AuditionProgress> AuditionMoments { get; init; } = [];

    //when set, the check holds until cancellation and finishes after a beat, so a flow that never joined cannot pass by being fast
    public bool AuditionBlockUntilCancelled { get; init; }

    //set on the way out of a blocked check, so a test can prove the flow joined instead of only returned
    public bool AuditionUnwound { get; private set; }

    //count the check starts, since only the count tells a stop from a stop and a restart
    public int AuditionStarts { get; private set; }

    //the repo id the last check was handed, the badge's tag for the search that offered the model
    public string? AuditionRepoId { get; private set; }

    public AuditionCheck RunAudition(string p, string? repoId,
        IProgress<Gatto.Roles.Audition.AuditionProgress>? progress = null,
        CancellationToken ct = default)
    {
        //the deed before the count, so a test that sees the count also sees the deed
        Deed("audition");
        AuditionRepoId = repoId;
        AuditionStarts++;
        foreach (var m in AuditionMoments) progress?.Report(m);

        if (AuditionBlockUntilCancelled)
        {
            //bound the wait (a forgotten cancel would hang, and a hang in a test run can read as a pass). five seconds is longer than a real stop, and short enough to fail
            ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(5));
            Thread.Sleep(20);
            AuditionUnwound = ct.IsCancellationRequested;
            return new AuditionCheck(AuditionOutcome.CouldNotRun);
        }

        //only a check that ran takes the server. cancelled and unrunnable checks take nothing, and merging those two leaves the done step untested
        if (Audition.Outcome != AuditionOutcome.CouldNotRun) _checkTook = p;
        return Audition;
    }

    //the file name behind a model id. null by default, since a row whose profile cannot be read drops and an always-answering fake would hide that
    public string? ActiveFile { get; set; }

    //the active file's path and size, or null when there's no answer. null drops the row, and a test sets it mid-run since the flow writes the profile itself
    public (string Path, long Bytes)? ActiveModel { get; set; }

    public (string Path, long Bytes)? ActiveModelFile(string modelId) => ActiveModel;
    public string? ActiveFileFor(string modelId) => ActiveFile;
    //the id of the model holding the server, or null when nothing is served.
    public string? HeldBy { get; init; }

    //record the id the check loaded, since the check takes the server. a server never taken stays held
    private string? _checkTook;

    //one place computes the serve state, so the held-by and already-up answers can't describe different machines
    private Serve State(string modelId) =>
        _released ? Serve.Idle
        : string.Equals(_checkTook, modelId, StringComparison.Ordinal) ? Serve.This
        : string.Equals(HeldBy, modelId, StringComparison.Ordinal) ? Serve.This
        : HeldBy is { Length: > 0 } ? Serve.Other
        : Serve.Idle;

    private enum Serve { Idle, This, Other }

    public string? ServerHeldByAnother(string modelId) =>
        State(modelId) == Serve.Other ? HeldBy : null;

    //reads the same state as the holding question, from its other arm.
    public bool ServerAlreadyUp(string modelId)
    {
        //record this read in the same order list as the release and the run. a boolean cannot say where the read happened, and the position is the property
        Deed("serverup");
        return State(modelId) == Serve.This;
    }

    //both deeds go in one ordered list, since a release after the audition starts is the same defect in a passing test. reads lock, since two threads write
    public IReadOnlyList<string> ServerDeeds
    {
        get { lock (_deeds) return [.. _deeds]; }
    }

    private readonly List<string> _deeds = [];

    private void Deed(string what)
    {
        lock (_deeds) _deeds.Add(what);
    }

    //the release marks the server gone so the holder stops naming it, otherwise the check refuses for a reason that no longer exists
    private bool _released;

    //measured badges keyed by file name. empty by default, which is a machine that has measured nothing.
    public Dictionary<string, Gatto.Core.Acquire.Badge> Badges { get; } = [];

    public Gatto.Core.Acquire.Badge? BadgeForFile(string fileName) =>
        Badges.TryGetValue(fileName, out var b) ? b : null;

    public bool ReleaseHeldServer()
    {
        Deed("release");
        if (HeldBy is not { Length: > 0 }) return false;
        _released = true;
        return true;
    }
    public string? ExistingModel { get; init; }
    //the id another file already holds, or null for no collision.
    public string? Colliding { get; init; }

    //which kind of id clash the collision is. the default is two different models, which is the clash screen with no add offer
    public Gatto.Roles.IdClash ClashKind { get; init; } = Gatto.Roles.IdClash.DifferentModel;
    public string? ExistingEndpoint { get; init; }
    //per-path answers, for when two rows must be marked differently. the map wins over the scalar, so existing fixtures still mark both
    public Dictionary<string, string> Existing { get; } = new(StringComparer.OrdinalIgnoreCase);

    //what each added model's profile recorded, keyed by model id
    public Dictionary<string, AddedFacts> Added { get; } = new(StringComparer.OrdinalIgnoreCase);

    AddedFacts? ISetupProbes.Added(string modelId) => Added.TryGetValue(modelId, out var a) ? a : null;

    //the files with a projector by name in their folder
    public HashSet<string> ProjectorsBeside { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool ProjectorBeside(string ggufPath) => ProjectorsBeside.Contains(ggufPath);

    //the hub answer keyed by file name, since a repo listing has no paths. the repo answer outranks it
    public Dictionary<string, string> Named { get; } = new(StringComparer.OrdinalIgnoreCase);

    //sourced models keyed by repo id. production matches one by its source only, so an id here answers for that repo and the file of the same name stops matching
    public Dictionary<string, string> FromRepo { get; } = new(StringComparer.OrdinalIgnoreCase);

    //the id whose weights this machine holds, or null when nothing is loaded.
    public string? Loaded { get; init; }

    //how many times the shelf asked. the marks look the same either way, so only a count proves it asked once per shelf rather than once per row
    public int LoadedAsked { get; set; }

    //every ask per path, since the live probe reads the models folder on each one
    public Dictionary<string, int> ExistingAsked { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string? ExistingModelFor(string ggufPath)
    {
        ExistingAsked[ggufPath] = ExistingAsked.GetValueOrDefault(ggufPath) + 1;
        return Existing.TryGetValue(ggufPath, out var id) ? id : ExistingModel;
    }
    //the verdict for the id clash. the held id comes from Colliding and its kind from ClashKind, with no collision answering free
    public (Gatto.Roles.IdClash Kind, string? Id) ClashFor(string ggufPath, string? incomingRepoId) =>
        Colliding is { Length: > 0 } ? (ClashKind, Colliding) : (Gatto.Roles.IdClash.Free, null);

    public string? ModelForHubRow(string repoId, string ggufFileName)
    {
        //a sourced model answers for the repo listed here.
        if (FromRepo.TryGetValue(repoId, out var byRepo)) return byRepo;

        if (string.IsNullOrWhiteSpace(ggufFileName)) return null;

        //a sourced model stays unreachable by file name, even when Named points at it. without this clause a cross-repo fixture passes with the production rule gone
        return Named.TryGetValue(ggufFileName, out var byName)
            && !FromRepo.Values.Contains(byName, StringComparer.OrdinalIgnoreCase)
                ? byName
                : null;
    }

    public string? LoadedModelId()
    {
        LoadedAsked++;
        OnLoaded?.Invoke();
        return Loaded;
    }

    //runs inside the loaded-model probe, so a test can hold a server that answers late
    public Action? OnLoaded { get; init; }

    //the active file of the loaded model. null is the path-unknown branch, where a matching id still reads as loaded
    public string? LoadedPath { get; init; }

    //counted for the same reason as the loaded count, since once per shelf rather than per row cannot be seen in the marks
    public int LoadedPathAsked { get; set; }

    public string? LoadedModelPath()
    {
        LoadedPathAsked++;
        return LoadedPath;
    }
    //the default models a machine with gatto already installed. the path is fixed, so a screen renders a chosen state rather than the running test exe
    public string RunningFrom() => @"C:\Users\you\Downloads\gatto\gatto.exe";

    //settable for the tests that need another install state. the default is installed
    public Gatto.Cli.InstallState Install { get; init; } = Gatto.Cli.InstallState.Installed;

    //settable, since the summary line names this directory on the installed-elsewhere branch
    public string InstalledDir { get; init; } = @"C:\Programs\gatto";

    public (Gatto.Cli.InstallState State, string Dir) InstallStatus() =>
        (Install, InstalledDir);

    //settable, since the answer compares this against the running image, and a fixed value would leave one arm undriven
    public string? Installed { get; init; }

    public string? InstalledVersion() => Installed;

    //the version this fixture pretends to be running. the default is this build's own string, so a frame that names a version must set it
    public string Running { get; init; } = Gatto.Core.GattoVersion.String;

    public string RunningVersion() => Running;
    //the update question answers with a decision by default, so a test never has to answer it
    public bool? UpdateConsent() => UpdateAnswered;
    public bool? UpdateAnswered { get; init; } = false;
    public string? ExistingEndpointFor(string baseUrl) => ExistingEndpoint;
    //settable, since the done step renders this path and a frame diff must name the path inside its own frame
    public string Config { get; init; } = @"C:\home\gatto.json";

    public string ConfigPath() => Config;
    //settable, since a hard-coded pass would leave the failure screen unreachable
    public ProveOutcome Prove { get; init; } = new(true, "hi", TimeSpan.FromSeconds(2));

    //the number of times the load was requested. the skip path must load exactly once, and only a count tells that from a second load
    public int Proofs { get; private set; }

    //the proof fails when another model holds the server, a fixture where that server also passes describes no real machine
    public ProveOutcome ProveIt(string? modelId,
        IProgress<Gatto.Roles.Audition.AuditionProgress>? progress = null)
    {
        Proofs++;
        //pass the serving note through the member the real prove uses, the waiting screen takes its server row from there
        if (ServerHeldByAnother(modelId ?? "") is not { Length: > 0 })
            progress?.Report(Gatto.Roles.Audition.AuditionProgress.Note($"serving {modelId}"));

        //no guard here, since the three-arm state forbids naming another holder with the wanted model's own id
        return ServerHeldByAnother(modelId ?? "") is { Length: > 0 } h
            ? new ProveOutcome(false, $"{h} is the running server", TimeSpan.Zero, ServedByAnother: h)
            : Prove;
    }
}

//the one chip a request lit, null for the landing pair or every family, which is what the old request's family said for no chip
internal static class RequestFamily
{
    public static string? Family(this ModelSearchRequest r) => r.Lit.Count == 1 ? r.Lit.First() : null;
}
