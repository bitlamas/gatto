using System.Diagnostics;
using System.Net;
using Gatto.Cli;
using Gatto.Roles;

namespace Gatto.Tests.Copy;

//the corpus is only as wide as the lines pinned here. the funnel cases reach two of ServeManager's twenty human lines, and the rest are driven here
public class ServeCorpusTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-servecorpus-").FullName;

    //the health answer is scripted rather than asked. a real request reaches whatever listens on that port, so a golden can record the opposite of its test's state
    private sealed class ScriptedHealth : HttpMessageHandler
    {
        public required Func<HttpRequestMessage, HttpResponseMessage> Respond { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) => Task.FromResult(Respond(request));
    }

    private static HttpClient Healthy() => new(new ScriptedHealth
    {
        Respond = _ => new HttpResponseMessage(HttpStatusCode.OK),
    });

    private static HttpClient Unreachable() => new(new ScriptedHealth
    {
        Respond = _ => throw new HttpRequestException("connection refused"),
    });

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch (Exception) { } //best effort, since a leftover temp folder must not fail the run
    }

    //the process the manager looks up, since a real one cannot be fixed to a pid a golden can hold
    private sealed class FakeProc : ServeManager.IServeProcess
    {
        public int Pid { get; init; } = CorpusFacts.Pid;
        public bool HasExited { get; set; }
        public string ProcessName { get; init; } = "llama-server";
        public Exception? KillThrows { get; init; }
        public void Kill() { if (KillThrows is not null) throw KillThrows; }
    }

    private string ServeJson => Path.Combine(_home, "serve.json");

    private string FakeLlama()
    {
        var p = Path.Combine(_home, "llama-server.exe");
        File.WriteAllText(p, "");
        return p;
    }

    //the started stamp is fixed, since serve status reports an age and a real one would move the golden every second
    private void WriteServeJson(int pid, string model, int port) =>
        File.WriteAllText(ServeJson,
            $$"""{"pid":{{pid}},"model":"{{model}}","port":{{port}},"started":"2026-09-15T12:00:00Z"}""");

    private ServeManager Manager(Func<int, ServeManager.IServeProcess?> lookup,
        Func<ProcessStartInfo, ServeManager.IServeProcess>? spawn = null,
        HttpClient? http = null) =>
        new(_home, FakeLlama(),
            spawn: spawn ?? (_ => throw new Xunit.Sdk.XunitException("spawn must not be called")),
            lookup: lookup,
            http: http ?? Unreachable(),
            pollInterval: TimeSpan.FromMilliseconds(10),
            pollTimeout: TimeSpan.FromMilliseconds(120));

    //the manager reports to the listener RunServeAsync hands it, so a test that renders elsewhere would pin a line no user sees
    private static (StringWriter Sink, IServeListener Listener) Surfaced(CorpusMode mode)
    {
        var sink = new StringWriter();
        var cli = new CliSurface(sink, CorpusDriver.ThemeFor(mode), CorpusDriver.Glyphs);
        return (sink, new ServeLines(cli, CorpusDriver.Glyphs));
    }

    private void Check(string name, CorpusMode mode, Action<IServeListener> drive)
    {
        var (sink, listener) = Surfaced(mode);
        drive(listener);
        CopyGolden.Check($"{name}-{mode}", CorpusDriver.Visible(sink.ToString()));
    }

    //the stop path

    [Theory]
    [InlineData(CorpusMode.Dark)]
    [InlineData(CorpusMode.Plain)]
    public void STOP_A_LIVE_SERVER(CorpusMode mode)
    {
        WriteServeJson(CorpusFacts.Pid, CorpusFacts.ModelId, CorpusFacts.Port);
        var proc = new FakeProc();
        Check("serve-stop-live", mode, w =>
            Manager(pid => pid == proc.Pid ? proc : null).StopAsync(w, CancellationToken.None)
                .GetAwaiter().GetResult());
    }

    [Theory]
    [InlineData(CorpusMode.Dark)]
    [InlineData(CorpusMode.Plain)]
    public void STOP_WHEN_THE_KILL_FAILS(CorpusMode mode)
    {
        WriteServeJson(CorpusFacts.Pid, CorpusFacts.ModelId, CorpusFacts.Port);
        var proc = new FakeProc { KillThrows = new UnauthorizedAccessException("access is denied") };
        Check("serve-stop-kill-failed", mode, w =>
            Manager(pid => pid == proc.Pid ? proc : null).StopAsync(w, CancellationToken.None)
                .GetAwaiter().GetResult());
    }

    [Theory]
    [InlineData(CorpusMode.Dark)]
    [InlineData(CorpusMode.Plain)]
    public void STOP_WHEN_THE_PID_IS_SOMETHING_ELSE_NOW(CorpusMode mode)
    {
        WriteServeJson(CorpusFacts.Pid, CorpusFacts.ModelId, CorpusFacts.Port);
        var other = new FakeProc { ProcessName = "notepad" };
        Check("serve-stop-not-ours", mode, w =>
            Manager(pid => pid == other.Pid ? other : null).StopAsync(w, CancellationToken.None)
                .GetAwaiter().GetResult());
    }

    [Theory]
    [InlineData(CorpusMode.Dark)]
    [InlineData(CorpusMode.Plain)]
    public void STOP_WHEN_THE_PID_IS_GONE(CorpusMode mode)
    {
        WriteServeJson(CorpusFacts.Pid, CorpusFacts.ModelId, CorpusFacts.Port);
        Check("serve-stop-stale", mode, w =>
            Manager(_ => null).StopAsync(w, CancellationToken.None).GetAwaiter().GetResult());
    }

    //the status path

    [Theory]
    [InlineData(CorpusMode.Dark)]
    [InlineData(CorpusMode.Plain)]
    public void STATUS_WHEN_THE_RECORD_IS_STALE(CorpusMode mode)
    {
        WriteServeJson(CorpusFacts.Pid, CorpusFacts.ModelId, CorpusFacts.Port);
        Check("serve-status-stale", mode, w =>
            Manager(_ => null).StatusAsync(w, CancellationToken.None)
                .GetAwaiter().GetResult());
    }

    //a foreground start saw its server end, so status names the code. the record holds no time, since a golden cannot hold a time zone
    [Theory]
    [InlineData(CorpusMode.Dark)]
    [InlineData(CorpusMode.Plain)]
    public void STATUS_WHEN_A_FOREGROUND_SERVER_DIED(CorpusMode mode)
    {
        File.WriteAllText(ServeJson,
            $$"""{"pid":{{CorpusFacts.Pid}},"model":"{{CorpusFacts.ModelId}}","port":{{CorpusFacts.Port}},"started":"2026-09-15T12:00:00Z","log":false,"exited":-1073741819}""");
        Check("serve-status-died-foreground", mode, w =>
            Manager(_ => null).StatusAsync(w, CancellationToken.None)
                .GetAwaiter().GetResult());
    }

    //a detached server died with nothing watching, so the code is unknown and its own log's last lines are shown
    [Theory]
    [InlineData(CorpusMode.Dark)]
    [InlineData(CorpusMode.Plain)]
    public void STATUS_WHEN_A_DETACHED_SERVER_DIED(CorpusMode mode)
    {
        File.WriteAllText(ServeJson,
            $$"""{"pid":{{CorpusFacts.Pid}},"model":"{{CorpusFacts.ModelId}}","port":{{CorpusFacts.Port}},"started":"2026-09-15T12:00:00Z","log":true}""");
        File.WriteAllText(Path.Combine(_home, "serve.log"), "srv  update_slots: all slots are idle\nggml_vulkan: Device lost\n");
        Check("serve-status-died-detached", mode, w =>
            Manager(_ => null).StatusAsync(w, CancellationToken.None)
                .GetAwaiter().GetResult());
    }

    [Theory]
    [InlineData(CorpusMode.Dark)]
    [InlineData(CorpusMode.Plain)]
    public void STATUS_WHEN_THE_SERVER_DOES_NOT_ANSWER(CorpusMode mode)
    {
        WriteServeJson(CorpusFacts.Pid, CorpusFacts.ModelId, CorpusFacts.Port);
        var proc = new FakeProc();
        Check("serve-status-unhealthy", mode, w =>
            Manager(pid => pid == proc.Pid ? proc : null, http: Unreachable())
                .StatusAsync(w, CancellationToken.None).GetAwaiter().GetResult());
    }

    [Theory]
    [InlineData(CorpusMode.Dark)]
    [InlineData(CorpusMode.Plain)]
    public void STATUS_WHEN_THE_SERVER_ANSWERS(CorpusMode mode)
    {
        WriteServeJson(CorpusFacts.Pid, CorpusFacts.ModelId, CorpusFacts.Port);
        var proc = new FakeProc();
        Check("serve-status-healthy", mode, w =>
            Manager(pid => pid == proc.Pid ? proc : null, http: Healthy())
                .StatusAsync(w, CancellationToken.None).GetAwaiter().GetResult());
    }

    //the start path

    private static Model ModelFor(string llamaServer) => new(
        CorpusFacts.ModelId,
        new ModelProfile([new ModelFile("m.gguf", null, true)], CorpusFacts.Port, 8192,
            null, null, null, null, null, Array.Empty<string>(), null, llamaServer),
        null, null, null);

    [Theory]
    [InlineData(CorpusMode.Dark)]
    [InlineData(CorpusMode.Plain)]
    public void START_WHEN_ONE_IS_ALREADY_SERVING(CorpusMode mode)
    {
        WriteServeJson(CorpusFacts.Pid, CorpusFacts.ModelId, CorpusFacts.Port);
        var proc = new FakeProc();
        var bin = FakeLlama();
        Check("serve-start-already", mode, w =>
            Manager(pid => pid == proc.Pid ? proc : null)
                .StartAsync(ModelFor(bin), w, CancellationToken.None).GetAwaiter().GetResult());
    }

    [Theory]
    [InlineData(CorpusMode.Dark)]
    [InlineData(CorpusMode.Plain)]
    public void START_DETACHED_THAT_NEVER_BECOMES_READY(CorpusMode mode)
    {
        var proc = new FakeProc { Pid = 7777 };
        var bin = FakeLlama();
        Check("serve-start-still-loading", mode, w =>
            Manager(pid => pid == proc.Pid ? proc : null, spawn: _ => proc, http: Unreachable())
                .StartAsync(ModelFor(bin), w, CancellationToken.None).GetAwaiter().GetResult());
    }

    [Theory]
    [InlineData(CorpusMode.Dark)]
    [InlineData(CorpusMode.Plain)]
    public void START_DETACHED_THAT_BECOMES_READY(CorpusMode mode)
    {
        var proc = new FakeProc { Pid = 7777 };
        var bin = FakeLlama();
        Check("serve-start-ready", mode, w =>
            Manager(pid => pid == proc.Pid ? proc : null, spawn: _ => proc, http: Healthy())
                .StartAsync(ModelFor(bin), w, CancellationToken.None).GetAwaiter().GetResult());
    }

    [Theory]
    [InlineData(CorpusMode.Dark)]
    [InlineData(CorpusMode.Plain)]
    public void START_WHEN_THE_SERVER_EXITS_BEFORE_IT_IS_READY(CorpusMode mode)
    {
        var proc = new FakeProc { Pid = 7777, HasExited = true };
        var bin = FakeLlama();
        Check("serve-start-died", mode, w =>
            Manager(pid => pid == proc.Pid ? proc : null, spawn: _ => proc, http: Unreachable())
                .StartAsync(ModelFor(bin), w, CancellationToken.None).GetAwaiter().GetResult());
    }

    //a serve.log exists, so the failure prints its last lines rather than the no-log note
    [Theory]
    [InlineData(CorpusMode.Dark)]
    [InlineData(CorpusMode.Plain)]
    public void START_THAT_DIES_WITH_A_LOG_TAIL(CorpusMode mode)
    {
        File.WriteAllText(Path.Combine(_home, "serve.log"),
            string.Join("\n", Enumerable.Range(1, 12).Select(i => $"line {i} of the engine's log")));
        var proc = new FakeProc { Pid = 7777, HasExited = true };
        var bin = FakeLlama();
        Check("serve-start-died-tail", mode, w =>
            Manager(pid => pid == proc.Pid ? proc : null, spawn: _ => proc, http: Unreachable())
                .StartAsync(ModelFor(bin), w, CancellationToken.None).GetAwaiter().GetResult());
    }

    //the foreground start, whose three lines only this path prints, since ctrl+c arrives while gatto holds the terminal
    [Theory]
    [InlineData(CorpusMode.Dark)]
    [InlineData(CorpusMode.Plain)]
    public void START_IN_THE_FOREGROUND_UNTIL_CTRL_C(CorpusMode mode)
    {
        var proc = new FakeProc { Pid = 7777 };
        var bin = FakeLlama();
        using var cts = new CancellationTokenSource();
        cts.Cancel();   //stands in for ctrl+c arriving while the child holds the terminal
        Check("serve-start-cancelled", mode, listener =>
            Manager(pid => pid == proc.Pid ? proc : null, spawn: _ => proc, http: Unreachable())
                .StartAsync(ModelFor(bin), listener, cts.Token, foreground: true)
                .GetAwaiter().GetResult());
    }

    //the child goes down on its own while gatto is attached, a different event from a stop the user asked for
    [Theory]
    [InlineData(CorpusMode.Dark)]
    [InlineData(CorpusMode.Plain)]
    public void FOREGROUND_WHEN_THE_SERVER_GOES_DOWN_ON_ITS_OWN(CorpusMode mode)
    {
        var proc = new FakeProc { Pid = 7777, HasExited = true };
        var bin = FakeLlama();
        Check("serve-start-foreground-exited", mode, listener =>
            Manager(pid => pid == proc.Pid ? proc : null, spawn: _ => proc, http: Unreachable())
                .StartAsync(ModelFor(bin), listener, CancellationToken.None, foreground: true)
                .GetAwaiter().GetResult());
    }

    //the detached start through a pipe, where each rung commits once. no golden covers the painted face, since its frames depend on how long the poll takes
    [Theory]
    [InlineData(CorpusMode.Plain)]
    public async Task START_DETACHED_WITH_THE_WAIT_ON_SCREEN(CorpusMode mode)
    {
        var proc = new FakeProc { Pid = 7777 };
        var bin = FakeLlama();
        var sink = new StringWriter();
        var cli = new CliSurface(sink, CorpusDriver.ThemeFor(mode), CorpusDriver.Glyphs);
        using var tick = new TickLine(sink, CorpusDriver.ThemeFor(mode) is not null,
            windowWidth: () => 100);
        var row = LoadingLine.Over(tick, CorpusDriver.Glyphs, CorpusDriver.ThemeFor(mode));
        var lines = new ServeLines(cli, CorpusDriver.Glyphs, new StartWatch(
            elapsed => row.Draw(elapsed, CorpusFacts.ModelId, "~23.8 GB"), tick.Finish, "~23.8 GB"));

        await Manager(pid => pid == proc.Pid ? proc : null, spawn: _ => proc, http: Unreachable())
            .StartAsync(ModelFor(bin), lines, CancellationToken.None);
        tick.Finish();
        cli.Close();

        CopyGolden.Check($"serve-start-detached-watched-{mode}", CorpusDriver.Visible(sink.ToString()));
    }

    //an empty serve.log exists, so the failure reads differently from both the tail and the no-log arms
    [Theory]
    [InlineData(CorpusMode.Dark)]
    [InlineData(CorpusMode.Plain)]
    public void START_THAT_DIES_WITH_AN_EMPTY_LOG(CorpusMode mode)
    {
        File.WriteAllText(Path.Combine(_home, "serve.log"), "");
        var proc = new FakeProc { Pid = 7777, HasExited = true };
        var bin = FakeLlama();
        Check("serve-start-died-empty-log", mode, w =>
            Manager(pid => pid == proc.Pid ? proc : null, spawn: _ => proc, http: Unreachable())
                .StartAsync(ModelFor(bin), w, CancellationToken.None).GetAwaiter().GetResult());
    }
}
