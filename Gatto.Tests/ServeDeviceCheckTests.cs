using System.Diagnostics;
using System.Net;
using Gatto.Roles.Models;
using Gatto.Roles.Serve;
using Gatto.Tests.Roles;

namespace Gatto.Tests;

//a profile that names its card is checked against the engine's own log line, and only that: no line means no check, no name means nothing read
public class ServeDeviceCheckTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-card-").FullName;

    public void Dispose() { try { Directory.Delete(_home, recursive: true); } catch (Exception) { } }

    private sealed class ScriptedHealth(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond());
    }

    private static HttpClient Healthy() => new(new ScriptedHealth(() => new HttpResponseMessage(HttpStatusCode.OK)));

    private sealed class FakeProc : ServeManager.IServeProcess
    {
        public int Pid { get; init; } = 7777;
        public bool HasExited { get; set; }
        public string ProcessName { get; init; } = "llama-server";
        public int KillCount { get; private set; }
        public Exception? KillThrows { get; init; }
        public void Kill() { KillCount++; if (KillThrows is not null) throw KillThrows; }
    }

    private string ServeJson => Path.Combine(_home, "serve.json");
    private string ServeLog => Path.Combine(_home, "serve.log");

    private string FakeLlama()
    {
        var p = Path.Combine(_home, "llama-server.exe");
        File.WriteAllText(p, "");
        return p;
    }

    private static Model Naming(string? device) => new(
        "test-model",
        new ModelProfile([new ModelFile("m.gguf", null, true)], 1235, 8192, null, null, null, null, null, Array.Empty<string>(), Device: device),
        null, null);

    private ServeManager Manager(FakeProc proc, HttpClient http) => new(_home, FakeLlama(),
        spawn: _ => proc, lookup: pid => pid == proc.Pid ? proc : null, http: http,
        pollInterval: TimeSpan.FromMilliseconds(10), pollTimeout: TimeSpan.FromSeconds(3));

    private const string TookB = "0.00.297.169 I llama_prepare_model_devices: using device Vulkan1 (Invented Card B) (unknown id) - 7000 MiB free\n";
    private const string TookA = "0.00.297.169 I llama_prepare_model_devices: using device Vulkan0 (Invented Card A) (unknown id) - 120000 MiB free\n";

    [Fact]
    public async Task THE_OTHER_CARD_IN_THE_LOG_STOPS_THE_SERVER_AND_NAMES_BOTH()
    {
        File.WriteAllText(ServeLog, "loading\n" + TookB);
        var proc = new FakeProc();
        var heard = new RecordingServeListener();

        var exit = await Manager(proc, Healthy()).StartAsync(Naming("Invented Card A"), heard, CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Equal(new object?[] { "test-model", 7777, "Invented Card A", "Invented Card B", "Vulkan1" }, heard.Facts("WrongDevice"));
        Assert.Equal(1, proc.KillCount);
        Assert.False(File.Exists(ServeJson));
        Assert.False(heard.Heard("Ready"));
    }

    [Fact]
    public async Task THE_SAME_CARD_IN_THE_LOG_SAYS_NOTHING_AND_REACHES_READY()
    {
        File.WriteAllText(ServeLog, TookA);
        var proc = new FakeProc();
        var heard = new RecordingServeListener();

        var exit = await Manager(proc, Healthy()).StartAsync(Naming("Invented Card A"), heard, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.True(heard.Heard("Ready"));
        Assert.False(heard.Heard("WrongDevice"));
        Assert.False(heard.Heard("DeviceUnread"));
        Assert.Equal(0, proc.KillCount);
    }

    //a name holding parentheses stays whole, since the engine follows the name with its own bracketed words
    [Fact]
    public async Task A_NAME_WITH_PARENTHESES_IS_READ_WHOLE()
    {
        File.WriteAllText(ServeLog, "0.00.1 I llama_prepare_model_devices: using device Vulkan0 (Invented (TM) Card A) (unknown id) - 1 MiB free\n");
        var proc = new FakeProc();
        var heard = new RecordingServeListener();

        await Manager(proc, Healthy()).StartAsync(Naming("Invented (TM) Card A"), heard, CancellationToken.None);

        Assert.True(heard.Heard("Ready"));
        Assert.False(heard.Heard("WrongDevice"));
    }

    [Fact]
    public async Task NO_LINE_BY_READY_IS_SAID_ONCE_AND_THE_SERVER_RUNS()
    {
        File.WriteAllText(ServeLog, "loading\n");
        var proc = new FakeProc();
        var heard = new RecordingServeListener();

        var exit = await Manager(proc, Healthy()).StartAsync(Naming("Invented Card A"), heard, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal(new object?[] { "test-model", "Invented Card A", false }, heard.Facts("DeviceUnread"));
        Assert.Equal(1, heard.Names.Count(n => n == "DeviceUnread"));
        Assert.True(heard.Heard("Ready"));
        Assert.Equal(0, proc.KillCount);
    }

    [Fact]
    public async Task A_PROFILE_WITHOUT_A_NAME_IS_NOT_CHECKED()
    {
        File.WriteAllText(ServeLog, TookB);
        var proc = new FakeProc();
        var heard = new RecordingServeListener();

        await Manager(proc, Healthy()).StartAsync(Naming(null), heard, CancellationToken.None);

        Assert.True(heard.Heard("Ready"));
        Assert.False(heard.Heard("WrongDevice"));
        Assert.False(heard.Heard("DeviceUnread"));
    }

    [Fact]
    public async Task A_FOREGROUND_START_SAYS_THE_NAME_WAS_NOT_CHECKED()
    {
        var proc = new FakeProc { HasExited = true };
        var heard = new RecordingServeListener();

        await Manager(proc, Healthy()).StartAsync(Naming("Invented Card A"), heard, CancellationToken.None, foreground: true);

        Assert.Equal(new object?[] { "test-model", "Invented Card A", true }, heard.Facts("DeviceUnread"));
    }

    //the engine keeps serve.log open for writing while it loads, so the line must be readable beside that writer
    [Fact]
    public async Task THE_LINE_IS_READ_WHILE_THE_ENGINE_HOLDS_THE_LOG_OPEN()
    {
        using var writer = new FileStream(ServeLog, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        var bytes = System.Text.Encoding.UTF8.GetBytes(TookB);
        writer.Write(bytes, 0, bytes.Length);
        writer.Flush();
        var proc = new FakeProc();
        var heard = new RecordingServeListener();

        var exit = await Manager(proc, Healthy()).StartAsync(Naming("Invented Card A"), heard, CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.True(heard.Heard("WrongDevice"));
    }

    //the server is alive after a failed kill, so its record stays for stop to retry
    [Fact]
    public async Task A_KILL_THAT_FAILS_KEEPS_THE_RECORD()
    {
        File.WriteAllText(ServeLog, TookB);
        var proc = new FakeProc { KillThrows = new InvalidOperationException("access denied") };
        var heard = new RecordingServeListener();

        var exit = await Manager(proc, Healthy()).StartAsync(Naming("Invented Card A"), heard, CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.True(heard.Heard("WrongDevice"));
        Assert.Equal(new object?[] { 7777, "access denied" }, heard.Facts("KillFailed"));
        Assert.True(File.Exists(ServeJson));
    }
}
