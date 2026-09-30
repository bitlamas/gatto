using System.Globalization;
using Gatto.Cli;
using Gatto.Roles;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests;

//a start shows one live line while the model loads, and a failed start reports the log a line at a time. the tests drive StartAndWaitAsync directly
public sealed class LaunchStartLineTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-launchstart-").FullName;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(2) };

    public void Dispose()
    {
        _http.Dispose();
        try { Directory.Delete(_home, true); } catch (Exception) { } //the temp folder cleanup is best effort, so a delete failure is ignored.
    }

    private sealed class Proc(bool exited) : ServeManager.IServeProcess
    {
        public int Pid => 30661;
        public bool HasExited => exited;
        public string ProcessName => "llama-server";
        public void Kill() => throw new Xunit.Sdk.XunitException("nothing may be stopped by the start");
    }

    private static Model ModelOn(int port) => new(
        "test-model",
        new ModelProfile([new ModelFile("m.gguf", null, true)], port, 8192, null, null, null, null, null, Array.Empty<string>()),
        null, null, null);

    private ServeManager Manager(Proc proc, Func<int, ServeManager.IServeProcess?> lookup)
    {
        File.WriteAllText(Path.Combine(_home, "llama-server.exe"), "");
        return new ServeManager(_home, Path.Combine(_home, "llama-server.exe"),
            spawn: _ => proc, lookup: lookup, http: _http,
            pollInterval: TimeSpan.FromMilliseconds(5), pollTimeout: TimeSpan.FromMilliseconds(30));
    }

    //one live line counts the start and the wait, and its seconds keep rising. pid lookups tell start rows from wait rows (the start poll skips the lookup)
    [Fact]
    public async Task ONE_LINE_COUNTS_THE_START_AND_THE_WAIT_WITHOUT_STARTING_OVER()
    {
        await using var server = new FakeOpenAiServer();   //no response is enqueued, so every /health call fails with 500
        var proc = new Proc(exited: false);
        var lookups = 0;
        var manager = Manager(proc, pid => ++lookups <= 2 && pid == proc.Pid ? proc : null);
        var reads = 0;
        var lines = new List<(string Text, int Lookups)>();

        var row = new LoadingLine(rich: true, live: l => lines.Add((l, lookups)),
            note: l => lines.Add((l, lookups)), GlyphSet.Unicode, theme: null);
        var ok = await AutoServeAsk.StartAndWaitAsync(manager, ModelOn(new Uri(server.BaseUrl).Port), _ => { },
            CancellationToken.None, GlyphSet.Unicode, row, size: null,
            clock: () => TimeSpan.FromSeconds(10 * ++reads));

        Assert.False(ok);   //the fake pid lookup stops finding the process, so the wait ends as died and reports failure.
        Assert.All(lines, l => Assert.Contains("test-model…", l.Text, StringComparison.Ordinal));
        var counted = lines.Where(l => l.Text.EndsWith('s')).ToList();
        var shown = string.Join(" | ", counted.Select(l => l.Text));
        Assert.True(counted.Any(l => l.Lookups == 0), $"no row during the start: {shown}");
        Assert.True(counted.Any(l => l.Lookups > 0), $"no row during the wait: {shown}");
        var seconds = counted.Select(l => Seconds(l.Text)).ToList();
        for (var i = 1; i < seconds.Count; i++)
            Assert.True(seconds[i] > seconds[i - 1], $"the count did not rise: {shown}");
    }

    //each log line must arrive in its own call. passing the whole log in one call collapses it into a single warning line within the session.
    [Fact]
    public async Task A_START_THAT_DIES_SAYS_ITS_LOG_ONE_LINE_AT_A_TIME()
    {
        var manager = Manager(new Proc(exited: true), _ => null);
        File.WriteAllText(Path.Combine(_home, "serve.log"),
            "load_model: loading model 'm.gguf'" + Environment.NewLine
            + "ggml_vulkan: device memory allocation of 21474836480 failed");
        var said = new List<string>();

        var ok = await AutoServeAsk.StartAndWaitAsync(manager, ModelOn(1), said.Add, CancellationToken.None,
            GlyphSet.Unicode);

        Assert.False(ok);
        Assert.All(said, s => Assert.DoesNotContain("\n", s, StringComparison.Ordinal));
        Assert.Contains("  load_model: loading model 'm.gguf'", said);
        Assert.Contains("  ggml_vulkan: device memory allocation of 21474836480 failed", said);
    }

    //reads the trailing elapsed text from ElapsedText.Of and returns its seconds
    private static long Seconds(string line)
    {
        var m = System.Text.RegularExpressions.Regex.Match(line, @"(?:(\d+)h )?(?:(\d+)m )?(\d+)s$");
        Assert.True(m.Success, line);
        long Part(int g) => m.Groups[g].Success ? long.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture) : 0;
        return Part(1) * 3600 + Part(2) * 60 + Part(3);
    }
}
