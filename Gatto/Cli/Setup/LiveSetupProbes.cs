using Gatto.Core.Acquire;
using Gatto.Core.Hardware;
using Gatto.Core.Home;
using Gatto.Roles;

namespace Gatto.Cli.Setup;

//every method here only reads, failures answer honestly rather than throwing, and the parameters exist so a test can keep off this machine
internal sealed class LiveSetupProbes(string homePath, Gatto.Terminal.GlyphSet glyphs,
    TextWriter? notes = null, bool rich = false,
    Func<ProbeOutcome>? readHardware = null, string? downloadsDir = null,
    Func<int, Gatto.Roles.ServeManager.IServeProcess?>? serverLookup = null,
    //null means pool index 0, GattoApp passes the real draw, and a test that builds a probe can't reach randomness
    Func<Gatto.Repl.Render.PurrFrames>? purrSet = null,
    //the factory is what lets a test watch the manager's stop path, null keeps each site's own construction
    Func<Gatto.Roles.ServeManager>? managerFactory = null,
    //null keeps the runner's ten-minute budget, a test shortens it rather than letting its fake server die, which stops the double-start guard firing
    TimeSpan? readyBudget = null,
    //where Dispose's own lines go, the note sink is not read again after the record is printed, so null means the notes writer
    TextWriter? afterWalk = null,
    //a server start in progress, said to the face's purr and not to the notes, so it never reaches the scrollback
    Action? working = null,
    //the shelf's Hub requests go through this handler, null keeps the network, and a test hands a fake Hub here
    HttpMessageHandler? hubHandler = null)
    : ISetupProbes, IDisposable
{
    private readonly Gatto.Terminal.GlyphSet _glyphs = glyphs;

    //the factory wins when one is injected, and lookup stays on the serving read alone so stop and describe keep their own pids
    private Gatto.Roles.ServeManager Manager(GattoConfig config,
        Func<int, Gatto.Roles.ServeManager.IServeProcess?>? lookup = null) =>
        managerFactory?.Invoke() ?? new Gatto.Roles.ServeManager(homePath, config.LlamaServer ?? "", lookup);

    //the size the model's active file comes to, null when the id names nothing loadable and the rung drops the size
    private string? SizeWordsOrNull(string modelId)
    {
        try
        {
            var model = Gatto.Roles.Model.Load(Path.Combine(homePath, "models"), modelId);
            return Gatto.Core.Acquire.ModelDiscovery.SetBytesOrNull(model.Profile.ActivePath) is { } bytes
                ? SizeWords.Gb(bytes, approx: true)
                : null;
        }
        catch (Exception) { return null; }
    }

    //the serve words with no theme, the screen appends them to a sentence of its own
    private ServeLines Lines(TextWriter log, Action<TimeSpan>? loading = null) =>
        new(new CliSurface(log, null, _glyphs), _glyphs,
            loading is null ? null : new StartWatch(loading, () => { }));

    //passes every event of a start through and keeps whether the poll saw the server ready, so a line can say what the poll saw
    private sealed class StartOutcome(Gatto.Roles.IServeListener inner) : Gatto.Roles.IServeListener
    {
        public bool SawReady { get; private set; }

        public void Refused(string model, int pid) => inner.Refused(model, pid);
        public void Ready(string model, int port, int pid) { SawReady = true; inner.Ready(model, port, pid); }
        public void Loading(TimeSpan elapsed) => inner.Loading(elapsed);
        public void StillLoading(string model, int pid) => inner.StillLoading(model, pid);
        public void ServingForeground(string model, int pid) => inner.ServingForeground(model, pid);
        public void Exited(string model) => inner.Exited(model);
        public void Stopping(string model, int pid) => inner.Stopping(model, pid);
        public void KillFailed(int pid, string message) => inner.KillFailed(pid, message);
        public void DiedDuringLoad(IReadOnlyList<string> tail, bool logAbsent) => inner.DiedDuringLoad(tail, logAbsent);
        public void NotServingOnStop() => inner.NotServingOnStop();
        public void Stopped(string model, int pid) => inner.Stopped(model, pid);
        public void NotOurs(int pid, string processName) => inner.NotOurs(pid, processName);
        public void StaleOnStop(int pid, string model) => inner.StaleOnStop(pid, model);
        public void NotServingOnStatus() => inner.NotServingOnStatus();
        public void StaleOnStatus(int pid, string model) => inner.StaleOnStatus(pid, model);
        public void DiedOnStatus(string model, int pid, int? exitCode, string? exitedAt, IReadOnlyList<string>? tail) =>
            inner.DiedOnStatus(model, pid, exitCode, exitedAt, tail);
        public void Status(string model, int port, int pid, bool healthy) => inner.Status(model, port, pid, healthy);
        public void PumpFault(Gatto.Roles.ServeLogFault fault) => inner.PumpFault(fault);
    }

    //the server this wizard started and therefore must stop, a server that was already running belongs to somebody else
    private Gatto.Roles.ServeManager? _started;

    //one server lives from here to Dispose, shared by the audition and prove-it. the return is null while serving, or a short reason the caller must use

    //the one read of the serving fact, and an unreadable machine answers Idle rather than throwing
    private Gatto.Roles.ServingState Serving(string modelId)
    {
        if (_started is not null) return _started.Serving(modelId);   //ours, this wizard run started it
        try
        {
            var config = GattoConfig.Load(homePath);
            //the lookup is null in production and the manager asks the OS, a test passes one to avoid a live llama-server process
            return Manager(config, serverLookup).Serving(modelId);
        }
        catch (Exception) { return new Gatto.Roles.ServingState.Idle(); }
    }

    //the id of the other model being served, taken from the ServingOther record rather than compared as a string
    public string? ServerHeldByAnother(string modelId) => Serving(modelId).Match<string?>(
        idle: () => null,
        servingThis: _ => null,
        servingOther: r => r.Model);

    //the other half of the same read, asked through the same Match
    public bool ServerAlreadyUp(string modelId) => Serving(modelId).Match(
        idle: () => false,
        servingThis: _ => true,
        servingOther: _ => false);

    //asks the register by the key it is written under, the verdict and date clauses stay in BadgeRegister.Read
    public Gatto.Core.Acquire.Badge? BadgeForFile(string fileName) =>
        string.IsNullOrWhiteSpace(fileName) ? null : BadgeRegister.Lookup(homePath, fileName);

    //stops the session's server after a yes to the swap, leaves the wizard's own to Dispose, and answers false on a failed stop
    public bool ReleaseHeldServer()
    {
        try
        {
            var config = GattoConfig.Load(homePath);
            var manager = Manager(config);

            //read the held model's id before the stop, the restart needs a Model and a stale record would pretend it still loads
            var held = manager.DescribeRunning()?.Model;

            var log = new StringWriter();
            if (manager.StopAsync(Lines(log), CancellationToken.None).GetAwaiter().GetResult() == 0)
            {
                _released = held;
                return true;
            }
            Note("couldn't stop the running server. " + log.ToString().Trim());
            return false;
        }
        catch (Exception ex)
        {
            Note($"couldn't stop the running server. {ex.Message}");
            return false;
        }
    }

    //the wizard's live screen through the model load, prove-it passes null because it reports its own arrival

    //maps the runner's stumble for the screen, and the log path comes from ServeManager rather than being spelled here
    internal static CheckStumble StumbleFrom(
        Gatto.Roles.Audition.AuditionRunner.AuditionStumbleException ex, string homePath) =>
        ex.Kind == Gatto.Roles.Audition.AuditionStumbleKind.ExitedWhileLoading
            ? new CheckStumble.Exited(ex.LastLines, Gatto.Roles.ServeManager.LogPathFor(homePath))
            : new CheckStumble.Stopped(ex.LastLines);

    //every failure answers null, and the floor comes from the header the predicate read rather than being recomputed here
    private CheckStumble.Incomplete? FiledIncomplete(string modelId)
    {
        try
        {
            var path = Model.Load(System.IO.Path.Combine(homePath, "models"), modelId)
                .Profile.ActivePath;
            var found = new System.IO.FileInfo(path).Length;
            using var fs = new System.IO.FileStream(path, System.IO.FileMode.Open,
                System.IO.FileAccess.Read, System.IO.FileShare.Read);
            var header = Gatto.Core.Models.GgufHeaderParser.Parse(fs);
            return header.IsShorterThanItsHeaderClaims(found) && header.Tensors is { } tensors
                ? new CheckStumble.Incomplete(
                    System.IO.Path.GetFileName(path),
                    //keep the trailing separator, the row reads in <folder> and a bare name would read as a file
                    System.IO.Path.GetDirectoryName(path) is { Length: > 0 } dir
                        ? dir + System.IO.Path.DirectorySeparatorChar
                        : "",
                    found, tensors.MinimumFileBytes)
                : null;
        }
        catch (Exception) { return null; }
    }

    private string? EnsureServer(string modelId,
        IProgress<Gatto.Roles.Audition.AuditionProgress>? progress = null)
    {
        if (_started is not null) return null;                  //ours already and still running, so nothing to start
        if (_startFailure is not null) return _startFailure;    //the first failure comes back, there is no second attempt
        try
        {
            var config = GattoConfig.Load(homePath);
            var manager = Manager(config);
            //one read with three arms, and the refusal names what the record says is loaded
            var answer = manager.Serving(modelId).Match(
                idle: () => (Start: true, Why: (string?)null),
                servingThis: _ => (Start: false, Why: (string?)null),   //already serving the model we asked for
                servingOther: r => (Start: false, Why: (string?)$"{r.Model} is the running server"));
            if (!answer.Start) return answer.Why;

            var model = Gatto.Roles.Model.Load(System.IO.Path.Combine(homePath, "models"), modelId);
            working?.Invoke();

            var log = new StringWriter();
            if (manager.StartAsync(model, Lines(log), CancellationToken.None).GetAwaiter().GetResult() != 0)
            {
                Note(log.ToString().Trim());
                return _startFailure = $"the server for '{modelId}' did not start";
            }
            _started = manager;
            //only here, the process is up and the weights are not in yet, so loading the model is true
            progress?.Report(Gatto.Roles.Audition.AuditionProgress.Note($"serving {modelId}"));

            var last = TimeSpan.Zero;
            var readiness = manager.AwaitReadyAsync(
                model.Profile.Port, readyBudget ?? Gatto.Roles.Audition.AuditionRunner.ReadyBudget,
                elapsed =>
                {
                    if (elapsed - last < TimeSpan.FromSeconds(30)) return;
                    last = elapsed;
                    working?.Invoke();
                    //the live screen says it in its own words, inside the interval guard so no row claims a wait that hasn't happened
                    progress?.Report(Gatto.Roles.Audition.AuditionProgress.Loading());
                },
                CancellationToken.None).GetAwaiter().GetResult();

            if (readiness != Gatto.Roles.ServerReadiness.Ready)
                Note($"{modelId} has not answered yet, the next step may not reach it");
        }
        //a slow server is not a failed start, so the note stays on the transcript for a caller that fails later
        catch (Exception ex)
        {
            Note(ex.Message);
            return _startFailure = ex.Message;
        }
        return null;
    }

    //a failed start is remembered, a retry could only fail the same way and would repeat the log on the screen
    private string? _startFailure;

    //one note per line, split before sanitizing since Sanitize strips newlines and would flatten a log into one row
    private void Note(string text)
    {
        if (notes is null || text.Length == 0) return;
        foreach (var row in TranscriptRows(text)) notes.WriteLine(row);
        notes.Flush();
    }

    //one text in, one sanitized row per line out, extracted so the flattening bug has a test
    internal static string[] TranscriptRows(string text)
    {
        var lines = text.Split('\n');
        var rows = new string[lines.Length];
        for (var i = 0; i < lines.Length; i++)
            rows[i] = "  " + Gatto.Terminal.TermText.Sanitize(lines[i].TrimEnd('\r'));
        return rows;
    }

    //hands the server to the session and clears it so Dispose leaves it alone, only the "start gatto now" ending calls this
    public bool HandOverServer()
    {
        if (_started is null) return false;
        _started = null;
        return true;
    }

    //stops what this wizard started and says so on the transcript when that fails
    public void Dispose()
    {
        //no early return, the restore below must run even when nothing was started, and Dispose's lines go through afterWalk
        var console = afterWalk ?? notes ?? TextWriter.Null;
        using var line = new TickLine(console, rich);
        if (_started is { } manager)
        {
            _started = null;
            try
            {
                var log = new StringWriter();
                if (manager.StopAsync(Lines(log), CancellationToken.None).GetAwaiter().GetResult() != 0)
                    Say(line, "couldn't stop the server setup started, run gatto serve stop. " + log.ToString().Trim());
            }
            catch (Exception ex)
            {
                Say(line, $"couldn't stop the server setup started, run gatto serve stop. {ex.Message}");
            }
        }

        RestoreReleased(line);
        //one empty line after the last row, so the prompt doesn't sit against it
        if (line.Noted) console.WriteLine();
        lock (_hubGate) _hubHttp?.Dispose();
    }

    //the same row rule as Note, applied to the line Dispose speaks through
    private static void Say(TickLine line, string text)
    {
        if (text.Length == 0) return;
        foreach (var row in TranscriptRows(text)) line.Note(row);
    }

    //restores on every exit and only after the stop, so the stop can't take the restored server back down
    private void RestoreReleased(TickLine line)
    {
        if (_released is not { Length: > 0 } id) return;
        _released = null;

        try
        {
            var config = GattoConfig.Load(homePath);
            Gatto.Roles.Model model;
            try { model = Gatto.Roles.Model.Load(Path.Combine(homePath, "models"), id); }
            catch (GattoConfigException ex)
            {
                Say(line, $"couldn't put {id} back: {ex.Message}");
                return;
            }

            //the number on the line is the elapsed time the poll measured
            var putting = "  " + Gatto.Terminal.TermText.Sanitize($"putting {id} back{_glyphs.Ellipsis}");
            if (line.Rich) line.Live(putting); else line.Note(putting);

            var manager = Manager(config);
            var log = new StringWriter();
            var start = new StartOutcome(Lines(log, elapsed => line.Live($"{putting} {Gatto.Core.ElapsedText.Of(elapsed)}")));
            if (manager.StartAsync(model, start, CancellationToken.None).GetAwaiter().GetResult() != 0)
            {
                Say(line, $"couldn't put {id} back, run gatto serve start {id}. " + log.ToString().Trim());
                return;
            }

            //a zero from StartAsync means ready or a poll that ran out, and the start's own report says which
            Say(line, start.SawReady ? $"{id} is back" : "still loading, gatto serve status shows when it answers");
        }
        catch (Exception ex)
        {
            Say(line, $"couldn't put {id} back, run gatto serve start {id}. {ex.Message}");
        }
    }

    //the id of the server ReleaseHeldServer stopped, cleared by the restore so a second Dispose can't start it twice
    private string? _released;
    //the probe is bounded, a wizard that hangs on a wedged WMI is worse than one that says it couldn't tell

    //not implemented yet, the rows fall back to the processor and the graphics chip until a read ignores a departed card's leftover DriverDesc

    //the two names cleaned for the screen, read off the same run as Hardware so name and memory figure can't disagree
    public HardwareNames HardwareNames() => HardwareNaming.Clean(Reading().CpuName, Reading().GpuName);

    //read off the same run, so the pin names the device the fit counted
    public string? ServeOnlyGpu() => Reading().ServeOnlyGpu;

    public HardwareSnapshot? Hardware() => Reading().Snapshot;

    //one probe run per wizard, latched so the memory figure and the chip name can't come from two runs, a failed read included
    private ProbeOutcome Reading()
    {
        //the search runs beside the flow, so two threads can ask at once and the lock keeps it to one probe
        lock (_readingGate) return _reading ??= (readHardware ?? Read)();
    }

    private readonly object _readingGate = new();

    //15 seconds max, about fifty times the 300 ms the vulkan loader measured on this machine, so a cold driver has room
    private static ProbeOutcome Read()
    {
        try { return HardwareProbe.Run(TimeSpan.FromSeconds(15)); }
        catch (Exception ex) { return new(null, ex.Message); }   //an unread machine, which the wizard reports instead of dying
    }

    private ProbeOutcome? _reading;

    //the configured llama-server only when the file is on disk, a configured path to a deleted file is not satisfied

    //hands EngineSearch the machine's real roots, and the drive root comes from SystemDirectory so Windows on another letter still sweeps
    public IReadOnlyList<Gatto.Roles.FoundEngine> EngineSweep() =>
        Gatto.Roles.EngineSearch.Find(
            (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            Path.Combine(homePath, "llama"),
            Path.GetPathRoot(Environment.SystemDirectory),
            //the sweep must include the engine this home already names, or a configured engine reads as missing
            LlamaServerPath());

    public string? LlamaServerPath()
    {
        try
        {
            var configured = GattoConfig.Load(homePath).LlamaServer;
            return configured is { Length: > 0 } p && File.Exists(p) ? p : null;
        }
        catch (GattoConfigException) { return null; }
    }

    //asks the shipped check whether the default model resolves and its files are all there
    public bool HasResolvableModel()
    {
        try
        {
            var config = GattoConfig.Load(homePath);
            if (config.DefaultModel is not { Length: > 0 } id) return false;

            var model = Model.Load(System.IO.Path.Combine(homePath, "models"), id);
            return Gatto.Core.Acquire.ModelDiscovery
                .MissingFiles(model.Profile.ActivePath, model.Profile.MmProj).Count == 0;
        }
        catch (GattoConfigException) { return false; }   //an unloadable config resolves nothing
    }

    //the machine's answer, read through the glyph set's own rule
    public bool IsLegacyConsole() =>
        Gatto.Terminal.GlyphSet.IsLegacyConsole(Gatto.Terminal.GlyphSet.HostEnv());

    //the active file and its size from the same Model.Load the name comes from, so both rows describe one file
    public (string Path, long Bytes)? ActiveModelFile(string modelId)
    {
        try
        {
            var model = Model.Load(System.IO.Path.Combine(homePath, "models"), modelId);
            var info = new System.IO.FileInfo(model.Profile.ActivePath);
            //a gone file is "we cannot say" rather than zero bytes, and the check stays though the catch answers null too
            return info.Exists ? (info.FullName, info.Length) : null;
        }
        catch (Exception)
        {
            //every failure is null, a missing or unreadable profile would otherwise put an unmeasured number on the screen
            return null;
        }
    }

    //the repo and projector the profile recorded, from the same Model.Load the other reads use, null when it will not load
    public AddedFacts? Added(string modelId)
    {
        try
        {
            var profile = Model.Load(System.IO.Path.Combine(homePath, "models"), modelId).Profile;
            return new AddedFacts(profile.Source?.RepoId, profile.MmProj is { Length: > 0 });
        }
        catch (Exception) { return null; }
    }

    //the name rule alone, the header pass ProjectorFor falls back to would read every sibling of every row
    public bool ProjectorBeside(string ggufPath)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(ggufPath);
            return dir is { Length: > 0 } && Directory.Exists(dir)
                && Directory.EnumerateFiles(dir, "*.gguf").Any(p =>
                    !string.Equals(p, ggufPath, StringComparison.OrdinalIgnoreCase) && ModelDiscovery.IsProjector(p, header: null));
        }
        catch (Exception) { return false; }
    }

    //reads the file name from the profile the writes already produced, so the check runs against the model on disk
    public string? ActiveFileFor(string modelId)
    {
        try
        {
            var model = Model.Load(System.IO.Path.Combine(homePath, "models"), modelId);
            return System.IO.Path.GetFileName(model.Profile.ActivePath);
        }
        //every failure is null, a wrong file name would send the user off checking the wrong model
        catch (Exception) { return null; }
    }

    //blocks on the async probe at the edge, with an untimed client and a CTS deadline so no HttpClient.Timeout caps it
    public ConnectProbe? ProbeServer(IReadOnlyList<int>? skip = null)
    {
        try
        {
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            return ServerConnect.ProbeAsync(http, cts.Token, skip).GetAwaiter().GetResult();
        }
        catch (Exception) { return null; }   //nothing answered is a real state, so null is the answer
    }

    public ConnectProbe? ProbeAt(string baseUrl)
    {
        try
        {
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            return ServerConnect.ProbeOneAsync(http, baseUrl, cts.Token).GetAwaiter().GetResult();
        }
        catch (Exception) { return null; }   //an address that does not answer is a state of its own
    }

    //asks DescribeRunning so the pid rule decides, a foreign server on gatto's port answers null
    public string? GattoServingOn(int port)
    {
        try
        {
            var config = GattoConfig.Load(homePath);
            var manager = Manager(config);
            return manager.DescribeRunning() is { } running && running.Port == port
                ? running.Model
                : null;
        }
        catch (Exception) { return null; }   //cannot say is the same answer as no
    }

    //the roots are policy and live here, and Downloads is on the list because that's where a browser puts a 30 GB file
    public ScanResult Scan(string? extraRoot)
    {
        string? weightsDir = null;
        try { weightsDir = GattoConfig.Load(homePath).WeightsRoot; }
        catch (GattoConfigException) { } //an unloadable config costs a root, the scan goes on

        var roots = ScanRootsFor(homePath, weightsDir, downloadsDir ?? ModelLocation.DownloadsDir,
            extraRoot);

        //two sweeps at two depths, the typed root deeper since naming it is the consent, and a merge can only duplicate a whole set
        var ambient = extraRoot is { Length: > 0 }
            ? roots.Where(r => !string.Equals(r, extraRoot, StringComparison.Ordinal)).ToArray()
            : roots;
        try
        {
            var found = ModelDiscovery.Scan(ambient, ModelDiscovery.AmbientDepth, _localReads).ToList();
            if (extraRoot is { Length: > 0 })
            {
                var seen = found.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
                found.AddRange(ModelDiscovery
                    .Scan([extraRoot], ModelDiscovery.TypedDepth, _localReads)
                    .Where(f => seen.Add(f.Path)));
            }
            return new ScanResult(found, roots);
        }
        catch (Exception) { return new ScanResult([], roots); }
    }

    //the scan's header reads go to disk under the home, so a second run reads only the files that changed
    private readonly LocalReadStore _localReads = new(homePath);

    //the root policy in one place, so a test can point Downloads elsewhere and no second copy of the rule can drift
    internal static IReadOnlyList<string> ScanRootsFor(
        string homePath, string? configWeightsDir, string downloadsDir, string? extraRoot)
    {
        //the weights root comes from SuggestedDir, the function the move offer and the uninstall already ask, and the models folder is never scanned
        var roots = new List<string> { ModelLocation.SuggestedDir(homePath, configWeightsDir) };
        if (downloadsDir is { Length: > 0 }) roots.Add(downloadsDir);
        if (extraRoot is { Length: > 0 }) roots.Add(extraRoot);
        return roots;
    }

    //empty is a legitimate answer, and an unreadable hardware probe returns nothing rather than pricing an invented machine

    //the tree memo lives on the session object, a memo inside one HubClient would forget on every chip switch. its header reads also go to disk under the home
    private readonly HubTreeMemo _trees = new(new HubReadStore(homePath));

    //one Hub client for the session, so the window the Hub reported in one search binds the next. its http is untimed and each search holds its own deadline
    private HubClient? _hub;
    private HttpClient? _hubHttp;
    private readonly object _hubGate = new();

    private HubClient Hub()
    {
        lock (_hubGate)
        {
            _hubHttp ??= hubHandler is null
                ? new HttpClient { Timeout = Timeout.InfiniteTimeSpan }
                : new HttpClient(hubHandler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
            return _hub ??= new HubClient(_hubHttp);
        }
    }

    //the last unlifted shelf and the lit families it was for, so a lifts from it and a stop never takes a landing row away
    private (string Lit, IReadOnlyList<ModelRow> Rows)? _landing;

    private static string LitKey(IReadOnlySet<string> lit) =>
        string.Join(",", lit.Select(f => f.ToLowerInvariant()).Order(StringComparer.Ordinal));

    //the shelf of original models, or a typed search, under the same deadline as every search. a surprise is an empty shelf with no cause
    public ShelfOutcome SearchModels(ModelSearchRequest request, IProgress<SearchProgress>? progress, CancellationToken ct)
    {
        var empty = new ShelfOutcome([], 0, 0, 0, false, 0, [], null);
        if (Hardware() is not { } snapshot) return empty;
        var hw = Gatto.Core.Hardware.HardwareClassifier.Classify(snapshot);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            if (request.Search is { } typed && !string.IsNullOrWhiteSpace(typed))
                return HubSearch.TypedSearchAsync(Hub(), UploaderAllowlist.Load(), Families.Load(), typed.Trim(), hw,
                    Tui.Pane.KvContext, cts.Token, _trees, request.Lifted, caller: ct).GetAwaiter().GetResult();

            var key = LitKey(request.Lit);
            var landing = request.Lifted && _landing is { } l && l.Lit == key ? l.Rows : null;
            var outcome = ShelfSearch.AssembleAsync(Hub(), UploaderAllowlist.Load(), Families.Load(), request.Lit, hw,
                Tui.Pane.KvContext, request.Lifted, cts.Token, memo: _trees, progress: progress, landing: landing, caller: ct)
                .GetAwaiter().GetResult();
            if (!request.Lifted) _landing = (key, outcome.Rows);
            return outcome;
        }
        catch (Exception) { return empty; }
    }

    //read a null Status as the malformed case (the guard refused the id), a status code as the Hub answering badly
    public TypedIdOutcome EvaluateTypedId(string repoId)
    {
        try
        {
            if (Hardware() is not { } snapshot) return new TypedIdOutcome.Unreachable(false);
            var hw = Gatto.Core.Hardware.HardwareClassifier.Classify(snapshot);

            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            //look a typed id up directly, the allowlist shapes browsing and never limits what a user may name. the fit window is the pane's own value
            var lookup = HubSearch.LookupModelAsync(
                new HubClient(http), repoId, hw, Tui.Pane.KvContext, cts.Token).GetAwaiter().GetResult();

            //the floor decides the pick and never what a user may take, so a named repo whose every file sits under it is priced lifted
            if (lookup.Row is { RowFile: null } bare) lookup = (ShelfSearch.Lifted(bare, hw, Tui.Pane.KvContext), false);

            //a null row means the repo answered with nothing usable (a failed request throws instead), and the outcome type says which nothing
            return TypedIdOutcome.For(lookup);
        }
        catch (HubUnavailableException ex)
        {
            //a null Status means the repo-id guard refused it before the wire, so it's a typo rather than an outage
            return ex.Status is null ? new TypedIdOutcome.Malformed() : new TypedIdOutcome.Unreachable(ex.Gated);
        }
        catch (Exception) { return new TypedIdOutcome.Unreachable(false); }
    }

    //a GattoConfigException is the harness failing, so report CouldNotRun and no verdict about the model

    //the look-up forwards to the one map the other call sites read, so modeled means one thing here
    public string? ExistingModelFor(string ggufPath) =>
        Gatto.Roles.ModelScaffold.ModelFor(System.IO.Path.Combine(homePath, "models"), ggufPath);

    //a forwarder on purpose, the clash rule lives in ModelScaffold beside the slugging it is about
    public (Gatto.Roles.IdClash Kind, string? Id) ClashFor(string ggufPath, string? incomingRepoId) =>
        Gatto.Roles.ModelScaffold.ClashFor(
            System.IO.Path.Combine(homePath, "models"), ggufPath, incomingRepoId);

    //a forwarder on purpose, the rule reads the same map as its neighbour and lives in ModelScaffold
    public string? ModelForHubRow(string repoId, string ggufFileName) =>
        Gatto.Roles.ModelScaffold.ModelForHubRow(
            System.IO.Path.Combine(homePath, "models"), repoId, ggufFileName);

    //asks the same Model.MatchesLoaded test the picker uses, answers null on any doubt, and starts no server to do it
    public string? LoadedModelId()
    {
        try
        {
            var config = GattoConfig.Load(homePath);
            var endpointName = config.DefaultEndpoint ?? "local";
            if (!config.Endpoints.TryGetValue(endpointName, out var ep)) return null;
            if (ep.BaseUrl is not { Length: > 0 } baseUrl) return null;

            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var loaded = Gatto.Core.Client.ServeProbe
                .ProbeAsync(http, baseUrl, cts.Token, ep.ApiKey).GetAwaiter().GetResult();
            if (loaded is null) return null;

            var modelsDir = System.IO.Path.Combine(homePath, "models");
            foreach (var id in Model.ListIds(modelsDir))
            {
                Model model;
                try { model = Model.Load(modelsDir, id); }
                catch (GattoConfigException) { continue; }   //a broken model is not the loaded one
                if (Model.MatchesLoaded(model, loaded) == true) return id;
            }

            return null;
        }
        catch (Exception) { return null; }
    }

    //reads the file off the same Model that answered MatchesLoaded, so the path can't name a different profile
    public string? LoadedModelPath()
    {
        try
        {
            var config = GattoConfig.Load(homePath);
            var endpointName = config.DefaultEndpoint ?? "local";
            if (!config.Endpoints.TryGetValue(endpointName, out var ep)) return null;
            if (ep.BaseUrl is not { Length: > 0 } baseUrl) return null;

            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var loaded = Gatto.Core.Client.ServeProbe
                .ProbeAsync(http, baseUrl, cts.Token, ep.ApiKey).GetAwaiter().GetResult();
            if (loaded is null) return null;

            var modelsDir = System.IO.Path.Combine(homePath, "models");
            foreach (var id in Model.ListIds(modelsDir))
            {
                Model model;
                try { model = Model.Load(modelsDir, id); }
                catch (GattoConfigException) { continue; }
                if (Model.MatchesLoaded(model, loaded) == true) return model.ActivePath;
            }

            return null;
        }
        catch (Exception) { return null; }
    }

    //an unreadable config answers never asked, so the update question is put again
    public bool? UpdateConsent()
    {
        try { return GattoConfig.Load(homePath).UpdateCheck; }
        catch (Exception) { return null; }
    }

    //empty when the runtime won't say where gatto runs from, the screen then shows the pair with no source
    public string RunningFrom() => Environment.ProcessPath ?? "";

    //an unreadable PATH counts as not on it (gatto then offers to finish an install rather than claim one works)
    public (InstallState State, string Dir) InstallStatus()
    {
        var dir = SelfInstall.DefaultDir();
        var (raw, _) = PathInstaller.ReadRaw();
        var onPath = raw is not null && UserPath.Contains(raw, dir);
        return (SelfInstall.Probe(dir, Environment.ProcessPath,
            System.IO.File.Exists(SelfInstall.ExeIn(dir)), onPath,
            SelfInstall.IsFrameworkDependent(Environment.ProcessPath)), dir);
    }

    //reads the same directory InstallStatus reports on, so state and version can't describe two installs
    public string? InstalledVersion() => SelfInstall.InstalledVersion(SelfInstall.DefaultDir());

    public string RunningVersion() => Gatto.Core.GattoVersion.String;

    //match on the ordinal comparison with a trailing slash ignored, and answer no endpoint when the config won't read
    public string? ExistingEndpointFor(string baseUrl)
    {
        static string Norm(string u) => u.TrimEnd('/');
        try
        {
            foreach (var (name, endpoint) in GattoConfig.Load(homePath).Endpoints)
                if (endpoint.BaseUrl is { Length: > 0 } url
                    && string.Equals(Norm(url), Norm(baseUrl), StringComparison.OrdinalIgnoreCase))
                    return name;
        }
        catch (GattoConfigException) { }
        return null;
    }

    public AuditionCheck RunAudition(string modelId, string? repoId,
        IProgress<Gatto.Roles.Audition.AuditionProgress>? progress = null,
        CancellationToken ct = default)
    {
        try
        {
            //no preamble note here, the face prints notes after the wait they would announce

            //decided before any spawn, only this arm may name a cause (a file shorter than its furthest tensor's end can't be complete)
            if (FiledIncomplete(modelId) is { } filed)
                return new AuditionCheck(AuditionOutcome.CouldNotRun, Stumble: filed);

            //pass the sink in, this call is where the wait happens, and return the server's own reason as the stumble
            if (EnsureServer(modelId, progress) is { } why)   //one server for the whole post-write phase, the runner reuses it and leaves it up for prove-it
                return new AuditionCheck(AuditionOutcome.CouldNotRun,   //don't run the battery, the runner would fail the same way and print the same log again
                    Stumble: new CheckStumble.Unavailable(why));

            //take the ticker row down before anything else is written, a tick and a committed line would collide on one row
            using var ticker = AuditionNotes();
            //both readers see one run, and the ticker goes first, the transcript's row must keep its place
            var feed = Both(ticker, progress);
            var verdict = Gatto.Roles.Audition.AuditionRunner
                .RunAsync(homePath, modelId, feed, ct, serveVoice: w => Lines(w),
                    loadingWords: elapsed => Gatto.Cli.LoadingRow.Words(
                        elapsed, modelId, SizeWordsOrNull(modelId), _glyphs))
                .GetAwaiter().GetResult();
            ticker?.Finish();

            //records what was measured, is never fatal, and keys the record by file name plus the Hub repo id
            DateOnly? recorded = null;
            try
            {
                recorded = Gatto.Roles.Audition.BadgeWriter.Write(
                    homePath, verdict.Stamp.ModelFileName, verdict, repoId);
            }
            catch (Exception ex) { Note($"couldn't record the result: {ex.Message}"); }

            //the stamp's own fields go through verbatim, and an empty ThinkingNote becomes null so no blank row renders
            var tally = verdict.Tally();
            return new AuditionCheck(
                verdict.Pass ? AuditionOutcome.Passed : AuditionOutcome.Failed,
                new AuditionFacts(
                    verdict.Stamp.Quant,
                    verdict.Stamp.SamplingNote,
                    verdict.Stamp.ThinkingNote is { Length: > 0 } t ? t : null,
                    verdict.DecodeTokS,
                    //take the verdict's own tally, a count taken here would be a second opinion about the same run
                    tally.Passed,
                    tally.Ran),
                recorded,
                //pass the runner's records whole, so the wizard's rows and the CLI report say the same thing about one run
                verdict.Tasks);
        }
        //a cancel from this token is CouldNotRun and prints nothing, the transcript must not narrate the key the user pressed
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new AuditionCheck(AuditionOutcome.CouldNotRun);
        }
        //the exception holds the case beside the prose, so the wizard picks a screen without reading the message back into a fact
        catch (Gatto.Roles.Audition.AuditionRunner.AuditionStumbleException ex)
        {
            Note(ex.Message);
            return new AuditionCheck(AuditionOutcome.CouldNotRun, Stumble: StumbleFrom(ex, homePath));
        }
        catch (Exception ex)
        {
            //nothing was measured, so this is CouldNotRun, and the cause goes on the transcript beside it
            Note(ex.Message);
            return new AuditionCheck(AuditionOutcome.CouldNotRun);
        }
    }

    //one row updating in place, and rich-ness passed in so /setup keeps to one committed line per task
    private AuditionTicker? AuditionNotes() =>
        notes is null ? null : new AuditionTicker(notes, rich, _glyphs,
            frames: purrSet is null ? Gatto.Repl.Render.PurrFrames.Full : purrSet());

    //feeds both sinks from one run, and either side may be null without silencing the other
    internal static IProgress<Gatto.Roles.Audition.AuditionProgress> Both(
        IProgress<Gatto.Roles.Audition.AuditionProgress>? first,
        IProgress<Gatto.Roles.Audition.AuditionProgress>? second) =>
        new SyncProgress(p => { first?.Report(p); second?.Report(p); });

    //report on the runner's own thread, Progress<T> posts to a captured context and lands after the screen it belongs to
    private sealed class SyncProgress(Action<Gatto.Roles.Audition.AuditionProgress> onReport)
        : IProgress<Gatto.Roles.Audition.AuditionProgress>
    {
        public void Report(Gatto.Roles.Audition.AuditionProgress value) => onReport(value);
    }

    //synchronous like SyncProgress, and it reports on the fetching thread so the last tick must be safe cross-thread
    private sealed class Sink(Action<(long Done, long Total)> onReport)
        : IProgress<(long Done, long Total)>
    {
        public void Report((long Done, long Total) value) => onReport(value);
    }

    public Gatto.Roles.LlamaAsset? ChooseLlamaAsset()
    {
        if (Hardware() is not { } snapshot) return null;
        return Gatto.Roles.LlamaAssetSteering.Choose(
            Gatto.Core.Hardware.HardwareClassifier.Classify(snapshot));
    }

    //every failure answers with a null asset instead of throwing, and the folder still comes back for the offline arm
    public Gatto.Cli.Setup.EngineFetchOffer? EngineOffer()
    {
        if (ChooseLlamaAsset() is not { } pick) return null;

        var into = Gatto.Cli.LlamaFetch.DirFor(homePath, LlamaAssetSteering.PinnedRelease);
        try
        {
            using var http = Gatto.Cli.UpdateDownload.Client(TimeSpan.FromSeconds(10));
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var release = Gatto.Cli.UpdateCheck.FetchAsync(
                    http, Gatto.Cli.LlamaFetch.ReleaseUrl(LlamaAssetSteering.PinnedRelease),
                    DateTimeOffset.UtcNow, cts.Token)
                .GetAwaiter().GetResult();

            //match the asset by name against the build this machine was steered to, any other asset is the failure this step prevents
            Gatto.Cli.Setup.EngineAsset? Named(string? name) =>
                name is { Length: > 0 }
                && release?.Assets.FirstOrDefault(
                    a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)) is { } a
                    ? new(a.Name, a.Url, a.Sha256, a.Size)
                    : null;

            var server = Named(pick.ZipName);

            //both files come from the same payload, so a release missing the companion yields no offer at all
            var companion = Named(pick.CudartZipName);
            if (server is null || (pick.CudartZipName is { Length: > 0 } && companion is null))
                return new(into, null);

            return new(into, new Gatto.Cli.Setup.EnginePair(server, companion));
        }
        catch (Exception) { return new(into, null); }
    }


    //offer only a folder that has both its record and its parts, and count the arrived members in Done as well
    public IReadOnlyList<ISetupProbes.PausedFetch> PausedFetches()
    {
        try
        {
            var root = Gatto.Core.Acquire.ModelLocation.SuggestedDir(homePath, SafeWeightsDir());
            if (!Directory.Exists(root)) return [];

            var found = new List<(DateTime At, ISetupProbes.PausedFetch Paused)>();
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                if (Gatto.Core.Acquire.HubFetch.ReadRecord(dir) is not { } record) continue;

                var done = 0L;
                var parts = 0;
                foreach (var m in record.Members)
                {
                    var whole = new FileInfo(Path.Combine(dir, m.FileName));
                    var part = new FileInfo(
                        Gatto.Core.Acquire.HubFetch.PartPath(dir, m.FileName));
                    if (whole.Exists) done += whole.Length;
                    else if (part.Exists) { done += part.Length; parts++; }
                }
                //a record with no part left is a fetch that finished or was cleared, so there is nothing to resume
                if (parts == 0) continue;

                found.Add((Directory.GetLastWriteTimeUtc(dir), new ISetupProbes.PausedFetch(
                    record.RepoId, Path.GetFileName(dir), done, record.Total, dir)));
            }

            return [.. found.OrderByDescending(f => f.At).Select(f => f.Paused)];
        }
        catch (Exception) { return []; }
    }

    //rebuilds the offer from the record, and the members' own hashes mean the resumed fetch verifies as the first one would
    public Gatto.Cli.Setup.ModelFetchOffer? ResumeOffer(ISetupProbes.PausedFetch paused)
    {
        if (Gatto.Core.Acquire.HubFetch.ReadRecord(paused.Dir) is not { } record) return null;

        var quant = new Gatto.Core.Acquire.HubQuant(
            record.Members[0].FileName, record.Total, record.Members[0].Sha256,
            record.Members.Count, record.Members);
        return new Gatto.Cli.Setup.ModelFetchOffer(record.RepoId, paused.ModelId,
            paused.Dir + Path.DirectorySeparatorChar, quant, null);
    }

    //read the free space from the path's root, and answer null when it can't be read, since null is not a refusal
    public long? FreeSpaceOn(string path)
    {
        try
        {
            var root = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(path));
            if (root is not { Length: > 0 }) return null;
            var drive = new DriveInfo(root);
            return drive.IsReady ? drive.AvailableFreeSpace : null;
        }
        catch (Exception)
        {
            //every exception here means the same thing to the caller, gatto does not know, so one catch covers them
            return null;
        }
    }

    //withhold the offer unless every file can be verified, the consent screen says each fingerprint is checked
    public Gatto.Cli.Setup.ModelFetchOffer? ModelOffer(Gatto.Core.Acquire.ModelRow row)
    {
        if (row.RowFile is not { } file || row.RowQuant is not { } weights) return null;
        if (!Gatto.Core.Acquire.HubFetch.CanVerify(weights)) return null;

        //the encoder comes from the chosen file's own repo, since another publisher's encoder belongs to another download
        var encoder = Gatto.Core.Acquire.ProjectorPick.Best(row.RowProjectors);
        if (encoder is not null && !Gatto.Core.Acquire.HubFetch.CanVerify(encoder)) return null;

        //take the id from the repo through the rule the scaffold uses, so the fetch's folder and the model's id are one name
        var id = Gatto.Core.Models.ModelId.FromRepo(file.RepoId);
        return new(file.RepoId, id,
            ModelLocation.ForModel(homePath, SafeWeightsDir(), id) + Path.DirectorySeparatorChar,
            weights, encoder);
    }

    //fetch the weights first and stop there if they fail, so a bad quant never spends bandwidth on the encoder
    public Gatto.Core.Acquire.HubFetchResult FetchModel(
        Gatto.Cli.Setup.ModelFetchOffer offer,
        IProgress<FetchTick>? progress = null,
        CancellationToken ct = default,
        CancellationToken pause = default)
    {
        //no client timeout here, the caller's token owns the deadline and a client timeout under it would cap the download
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

        //pass the pause token to both calls, the encoder would otherwise ignore the key
        var weights = Gatto.Core.Acquire.HubFetch
            .FetchAsync(http, offer.RepoId, offer.Weights, offer.IntoDir, progress, ct, pause: pause)
            .GetAwaiter().GetResult();
        if (weights.Outcome != Gatto.Core.Acquire.HubFetchOutcome.Arrived || offer.Projector is null)
            return weights;

        return Gatto.Core.Acquire.HubFetch
            .FetchAsync(http, offer.RepoId, offer.Projector, offer.IntoDir, progress, ct, pause: pause)
            .GetAwaiter().GetResult();
    }

    //delete both quants' partials, a paused pair can have bytes for either one
    public void DeletePartial(Gatto.Cli.Setup.ModelFetchOffer offer)
    {
        Gatto.Core.Acquire.HubFetch.DeletePartial(offer.Weights, offer.IntoDir);
        if (offer.Projector is { } projector)
            Gatto.Core.Acquire.HubFetch.DeletePartial(projector, offer.IntoDir);
    }


    //a .part beside the file means it is still being written
    public bool StillArriving(string ggufPath)
    {
        try { return File.Exists(ggufPath + ".part"); }
        catch (Exception) { return false; }
    }

    //hash what landed, and answer null instead of throwing, an unreadable file is a state the screen has words for
    public string? Sha256Of(string ggufPath)
    {
        try
        {
            return Gatto.Core.Acquire.Checksum
                //an empty expected hash on purpose, this reads the actual hash and leaves the comparison to the flow
                .Sha256Async(ggufPath, string.Empty, null, CancellationToken.None)
                .GetAwaiter().GetResult().ActualSha256;
        }
        catch (Exception) { return null; }
    }

    //stage the zip in the update folder and sweep it whatever happens, a stray archive in the engine folder would confuse the sweep
    public Gatto.Cli.EngineFetch FetchEngine(
        Gatto.Cli.Setup.EnginePair assets,
        IProgress<FetchTick>? progress = null,
        CancellationToken ct = default)
    {
        var staging = Gatto.Cli.UpdateDownload.Dir(homePath);
        var into = Gatto.Cli.LlamaFetch.DirFor(homePath, LlamaAssetSteering.PinnedRelease);
        //one or two files, so a tick can say file 1 of 2 without asking again
        var count = assets.Companion is null ? 1 : 2;
        try
        {
            using var http = Gatto.Cli.UpdateDownload.Client(TimeSpan.FromSeconds(10));

            //the order lives in Land, a helper the suite can drive, this method supplies only the network and the disk
            return Gatto.Cli.LlamaFetch.Land(assets, Get,
                (zip, isServer) => Gatto.Cli.LlamaFetch.Extract(zip, into, isServer));

            Gatto.Cli.Landing Get(Gatto.Cli.Setup.EngineAsset a)
            {
                //read which half this is off the pair, so a reordering in Land can't make the screen name the wrong file
                var index = ReferenceEquals(a, assets.Server) ? 1 : 2;
                //start the clock when this file does, a stopwatch over the pair would count the second file's wait as progress
                var started = System.Diagnostics.Stopwatch.StartNew();
                var zip = Gatto.Cli.UpdateDownload.FetchAsync(
                        http, new Gatto.Cli.ReleaseAsset(a.ZipName, a.Url, a.Sha256, a.Size),
                        staging,
                        progress: progress is null ? null : new Sink(p => progress.Report(
                            new FetchTick(a.ZipName, index, count, p.Done, p.Total,
                                started.ElapsedMilliseconds))),
                        ct)
                    .GetAwaiter().GetResult();

                //verify both halves, an absent digest refuses as loudly as a wrong one
                return Gatto.Cli.UpdateDownload.VerifyAsync(zip, a.Sha256, ct,
                    LlamaAssetSteering.ReleasePage).GetAwaiter().GetResult() is { } bad
                    ? new(null, bad)
                    : new(zip, null);
            }
        }
        catch (Exception ex)
        {
            return new(null, $"gatto couldn't download the engine: {ex.Message}");
        }
        finally { Gatto.Cli.UpdateDownload.Sweep(homePath); }
    }

    //bounded at 10 s, a binary that never answers must not hang the wizard

    //resolve the path first, a folder is a legal answer, and report the path that was actually run
    public Gatto.Core.Tools.ProbeResult VerifyLlamaServer(string exePath)
    {
        var resolved = LlamaAssetSteering.ResolveServerExe(exePath);
        return Gatto.Core.Tools.LlamaServerProbe.Run(resolved, TimeSpan.FromSeconds(10))
            with { Ran = resolved };
    }

    //read the projector's size here, so the pair arithmetic has one answer, and answer null when it can't be measured
    public (string Path, long Bytes)? ProjectorFor(string ggufPath)
    {
        try
        {
            if (ModelDiscovery.ProjectorFor(ggufPath) is not { } path) return null;
            var info = new FileInfo(path);
            return info.Exists ? (path, info.Length) : null;
        }
        catch (Exception) { return null; }
    }

    //offer a move only for a file under Downloads, and price the whole shard set rather than the first file
    public MoveOffer? MoveOfferFor(string ggufPath)
    {
        try
        {
            var full = Path.GetFullPath(ggufPath);
            if (!ModelLocation.IsInside(ModelLocation.DownloadsDir, full)) return null;

            var suggested = ModelLocation.SuggestedDir(homePath, SafeWeightsDir());
            if (ModelLocation.IsInside(suggested, full)) return null;

            //don't offer a move for a file that is still arriving, a .part or .crdownload beside it is the tell
            if (ModelDiscovery.DownloadInProgress(full)) return null;

            //read the set's size through SetBytesOrNull, one spelling for how big a model is, and keep 0 for an unreadable path
            var bytes = ModelDiscovery.SetBytesOrNull(full) ?? 0;

            return new MoveOffer(suggested, ModelLocation.IsCrossVolume(full, suggested), bytes,
                StorageSense.DescribeState());
        }
        catch (Exception) { return null; }
    }

    private string? SafeWeightsDir()
    {
        try { return GattoConfig.Load(homePath).WeightsRoot; }
        catch (Exception) { return null; }   //a broken config is not a reason to have no suggestion
    }

    //read the architecture from the file's header, and answer null when it won't parse, so gatto's failure isn't a claim about the model
    public string? ArchitectureOf(string ggufPath)
    {
        try { return Gatto.Core.Models.GgufReader.Read(ggufPath).Architecture; }
        catch (Exception) { return null; }
    }

    //the regime for the model alone and with the projector, null when the machine or the header can't be read
    public (Gatto.Core.Models.FitRegime Alone, Gatto.Core.Models.FitRegime WithProjector)? PairFit(
        string ggufPath, long projectorBytes)
    {
        try
        {
            if (Hardware() is not { } snapshot) return null;
            var hw = Gatto.Core.Hardware.HardwareClassifier.Classify(snapshot);

            using var fs = File.OpenRead(ggufPath);
            //parse the model's own header, the projector's would judge the wrong file
            var header = Gatto.Core.Models.GgufHeaderParser.Parse(fs);
            //price the whole shard set, and return null when none of it can be read, 0 would be a wrong regime
            if (ModelDiscovery.SetBytesOrNull(ggufPath) is not { } bytes) return null;
            var ctx = ContextFor(ggufPath);
            var streamed = ModelDiscovery.StreamedBytesOrNull(ggufPath, header);

            var alone = Gatto.Core.Models.FitArithmetic.Judge(
                Gatto.Core.Models.FitArithmetic.Estimate(
                    header, bytes, ctx, Gatto.Core.Models.KvCacheKind.F16, streamed), hw);
            var paired = ProjectorPair.Regime(
                header, bytes, projectorBytes, ctx, Gatto.Core.Models.KvCacheKind.F16, hw, streamed);
            return (alone, paired);
        }
        catch (Exception) { return null; }
    }

    //fall back to the offered context when anything is unreadable, a ceiling that cannot load is worse
    public int ContextFor(string ggufPath)
    {
        try
        {
            if (Hardware() is not { } snapshot) return SetupFlow.OfferedContext;
            var hw = Gatto.Core.Hardware.HardwareClassifier.Classify(snapshot);

            //parse with the parser's header, the arithmetic needs the KV terms and GgufMetadata has only three strings
            using var fs = File.OpenRead(ggufPath);
            var header = Gatto.Core.Models.GgufHeaderParser.Parse(fs);

            var ceiling = header.ContextLength is long c and > 0 ? (int)Math.Min(c, int.MaxValue) : SetupFlow.OfferedContext;
            //price the whole set before the ladder steps down, a one-shard price stops at the ceiling and cannot load
            if (ModelDiscovery.SetBytesOrNull(ggufPath) is not { } bytes) return SetupFlow.OfferedContext;
            var streamed = ModelDiscovery.StreamedBytesOrNull(ggufPath, header);

            //the largest budget that actually fits, tried from the model's own ceiling down
            foreach (var candidate in new[] { ceiling, 131072, 65536, 32768, 16384, 8192, 4096 })
            {
                if (candidate > ceiling) continue;
                var estimate = Gatto.Core.Models.FitArithmetic.Estimate(
                    header, bytes, candidate, Gatto.Core.Models.KvCacheKind.F16, streamed);
                if (Gatto.Core.Models.FitArithmetic.Judge(estimate, hw) is
                    Gatto.Core.Models.FitRegime.FitsGpu or Gatto.Core.Models.FitRegime.FitsRamOnly)
                    return candidate;
            }
            return SetupFlow.OfferedContext;
        }
        catch (Exception) { return SetupFlow.OfferedContext; }
    }

    //a generous deadline, a cold 30 GB load is the normal case here and minutes of silence are expected

    //the id comes in as an argument rather than from default_model, so this proves what the run just built

    //the file the user opens afterwards, composed like every other home path here so it can't name a file the writer didn't write
    public string ConfigPath() => System.IO.Path.Combine(homePath, "gatto.json");

    public ProveOutcome ProveIt(string? modelId,
        IProgress<Gatto.Roles.Audition.AuditionProgress>? progress = null)
    {
        try
        {
            //ask who holds the server before starting one, a held server is a state rather than a start failure
            if (modelId is { Length: > 0 } want && ServerHeldByAnother(want) is { } holding)
                return new ProveOutcome(false, $"{holding} is the running server", TimeSpan.Zero,
                    ServedByAnother: holding);

            if (modelId is { Length: > 0 } wanted && EnsureServer(wanted) is { } why)
                return new ProveOutcome(false, why, TimeSpan.Zero, StartFailed: true);

            //say the server is up only after the start succeeded, so the screen's row is a report
            progress?.Report(Gatto.Roles.Audition.AuditionProgress.Note($"serving {modelId}"));

            var config = GattoConfig.Load(homePath);
            var endpointName = config.DefaultEndpoint ?? "local";
            if (!config.Endpoints.TryGetValue(endpointName, out var ep))
                return new ProveOutcome(false, "no endpoint is configured to talk to", TimeSpan.Zero);

            var baseUrl = ep.BaseUrl;
            if (baseUrl is null && (modelId ?? config.DefaultModel) is { } id)
            {
                var model = Gatto.Roles.Model.Load(System.IO.Path.Combine(homePath, "models"), id);
                baseUrl = $"http://127.0.0.1:{model.Profile.Port}";
            }
            if (baseUrl is null) return new ProveOutcome(false, "no endpoint is configured to talk to", TimeSpan.Zero);

            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(6));
            var client = new Gatto.Core.Client.OpenAiCompatClient(
                //this slot takes the endpoint name, the model reaches the wire through the ChatRequest
                http, endpointName, ep with { BaseUrl = baseUrl },
                //don't tell the user to check network or base_url, a wizard-built local endpoint has neither
                downHint: $"nothing answered at {baseUrl}, the server may still be loading, or may "
                          + "have stopped; gatto doctor says which");

            //the namespace stays, this class has a ProveIt of its own. the model is this run's, or the config's, the served id the connect road found
            return Gatto.Core.Acquire.ProveIt.RunAsync(client, modelId ?? config.DefaultModel ?? "", cts.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex) { return new ProveOutcome(false, ex.Message, TimeSpan.Zero); }
    }
}
