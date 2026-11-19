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

    private static Model Naming(string? device, params string[] extraArgs) => new(
        "test-model",
        new ModelProfile([new ModelFile("m.gguf", null, true)], 1235, 8192, null, null, null, null, null, extraArgs, Device: device),
        null, null);

    //the listing is a delegate here, so no test runs the empty file that stands in for the engine. the default answers that it cannot say
    private ServeManager Manager(FakeProc proc, HttpClient http, Func<string, string, string, string?>? deviceFor = null, List<string>? argv = null) => new(_home, FakeLlama(),
        spawn: psi => { argv?.AddRange(psi.ArgumentList); return proc; }, lookup: pid => pid == proc.Pid ? proc : null, http: http,
        pollInterval: TimeSpan.FromMilliseconds(10), pollTimeout: TimeSpan.FromSeconds(3),
        deviceFor: deviceFor ?? ((_, _, _) => null));

    //the value after each flag in the argv a start handed to the spawn
    private static string[] After(List<string> argv, string flag) =>
        [.. argv.Select((a, i) => (a, i)).Where(x => x.a == flag && x.i + 1 < argv.Count).Select(x => argv[x.i + 1])];

    private async Task<List<string>> ArgvOf(Model model, Func<string, string, string, string?> deviceFor, bool foreground = false)
    {
        File.WriteAllText(ServeLog, "");
        File.Delete(ServeJson);   //a second start in 1 test must not be refused as already running
        var argv = new List<string>();
        var proc = new FakeProc { HasExited = foreground };
        await Manager(proc, Healthy(), deviceFor, argv).StartAsync(model, new RecordingServeListener(), CancellationToken.None, foreground: foreground);
        return argv;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_START_PUTS_THE_NAMED_CARD_WHERE_THE_ENGINE_LISTS_IT_NOW(bool foreground)
    {
        var asked = new List<(string Exe, string Name, string Held)>();

        var argv = await ArgvOf(Naming("Invented Card A", "-dev", "Vulkan0", "--threads", "4"),
            (exe, name, held) => { asked.Add((exe, name, held)); return "Vulkan1"; }, foreground);

        Assert.Equal(new[] { "Vulkan1" }, After(argv, "-dev"));
        Assert.DoesNotContain("Vulkan0", argv);
        Assert.Equal(new[] { "4" }, After(argv, "--threads"));
        Assert.Equal((Path.Combine(_home, "llama-server.exe"), "Invented Card A", "Vulkan0"), Assert.Single(asked));
    }

    [Fact]
    public async Task THE_LAST_DEVICE_FLAG_IS_THE_ONE_THAT_MOVES()
    {
        var argv = await ArgvOf(Naming("Invented Card A", "-dev", "Vulkan3", "--device", "Vulkan0"), (_, _, _) => "Vulkan1");

        Assert.Equal(new[] { "Vulkan3" }, After(argv, "-dev"));
        Assert.Equal(new[] { "Vulkan1" }, After(argv, "--device"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Vulkan0")]
    public async Task AN_ENGINE_THAT_CANNOT_SAY_OR_SAYS_THE_SAME_LEAVES_THE_ARGV_AS_THE_PROFILE_HOLDS_IT(string? answer)
    {
        var plain = await ArgvOf(Naming(null, "-dev", "Vulkan0", "-devd", "Vulkan0"), (_, _, _) => "Vulkan1");

        var argv = await ArgvOf(Naming("Invented Card A", "-dev", "Vulkan0", "-devd", "Vulkan0"), (_, _, _) => answer);

        Assert.Equal(plain, argv);
    }

    [Fact]
    public async Task A_PROFILE_WITHOUT_A_NAME_NEVER_ASKS_THE_ENGINE()
    {
        var asked = 0;

        var argv = await ArgvOf(Naming(null, "-dev", "Vulkan0"), (_, _, _) => { asked++; return "Vulkan1"; });

        Assert.Equal(0, asked);
        Assert.Equal(new[] { "Vulkan0" }, After(argv, "-dev"));
    }

    [Fact]
    public async Task A_NAMED_PROFILE_WITH_NO_DEVICE_FLAG_GAINS_NONE_AND_NEVER_ASKS()
    {
        var asked = 0;
        var plain = await ArgvOf(Naming(null, "--threads", "4"), (_, _, _) => "Vulkan1");

        var argv = await ArgvOf(Naming("Invented Card A", "--threads", "4"), (_, _, _) => { asked++; return "Vulkan1"; });

        Assert.Equal(0, asked);
        Assert.Equal(plain, argv);
    }

    [Fact]
    public async Task A_LIST_OF_CARDS_GOES_TO_THE_ENGINE_AS_WRITTEN()
    {
        var asked = 0;

        var argv = await ArgvOf(Naming("Invented Card A", "-dev", "Vulkan0,Vulkan1"), (_, _, _) => { asked++; return "Vulkan1"; });

        Assert.Equal(0, asked);
        Assert.Equal(new[] { "Vulkan0,Vulkan1" }, After(argv, "-dev"));
    }

    [Fact]
    public async Task A_PROFILE_SET_TO_NO_OFFLOAD_STAYS_OFF_THE_CARD()
    {
        var asked = 0;

        var argv = await ArgvOf(Naming("Invented Card A", "-dev", "none"), (_, _, _) => { asked++; return "Vulkan1"; });

        Assert.Equal(0, asked);
        Assert.Equal(new[] { "none" }, After(argv, "-dev"));
    }

    [Fact]
    public async Task A_CANCEL_DURING_THE_LISTING_ENDS_THE_START_BEFORE_ANY_SPAWN()
    {
        File.WriteAllText(ServeLog, "");
        var argv = new List<string>();
        using var cts = new CancellationTokenSource();
        using var release = new ManualResetEventSlim();
        var manager = Manager(new FakeProc(), Healthy(), (_, _, _) => { cts.Cancel(); release.Wait(TimeSpan.FromSeconds(10)); return "Vulkan1"; }, argv);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            manager.StartAsync(Naming("Invented Card A", "-dev", "Vulkan0"), new RecordingServeListener(), cts.Token));
        release.Set();

        Assert.Empty(argv);
        Assert.False(File.Exists(ServeJson));
    }

    //the manager's own delegate, over a script that prints a listing the way the engine does, so the wiring from the profile to the argv is run once for real
    [Fact]
    public async Task THE_REAL_LISTING_MOVES_THE_NAMED_CARD()
    {
        var engine = Path.Combine(_home, "llama-server.cmd");
        File.WriteAllText(engine, "@echo off\r\necho Available devices:\r\n"
            + "echo   Vulkan0: Invented Card B (8000 MiB, 7000 MiB free)\r\n"
            + "echo   Vulkan1: Invented Card A (16000 MiB, 15000 MiB free)\r\n");
        File.WriteAllText(ServeLog, "");
        var argv = new List<string>();
        var proc = new FakeProc();
        var manager = new ServeManager(_home, engine,
            spawn: psi => { argv.AddRange(psi.ArgumentList); return proc; }, lookup: pid => pid == proc.Pid ? proc : null, http: Healthy(),
            pollInterval: TimeSpan.FromMilliseconds(10), pollTimeout: TimeSpan.FromSeconds(3));

        await manager.StartAsync(Naming("Invented Card A", "-dev", "Vulkan0", "-devd", "Vulkan0"), new RecordingServeListener(), CancellationToken.None);

        Assert.Equal(new[] { "Vulkan1" }, After(argv, "-dev"));
        Assert.Equal(new[] { "Vulkan1" }, After(argv, "-devd"));
    }

    [Theory]
    [InlineData("-devd")]
    [InlineData("--device-draft")]
    [InlineData("--spec-draft-device")]
    public async Task THE_DRAFT_FOLLOWS_ONLY_WHEN_IT_SAT_ON_THE_SAME_CARD(string flag)
    {
        var together = await ArgvOf(Naming("Invented Card A", "-dev", "Vulkan0", flag, "Vulkan0"), (_, _, _) => "Vulkan1");
        var apart = await ArgvOf(Naming("Invented Card A", "-dev", "Vulkan0", flag, "Vulkan2"), (_, _, _) => "Vulkan1");

        Assert.Equal(new[] { "Vulkan1" }, After(together, flag));
        Assert.Equal(new[] { "Vulkan2" }, After(apart, flag));
        Assert.Equal(new[] { "Vulkan1" }, After(apart, "-dev"));
    }

    [Fact]
    public async Task A_MOVED_CARD_STILL_ANSWERS_TO_THE_CHECK_AFTER_THE_SPAWN()
    {
        File.WriteAllText(ServeLog, TookB);
        var proc = new FakeProc();
        var heard = new RecordingServeListener();

        var exit = await Manager(proc, Healthy(), (_, _, _) => "Vulkan1").StartAsync(Naming("Invented Card A", "-dev", "Vulkan0"), heard, CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.True(heard.Heard("WrongDevice"));
        Assert.Equal(1, proc.KillCount);
    }

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
