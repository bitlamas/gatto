using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Gatto.Core;
using Gatto.Core.Client;
using Gatto.Core.Home;
using Gatto.Roles.Models;

namespace Gatto.Roles.Serve;

//this layer reports through IServeListener and composes no sentence, since the wording belongs to the layer that paints it
public sealed class ServeManager
{
    //the smallest process surface the manager needs, faked in tests, and ProcessName is the kill guard's only proof a pid is still ours
    internal interface IServeProcess
    {
        int Pid { get; }
        bool HasExited { get; }
        string ProcessName { get; }
        void Kill();
        int? ExitCode => null;
    }

    private readonly string _homePath;
    private readonly string _llamaServerPath;
    private readonly Func<ProcessStartInfo, IServeProcess> _spawn;
    private readonly Func<int, IServeProcess?> _lookup;   //a pid lookup, null when no such process exists
    private readonly HttpClient _http;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _pollTimeout;

    //the /props probe's own deadline, null for the probe's default, so a test can give a fake server room that the suite's load takes
    private readonly TimeSpan? _probeDeadline;

    //each health read holds its own deadline, since a timeout on the client would bound every caller's read beneath its token
    private static readonly TimeSpan HealthReadDeadline = TimeSpan.FromSeconds(5);

    private string ServeJsonPath => Path.Combine(_homePath, "serve.json");
    //the one place the server log path is composed, so every screen that names the file names the same one
    internal static string LogPathFor(string homePath) => Path.Combine(homePath, "serve.log");

    private string ServeLogPath => LogPathFor(_homePath);

    public ServeManager(string homePath, string llamaServerPath)
        : this(homePath, llamaServerPath, null)
    {
    }

    //the same manager with a replaced pid lookup, for driving a recorded serve.json, and it delegates the defaults to the main constructor
    internal ServeManager(string homePath, string llamaServerPath,
        Func<int, IServeProcess?>? lookup)
        : this(homePath, llamaServerPath, null, lookup,
               new HttpClient { Timeout = Timeout.InfiniteTimeSpan },
               TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(60))
    {
    }

    internal ServeManager(
        string homePath, string llamaServerPath,
        Func<ProcessStartInfo, IServeProcess>? spawn,
        Func<int, IServeProcess?>? lookup,
        HttpClient http,
        TimeSpan pollInterval,
        TimeSpan pollTimeout,
        TimeSpan? probeDeadline = null)
    {
        _homePath = homePath;
        _llamaServerPath = llamaServerPath;
        _spawn = spawn ?? DefaultSpawn;
        _lookup = lookup ?? DefaultLookup;
        _http = http;
        _pollInterval = pollInterval;
        _pollTimeout = pollTimeout;
        _probeDeadline = probeDeadline;
    }

    //counts the refused starts, since the refusal fires before any spawn and a spawn count cannot tell an ask from no ask
    internal int RefusedStarts { get; private set; }

    //each unanswered detached poll raises Loading with the elapsed time, so a caller can keep the wait visible (the foreground path never polls)
    public async Task<int> StartAsync(Model model, IServeListener listener, CancellationToken ct,
        bool foreground = false)
    {
        //check the binary path before touching serve.json or spawning anything (a model's own llama_server wins over the global one)
        var overridden = !string.IsNullOrWhiteSpace(model.Profile.LlamaServer);
        var serverPath = overridden ? model.Profile.LlamaServer! : _llamaServerPath;
        if (string.IsNullOrWhiteSpace(serverPath) || !File.Exists(serverPath))
            throw new GattoConfigException(overridden
                ? $"model '{model.Id}' sets llama_server to '{serverPath}' which doesn't exist"
                : "set llama_server in gatto.json to your llama-server.exe path");

        //refuse only when a live server of ours is still recorded. a stale or recycled serve.json is overwritten, the pid's new owner keeps running
        if (ReadState() is { } existing && Classify(existing).State == PidState.Ours)
        {
            RefusedStarts++;
            listener.Refused(existing.Model, existing.Pid);
            return 1;
        }

        var port = model.Profile.Port;
        var psi = new ProcessStartInfo
        {
            FileName = serverPath,
            UseShellExecute = false,
            //don't set CreateNoWindow for foreground, it denies the child a console outright unless output is redirected, and the prints would vanish silently
            RedirectStandardOutput = !foreground,
            RedirectStandardError = !foreground,
            CreateNoWindow = !foreground,
        };
        if (!foreground)
        {
            //read the detached pipes as UTF-8 without BOM, .NET's default decoding via the OEM codepage garbles the log. the setter throws when output isn't redirected
            var utf8 = new UTF8Encoding(false);
            psi.StandardOutputEncoding = utf8;
            psi.StandardErrorEncoding = utf8;
        }
        //keep the api_key out of argv, any process lister reads command lines. it goes through a file rewritten on each start so it can't go stale
        string? apiKeyFile = null;
        if (model.Profile.ApiKey is { Length: > 0 } apiKey)
        {
            apiKeyFile = Path.Combine(_homePath, "models", model.Id, "api-key.txt");
            SecretFile.Write(apiKeyFile, apiKey + Environment.NewLine);
        }

        foreach (var arg in ServeArgs.Compose(model.Profile, apiKeyFile))
            psi.ArgumentList.Add(arg);

        var proc = _spawn(psi);
        //write serve.json before the health poll, stop needs the pid of a child that never got ready. only a detached run writes serve.log
        WriteState(new ServeState(proc.Pid, model.Id, port,
            DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture), Logged: !foreground));

