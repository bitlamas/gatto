using Gatto.Core.Acquire;
using Gatto.Core.Hardware;

namespace Gatto.Cli.Setup;

//what the where-it-lives screen states as fact. the size is the whole shard set's, and a null storage policy never gates the move
internal sealed record MoveOffer(string SuggestedDir, bool CrossVolume, long Bytes, string? StorageSense);

//what a sweep found, with the folders it looked in even when the sweep threw. the roots keep their order and sanitize at the render seam
internal sealed record ScanResult(
    IReadOnlyList<Gatto.Core.Acquire.FoundModel> Found,
    IReadOnlyList<string> Roots);

//what an added model's profile recorded, which a local row reads for its publisher and its vision
internal sealed record AddedFacts(string? RepoId, bool HasProjector);

//everything the wizard learns about the world, behind the one seam the tests fake. a probe never writes, and each segment asks its question before it renders
internal interface ISetupProbes
{
    //what this machine has, or null when the probe could not run. a null classifies as CPU-ONLY rather than as a guess
    HardwareSnapshot? Hardware();

    //what the machine's parts are called, kept off the snapshot so the classifier can never read them. a null name falls back to the plain word
    HardwareNames HardwareNames();

    //the integrated GPU a new model's server must be pinned to, null when llama-server's own device pick matches the fit
    string? ServeOnlyGpu() => null;

    //the configured or discovered llama-server.exe, or null when there's none
    string? LlamaServerPath();

    //every engine on this machine. the default derives from LlamaServerPath rather than returning empty (Build stays null until the probe that runs the exe fills it)
    IReadOnlyList<Gatto.Roles.FoundEngine> EngineSweep() =>
        LlamaServerPath() is { Length: > 0 } p ? [new Gatto.Roles.FoundEngine(p, null)] : [];

    //whether a launch would resolve a model today. true on a re-run with a working config, so the model segment skips instead of asking
    bool HasResolvableModel();

    //whether the install screen shows its Windows Terminal aside. false by default, and GlyphSet.IsLegacyConsole answers it so both readings agree
    bool IsLegacyConsole() => false;

    //what answers on the probed ports, or null when nothing does. a probed port is never named, and a null NCtx is a real answer the wizard must not fill in
    Gatto.Core.Acquire.ConnectProbe? ProbeServer(IReadOnlyList<int>? skip = null);

    //the model gatto's own server serves on this port, or null. keyed by the port that answered, and a stale record from a dead pid answers null
    string? GattoServingOn(int port);

    //the address the user typed, or null when nothing usable answers there. a silent typed address never sends the wizard back to gatto's port list
    Gatto.Core.Acquire.ConnectProbe? ProbeAt(string baseUrl);

    //every model already on this machine, newest-useful-first. it returns the roots it swept as well as the finds, so a miss can say where it looked
    ScanResult Scan(string? extraRoot);

    //the shelf of original models over the lit families, lifted or typed. a probe that cannot search answers an empty shelf with no cause
    Gatto.Core.Acquire.ShelfOutcome SearchModels(Gatto.Core.Acquire.ModelSearchRequest request,
        IProgress<Gatto.Core.Acquire.SearchProgress>? progress, CancellationToken ct) =>
        new([], 0, 0, 0, false, 0, [], null);

    //evaluate a repo id the user typed, the escape hatch that takes any id. a malformed id is refused as a typo, before it reaches the wire
    TypedIdOutcome EvaluateTypedId(string repoId);

    //run the audition and return its measurements. call it only after both the model and the llama-server path are written
    AuditionCheck RunAudition(string modelId, string? repoId,
        IProgress<Gatto.Roles.Audition.AuditionProgress>? progress = null,
        CancellationToken ct = default);

    //the id of a different model already holding gatto's server, or null. read-only and cheap, so the offer can ask before it offers
    string? ServerHeldByAnother(string modelId) => null;

    //whether this model's server is already up. a bool on purpose, since the neighbour's null cannot tell nothing serving from this model
    bool ServerAlreadyUp(string modelId) => false;

    //the badge for a file on this machine, or null when nothing has measured it. the register is keyed by file name, so the local shelf asks by name
    Gatto.Core.Acquire.Badge? BadgeForFile(string fileName) => null;

    //stop the server another model holds, and report whether one was released. call it only after the user confirms the swap
    bool ReleaseHeldServer() => false;

    //which llama.cpp asset this machine wants, or null when the hardware could not be read. an unreadable machine is never steered to one of thirteen builds
    Gatto.Roles.LlamaAsset? ChooseLlamaAsset();

    //what a fetch of the pinned llama.cpp release would put on this machine. null means this probe cannot fetch, and a missing asset is the offline arm
    Gatto.Cli.Setup.EngineFetchOffer? EngineOffer() => null;

    //download the asset, verify it against GitHub's digest and extract it where gatto said. it throws by default, and a cancel must leave no partial behind
    Gatto.Cli.EngineFetch FetchEngine(
        Gatto.Cli.Setup.EnginePair assets,
        IProgress<FetchTick>? progress = null,
        CancellationToken ct = default) =>
        throw new NotSupportedException(
            $"this setup probe offered a fetch of {assets.Server.ZipName} and cannot perform one");

    //the offer to fetch this model itself, or null when gatto cannot. null by default, so a fake that says nothing about fetching keeps the browser screen
    Gatto.Cli.Setup.ModelFetchOffer? ModelOffer(Gatto.Core.Acquire.ModelRow row) => null;

    //a fetch that was paused and can be picked up again. found on disk, since the flow keeps no state, and a part file with no record is not offered
    internal sealed record PausedFetch(
        string RepoId, string ModelId, long Done, long Total, string Dir);

