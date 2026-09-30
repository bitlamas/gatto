using System.Diagnostics;
using System.Text.Json;
using Gatto.Core.Home;
using Gatto.Roles;
using Gatto.Roles.Audition;

namespace Gatto.Tests.Audition;

//drive the server start of AuditionRunner.RunAsync through a fake process and a closed port (RunBatteryAsync tests begin after the server is ready)
public class AuditionServerReadinessTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-readiness-").FullName;
    //the temp folder name must not match gatto-audition-*. another test counts those folders to prove cleanup, and a match fails it in a parallel run
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(2) };

    public void Dispose()
    {
        _http.Dispose();
        try { Directory.Delete(_home, recursive: true); } catch { }
    }

    private sealed class FakeProc : ServeManager.IServeProcess
    {
        public int Pid { get; init; } = 30660;
        public bool HasExited { get; set; }
        public string ProcessName { get; init; } = "llama-server";
        public int KillCount { get; private set; }
        public Exception? KillThrows { get; init; }
        public void Kill()
        {
            KillCount++;
            if (KillThrows is not null) throw KillThrows;
        }
    }

    private sealed class Recorder : IProgress<AuditionProgress>
    {
        public List<AuditionProgress> Events { get; } = [];
        public void Report(AuditionProgress value) => Events.Add(value);
        public IEnumerable<string> Notes =>
            Events.Where(e => e.Stage == AuditionStage.Note).Select(e => e.Text);
    }

    //write the model file and the llama-server.exe path to disk, the pre-flight refuses a model whose files are missing
    private string WriteHome(int port)
    {
        File.WriteAllText(Path.Combine(_home, "llama-server.exe"), "");
        var gguf = Path.Combine(_home, "Some-Model-Q4_K_M.gguf");
        File.WriteAllText(gguf, "");

        var modelDir = Path.Combine(_home, "models", "slow-model");
        Directory.CreateDirectory(modelDir);
        File.WriteAllText(Path.Combine(modelDir, "profile.json"),
            $$"""{ "files": [{ "path": {{JsonSerializer.Serialize(gguf)}}, "active": true }], "port": {{port}}, "context": 4096 }""");
        File.WriteAllText(Path.Combine(_home, "gatto.json"),
            $$"""
            {
              "endpoints": { "local": { "base_url": "http://127.0.0.1:{{port}}" } },
              "default_endpoint": "local",
              "llama_server": {{JsonSerializer.Serialize(Path.Combine(_home, "llama-server.exe"))}}
            }
            """);
        return "slow-model";
    }

    //the child never answers the health check, and port 1 is closed on every platform, so the poll gets connection refused
    private ServeManager LoadingForever(FakeProc proc, out FakeProc spawned)
    {
        spawned = proc;
        var p = proc;
        return new ServeManager(_home, Path.Combine(_home, "llama-server.exe"),
            spawn: _ => p,
            lookup: pid => pid == p.Pid && !p.HasExited ? p : null,
            http: _http,
            pollInterval: TimeSpan.FromMilliseconds(10),
            pollTimeout: TimeSpan.FromMilliseconds(30));
    }

    private const int ClosedPort = 1;

    [Fact]
    public async Task A_PACK_WHOSE_PROJECTOR_IS_GONE_IS_REFUSED_BEFORE_ANYTHING_STARTS()
    {
        //the projector check belongs to the pre-flight, alongside the model files, before any server starts
        var gguf = Path.Combine(_home, "Seeing-Model-Q4_K_M.gguf");
        File.WriteAllText(gguf, "");
        var projector = Path.Combine(_home, "mmproj-Seeing-Model-BF16.gguf");   //don't create this file, the refusal needs the projector to be missing
        File.WriteAllText(Path.Combine(_home, "llama-server.exe"), "");

        var modelDir = Path.Combine(_home, "models", "seeing");
        Directory.CreateDirectory(modelDir);
        File.WriteAllText(Path.Combine(modelDir, "profile.json"),
            $$"""
            {
              "files": [{ "path": {{JsonSerializer.Serialize(gguf)}}, "active": true }],
              "mmproj": {{JsonSerializer.Serialize(projector)}},
              "port": {{ClosedPort}}, "context": 4096
            }
            """);
        File.WriteAllText(Path.Combine(_home, "gatto.json"),
            $$"""
            {
              "endpoints": { "local": { "base_url": "http://127.0.0.1:{{ClosedPort}}" } },
              "default_endpoint": "local",
              "llama_server": {{JsonSerializer.Serialize(Path.Combine(_home, "llama-server.exe"))}}
            }
            """);

        var manager = LoadingForever(new FakeProc(), out var proc);

        var ex = await Assert.ThrowsAsync<GattoConfigException>(() =>
            AuditionRunner.RunAsync(_home, "seeing", null, CancellationToken.None,
                manager, readyBudget: TimeSpan.FromMilliseconds(120), serveVoice: Voice));

        Assert.Contains(projector, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, proc.KillCount);        //no kill means the refusal came before any start
    }

    [Fact]
    public async Task A_server_that_is_STILL_LOADING_is_never_reported_as_unreachable()
    {
        //a server that is still loading gets a message that says so, and never names the network or base_url
        var modelId = WriteHome(ClosedPort);
        var progress = new Recorder();
        var manager = LoadingForever(new FakeProc(), out _);

        var ex = await Assert.ThrowsAsync<GattoConfigException>(() =>
            AuditionRunner.RunAsync(_home, modelId, progress, CancellationToken.None,
                manager, readyBudget: TimeSpan.FromMilliseconds(120), serveVoice: Voice));

        Assert.Contains("still loading", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(modelId, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("unreachable", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("base_url", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("network", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Nothing_is_MEASURED_when_the_server_never_became_ready()
    {
        //a harness failure must produce no verdict, and an empty feed proves the battery never announced a task
        var modelId = WriteHome(ClosedPort);
        var progress = new Recorder();
        var manager = LoadingForever(new FakeProc(), out _);

        await Assert.ThrowsAsync<GattoConfigException>(() =>
            AuditionRunner.RunAsync(_home, modelId, progress, CancellationToken.None,
                manager, readyBudget: TimeSpan.FromMilliseconds(120), serveVoice: Voice));

        Assert.DoesNotContain(progress.Events, e => e.Stage == AuditionStage.Started);
        Assert.DoesNotContain(progress.Events, e => e.Stage == AuditionStage.Done);
    }

    //a child that exits inside the poll of the start never reaches the readiness wait. a death during the start and a death after it need separate tests.
    [Fact]
    public async Task A_server_that_dies_INSIDE_the_start_carries_llama_servers_own_last_words()
    {
        //a server that dies during the start shows the log lines of llama-server in the error. without them the user needs a second command to learn the cause
        var modelId = WriteHome(ClosedPort);
        var proc = new FakeProc { HasExited = true };            //an exited child fails the start inside its own poll
        var manager = LoadingForever(proc, out _);
        File.WriteAllText(Path.Combine(_home, "serve.log"),
            "load_model: loading model 'Some-Model-Q4_K_M.gguf'" + Environment.NewLine
            + "ggml_vulkan: device memory allocation of 21474836480 failed");

        //a start that fails throws a plain config exception, and no screen tells this case apart from a readiness stumble
        var ex = await Assert.ThrowsAsync<GattoConfigException>(() =>
            AuditionRunner.RunAsync(_home, modelId, null, CancellationToken.None,
                manager, readyBudget: TimeSpan.FromMilliseconds(120), serveVoice: Voice));

        Assert.Contains("device memory allocation", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("serve start", ex.Message, StringComparison.Ordinal);   //the message must not send the user to gatto serve start
    }

    [Fact]
    public async Task A_server_that_dies_AFTER_the_start_returned_is_told_apart_from_one_that_is_slow()
    {
        //only the loss of the recorded pid shows that the server died. a failed health check also describes a server that is still loading.
        var modelId = WriteHome(ClosedPort);
        var proc = new FakeProc();
        File.WriteAllText(Path.Combine(_home, "serve.log"),
            "load_model: loading model 'Some-Model-Q4_K_M.gguf'" + Environment.NewLine
            + "ggml_vulkan: device memory allocation of 21474836480 failed");

        //the child is alive during the start, and its pid is gone by the time the readiness wait polls
        var manager = new ServeManager(_home, Path.Combine(_home, "llama-server.exe"),
            spawn: _ => proc,
            lookup: _ => null,
            http: _http,
            pollInterval: TimeSpan.FromMilliseconds(10),
            pollTimeout: TimeSpan.FromMilliseconds(30));

        //the exception names the kind of stumble, each kind gets its own screen. a screen must not parse the kind out of the message
        var ex = await Assert.ThrowsAsync<AuditionRunner.AuditionStumbleException>(() =>
            AuditionRunner.RunAsync(_home, modelId, null, CancellationToken.None,
                manager, readyBudget: TimeSpan.FromMilliseconds(120), serveVoice: Voice));

        Assert.Equal(AuditionStumbleKind.ExitedWhileLoading, ex.Kind);
        Assert.Contains("exited while loading", ex.Message, StringComparison.OrdinalIgnoreCase);
        //the exception must include the log lines this run read, since the rolling log file can change before the screen draws them
        Assert.NotEmpty(ex.LastLines);
        Assert.DoesNotContain("still loading", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("device memory allocation", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_long_wait_SAYS_SO_rather_than_going_quiet()
    {
        //a long start must report progress, since minutes of silence look like a hang and people stop a hang
        var modelId = WriteHome(ClosedPort);
        var progress = new Recorder();
        var manager = LoadingForever(new FakeProc(), out _);

        await Assert.ThrowsAsync<GattoConfigException>(() =>
            AuditionRunner.RunAsync(_home, modelId, progress, CancellationToken.None,
                manager, readyBudget: TimeSpan.FromMilliseconds(120), serveVoice: Voice));

        Assert.Contains(progress.Notes, n => n.Contains("starting", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_readiness_budget_is_MINUTES_not_seconds()
    {
        //the budget must outlast the load of a model that barely fits, a shorter one reports a failure that does not exist
        Assert.True(AuditionRunner.ReadyBudget >= TimeSpan.FromMinutes(5),
            "a readiness budget in seconds reports a failed load on a machine where the model barely fits and is only slow");
    }

    [Fact]
    public async Task A_SERVER_THAT_DIES_MID_CHECK_IS_NAMED_not_reported_as_unreachable()
    {
        //a server whose recorded pid is gone is reported as stopped. the pid check keeps a live server's ordinary connection error out of that report.
        var modelId = WriteHome(ClosedPort);
        var proc = new FakeProc();
        File.WriteAllText(Path.Combine(_home, "serve.log"),
            "slot launch_slot_: id  0 | task 59 | processing task");

        //the fake server answers the health check so the battery starts, and no process has the recorded pid. readiness checks health first, so both hold without a race
        var manager = new ServeManager(_home, Path.Combine(_home, "llama-server.exe"),
            spawn: _ => proc,
            lookup: _ => null,                      //no process has the recorded pid when the manager looks it up.
            http: _http,
            pollInterval: TimeSpan.FromMilliseconds(10),
            pollTimeout: TimeSpan.FromMilliseconds(30));

        await using var server = new Gatto.Tests.Fakes.FakeOpenAiServer();
        RewritePort(modelId, new Uri(server.BaseUrl).Port);
        //queue two health answers, the start polls once before the readiness wait. the first chat request of the battery then finds an empty queue and fails
        server.Enqueue(new Gatto.Tests.Fakes.FakeResponse(Status: 200, Body: "{}"));   //the health answer for the poll of the start.
        server.Enqueue(new Gatto.Tests.Fakes.FakeResponse(Status: 200, Body: "{}"));   //the health answer for the readiness wait.

        var ex = await Assert.ThrowsAsync<AuditionRunner.AuditionStumbleException>(() =>
            AuditionRunner.RunAsync(_home, modelId, null, CancellationToken.None,
                manager, readyBudget: TimeSpan.FromMilliseconds(120), serveVoice: Voice));

        //assert a different kind here than the exit-during-load test, a constant kind passes each assertion alone
        Assert.Equal(AuditionStumbleKind.StoppedMidCheck, ex.Kind);
        Assert.Contains("stopped while the check was running", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("task 59", ex.Message, StringComparison.Ordinal);   //the message must include the last log lines of the server.
    }

    //the port of the fake server is known only after it starts, so write the profile again with that port.
    private void RewritePort(string modelId, int port)
    {
        var profile = Path.Combine(_home, "models", modelId, "profile.json");
        var gguf = Path.Combine(_home, "Some-Model-Q4_K_M.gguf");
        File.WriteAllText(profile,
            $$"""{ "files": [{ "path": {{JsonSerializer.Serialize(gguf)}}, "active": true }], "port": {{port}}, "context": 4096 }""");
    }

    //a test that reads the words of a serve failure hands the runner one built by the layer that paints them. the default reports the facts and says nothing
    private static Gatto.Roles.IServeListener Voice(TextWriter w) =>
        new Gatto.Cli.ServeLines(
            new Gatto.Cli.CliSurface(w, null, Gatto.Terminal.GlyphSet.Unicode),
            Gatto.Terminal.GlyphSet.Unicode);

    [Fact]
    public async Task A_stop_that_FAILS_is_reported_instead_of_swallowed()
    {
        //a stop that fails must say the server still runs and name the command that stops it. a silent failure leaves a server running that the user never started
        var modelId = WriteHome(ClosedPort);
        var progress = new Recorder();
        var proc = new FakeProc { KillThrows = new InvalidOperationException("Access is denied") };
        var manager = LoadingForever(proc, out _);

        await Assert.ThrowsAsync<GattoConfigException>(() =>
            AuditionRunner.RunAsync(_home, modelId, progress, CancellationToken.None,
                manager, readyBudget: TimeSpan.FromMilliseconds(120), serveVoice: Voice));

        var told = string.Join(" | ", progress.Notes);
        Assert.Contains("couldn't stop", told, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("gatto serve stop", told, StringComparison.Ordinal);
        Assert.Contains("30660", told, StringComparison.Ordinal);   //the pid comes from the stop line of ServeManager, which the note must include
    }

    [Fact]
    public async Task A_stop_that_WORKS_says_nothing_at_all()
    {
        //a stop that works must add no note, since a note on every successful run teaches the user to skip the notes
        var modelId = WriteHome(ClosedPort);
        var progress = new Recorder();
        var manager = LoadingForever(new FakeProc(), out var spawned);

        await Assert.ThrowsAsync<GattoConfigException>(() =>
            AuditionRunner.RunAsync(_home, modelId, progress, CancellationToken.None,
                manager, readyBudget: TimeSpan.FromMilliseconds(120), serveVoice: Voice));

        Assert.DoesNotContain(progress.Notes, n => n.Contains("couldn't stop", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, KillCountOf(spawned));
        Assert.False(File.Exists(Path.Combine(_home, "serve.json")));   //a successful stop also removes the serve.json record
    }

    [Fact]
    public async Task The_server_this_run_started_is_STOPPED_even_when_readiness_gives_up()
    {
        //the readiness failure throws after a spawn, so it must sit inside the try and finally. otherwise the run leaves the server it started behind
        var modelId = WriteHome(ClosedPort);
        var manager = LoadingForever(new FakeProc(), out var spawned);

        await Assert.ThrowsAsync<GattoConfigException>(() =>
            AuditionRunner.RunAsync(_home, modelId, null, CancellationToken.None,
                manager, readyBudget: TimeSpan.FromMilliseconds(120), serveVoice: Voice));

        Assert.Equal(1, KillCountOf(spawned));
    }

    [Fact]
    public async Task A_server_we_did_NOT_start_is_never_stopped_and_never_killed()
    {
        //a failed run must not stop a server that another session started. the record names the same model, so the run reuses that server and starts nothing
        var modelId = WriteHome(ClosedPort);
        var proc = new FakeProc();
        File.WriteAllText(Path.Combine(_home, "serve.json"),
            $$"""{"pid":{{proc.Pid}},"model":"{{modelId}}","port":{{ClosedPort}},"started":"2026-08-11T00:00:00Z"}""");

        var manager = new ServeManager(_home, Path.Combine(_home, "llama-server.exe"),
            spawn: _ => throw new Xunit.Sdk.XunitException("must not spawn: a server for this model is already running"),
            lookup: pid => pid == proc.Pid ? proc : null,
            http: _http,
            pollInterval: TimeSpan.FromMilliseconds(10),
            pollTimeout: TimeSpan.FromMilliseconds(30));

        var ex = await Assert.ThrowsAsync<GattoConfigException>(() =>
            AuditionRunner.RunAsync(_home, modelId, null, CancellationToken.None,
                manager, readyBudget: TimeSpan.FromMilliseconds(120), serveVoice: Voice));

        Assert.Equal(0, KillCountOf(proc));
        Assert.True(File.Exists(Path.Combine(_home, "serve.json")));
        //the message for a reused server names gatto serve status, since that server still runs after the check ends
        Assert.Contains("gatto serve status", ex.Message, StringComparison.Ordinal);
    }

    private static int KillCountOf(ServeManager.IServeProcess p) => ((FakeProc)p).KillCount;

    //a server that holds another model is refused before any start is asked. assert RefusedStarts here, a zero spawn count also passes when a refused start was asked
    [Fact]
    public async Task A_server_holding_another_model_is_answered_without_asking_for_a_start()
    {
        var modelId = WriteHome(ClosedPort);                    //the wanted id is slow-model
        const string holder = "a-completely-different-model";   //the holder id must differ from the wanted id, or the test cannot tell which id the message names.
        File.WriteAllText(Path.Combine(_home, "serve.json"),
            "{\"pid\":30660,\"model\":\"" + holder + "\",\"port\":9,\"started\":\"2026-08-22T00:00:00Z\"}");

        var proc = new FakeProc();                              //the default pid 30660 matches the record, and the process name is llama-server
        var manager = new ServeManager(_home, Path.Combine(_home, "llama-server.exe"),
            spawn: _ => throw new Xunit.Sdk.XunitException("nothing may be spawned here"),
            lookup: pid => pid == proc.Pid ? proc : null,
            http: _http,
            pollInterval: TimeSpan.FromMilliseconds(10), pollTimeout: TimeSpan.FromMilliseconds(30));

        var ex = await Assert.ThrowsAsync<GattoConfigException>(() =>
            AuditionRunner.RunAsync(_home, modelId, null, CancellationToken.None,
                manager, readyBudget: TimeSpan.FromMilliseconds(120), serveVoice: Voice));

        //the message must name the model that actually holds the server.
        Assert.Contains(holder, ex.Message, StringComparison.Ordinal);
        //the message must not use the refusal text of StartAsync, since no start was asked
        Assert.DoesNotContain("gatto serve stop first", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, manager.RefusedStarts);
    }

    [Fact]
    public async Task A_server_that_answers_is_READY_immediately_even_under_a_zero_budget()
    {
        //a reused server is ready on the first check, with no wait for a poll interval
        await using var server = new Gatto.Tests.Fakes.FakeOpenAiServer();
        var port = new Uri(server.BaseUrl).Port;
        server.Enqueue(new Gatto.Tests.Fakes.FakeResponse(Status: 200, Body: "{}"));

        var manager = new ServeManager(_home, Path.Combine(_home, "llama-server.exe"),
            spawn: null, lookup: null, http: _http,
            pollInterval: TimeSpan.FromMilliseconds(10), pollTimeout: TimeSpan.FromSeconds(1));

        var readiness = await manager.AwaitReadyAsync(port, TimeSpan.Zero, null, CancellationToken.None);

        Assert.Equal(ServerReadiness.Ready, readiness);
    }

    [Fact]
    public async Task Cancelling_the_wait_propagates_rather_than_reading_as_still_loading()
    {
        //a cancel during a long load is the user's choice and must not be reported as a server that is still loading
        var manager = new ServeManager(_home, Path.Combine(_home, "llama-server.exe"),
            spawn: null, lookup: null, http: _http,
            pollInterval: TimeSpan.FromMilliseconds(10), pollTimeout: TimeSpan.FromSeconds(1));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            manager.AwaitReadyAsync(ClosedPort, TimeSpan.FromSeconds(30), null, cts.Token));
    }
}
