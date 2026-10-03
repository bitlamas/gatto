using System.Diagnostics;
using Gatto.Cli.Setup;
using Gatto.Cli.Setup.Tui;
using Gatto.Roles;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup;

//a released server must be restored on every exit, even Esc and a throw, and only when something was released
public class HeldServerRestoreTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-restore-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_home, true); } catch (Exception) { }
    }

    private const int HeldPid = 4242;

    public enum Exit { Answered, Escaped, Threw, EscapedBefore }

    //the process the recorded pid resolves to, live and named like our server, so the record reads as held
    private sealed class LiveHeld(bool killThrows = false) : ServeManager.IServeProcess
    {
        public int Pid => HeldPid;
        public bool HasExited => false;
        public string ProcessName => "llama-server";
        public void Kill()
        {
            //a stop that cannot kill is a real state (access denied on someone else's process), and the only way to make StopAsync return non-zero here
            if (killThrows) throw new InvalidOperationException("access is denied");
        }
    }

    //records every start request and never touches the operating system.
    private class RecordingSpawn
    {
        //the list holds processes so a test can assert the flow's own server was killed before the held one started. give each fake its own pid and kill record
        public List<Fake> Spawned { get; } = [];
        public List<string> Started => [.. Spawned.Select(p => p.Arguments)];
        public string? LastStartedModelId => Spawned.Count > 0 ? Spawned[^1].Arguments : null;

        public virtual ServeManager.IServeProcess Spawn(ProcessStartInfo psi)
        {
            //the model is read from both argument channels joined, since gatto composes through ArgumentList and Arguments alone is empty
            var p = new Fake(psi.Arguments + " " + string.Join(" ", psi.ArgumentList), 5150 + Spawned.Count);
            Spawned.Add(p);
            return p;
        }

        //the Kill method sets HasExited, so a lookup answers honestly about which server is still up. a fake that never exits would hide every stop
        public sealed class Fake(string arguments, int pid) : ServeManager.IServeProcess
        {
            private bool _killed;

            public string Arguments { get; } = arguments;
            public int Pid => pid;   //the pid is never the held one, since a spawn is a different process
            public string ProcessName => "llama-server";
            public void Kill() => _killed = true;

            //the fake stays alive until killed, or it exits early and the double-start guard never fires, letting the wrong order pass
            public bool HasExited => _killed;
        }
    }

    [Theory]
    [InlineData(Exit.Answered, true)]
    [InlineData(Exit.Escaped, true)]
    [InlineData(Exit.Threw, true)]
    [InlineData(Exit.EscapedBefore, false)]   //nothing was released, so no restart is expected.
    public void THE_RELEASED_SERVER_IS_RESTORED_ON_EVERY_EXIT(Exit how, bool expectRestore)
    {
        var spawn = new RecordingSpawn();
        var probes = ProbesOver(spawn);

        try
        {
            using (probes)
            {
                if (how != Exit.EscapedBefore) probes.ReleaseHeldServer();

                //the released server must stay down until the exit, since the check runs in between and needs that port
                Assert.Null(spawn.LastStartedModelId);

                if (how == Exit.Threw) throw new InvalidOperationException("the walk fell over");
            }
        }
        catch (InvalidOperationException) { } //the exception is the exit being tested here.

        Assert.Equal(expectRestore, spawn.LastStartedModelId is not null);
    }

    //the restore must run after the flow's own server stops, or it hits the double-start guard and the start refuses quietly
    [Fact]
    public void THE_RESTORE_RUNS_AFTER_THE_WALKS_OWN_SERVER_IS_STOPPED()
    {
        var spawn = new RecordingSpawn();
        var probes = ProbesOver(spawn);

        using (probes)
        {
            probes.ReleaseHeldServer();
            //a different model keeps the two spawns distinguishable, since restarting the held model would make both arguments identical
            probes.ProveIt("other-model");
        }

        //two spawns in this order, since the wrong order hits the double-start guard and quietly leaves only the flow's own server started
        Assert.Equal(2, spawn.Spawned.Count);
        Assert.Contains("other.gguf", spawn.Spawned[0].Arguments, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("held.gguf", spawn.Spawned[1].Arguments, StringComparison.OrdinalIgnoreCase);
    }

    //a server start is a moment for the purr, a line kept in the notes would print above the closing after the wait was over
    [Fact]
    public void A_SERVER_START_WORKS_THE_PURR_AND_LEAVES_NO_NOTE()
    {
        var notes = new StringWriter();
        var spawn = new RecordingSpawn();
        var worked = 0;
        var probes = ProbesOver(spawn, notes, working: () => worked++);

        using (probes)
        {
            probes.ReleaseHeldServer();
            probes.ProveIt("other-model");
        }

        Assert.Contains("other.gguf", spawn.Spawned[0].Arguments, StringComparison.OrdinalIgnoreCase);
        Assert.True(worked > 0, "the start never reached the purr");
        Assert.DoesNotContain("starting other-model", notes.ToString(), StringComparison.Ordinal);
    }

    //a stop that failed must remember nothing, or the restore names a model that never needed putting back
    [Fact]
    public void A_STOP_THAT_FAILED_REMEMBERS_NOTHING()
    {
        var notes = new StringWriter();
        var spawn = new RecordingSpawn();
        var probes = ProbesOver(spawn, notes, killThrows: true);

        using (probes)
        {
            Assert.False(probes.ReleaseHeldServer());
        }

        Assert.Empty(spawn.Spawned);
        //nothing was released, so no restart note may appear
        Assert.DoesNotContain("couldn't put", notes.ToString(), StringComparison.Ordinal);
    }

    //the release flag is cleared after use, so the restore happens exactly once
    [Fact]
    public void A_SECOND_DISPOSE_DOES_NOT_START_IT_AGAIN()
    {
        var spawn = new RecordingSpawn();
        var probes = ProbesOver(spawn);

        probes.ReleaseHeldServer();
        probes.Dispose();
        probes.Dispose();

        Assert.Single(spawn.Started);
    }

    //a previous model that no longer loads must be reported, since the flow borrowed the machine's server and must say what it left behind
    [Fact]
    public void A_PREVIOUS_MODEL_THAT_NO_LONGER_LOADS_IS_REPORTED()
    {
        var notes = new StringWriter();
        var spawn = new RecordingSpawn();
        var probes = ProbesOver(spawn, notes);

        using (probes)
        {
            probes.ReleaseHeldServer();
            //the profile is deleted after the release, so the id is remembered and the load fails.
            Directory.Delete(Path.Combine(_home, "models", "held-model"), true);
        }

        var said = notes.ToString();
        Assert.Contains("held-model", said, StringComparison.Ordinal);
        Assert.Null(spawn.LastStartedModelId);
        //the missing-profile wording and the will-not-start wording must stay different, or both tests stay green on one sentence
        Assert.DoesNotContain("gatto serve start", said, StringComparison.Ordinal);
    }

    //a model that loads but will not start is a different failure from a missing profile, so one test cannot cover both
    [Fact]
    public void A_PREVIOUS_MODEL_THAT_WILL_NOT_START_IS_REPORTED_NOT_SWALLOWED()
    {
        var notes = new StringWriter();
        var probes = ProbesOver(new ThrowingSpawn(), notes);

        using (probes) { probes.ReleaseHeldServer(); }

        var said = notes.ToString();
        Assert.Contains("held-model", said, StringComparison.Ordinal);
        //the message must state the restart command, rather than leaving the machine serverless silently.
        Assert.Contains("gatto serve start", said, StringComparison.Ordinal);
    }

    //dispose runs after the session drains the notes, so a restore failure written only to the face is lost
    [Fact]
    public void A_FAILED_RESTORE_IS_SAID_ON_THE_CONSOLE_AFTER_THE_RECORD()
    {
        var surface = new RecordingSurface { Width = 100 };
        using var face = new TuiWizardSurface(surface, new NoKeys(), new Theme(new TermCaps(true, true)),
            "0.5.0", "1a2b3c4", () => 0);
        var console = new StringWriter();
        var probes = ProbesOver(new ThrowingSpawn(), face.Notes, afterWalk: console);

        using (probes)
        {
            WizardSession.Run(new AltScreen(surface, title: null),
                () => { probes.ReleaseHeldServer(); return 0; },
                () => ["the record"], console, notes: () => face.CapturedNotes,
                registerHooks: false, take: () => null, giveBack: _ => { });
        }

        var said = console.ToString();
        var record = said.IndexOf("the record", StringComparison.Ordinal);
        var failure = said.IndexOf("couldn't put held-model back", StringComparison.Ordinal);
        Assert.True(record >= 0, said);
        Assert.True(failure > record, $"the restore's failure did not reach the console after the record:\n{said}");
    }

    //the line while the server loads reports elapsed time measured by polling, and the final line says the model is back
    [Fact]
    public async Task THE_RESTORE_SHOWS_A_LIVE_LINE_AND_ENDS_ON_BACK()
    {
        await using var server = new FakeOpenAiServer();
        server.Enqueue(new FakeResponse(Status: 503, Body: "{}"));   //a 503 answer means the server is still loading.
        server.Enqueue(new FakeResponse(Status: 503, Body: "{}"));
        server.Enqueue(new FakeResponse(Status: 200, Body: "{}"));   //a 200 answer means the server is ready.
        var said = new StringWriter();
        var probes = ProbesOver(new RecordingSpawn(), said, heldPort: new Uri(server.BaseUrl).Port,
            pollTimeout: TimeSpan.FromSeconds(30), rich: true,
            clientTimeout: TimeSpan.FromSeconds(30));

        using (probes) { probes.ReleaseHeldServer(); }

        var text = said.ToString();
        //the assertion targets a tick line, the ellipsis followed by elapsed time, which the first render before polling omits
        Assert.Contains("putting held-model back… ", text, StringComparison.Ordinal);
        Assert.EndsWith("held-model is back", text.TrimEnd(), StringComparison.Ordinal);
    }

    //when the polling ends with no answer the sentence says the model is still loading, with no timing number
    [Fact]
    public void A_RESTORE_THAT_OUTLASTS_THE_POLL_SAYS_IT_IS_STILL_LOADING()
    {
        var said = new StringWriter();
        var probes = ProbesOver(new RecordingSpawn(), said);   //no server answers on the held model's port here.

        using (probes) { probes.ReleaseHeldServer(); }

        Assert.Contains("still loading, gatto serve status shows when it answers", said.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("is back", said.ToString(), StringComparison.Ordinal);
    }

    //one blank line follows the restore's last line, so the next prompt does not crowd it.
    [Fact]
    public async Task THE_RESTORE_ENDS_WITH_ONE_EMPTY_LINE()
    {
        await using var server = new FakeOpenAiServer();
        server.Enqueue(new FakeResponse(Status: 200, Body: "{}"));   //a 200 answer means the server is ready.
        var said = new StringWriter();
        var probes = ProbesOver(new RecordingSpawn(), said, heldPort: new Uri(server.BaseUrl).Port,
            pollTimeout: TimeSpan.FromSeconds(30), rich: true,
            clientTimeout: TimeSpan.FromSeconds(30));

        using (probes) { probes.ReleaseHeldServer(); }

        Assert.EndsWith("held-model is back" + Environment.NewLine + Environment.NewLine, said.ToString(),
            StringComparison.Ordinal);
    }

    //the poll's own answer picks the final line, so a read after it that fails cannot turn a server that answered into one still loading
    [Fact]
    public async Task A_POLL_THAT_SAW_READY_ENDS_ON_BACK_WHATEVER_A_LATER_READ_SAYS()
    {
        await using var server = new FakeOpenAiServer();
        server.Enqueue(new FakeResponse(Status: 200, Body: "{}"));   //one ready answer for the poll, and any read after it gets the fake's 500
        var said = new StringWriter();
        var probes = ProbesOver(new RecordingSpawn(), said, heldPort: new Uri(server.BaseUrl).Port,
            pollTimeout: TimeSpan.FromSeconds(30), rich: true,
            clientTimeout: TimeSpan.FromSeconds(30));

        using (probes) { probes.ReleaseHeldServer(); }

        Assert.EndsWith("held-model is back", said.ToString().TrimEnd(), StringComparison.Ordinal);
    }

    //nothing is written after a run that released nothing, and the guard covers a restore that writes on every dispose
    [Fact]
    public void A_WALK_THAT_RELEASED_NOTHING_WRITES_NOTHING_AFTER_IT()
    {
        var said = new StringWriter();
        var probes = ProbesOver(new RecordingSpawn(), said);

        using (probes) { }

        Assert.Equal("", said.ToString());
    }

    private sealed class NoKeys : Gatto.Terminal.IKeySource
    {
        public bool KeyAvailable => false;
        public ConsoleKeyInfo ReadKey() => throw new InvalidOperationException("no key is read in this test");
    }

    //a spawn that refuses stands in for a non-zero result from StartAsync
    private sealed class ThrowingSpawn : RecordingSpawn
    {
        public override ServeManager.IServeProcess Spawn(ProcessStartInfo psi) =>
            throw new InvalidOperationException("the engine refused");
    }

    private LiveSetupProbes ProbesOver(RecordingSpawn spawn, TextWriter? notes = null,
        bool killThrows = false, int heldPort = 41888, TimeSpan? pollTimeout = null, bool rich = false,
        TextWriter? afterWalk = null, TimeSpan? clientTimeout = null, Action? working = null)
    {
        WriteHome(heldPort);
        //the lookup must report the recorded pid as a live server, or the record reads as stale and there is nothing to release
        return new LiveSetupProbes(_home, Gatto.Terminal.GlyphSet.Unicode, notes, rich: rich,
            managerFactory: () => new ServeManager(_home, Path.Combine(_home, "llama-server.exe"),
                spawn.Spawn,
                //the lookup answers for the held pid and for every live process this test spawned, or the stop cannot tell which server is ours
                pid => pid == HeldPid
                    ? new LiveHeld(killThrows)
                    : spawn.Spawned.FirstOrDefault(s => s.Pid == pid && !s.HasExited),
                new HttpClient { Timeout = clientTimeout ?? TimeSpan.FromMilliseconds(200) },
                TimeSpan.FromMilliseconds(1), pollTimeout ?? TimeSpan.FromMilliseconds(5)),
            //this budget ends the polling without changing when the fake process dies.
            readyBudget: TimeSpan.FromMilliseconds(200),
            afterWalk: afterWalk,
            working: working);
    }

    //builds a home whose serve.json names a different model as the live holder, the only state where the release applies
    private void WriteHome(int heldPort)
    {
        File.WriteAllText(Path.Combine(_home, "llama-server.exe"), "");
        var gguf = Path.Combine(_home, "held.gguf");
        File.WriteAllText(gguf, "");

        var dir = Path.Combine(_home, "models", "held-model");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "profile.json"),
            $$"""{ "files": [{ "path": {{System.Text.Json.JsonSerializer.Serialize(gguf)}}, "active": true }], "port": {{heldPort}}, "context": 4096 }""");

        //give the second model its own port, or both argument strings are identical and the spawn order cannot be checked
        var other = Path.Combine(_home, "other.gguf");
        File.WriteAllText(other, "");
        var dir2 = Path.Combine(_home, "models", "other-model");
        Directory.CreateDirectory(dir2);
        File.WriteAllText(Path.Combine(dir2, "profile.json"),
            $$"""{ "files": [{ "path": {{System.Text.Json.JsonSerializer.Serialize(other)}}, "active": true }], "port": 41889, "context": 4096 }""");

        File.WriteAllText(Path.Combine(_home, "gatto.json"),
            $$"""
            {
              "endpoints": { "local": { "base_url": "http://127.0.0.1:41888" } },
              "default_endpoint": "local",
              "llama_server": {{System.Text.Json.JsonSerializer.Serialize(Path.Combine(_home, "llama-server.exe"))}}
            }
            """);

        File.WriteAllText(Path.Combine(_home, "serve.json"),
            $$"""{ "pid": {{HeldPid}}, "model": "held-model", "port": 41888, "started": "2026-09-10T10:00:00Z" }""");
    }
}
