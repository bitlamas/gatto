using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Gatto.Core.Home;
using Gatto.Roles;
using Gatto.Tests.Fakes;
using Gatto.Tests.Roles;
using Gatto.Repl.Term;

namespace Gatto.Tests;

public class ServeManagerTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-serve-").FullName;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(2) };

    public void Dispose()
    {
        _http.Dispose();
        try { Directory.Delete(_home, recursive: true); } catch { }
    }

    private sealed class FakeProc : ServeManager.IServeProcess
    {
        public int Pid { get; init; } = 4242;
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

    private string ServeJson => Path.Combine(_home, "serve.json");
    private string ServeLog => Path.Combine(_home, "serve.log");

    //an empty file is all File.Exists needs for the server path
    private string FakeLlama()
    {
        var p = Path.Combine(_home, "llama-server.exe");
        File.WriteAllText(p, "");
        return p;
    }

    private static Model ModelOn(int port) => new(
        "test-model",
        new ModelProfile([new ModelFile("m.gguf", null, true)], port, 8192, null, null, null, null, null, Array.Empty<string>()),
        null, null, null);

    //the same, with an optional model-scoped server binary override.
    private static Model ModelWithServer(int port, string? llamaServer) => new(
        "test-model",
        new ModelProfile([new ModelFile("m.gguf", null, true)], port, 8192, null, null, null, null, null, Array.Empty<string>(), null, llamaServer),
        null, null, null);

    private static Model KeyedModel(int port, string apiKey) => new(
        "test-model",
        new ModelProfile([new ModelFile("m.gguf", null, true)], port, 8192, null, null, null, null, null, Array.Empty<string>(),
            null, null, ApiKey: apiKey),
        null, null, null);

    private void WriteServeJson(int pid, string model, int port) =>
        File.WriteAllText(ServeJson,
            $$"""{"pid":{{pid}},"model":"{{model}}","port":{{port}},"started":"2026-07-09T00:00:00Z"}""");

    private static Func<ProcessStartInfo, ServeManager.IServeProcess> NoSpawn =>
        _ => throw new Xunit.Sdk.XunitException("spawn must not be called");

    //the served id and the wanted id must be different strings, otherwise the fixture cannot fail
    [Fact]
    public void The_serving_question_answers_in_three_shapes_and_the_ids_are_different_strings()
    {
        var llama = FakeLlama();
        var proc = new FakeProc();                       //the fake reports pid 4242 and the name llama-server.
        ServeManager Manager() => new(_home, llama, NoSpawn,
            pid => pid == proc.Pid && !proc.HasExited ? proc : null,
            _http, TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(30));

        Assert.Equal("idle", Describe(Manager().Serving("qwen-qwen3.6-35b-a3b")));

        WriteServeJson(proc.Pid, "qwen-qwen3.6-35b-a3b", 1235);
        Assert.Equal("this:qwen-qwen3.6-35b-a3b", Describe(Manager().Serving("qwen-qwen3.6-35b-a3b")));

        Assert.Equal("other:qwen-qwen3.6-35b-a3b", Describe(Manager().Serving("gemma-4-26b-a4b")));

        //model ids are compared case-sensitively, as everywhere else.
        Assert.Equal("other:qwen-qwen3.6-35b-a3b", Describe(Manager().Serving("Qwen-Qwen3.6-35B-A3B")));

        //a recorded pid that is no longer ours reads as idle, a stale record must not tell a caller a server is up
        proc.HasExited = true;
        Assert.Equal("idle", Describe(Manager().Serving("qwen-qwen3.6-35b-a3b")));
    }

    //the answer is read through Match, the state's only readable form, so the test drives the guard itself
    private static string Describe(ServingState state) => state.Match(
        idle: () => "idle",
        servingThis: r => $"this:{r.Model}",
        servingOther: r => $"other:{r.Model}");

    [Fact]
    public async Task Start_uses_model_llama_server_override_when_present()
    {
        await using var server = new FakeOpenAiServer();
        var port = new Uri(server.BaseUrl).Port;
        server.Enqueue(new FakeResponse(Status: 200, Body: "{}"));

        var forkBin = Path.Combine(_home, "poolside-llama-server.exe");
        File.WriteAllText(forkBin, "");

        ProcessStartInfo? captured = null;
        var proc = new FakeProc { Pid = 7777 };
        var mgr = new ServeManager(_home, FakeLlama(),   //the global binary exists, but the override must be the one chosen.
            spawn: psi => { captured = psi; return proc; },
            lookup: pid => pid == proc.Pid ? proc : null,
            http: _http,
            pollInterval: TimeSpan.FromMilliseconds(10),
            pollTimeout: TimeSpan.FromSeconds(3));

        await mgr.StartAsync(ModelWithServer(port, forkBin), new RecordingServeListener(), CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal(forkBin, captured!.FileName);
    }

    [Fact]
    public async Task Start_passes_the_api_key_by_file_and_never_on_the_command_line()
    {
        await using var server = new FakeOpenAiServer();
        var port = new Uri(server.BaseUrl).Port;
        server.Enqueue(new FakeResponse(Status: 200, Body: "{}"));

        ProcessStartInfo? captured = null;
        var proc = new FakeProc { Pid = 7777 };
        var mgr = new ServeManager(_home, FakeLlama(),
            spawn: psi => { captured = psi; return proc; },
            lookup: pid => pid == proc.Pid ? proc : null,
            http: _http,
            pollInterval: TimeSpan.FromMilliseconds(10),
            pollTimeout: TimeSpan.FromSeconds(3));

        await mgr.StartAsync(KeyedModel(port, "s3cr3t-value"), new RecordingServeListener(), CancellationToken.None);

        Assert.NotNull(captured);
        var argv = captured!.ArgumentList.ToList();
        Assert.DoesNotContain("s3cr3t-value", string.Join(" ", argv), StringComparison.Ordinal);
        Assert.DoesNotContain("--api-key", argv);
        Assert.Contains("--api-key-file", argv);

        var keyFile = argv[argv.IndexOf("--api-key-file") + 1];
        Assert.True(File.Exists(keyFile), "serve start must write the key file before spawning");
        Assert.Contains("s3cr3t-value", File.ReadAllText(keyFile), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Start_rewrites_the_key_file_each_time_so_it_never_goes_stale()
    {
        //the key file is derived from profile.json, a rotated key must not leave the old one on disk arming the server
        await using var server = new FakeOpenAiServer();
        var port = new Uri(server.BaseUrl).Port;
        server.Enqueue(new FakeResponse(Status: 200, Body: "{}"));
        server.Enqueue(new FakeResponse(Status: 200, Body: "{}"));

        ProcessStartInfo? captured = null;
        var proc = new FakeProc { Pid = 7777 };
        ServeManager Mgr() => new(_home, FakeLlama(),
            spawn: psi => { captured = psi; return proc; },
            lookup: pid => pid == proc.Pid ? proc : null,
            http: _http,
            pollInterval: TimeSpan.FromMilliseconds(10),
            pollTimeout: TimeSpan.FromSeconds(3));

        await Mgr().StartAsync(KeyedModel(port, "old-key-that-is-long"), new RecordingServeListener(), CancellationToken.None);
        var keyFile = captured!.ArgumentList[captured.ArgumentList.IndexOf("--api-key-file") + 1];

        File.Delete(ServeJson);   //delete the record so the second start proceeds instead of refusing as already serving.
        await Mgr().StartAsync(KeyedModel(port, "new-key"), new RecordingServeListener(), CancellationToken.None);

        var written = File.ReadAllText(keyFile);
        Assert.Contains("new-key", written, StringComparison.Ordinal);
        Assert.DoesNotContain("old-key-that-is-long", written, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Start_without_an_api_key_writes_no_key_file_and_composes_no_flag()
    {
        await using var server = new FakeOpenAiServer();
        var port = new Uri(server.BaseUrl).Port;
        server.Enqueue(new FakeResponse(Status: 200, Body: "{}"));

        ProcessStartInfo? captured = null;
        var proc = new FakeProc { Pid = 7777 };
        var mgr = new ServeManager(_home, FakeLlama(),
            spawn: psi => { captured = psi; return proc; },
            lookup: pid => pid == proc.Pid ? proc : null,
            http: _http,
            pollInterval: TimeSpan.FromMilliseconds(10),
            pollTimeout: TimeSpan.FromSeconds(3));

        await mgr.StartAsync(ModelWithServer(port, null), new RecordingServeListener(), CancellationToken.None);

        Assert.DoesNotContain("--api-key-file", captured!.ArgumentList);
        Assert.False(File.Exists(Path.Combine(_home, "models", "test-model", "api-key.txt")));
    }

    [Fact]
    public async Task Start_falls_back_to_global_llama_server_when_model_has_no_override()
    {
        await using var server = new FakeOpenAiServer();
        var port = new Uri(server.BaseUrl).Port;
        server.Enqueue(new FakeResponse(Status: 200, Body: "{}"));

        var global = FakeLlama();
        ProcessStartInfo? captured = null;
        var proc = new FakeProc { Pid = 7777 };
        var mgr = new ServeManager(_home, global,
            spawn: psi => { captured = psi; return proc; },
            lookup: pid => pid == proc.Pid ? proc : null,
            http: _http,
            pollInterval: TimeSpan.FromMilliseconds(10),
            pollTimeout: TimeSpan.FromSeconds(3));

        await mgr.StartAsync(ModelWithServer(port, null), new RecordingServeListener(), CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal(global, captured!.FileName);
    }

    [Fact]
    public async Task Start_throws_config_error_naming_model_when_override_binary_missing()
    {
        var missing = Path.Combine(_home, "no-such-dir", "llama-server.exe");
        var mgr = new ServeManager(_home, FakeLlama(),
            spawn: NoSpawn,
            lookup: _ => null,
            http: _http,
            pollInterval: TimeSpan.FromMilliseconds(10),
            pollTimeout: TimeSpan.FromSeconds(1));

        var ex = await Assert.ThrowsAsync<GattoConfigException>(
            () => mgr.StartAsync(ModelWithServer(1235, missing), new RecordingServeListener(), CancellationToken.None));
        Assert.Contains("test-model", ex.Message);
        Assert.Contains(missing, ex.Message);
    }

    [Fact]
    public async Task Start_writes_serve_json_and_polls_to_ready()
    {
        await using var server = new FakeOpenAiServer();
        var port = new Uri(server.BaseUrl).Port;
        server.Enqueue(new FakeResponse(Status: 200, Body: "{}"));   //the health endpoint answers 200 once.

        var proc = new FakeProc { Pid = 7777 };
        var mgr = new ServeManager(_home, FakeLlama(),
            spawn: _ => proc,
            lookup: pid => pid == proc.Pid ? proc : null,
            http: _http,
            pollInterval: TimeSpan.FromMilliseconds(10),
            pollTimeout: TimeSpan.FromSeconds(3));

        var output = new StringWriter();
        var heard = new RecordingServeListener();
        var exit = await mgr.StartAsync(ModelOn(port), heard, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.True(File.Exists(ServeJson));
        using var doc = JsonDocument.Parse(File.ReadAllText(ServeJson));
        Assert.Equal(7777, doc.RootElement.GetProperty("pid").GetInt32());
        Assert.Equal("test-model", doc.RootElement.GetProperty("model").GetString());
        Assert.Equal(port, doc.RootElement.GetProperty("port").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("started").GetString()));
        Assert.Equal(port, heard.Facts("Ready")[1]);
    }

    [Fact]
    public async Task Start_detached_pins_utf8_no_bom_on_the_redirected_pipes()
    {
        await using var server = new FakeOpenAiServer();
        var port = new Uri(server.BaseUrl).Port;
        server.Enqueue(new FakeResponse(Status: 200, Body: "{}"));   //the health endpoint answers 200 once.

        ProcessStartInfo? captured = null;
        var proc = new FakeProc { Pid = 7777 };
        var mgr = new ServeManager(_home, FakeLlama(),
            spawn: psi => { captured = psi; return proc; },
            lookup: pid => pid == proc.Pid ? proc : null,
            http: _http,
            pollInterval: TimeSpan.FromMilliseconds(10),
            pollTimeout: TimeSpan.FromSeconds(3));

        await mgr.StartAsync(ModelOn(port), new RecordingServeListener(), CancellationToken.None);

        Assert.NotNull(captured);
        //the llama-server banner is UTF-8 and may hold CJK paths, so the read side must decode UTF-8 or the log garbles
        Assert.IsType<UTF8Encoding>(captured!.StandardOutputEncoding);
        Assert.IsType<UTF8Encoding>(captured.StandardErrorEncoding);
        //the encoding must have no preamble. a preamble would corrupt the first logged line.
        Assert.Empty(((UTF8Encoding)captured.StandardOutputEncoding!).GetPreamble());
        Assert.Empty(((UTF8Encoding)captured.StandardErrorEncoding!).GetPreamble());
    }

    [Fact]
    public async Task Start_foreground_leaves_pipe_encoding_unset()
    {
        //foreground inherits the console and redirects no pipes. setting an encoding then throws, so the manager must not set one.
        ProcessStartInfo? captured = null;
        var proc = new FakeProc { Pid = 4321, HasExited = false };
        var mgr = new ServeManager(_home, FakeLlama(),
            spawn: psi => { captured = psi; return proc; },
            lookup: pid => pid == proc.Pid ? proc : null,
            http: _http,
            pollInterval: TimeSpan.FromMilliseconds(10),
            pollTimeout: TimeSpan.FromSeconds(3));

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await mgr.StartAsync(ModelOn(1235), new RecordingServeListener(), cts.Token, foreground: true);

        Assert.NotNull(captured);
        Assert.Null(captured!.StandardOutputEncoding);
        Assert.Null(captured.StandardErrorEncoding);
    }

    [Fact]
    public async Task Start_foreground_stops_the_server_and_cleans_up_on_cancellation()
    {
        var proc = new FakeProc { Pid = 4321, HasExited = false };
        var mgr = new ServeManager(_home, FakeLlama(),
            spawn: _ => proc,
            lookup: pid => pid == proc.Pid ? proc : null,
            http: _http,
            pollInterval: TimeSpan.FromMilliseconds(10),
            pollTimeout: TimeSpan.FromSeconds(3));

        using var cts = new CancellationTokenSource();
        cts.Cancel();   //the cancellation stands in for ctrl+c arriving while attached.

        var output = new StringWriter();
        var heard = new RecordingServeListener();
        var exit = await mgr.StartAsync(ModelOn(1235), heard, cts.Token, foreground: true);

        Assert.Equal(0, exit);
        Assert.Equal(1, proc.KillCount);        //the server we started must be stopped exactly once.
        Assert.False(File.Exists(ServeJson));
        Assert.True(heard.Heard("Stopping"));
    }

    [Fact]
    public async Task Start_foreground_returns_nonzero_when_the_server_exits_on_its_own()
    {
        var proc = new FakeProc { Pid = 4322, HasExited = true };
        var mgr = new ServeManager(_home, FakeLlama(),
            spawn: _ => proc,
            lookup: pid => pid == proc.Pid ? proc : null,
            http: _http,
            pollInterval: TimeSpan.FromMilliseconds(10),
            pollTimeout: TimeSpan.FromSeconds(3));

        var output = new StringWriter();
        var heard = new RecordingServeListener();
        var exit = await mgr.StartAsync(ModelOn(1235), heard, CancellationToken.None, foreground: true);

        Assert.Equal(1, exit);                  //a server that went down on its own gives a non-zero exit.
        Assert.Equal(0, proc.KillCount);        //there is nothing to kill, so kill must not be called.
        Assert.False(File.Exists(ServeJson));
        Assert.True(heard.Heard("Exited"));
    }

    [Fact]
    public async Task Start_refused_when_pid_alive_and_ours()
    {
        WriteServeJson(pid: 5000, model: "old-model", port: 1235);
        var running = new FakeProc { Pid = 5000, ProcessName = "llama-server" };

        var mgr = new ServeManager(_home, FakeLlama(),
            spawn: NoSpawn,
            lookup: pid => pid == 5000 ? running : null,
            http: _http,
            pollInterval: TimeSpan.FromMilliseconds(10),
            pollTimeout: TimeSpan.FromSeconds(1));

        var output = new StringWriter();
        var heard = new RecordingServeListener();
        var exit = await mgr.StartAsync(ModelOn(9999), heard, CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Equal(new object?[] { "old-model", 5000 }, heard.Facts("Refused"));
    }

    [Fact]
    public async Task Stop_kills_matching_llama_server_pid_and_cleans_up()
    {
        WriteServeJson(pid: 6000, model: "p", port: 1235);
        var ours = new FakeProc { Pid = 6000, ProcessName = "llama-server" };

        var mgr = new ServeManager(_home, FakeLlama(),
            spawn: NoSpawn, lookup: pid => pid == 6000 ? ours : null,
            http: _http, pollInterval: TimeSpan.FromMilliseconds(10), pollTimeout: TimeSpan.FromSeconds(1));

        var heard = new RecordingServeListener();
        var exit = await mgr.StopAsync(heard, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal(1, ours.KillCount);            //the matching process is killed exactly once.
        Assert.False(File.Exists(ServeJson));
    }

    [Fact]
    public async Task Stop_does_not_kill_recycled_notepad_pid()
    {
        WriteServeJson(pid: 6000, model: "p", port: 1235);
        var notepad = new FakeProc { Pid = 6000, ProcessName = "notepad" };

        var mgr = new ServeManager(_home, FakeLlama(),
            spawn: NoSpawn, lookup: pid => pid == 6000 ? notepad : null,
            http: _http, pollInterval: TimeSpan.FromMilliseconds(10), pollTimeout: TimeSpan.FromSeconds(1));

        var output = new StringWriter();
        var heard = new RecordingServeListener();
        var exit = await mgr.StopAsync(heard, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal(0, notepad.KillCount);
        Assert.False(File.Exists(ServeJson));       //the record is removed even though the process was not ours.
        Assert.Equal("notepad", heard.Facts("NotOurs")[1]);
    }

    [Fact]
    public async Task Stop_kill_failure_keeps_serve_json_prints_hint_and_exits_one()
    {
        WriteServeJson(pid: 6000, model: "p", port: 1235);
        var ours = new FakeProc
        {
            Pid = 6000,
            ProcessName = "llama-server",
            KillThrows = new InvalidOperationException("access denied"),
        };

        var mgr = new ServeManager(_home, FakeLlama(),
            spawn: NoSpawn, lookup: pid => pid == 6000 ? ours : null,
            http: _http, pollInterval: TimeSpan.FromMilliseconds(10), pollTimeout: TimeSpan.FromSeconds(1));

        var output = new StringWriter();
        var heard = new RecordingServeListener();
        var exit = await mgr.StopAsync(heard, CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Equal(1, ours.KillCount);
        Assert.True(File.Exists(ServeJson));   //the record stays because the server is still alive.
        //the event words are the oracle here, this pins the event and the reason it gave
        Assert.Equal("access denied", heard.Facts("KillFailed")[1]);
        Assert.False(heard.Heard("Stopped"));
    }

    [Fact]
    public async Task Stop_cleans_up_stale_serve_json_when_process_gone()
    {
        WriteServeJson(pid: 4000, model: "p", port: 1235);

        var mgr = new ServeManager(_home, FakeLlama(),
            spawn: NoSpawn, lookup: _ => null,
            http: _http, pollInterval: TimeSpan.FromMilliseconds(10), pollTimeout: TimeSpan.FromSeconds(1));

        var heard = new RecordingServeListener();
        var exit = await mgr.StopAsync(heard, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.False(File.Exists(ServeJson));
    }

    //only the pid and the port are load-bearing, a missing descriptive field must not null the state

    [Fact]
    public async Task A_serve_json_with_only_a_pid_and_a_port_still_stops_the_server()
    {
        File.WriteAllText(ServeJson, """{"pid":4000,"port":1235}""");

        var mgr = new ServeManager(_home, FakeLlama(),
            spawn: NoSpawn, lookup: _ => null,   //the lookup reports the pid dead, so the stale-cleanup path runs.
            http: _http, pollInterval: TimeSpan.FromMilliseconds(10), pollTimeout: TimeSpan.FromSeconds(1));

        var heard = new RecordingServeListener();
        var exit = await mgr.StopAsync(heard, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.False(heard.Heard("NotServingOnStop"));
        Assert.False(File.Exists(ServeJson));
    }

    [Fact]
    public async Task An_OLD_VOCABULARY_serve_json_still_stops_the_server()
    {
        //the record may keep the retired pack key, the code must ignore it and still stop the server
        File.WriteAllText(ServeJson,
            """{"pid":4000,"pack":"qwen3.6-35b","port":1235,"started":"2026-07-09T00:00:00Z"}""");

        var mgr = new ServeManager(_home, FakeLlama(),
            spawn: NoSpawn, lookup: _ => null,
            http: _http, pollInterval: TimeSpan.FromMilliseconds(10), pollTimeout: TimeSpan.FromSeconds(1));

        var output = new StringWriter();
        var heard = new RecordingServeListener();
        var exit = await mgr.StopAsync(heard, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.False(heard.Heard("NotServingOnStop"));
        Assert.False(File.Exists(ServeJson));
    }

    [Fact]
    public async Task Stop_when_no_serve_json_says_not_serving()
    {
        var mgr = new ServeManager(_home, FakeLlama(),
            spawn: NoSpawn, lookup: _ => null,
            http: _http, pollInterval: TimeSpan.FromMilliseconds(10), pollTimeout: TimeSpan.FromSeconds(1));

        var heard = new RecordingServeListener();
        var exit = await mgr.StopAsync(heard, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.True(heard.Heard("NotServingOnStop"));
    }

    //the outcome names what the stop did, the exit code reads 0 for no record, a kill, a recycled pid and a dead pid
    [Fact]
    public void Stop_reports_what_it_did_with_the_record_it_read()
    {
        var killed = new FakeProc { Pid = 6000 };
        var notepad = new FakeProc { Pid = 6004, ProcessName = "notepad" };
        var locked = new FakeProc { Pid = 6008, KillThrows = new InvalidOperationException("access denied") };
        var mgr = new ServeManager(_home, FakeLlama(),
            spawn: NoSpawn, lookup: pid => pid switch { 6000 => killed, 6004 => notepad, 6008 => locked, _ => null },
            http: _http, pollInterval: TimeSpan.FromMilliseconds(10), pollTimeout: TimeSpan.FromSeconds(1));

        Assert.Equal(new ServeManager.StopOutcome(ServeManager.StopResult.NotServing, null, null, null), mgr.Stop(NullServeListener.Instance));
        WriteServeJson(pid: 6000, model: "p", port: 1235);
        Assert.Equal(new ServeManager.StopOutcome(ServeManager.StopResult.Stopped, "p", 6000, null), mgr.Stop(NullServeListener.Instance));
        WriteServeJson(pid: 6004, model: "p", port: 1235);
        Assert.Equal(new ServeManager.StopOutcome(ServeManager.StopResult.NotOurs, "p", 6004, null), mgr.Stop(NullServeListener.Instance));
        WriteServeJson(pid: 4000, model: "p", port: 1235);
        Assert.Equal(new ServeManager.StopOutcome(ServeManager.StopResult.Stale, "p", 4000, null), mgr.Stop(NullServeListener.Instance));
        WriteServeJson(pid: 6008, model: "p", port: 1235);
        Assert.Equal(new ServeManager.StopOutcome(ServeManager.StopResult.KillFailed, "p", 6008, "access denied"), mgr.Stop(NullServeListener.Instance));
        Assert.Equal((1, 0, 1), (killed.KillCount, notepad.KillCount, locked.KillCount));
    }

    [Fact]
    public async Task Stop_treats_corrupt_serve_json_as_not_serving_and_cleans_up()
    {
        File.WriteAllText(ServeJson, "{ not json");

        var mgr = new ServeManager(_home, FakeLlama(),
            spawn: NoSpawn, lookup: _ => throw new Xunit.Sdk.XunitException("must not look up on corrupt state"),
            http: _http, pollInterval: TimeSpan.FromMilliseconds(10), pollTimeout: TimeSpan.FromSeconds(1));

        var heard = new RecordingServeListener();
        var exit = await mgr.StopAsync(heard, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.False(File.Exists(ServeJson));
    }

    [Fact]
    public async Task Start_missing_llama_server_throws_config_error_before_spawn()
    {
        var missing = Path.Combine(_home, "does-not-exist.exe");
        var mgr = new ServeManager(_home, missing,
            spawn: NoSpawn,   //the error must come before spawn is reached.
            lookup: _ => null,
            http: _http, pollInterval: TimeSpan.FromMilliseconds(10), pollTimeout: TimeSpan.FromSeconds(1));

        var ex = await Assert.ThrowsAsync<GattoConfigException>(
            () => mgr.StartAsync(ModelOn(1235), new RecordingServeListener(), CancellationToken.None));
        Assert.Contains("llama_server", ex.Message);
        Assert.False(File.Exists(ServeJson));
    }

    [Fact]
    public async Task Start_empty_llama_server_throws_config_error()
    {
        var mgr = new ServeManager(_home, "",
            spawn: NoSpawn, lookup: _ => null,
            http: _http, pollInterval: TimeSpan.FromMilliseconds(10), pollTimeout: TimeSpan.FromSeconds(1));

        var ex = await Assert.ThrowsAsync<GattoConfigException>(
            () => mgr.StartAsync(ModelOn(1235), new RecordingServeListener(), CancellationToken.None));
        Assert.Contains("llama_server", ex.Message);
    }

    [Fact]
    public async Task Start_timeout_with_live_process_says_still_loading_and_exits_zero()
    {
        //a free port with nothing listening never becomes healthy, and the fake process stays alive.
        var deadPort = FreePort();
        var proc = new FakeProc { Pid = 8888, HasExited = false };

        var mgr = new ServeManager(_home, FakeLlama(),
            spawn: _ => proc,
            lookup: pid => pid == proc.Pid ? proc : null,
            http: _http,
            pollInterval: TimeSpan.FromMilliseconds(10),
            pollTimeout: TimeSpan.FromMilliseconds(60));

        var output = new StringWriter();
        var heard = new RecordingServeListener();
        var exit = await mgr.StartAsync(ModelOn(deadPort), heard, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.True(heard.Heard("StillLoading"));
        Assert.True(File.Exists(ServeJson));
    }

    [Fact]
    public async Task Start_process_dies_prints_last_10_log_lines_and_exits_one()
    {
        //seed the log with twelve lines. the fake spawn never truncates it, but a real spawn does.
        var lines = Enumerable.Range(1, 12).Select(n => $"log line {n}").ToArray();
        File.WriteAllLines(ServeLog, lines);

        var deadPort = FreePort();
        var proc = new FakeProc { Pid = 9001, HasExited = true };

        var mgr = new ServeManager(_home, FakeLlama(),
            spawn: _ => proc,
            lookup: pid => pid == proc.Pid ? proc : null,
            http: _http,
            pollInterval: TimeSpan.FromMilliseconds(10),
            pollTimeout: TimeSpan.FromMilliseconds(200));

        var heard = new RecordingServeListener();
        var exit = await mgr.StartAsync(ModelOn(deadPort), heard, CancellationToken.None);

        Assert.Equal(1, exit);
        var tail = Assert.IsAssignableFrom<IReadOnlyList<string>>(heard.Facts("DiedDuringLoad")[0]);
        Assert.Equal("log line 12", tail[^1]);       //last line kept
        Assert.Equal("log line 3", tail[0]);         //10th from last kept
        Assert.DoesNotContain("log line 2", tail);   //11th from last is not
        Assert.False(File.Exists(ServeJson));
    }

    //a missing log and an empty log are different symptoms, so the missing one must name the foreground rule
    [Fact]
    public async Task A_missing_serve_log_says_so_and_names_the_foreground_rule()
    {
        Assert.False(File.Exists(ServeLog));   //the log is never written in the foreground shape.

        var deadPort = FreePort();
        var proc = new FakeProc { Pid = 9001, HasExited = true };
        var mgr = new ServeManager(_home, FakeLlama(),
            spawn: _ => proc, lookup: pid => pid == proc.Pid ? proc : null, http: _http,
            pollInterval: TimeSpan.FromMilliseconds(10), pollTimeout: TimeSpan.FromMilliseconds(200));

        var heard = new RecordingServeListener();
        await mgr.StartAsync(ModelOn(deadPort), heard, CancellationToken.None);

        Assert.True((bool)heard.Facts("DiedDuringLoad")[1]!);
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<string>>(heard.Facts("DiedDuringLoad")[0]));
    }

    //an existing log with no lines must report empty, or it collapses into the missing wording
    [Fact]
    public async Task An_empty_serve_log_still_reports_empty_not_missing()
    {
        File.WriteAllText(ServeLog, "");

        var deadPort = FreePort();
        var proc = new FakeProc { Pid = 9002, HasExited = true };
        var mgr = new ServeManager(_home, FakeLlama(),
            spawn: _ => proc, lookup: pid => pid == proc.Pid ? proc : null, http: _http,
            pollInterval: TimeSpan.FromMilliseconds(10), pollTimeout: TimeSpan.FromMilliseconds(200));

        var output = new StringWriter();
        var heard = new RecordingServeListener();
        await mgr.StartAsync(ModelOn(deadPort), heard, CancellationToken.None);

        Assert.False((bool)heard.Facts("DiedDuringLoad")[1]!);
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<string>>(heard.Facts("DiedDuringLoad")[0]));
    }

    [Fact]
    public async Task Start_cancelled_mid_poll_leaves_child_running_and_exits_zero()
    {
        var deadPort = FreePort();   //a free port with nothing listening never turns healthy.
        var proc = new FakeProc { Pid = 8080, HasExited = false };
        using var cts = new CancellationTokenSource();
        cts.Cancel();   //the token is already cancelled before the first poll.

        var mgr = new ServeManager(_home, FakeLlama(),
            spawn: _ => proc,
            lookup: pid => pid == proc.Pid ? proc : null,
            http: _http,
            pollInterval: TimeSpan.FromMilliseconds(10),
            pollTimeout: TimeSpan.FromSeconds(3));

        var output = new StringWriter();
        var heard = new RecordingServeListener();
        var exit = await mgr.StartAsync(ModelOn(deadPort), heard, cts.Token);

        Assert.Equal(0, exit);                       //cancellation must exit zero, it is not a failure
        Assert.True(heard.Heard("StillLoading"));
        Assert.True(File.Exists(ServeJson));         //the record must stay in place after cancellation.
        Assert.Equal(0, proc.KillCount);             //the child must be left running.
    }

    [Fact]
    public async Task Status_not_serving_says_how_to_start()
    {
        var mgr = new ServeManager(_home, FakeLlama(),
            spawn: NoSpawn, lookup: _ => null,
            http: _http, pollInterval: TimeSpan.FromMilliseconds(10), pollTimeout: TimeSpan.FromSeconds(1));

        var output = new StringWriter();
        var heard = new RecordingServeListener();
        var exit = await mgr.StatusAsync(heard, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.True(heard.Heard("NotServingOnStatus"));
    }

    [Fact]
    public async Task Status_running_reports_model_port_and_health()
    {
        await using var server = new FakeOpenAiServer();
        var port = new Uri(server.BaseUrl).Port;
        server.Enqueue(new FakeResponse(Status: 200, Body: "{}"));
        WriteServeJson(pid: 6000, model: "live-model", port: port);
        var ours = new FakeProc { Pid = 6000, ProcessName = "llama-server" };

        var mgr = new ServeManager(_home, FakeLlama(),
            spawn: NoSpawn, lookup: pid => pid == 6000 ? ours : null,
            http: _http, pollInterval: TimeSpan.FromMilliseconds(10), pollTimeout: TimeSpan.FromSeconds(1));

        var output = new StringWriter();
        var heard = new RecordingServeListener();
        var exit = await mgr.StatusAsync(heard, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal("live-model", heard.Facts("Status")[0]);
        Assert.Equal(port, heard.Facts("Status")[1]);
    }

    [Fact]
    public async Task Status_json_not_serving_emits_all_null_object()
    {
        var mgr = new ServeManager(_home, FakeLlama(),
            spawn: NoSpawn, lookup: _ => null,
            http: _http, pollInterval: TimeSpan.FromMilliseconds(10), pollTimeout: TimeSpan.FromSeconds(1));

        var output = new StringWriter();
        var exit = await mgr.StatusJsonAsync(output, CancellationToken.None);

        Assert.Equal(0, exit);
        using var doc = JsonDocument.Parse(output.ToString());
        var root = doc.RootElement;
        Assert.Equal(JsonValueKind.Null, root.GetProperty("model_path").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("model_id").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("matches_model").ValueKind);
        Assert.True(root.TryGetProperty("schema_version", out _));
        //human prose must not leak into the json-only path.
        Assert.DoesNotContain("run 'gatto serve start", output.ToString());
    }

    [Fact]
    public async Task Status_json_running_probes_props_and_reports_model_match()
    {
        await using var server = new FakeOpenAiServer();
        var port = new Uri(server.BaseUrl).Port;
        var modelPath = Path.Combine(_home, "m.gguf");
        var modelPathJson = JsonSerializer.Serialize(modelPath);
        server.PropsResponse = new FakeResponse(Status: 200, Body:
            "{\"model_path\":" + modelPathJson + ",\"default_generation_settings\":{\"n_ctx\":8192}}");
        WriteServeJson(pid: 6100, model: "live-model", port: port);
        var ours = new FakeProc { Pid = 6100, ProcessName = "llama-server" };

        //create a model on disk whose model_path matches the probed one (matches_model reads true only then)
        var modelDir = Path.Combine(_home, "models", "live-model");
        Directory.CreateDirectory(modelDir);
        File.WriteAllText(Path.Combine(modelDir, "profile.json"),
            "{\"files\":[{\"path\":" + modelPathJson + ",\"active\":true}],\"port\":" + port + ",\"context\":8192}");

        var mgr = new ServeManager(_home, FakeLlama(),
            spawn: NoSpawn, lookup: pid => pid == 6100 ? ours : null,
            http: _http, pollInterval: TimeSpan.FromMilliseconds(10), pollTimeout: TimeSpan.FromSeconds(1));

        var output = new StringWriter();
        var exit = await mgr.StatusJsonAsync(output, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.True(server.PropsCalled);
        using var doc = JsonDocument.Parse(output.ToString());
        var root = doc.RootElement;
        Assert.Equal(modelPath, root.GetProperty("model_path").GetString());
        Assert.Equal(8192, root.GetProperty("n_ctx").GetInt32());
        Assert.Equal("live-model", root.GetProperty("model_id").GetString());
        Assert.True(root.GetProperty("matches_model").GetBoolean());
        Assert.Null(server.LastAuthorization);   //a keyless model must invent no credential.
    }

    [Fact]
    public async Task Status_json_sends_the_models_api_key_on_the_props_probe()
    {
        //the model must load before the probe, its profile api_key is the only credential this probe site has
        await using var server = new FakeOpenAiServer();
        var port = new Uri(server.BaseUrl).Port;
        var modelPath = Path.Combine(_home, "m.gguf");
        var modelPathJson = JsonSerializer.Serialize(modelPath);
        server.PropsResponse = new FakeResponse(Status: 200, Body:
            "{\"model_path\":" + modelPathJson + ",\"default_generation_settings\":{\"n_ctx\":8192}}");
        WriteServeJson(pid: 6400, model: "keyed-model", port: port);
        var ours = new FakeProc { Pid = 6400, ProcessName = "llama-server" };

        var modelDir = Path.Combine(_home, "models", "keyed-model");
        Directory.CreateDirectory(modelDir);
        File.WriteAllText(Path.Combine(modelDir, "profile.json"),
            "{\"files\":[{\"path\":" + modelPathJson + ",\"active\":true}],\"port\":" + port
            + ",\"context\":8192,\"api_key\":\"s3cr3t-value\"}");

        var mgr = new ServeManager(_home, FakeLlama(),
            spawn: NoSpawn, lookup: pid => pid == 6400 ? ours : null,
            http: _http, pollInterval: TimeSpan.FromMilliseconds(10), pollTimeout: TimeSpan.FromSeconds(1));

        var output = new StringWriter();
        var exit = await mgr.StatusJsonAsync(output, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.True(server.PropsCalled);
        Assert.Equal("Bearer s3cr3t-value", server.LastAuthorization);
        Assert.True(JsonDocument.Parse(output.ToString()).RootElement.GetProperty("matches_model").GetBoolean());
        Assert.DoesNotContain("s3cr3t-value", output.ToString());   //the key must never appear in the output.
    }

    [Fact]
    public async Task Status_json_model_removed_since_serving_started_leaves_matches_model_unknown()
    {
        await using var server = new FakeOpenAiServer();
        var port = new Uri(server.BaseUrl).Port;
        server.PropsResponse = new FakeResponse(Status: 200, Body: """{"model_path":"C:\\m.gguf"}""");
        WriteServeJson(pid: 6200, model: "vanished-model", port: port);   //the model folder does not exist on disk.
        var ours = new FakeProc { Pid = 6200, ProcessName = "llama-server" };

        var mgr = new ServeManager(_home, FakeLlama(),
            spawn: NoSpawn, lookup: pid => pid == 6200 ? ours : null,
            http: _http, pollInterval: TimeSpan.FromMilliseconds(10), pollTimeout: TimeSpan.FromSeconds(1));

        var output = new StringWriter();
        var exit = await mgr.StatusJsonAsync(output, CancellationToken.None);

        Assert.Equal(0, exit);
        using var doc = JsonDocument.Parse(output.ToString());
        var root = doc.RootElement;
        Assert.Equal("vanished-model", root.GetProperty("model_id").GetString());   //the reported id stays the recorded one.
        Assert.Equal(JsonValueKind.Null, root.GetProperty("matches_model").ValueKind);   //null is how the json reports an unknown match
    }

    [Fact]
    public async Task Status_json_stale_pid_surfaces_the_recorded_model_id_not_all_null()
    {
        WriteServeJson(pid: 6300, model: "stale-model", port: 1234);
        var mgr = new ServeManager(_home, FakeLlama(),
            spawn: NoSpawn, lookup: _ => null,   //the lookup reports the pid gone, so the state is stale.
            http: _http, pollInterval: TimeSpan.FromMilliseconds(10), pollTimeout: TimeSpan.FromSeconds(1));

        var output = new StringWriter();
        var exit = await mgr.StatusJsonAsync(output, CancellationToken.None);

        Assert.Equal(0, exit);
        using var doc = JsonDocument.Parse(output.ToString());
        var root = doc.RootElement;
        Assert.Equal("stale-model", root.GetProperty("model_id").GetString());              //a stale pid still reports the id from the record
        Assert.Equal(JsonValueKind.Null, root.GetProperty("model_path").ValueKind);       //the probe is skipped while the pid is stale
        Assert.Equal(JsonValueKind.Null, root.GetProperty("matches_model").ValueKind);     //null is the unverified signal.
        Assert.True(root.TryGetProperty("schema_version", out _));
    }

    //the log opens before the spawn, so a failed open cannot strand a server with no record
    [Fact]
    public void DefaultSpawn_survives_a_log_that_cannot_be_opened()
    {
        //hold serve.log open exclusively, so DefaultSpawn's own FileStream.Create fails
        using var lockStream = new FileStream(ServeLog, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        var mgr = new ServeManager(_home, FakeLlama());
        var psi = new ProcessStartInfo { FileName = Path.Combine(_home, "definitely-not-an-exe.exe"), RedirectStandardOutput = true };

        //the exception type is the oracle, only Process.Start throws Win32Exception
        Assert.Throws<System.ComponentModel.Win32Exception>(() => mgr.DefaultSpawn(psi));
    }

    //release the log handle on the stop itself, a running child would never fire Exited
    [Fact]
    public async Task Stopping_releases_the_log_handle_without_waiting_for_the_child_to_exit()
    {
        var mgr = new ServeManager(_home, FakeLlama());
        var cmd = Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe";
        //keep the child alive for the check, so a passing test cannot mean the exit released the handle.
        var psi = new ProcessStartInfo
        {
            FileName = cmd,
            Arguments = "/c ping -n 60 127.0.0.1",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        var proc = mgr.DefaultSpawn(psi);
        try
        {
            Assert.False(proc.HasExited);   //assert the premise that the handle is held before the stop, so the check cannot pass vacuously.

            await mgr.StopAsync(NullServeListener.Instance, CancellationToken.None);

            //the exclusive reopen succeeds only when no handle holds the file, so it proves the release happened.
            using var check = new FileStream(ServeLog, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        finally
        {
            try { proc.Kill(); } catch { }
        }
    }

    [Fact]
    public void DefaultSpawn_process_start_failure_disposes_the_log_stream()
    {
        var mgr = new ServeManager(_home, FakeLlama());
        //setting a redirect selects the detached spawn path
        var psi = new ProcessStartInfo { FileName = Path.Combine(_home, "definitely-not-an-exe.exe"), RedirectStandardOutput = true };

        Assert.ThrowsAny<Exception>(() => mgr.DefaultSpawn(psi));

        //dispose the log stream when Process.Start throws, the reopen proves no handle leaked
        using var check = new FileStream(ServeLog, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    //a write can throw IOException on a pool thread where nothing catches it, so the pump must fail open

    //this writer stands in for a full disk, every write throws IOException
    private sealed class ThrowingWriter : StringWriter
    {
        public override void WriteLine(string? value) => throw new IOException("Insufficient quota");
    }

    [Fact]
    public void A_failing_serve_log_write_does_not_escape_the_pump()
    {
        var warnings = new List<ServeLogFault>();
        var pump = ServeManager.CreateLogPump(new ThrowingWriter(), warnings.Add);

        //the OutputDataReceived delegate runs on a pool thread, an escaping throw kills the process
        var ex = Record.Exception(() => pump("llama-server: loading model"));

        Assert.Null(ex);                       //the log is only diagnostic and must never be load-bearing, so the pump must not throw.
        Assert.Single(warnings);               //failing open must still report one warning, so a broken log stays visible to the user.
        Assert.Equal(ServeLogFault.NotWritten, warnings[0]);   //the fault is the fact here, the wording belongs to the layer that paints it
    }

    [Fact]
    public void The_serve_log_failure_warning_is_latched_to_one_shot()
    {
        var warnings = new List<ServeLogFault>();
        var pump = ServeManager.CreateLogPump(new ThrowingWriter(), warnings.Add);

        for (var i = 0; i < 50; i++) pump($"line {i}");

        Assert.Single(warnings);   //a full disk must not produce fifty identical rows, so the pump latches the warning only once.
    }

    [Fact]
    public void A_disposed_serve_log_is_a_no_op_not_a_throw()
    {
        var warnings = new List<ServeLogFault>();
        var writer = new StringWriter();
        var pump = ServeManager.CreateLogPump(writer, warnings.Add, out var dispose);

        pump("before");
        dispose();                 //dispose the way the Exited handler does, while the pump may still be draining
        var ex = Record.Exception(() => pump("after"));

        Assert.Null(ex);
        Assert.Empty(warnings);    //a disposed log is an ordinary end of life, so the pump must report no failure.
        Assert.Contains("before", writer.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("after", writer.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_null_line_is_ignored_and_never_warns()
    {
        var warnings = new List<ServeLogFault>();
        var writer = new StringWriter();
        var pump = ServeManager.CreateLogPump(writer, warnings.Add);

        pump(null);   //the Process class raises OutputDataReceived with null Data at end of stream, which the pump skips

        Assert.Empty(warnings);
        Assert.Equal("", writer.ToString());
    }

    //a TextWriter on a human path would put sentences in Gatto.Roles, where no theme exists
    [Fact]
    public void NO_HUMAN_PATH_IN_SERVEMANAGER_TAKES_A_TEXTWRITER()
    {
        var taking = typeof(ServeManager)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static
                | System.Reflection.BindingFlags.DeclaredOnly)
            .Where(m => m.GetParameters().Any(a => typeof(TextWriter).IsAssignableFrom(a.ParameterType)))
            .Select(m => m.Name)
            .Distinct()
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        //both names below are known matches, so a reflection read that found nothing fails here first
        Assert.Contains("StatusJsonAsync", taking);
        Assert.Contains("CreateLogPump", taking);

        //the StatusJsonAsync writer feeds a parser and the CreateLogPump writer a log file, neither reaches a screen
        Assert.Equal(["CreateLogPump", "StatusJsonAsync"], taking);
    }
}