    //how much room the drive holding path has, or null when it cannot be read. null means unreadable, so nothing may refuse on it
    long? FreeSpaceOn(string path) => null;

    //every paused fetch under the weights root, newest first. empty by default, so a probe that predates this offers nothing
    IReadOnlyList<PausedFetch> PausedFetches() => [];

    //the offer that finishes a paused fetch, rebuilt from its record rather than a shelf row. null when the record has gone since the scan
    Gatto.Cli.Setup.ModelFetchOffer? ResumeOffer(PausedFetch paused) => null;

    //fetch the model's files, each checked against the Hub's fingerprint. the pause token is not ct, since a stop deletes the partial and a pause keeps it
    Gatto.Core.Acquire.HubFetchResult FetchModel(
        Gatto.Cli.Setup.ModelFetchOffer offer,
        IProgress<FetchTick>? progress = null,
        CancellationToken ct = default,
        CancellationToken pause = default) =>
        throw new NotSupportedException(
            "this walk reached the model fetch and the probe does not implement it");

    //throw away the partials a paused or dropped fetch kept. it throws by default, so a signature that drifts fails loudly instead of changing behaviour
    void DeletePartial(Gatto.Cli.Setup.ModelFetchOffer offer) =>
        throw new NotSupportedException(
            "this walk reached the partial delete and the probe does not implement it");


    //whether a file is still being written, read from a .part sibling. the order of the acceptance checks stays the flow's
    bool StillArriving(string ggufPath) => false;

    //the sha256 of a file that arrived, or null when it cannot be read. null is unreadable, which is a different screen from a mismatch
    string? Sha256Of(string ggufPath) => null;

    //the shipped --version probe on the path the user just gave, run at once so a wrong download shows as it is typed
    Gatto.Core.Tools.ProbeResult VerifyLlamaServer(string exePath);

    //the context gatto computes for a model on this machine. it is never the GGUF's trained ceiling, which would scaffold a model that cannot load here
    int ContextFor(string ggufPath);

    //ask the configured setup to actually say something, against what was written. a config never exercised is as unproven as a fresh install
    Gatto.Core.Acquire.ProveOutcome ProveIt(string? modelId,
        IProgress<Gatto.Roles.Audition.AuditionProgress>? progress = null);

    //the inputs for the where-it-lives offer, or null when there is nothing to offer. null when the file is not under Downloads, which is the common case
    MoveOffer? MoveOfferFor(string ggufPath);

    //the general.architecture in this file's own GGUF header, or null when it cannot be read. null claims nothing, so SetupFlow asks nothing
    string? ArchitectureOf(string ggufPath);

    //the vision projector sitting beside this model, or null. null means none was found, so the screen makes no claim
    (string Path, long Bytes)? ProjectorFor(string ggufPath);

    //the fit regime for a model alone and with its projector, priced as one process. null when the machine could not be read
    (Gatto.Core.Models.FitRegime Alone, Gatto.Core.Models.FitRegime WithProjector)? PairFit(
        string ggufPath, long projectorBytes);

    //the id of the model that already wraps this model file, or null. asked of every model by resolved path, so a non-default one is not invisible
    string? ExistingModelFor(string ggufPath);

    //the model id this file would take when a different file already has it, or null. null also covers an id ExistingModelFor already claimed

    //what adopting this file means for the model whose id it would take. a match adds, a proven difference collides, and anything less asks
    (Gatto.Roles.IdClash Kind, string? Id) ClashFor(string ggufPath, string? incomingRepoId);

    //the model already on gatto's list with a file of this name, or null. the repo answers, and the file name only for a model with no repo, all in one place
    string? ModelForHubRow(string repoId, string ggufFileName) => null;

    //the active file name for a model id, read from the profile's ActiveFile.Of. null drops the row, and the Hub row is null on adoption and discovery paths
    string? ActiveFileFor(string modelId) => null;

    //what a model's profile recorded: the repo it was fetched from and whether it names a projector. null when the profile will not load
    AddedFacts? Added(string modelId) => null;

    //true when a projector, by its file name, sits in the folder beside this file. no header is read, since the local shelf asks it of every row
    bool ProjectorBeside(string ggufPath) => false;

    //the active GGUF's path and size in one read, so the row cannot describe another file. the folder comes from this path, and verified is never claimed
    (string Path, long Bytes)? ActiveModelFile(string modelId) => null;

    //the id of the model whose weights the session holds, or null. null covers both nothing loaded and nothing readable, so a mark is drawn only when known
    string? LoadedModelId() => null;

    //the file the loaded model is holding, or null. the id alone cannot tell one file of a profile from its siblings
    string? LoadedModelPath() => null;

    //is this gatto installed where a terminal can find it. read-only, since the install itself is applied at the one apply point
    (InstallState State, string Dir) InstallStatus();

    //the installed copy's version, or null when it cannot be read. required rather than defaulted, so a probe cannot silently skip the offer
    string? InstalledVersion();

    //the version of the running image. a probe rather than GattoVersion.String, so a test can drive the comparison against InstalledVersion
    string RunningVersion();

    //the exe this gatto runs from, the from half of the install screen's pair. behind the probe seam, since SetupFlow must stay pure
    string RunningFrom();

    //whether the update-check question has been answered. null means never asked, the only state that may raise it
    bool? UpdateConsent();

    //the name of an endpoint already pointing at baseUrl, or null. the value is matched as written after trimming a trailing slash, so a URL parse never decides
    string? ExistingEndpointFor(string baseUrl);

    //where this run's settings are written, named after a successful finish and nowhere else. say nothing about writing until something is written
    string ConfigPath();
}