        try
        {
            return foreground
                ? await RunForegroundAsync(model, proc, listener, ct).ConfigureAwait(false)
                : await RunDetachedAsync(model, proc, port, listener, ct).ConfigureAwait(false);
        }
        finally
        {
            //report a log pump failure from this one finally, it covers every exit of both paths (foreground has no pump)
            ReportLogPumpFault(listener);
        }
    }

    //poll /health till it answers, the child dies, or we time out or get cancelled, leaving the server running and serve.json in place
    private async Task<int> RunDetachedAsync(Model model, IServeProcess proc, int port,
        IServeListener listener, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var deviceAnswered = false;
        while (sw.Elapsed < _pollTimeout)
        {
            if (proc.HasExited)
                return DiedDuringLoad(listener);

            if (DeviceGate(model, proc, ref deviceAnswered, last: false, listener) is { } stopped) return stopped;

            //ctrl+c stops the wait but must never touch the child (a detached server stays up and the exit is 0)
            try
            {
                if (await HealthOkAsync(port, ct).ConfigureAwait(false))
                {
                    if (DeviceGate(model, proc, ref deviceAnswered, last: true, listener) is { } late) return late;
                    listener.Ready(model.Id, port, proc.Pid);
                    return 0;
                }
                listener.Loading(sw.Elapsed);
                await Task.Delay(_pollInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
        }

        if (proc.HasExited)
            return DiedDuringLoad(listener);

        if (DeviceGate(model, proc, ref deviceAnswered, last: true, listener) is { } lateStop) return lateStop;

        //the server is still coming up. it keeps running and serve.json stays, the user checks with gatto serve status
        listener.StillLoading(model.Id, proc.Pid);
        return 0;
    }

    //the engine names the card it took in 1 log line before any weight moves, and a bracketed id follows the name, so a greedy name keeps its own parentheses
    private static readonly System.Text.RegularExpressions.Regex DeviceTaken =
        new(@"using device (?<dev>[A-Za-z]+\d+) \((?<name>.+)\) \(", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    //the first such line in serve.log, or null while there is none (no log, an unreadable one, or the engine has not written it yet)
    private (string Index, string Name)? DeviceLine()
    {
        foreach (var line in TailLog(int.MaxValue))
        {
            var m = DeviceTaken.Match(line);
            if (m.Success) return (m.Groups["dev"].Value, m.Groups["name"].Value);
        }
        return null;
    }

    //compares the line with the card the profile names, once: null continues the poll, an exit code means the server was stopped
    private int? DeviceGate(Model model, IServeProcess proc, ref bool answered, bool last, IServeListener listener)
    {
        if (answered || model.Profile.Device is not { } wanted) return null;
        if (DeviceLine() is { } took)
        {
            answered = true;
            if (string.Equals(took.Name.Trim(), wanted.Trim(), StringComparison.Ordinal)) return null;
            listener.WrongDevice(model.Id, proc.Pid, wanted, took.Name, took.Index);
            try { proc.Kill(); }
            catch (Exception ex)
            {
                //the server is alive after a failed kill, so its record stays for stop to retry
                listener.KillFailed(proc.Pid, ex.Message);
                return 1;
            }
            CleanupServeJson();
            return 1;
        }
        if (last)
        {
            answered = true;
            listener.DeviceUnread(model.Id, wanted, foreground: false);
        }
        return null;
    }

    //wait till the port really answers, start's exit 0 doesn't mean ready (it also comes back while the model loads). use this before sending requests
    public async Task<ServerReadiness> AwaitReadyAsync(
        int port, TimeSpan budget, Action<TimeSpan>? stillWaiting, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            //check health first, an already-running server must return Ready even on a zero budget
            if (await HealthOkAsync(port, ct).ConfigureAwait(false)) return ServerReadiness.Ready;

            if (ReadState() is { } s && Classify(s).State == PidState.Gone) return ServerReadiness.Died;

            if (sw.Elapsed >= budget) return ServerReadiness.StillLoading;

            stillWaiting?.Invoke(sw.Elapsed);
            await Task.Delay(_pollInterval, ct).ConfigureAwait(false);
        }
    }

    //the tail of the rolling serve.log, the server's own last words when a start goes wrong
    internal IReadOnlyList<string> RecentLogLines(int n) => TailLog(n);

    //the child streams its own log, gatto blocks until it exits or ctrl+c (then stop the server and clean serve.json)
    private async Task<int> RunForegroundAsync(Model model, IServeProcess proc, IServeListener listener,
        CancellationToken ct)
    {
        listener.ServingForeground(model.Id, proc.Pid);
        //no log in the foreground, so a named card cannot be checked and the row says so once
        if (model.Profile.Device is { } named) listener.DeviceUnread(model.Id, named, foreground: true);

        //set e.Cancel so gatto lives to stop the child cleanly and tidy serve.json (ctrl+c would otherwise kill both raw)
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; try { linked.Cancel(); } catch { } }; //the source may already be disposed when this fires
        Console.CancelKeyPress += onCancel;
        try
        {
            while (!proc.HasExited && !linked.IsCancellationRequested)
            {
                try { await Task.Delay(_pollInterval, linked.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
        finally { Console.CancelKeyPress -= onCancel; }

        if (proc.HasExited)
        {
            //it died by itself, its last output is already on screen here. the record stays with how it ended, for status and a session that loses it
            if (ReadState() is { } died && died.Pid == proc.Pid)
                WriteState(died with
                {
                    ExitCode = proc.ExitCode,
                    ExitedAt = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                });
            listener.Exited(model.Id);
            return 1;
        }

        //we were asked to stop, so stop the server we started
        listener.Stopping(model.Id, proc.Pid);
        try { proc.Kill(); }
        catch (Exception ex)
        {
            listener.KillFailed(proc.Pid, ex.Message);
            CleanupServeJson();
            return 1;
        }
        CleanupServeJson();
        listener.Stopped(model.Id, proc.Pid);
        return 0;
    }

    //no log file and an empty log are different facts. a foreground start writes no log on purpose, so report it as absent
    private int DiedDuringLoad(IServeListener listener)
    {
        listener.DiedDuringLoad(TailLog(10), logAbsent: !File.Exists(ServeLogPath));
        CleanupServeJson();
        return 1;
    }

    //the exit code reads 0 for four different outcomes. a caller that needs to know which one calls Stop and reads the outcome
    public Task<int> StopAsync(IServeListener listener, CancellationToken ct) =>
        Task.FromResult(Stop(listener).Result is StopResult.KillFailed ? 1 : 0);

    internal enum StopResult { Stopped, NotServing, Stale, NotOurs, KillFailed }

    //what a stop read and did
    internal sealed record StopOutcome(StopResult Result, string? Model, int? Pid, string? Error);

    internal StopOutcome Stop(IServeListener listener)
    {
        var state = ReadState();
        if (state is null)
        {
            //this covers never started and a corrupt serve.json, ReadState returns null for both
            CleanupServeJson();   //best effort, a corrupt file mustn't block the next start
            listener.NotServingOnStop();
            return new StopOutcome(StopResult.NotServing, null, null, null);
        }

        var (which, proc) = Classify(state);
        switch (which)
        {
            case PidState.Ours:
                try
                {
                    proc!.Kill();
                }
                catch (Exception ex)
                {
                    //the server may still be alive, leave serve.json so status and a retry find it
                    listener.KillFailed(state.Pid, ex.Message);
                    return new StopOutcome(StopResult.KillFailed, state.Model, state.Pid, ex.Message);
                }
                CleanupServeJson();
                listener.Stopped(state.Model, state.Pid);
                return new StopOutcome(StopResult.Stopped, state.Model, state.Pid, null);

            case PidState.NotOurs:
                //the pid is alive but some other program owns it now, leave it running and drop our stale record
                CleanupServeJson();
                listener.NotOurs(state.Pid, proc!.ProcessName);
                return new StopOutcome(StopResult.NotOurs, state.Model, state.Pid, null);

            default: //the pid is gone
                CleanupServeJson();
                listener.StaleOnStop(state.Pid, state.Model);
                return new StopOutcome(StopResult.Stale, state.Model, state.Pid, null);
        }
    }

    //the human status. the machine-readable one is its own method below, and the two share no branch.
    public async Task<int> StatusAsync(IServeListener listener, CancellationToken ct)
    {
        var state = ReadState();
        if (state is null)
        {
            //null means never started or a corrupt record, the reader can't tell them apart
            listener.NotServingOnStatus();
            return 0;
        }

        var (which, _) = Classify(state);
        if (which == PidState.Gone)
        {
            listener.DiedOnStatus(state.Model, state.Pid, state.ExitCode, state.ExitedAt,
                state.Logged == true ? TailLog(10) : null);
            return 0;
        }
        if (which != PidState.Ours)
        {
            listener.StaleOnStatus(state.Pid, state.Model);
            return 0;
        }

        var healthy = await HealthOkAsync(state.Port, ct).ConfigureAwait(false);
        listener.Status(state.Model, state.Port, state.Pid, healthy);
        return 0;
    }

    //a parser reads these bytes, they're a contract, don't add anything above or around the object
    public async Task<int> StatusJsonAsync(TextWriter output, CancellationToken ct)
    {
        var state = ReadState();
        if (state is null)
        {
            output.WriteLine(ServeStatusJson.Build(null, null, null));
            return 0;
        }

        var (which, _) = Classify(state);
        if (which != PidState.Ours)
        {
            //keep the model id in the output even when the pid is stale (all-null means never served, a stale pointer shows the id)
            output.WriteLine(ServeStatusJson.Build(null, state.Model, null));
            return 0;
        }

        //load the model before the probe, its profile is the only source of the api_key the probe may need
        Model? model = null;
        try
        {
            model = Model.Load(Path.Combine(_homePath, "models"), state.Model);
        }
        catch (GattoConfigException)
        {
        }

        var loaded = await ServeProbe.ProbeAsync(
            _http, $"http://127.0.0.1:{state.Port}", ct, model?.Profile.ApiKey, _probeDeadline).ConfigureAwait(false);
        var matches = model is null ? null : Model.MatchesLoaded(model, loaded);

        output.WriteLine(ServeStatusJson.Build(loaded, state.Model, matches));
        return 0;
    }
    //kill guard

    private enum PidState { Ours, NotOurs, Gone }

    //a record that holds how its server ended is gone, whatever process holds that pid now, so a stop can never kill its new owner
    private (PidState State, IServeProcess? Proc) Classify(ServeState s) =>
        s.ExitedAt is not null ? (PidState.Gone, null) : Classify(s.Pid);

    //every caller that might kill goes through here. only a live process named llama-server is ours, everything else is untouchable
    private (PidState State, IServeProcess? Proc) Classify(int pid)
    {
        var proc = _lookup(pid);
        if (proc is null || proc.HasExited)
            return (PidState.Gone, null);
        return proc.ProcessName.Contains("llama-server", StringComparison.OrdinalIgnoreCase)
            ? (PidState.Ours, proc)
            : (PidState.NotOurs, proc);
    }

    //serve.json state

    //the serve.json record only while the pid is still ours (a stale or recycled record reads as null)
    internal RunningInfo? DescribeRunning() =>
        ReadState() is { } s && Classify(s).State == PidState.Ours
            ? new RunningInfo(s.Model, s.Port, s.Started, s.Pid)
            : null;

    //every caller that decides asks here whether the model it wants is the one serving. this class owns that fact, callers don't re-derive it
    internal ServingState Serving(string wantModelId) =>
        DescribeRunning() is not { } running
            ? new ServingState.Idle()
            : string.Equals(running.Model, wantModelId, StringComparison.Ordinal)
                ? new ServingState.ServingThis(running)
                : new ServingState.ServingOther(running);

    private sealed record ServeState(int Pid, string Model, int Port, string Started,
        bool? Logged = null, int? ExitCode = null, string? ExitedAt = null);

    //the recorded server that stopped running, for the session that talks to its port. the code is null unless a foreground start saw it end
    internal sealed record DeadServer(string Model, int Pid, int? ExitCode, string? ExitedAt);

    internal DeadServer? Dead(int port) =>
        ReadState() is { } s && s.Port == port && Classify(s).State == PidState.Gone
            ? new DeadServer(s.Model, s.Pid, s.ExitCode, s.ExitedAt)
            : null;

    //only pid and port are required. a serve.json that parses them must never hide a live server
    private ServeState? ReadState()
    {
        if (!File.Exists(ServeJsonPath)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(ServeJsonPath));
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return null;

            //pid and port must read, or this file can't describe a process and "not serving" is the honest answer
            if (!r.TryGetProperty("pid", out var pidEl) || !pidEl.TryGetInt32(out var pid)) return null;
            if (!r.TryGetProperty("port", out var portEl) || !portEl.TryGetInt32(out var port)) return null;

            return new ServeState(pid, OptionalString(r, "model") ?? UnknownModelId, port,
                OptionalString(r, "started") ?? "",
                r.TryGetProperty("log", out var logEl) && logEl.ValueKind is JsonValueKind.True or JsonValueKind.False ? logEl.GetBoolean() : null,
                r.TryGetProperty("exited", out var codeEl) && codeEl.TryGetInt32(out var code) ? code : null,
                OptionalString(r, "exited_at"));
        }
        catch (Exception)
        {
            return null;   //corrupt or partial serve.json reads as not serving, it mustn't crash
        }
    }

    //what status shows when serve.json can't name the model, a word beats blanks and says what gatto knows
    internal const string UnknownModelId = "(unknown)";

    private static string? OptionalString(JsonElement root, string key) =>
        root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    //a written serve.json means a start was tried and this pid is the one to clean up. whether the server is healthy takes a probe
    private void WriteState(ServeState s)
    {
        Directory.CreateDirectory(_homePath);
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteNumber("pid", s.Pid);
            w.WriteString("model", s.Model);
            w.WriteNumber("port", s.Port);
            w.WriteString("started", s.Started);
            if (s.Logged is bool logged) w.WriteBoolean("log", logged);
            if (s.ExitCode is int code) w.WriteNumber("exited", code);
            if (s.ExitedAt is { } at) w.WriteString("exited_at", at);
            w.WriteEndObject();
        }
        //temp file then move, a reader never sees a half-written serve.json
        var tmp = ServeJsonPath + ".tmp";
        File.WriteAllBytes(tmp, ms.ToArray());
        File.Move(tmp, ServeJsonPath, overwrite: true);
    }

    private void CleanupServeJson()
    {
        try { if (File.Exists(ServeJsonPath)) File.Delete(ServeJsonPath); }
        catch (Exception) { } //best effort, a locked or missing file mustn't crash stop or status
    }

    private async Task<bool> HealthOkAsync(int port, CancellationToken ct)
    {
        using var read = CancellationTokenSource.CreateLinkedTokenSource(ct);
        read.CancelAfter(HealthReadDeadline);
        try
        {
            using var resp = await _http.GetAsync(
                $"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/health", read.Token).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;   //let real cancellation bubble, the caller's poll loop catches it
        }
        catch (Exception)
        {
            return false;   //connection refused, not up yet, per-request timeout: all of them just mean not ready
        }
    }

    private IReadOnlyList<string> TailLog(int n)
    {
        try
        {
            if (!File.Exists(ServeLogPath)) return Array.Empty<string>();
            //the engine holds the file open for writing while it runs, and a plain read asks for a share it refuses, so the read allows a writer
            using var stream = new FileStream(ServeLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, new UTF8Encoding(false));
            var lines = new List<string>();
            while (reader.ReadLine() is { } line) lines.Add(line);
            return lines.Count <= n ? lines : lines[^n..];
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    //real process seam (only the open-before-spawn ordering gets a unit test, the rest spawns for real)

    //the pump sets this on a thread-pool thread, both ends go through Volatile. nullable enums can't be volatile, so the fault is an int
    private const int NoLogFault = -1;
    private int _logPumpFault = NoLogFault;

    //drains the pump's one-shot fault, if it fired. called at every start-path exit, so a server that came up fine but could not write its log still says so.
    private void ReportLogPumpFault(IServeListener listener)
    {
        var fault = System.Threading.Volatile.Read(ref _logPumpFault);
        if (fault != NoLogFault) listener.PumpFault((ServeLogFault)fault);
    }

    //create serve.log first, then spawn. never open it after spawning (a failed open would leave an untracked server on the port)
    internal IServeProcess DefaultSpawn(ProcessStartInfo psi)
    {
        //foreground pipes aren't redirected, nothing to drain and no serve.log, just spawn
        if (!psi.RedirectStandardOutput)
        {
            var fg = Process.Start(psi) ?? throw new InvalidOperationException("failed to start llama-server");
            fg.EnableRaisingEvents = true;
            return new RealServeProcess(fg);
        }

        //the server writes serve.log itself, since a detached server outlives the gatto that started it and a pump here would die with that gatto
        try
        {
            new FileStream(ServeLogPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite).Dispose();
            psi.ArgumentList.Add("--log-file");
            psi.ArgumentList.Add(ServeLogPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Threading.Volatile.Write(ref _logPumpFault, (int)ServeLogFault.NotOpened);   //losing serve.log must never cost the server
        }

        var proc = Process.Start(psi) ?? throw new InvalidOperationException("failed to start llama-server");
        //drained while this gatto lives, a full unread pipe blocks the child, and the lines are already in serve.log
        proc.OutputDataReceived += (_, _) => { };
        proc.ErrorDataReceived += (_, _) => { };
        proc.EnableRaisingEvents = true;
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        return new RealServeProcess(proc);
    }

    private static IServeProcess? DefaultLookup(int pid)
    {
        try { return new RealServeProcess(Process.GetProcessById(pid)); }
        catch (ArgumentException) { return null; }   //thrown when no process has that pid
        catch (InvalidOperationException) { return null; }
    }

    private sealed class RealServeProcess(Process proc) : IServeProcess
    {
        public int Pid => proc.Id;
        //a dead or locked process throws when read, report HasExited and the guard keeps its hands off
        public bool HasExited { get { try { return proc.HasExited; } catch { return true; } } }
        public string ProcessName { get { try { return proc.ProcessName; } catch { return ""; } } }
        public void Kill() => proc.Kill(entireProcessTree: true);
        public int? ExitCode { get { try { return proc.HasExited ? proc.ExitCode : null; } catch { return null; } } }
    }
}

//what doctor's running-server line reads. the pid is there so a declined stop can name the process for Task Manager
public sealed record RunningInfo(string Model, int Port, string Started, int Pid = 0);

//what is serving relative to what I want, always a full case. only read it through Match, a bool test drops the model name a deed trips on
public abstract record ServingState
{
    private ServingState() { }

    //the only way to read this, all three continuations required
    public abstract T Match<T>(
        Func<T> idle, Func<RunningInfo, T> servingThis, Func<RunningInfo, T> servingOther);

    //nothing of ours is serving (a stale or recycled serve.json reads as this too)
    public sealed record Idle : ServingState
    {
        public override T Match<T>(
            Func<T> idle, Func<RunningInfo, T> servingThis, Func<RunningInfo, T> servingOther) => idle();
    }

    //our server is up and holds the model the caller asked about
    public sealed record ServingThis(RunningInfo Running) : ServingState
    {
        public override T Match<T>(
            Func<T> idle, Func<RunningInfo, T> servingThis, Func<RunningInfo, T> servingOther) =>
            servingThis(Running);
    }

    //our server is up with a different model. the record travels so the caller can name what's actually loaded
    public sealed record ServingOther(RunningInfo Running) : ServingState
    {
        public override T Match<T>(
            Func<T> idle, Func<RunningInfo, T> servingThis, Func<RunningInfo, T> servingOther) =>
            servingOther(Running);
    }
}

//what AwaitReady found. don't collapse StillLoading into either neighbor: called ready it fakes an unreachable, called dead it blames a working server
public enum ServerReadiness
{
    //the /health probe answered
    Ready,
    //the budget ran out with the process alive. nothing is wrong, it just isn't ready yet
    StillLoading,
    //the recorded pid is gone, it exited during load
    Died,
}
