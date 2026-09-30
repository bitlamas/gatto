using System.Text.Json;
using Gatto.Cli;
using Gatto.Cli.Setup;
using Gatto.Core.Acquire;
using Gatto.Core.Tools;
using Gatto.Repl;
using Gatto.Roles;

namespace Gatto.Tests;

//three profiles in one home, old dialect, new dialect and its own engine, and every refusal row asserts the home's bytes did not move
public sealed class EngineUpdateTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-engine-upd-").FullName;
    private readonly StringWriter _out = new();
    private string Output => _out.ToString();

    public void Dispose() { try { Directory.Delete(_home, true); } catch (Exception) { } }

    private static readonly string Pin = LlamaAssetSteering.PinnedRelease;
    private static readonly string[] OldArgs = ["-np", "1", "-fa", "on", "--no-mmap", "--jinja", "--no-context-shift", "--cache-reuse", "0"];
    private static readonly string[] NewArgs = ["-np", "1", "-fa", "on", "-lm", "none", "--jinja", "--no-context-shift", "--cache-reuse", "0"];

    private string OldServer => Path.Combine(_home, "llama", "b10737", "llama-server.exe");
    private string Landed => Path.Combine(_home, "llama", Pin, "llama-server.exe");
    private string ProfilePath(string id) => Path.Combine(_home, "models", id, "profile.json");

    public EngineUpdateTests()
    {
        WriteServer(OldServer);
        Config(OldServer);
        Profile("old-dialect", OldArgs, own: null);
        Profile("new-dialect", NewArgs, own: null);
        Profile("own-engine", OldArgs, own: @"C:\mixes\b10715\llama-server.exe");
    }

    private static void WriteServer(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "not a real server");
    }

    private void Config(string server) =>
        File.WriteAllText(Path.Combine(_home, "gatto.json"), JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["endpoints"] = new Dictionary<string, object> { ["local"] = new Dictionary<string, string> { ["base_url"] = "http://127.0.0.1:18235" } },
            ["default_endpoint"] = "local",
            ["llama_server"] = server,
        }, new JsonSerializerOptions { WriteIndented = true }));

    private void Profile(string id, string[] args, string? own)
    {
        Directory.CreateDirectory(Path.Combine(_home, "models", id));
        var doc = new Dictionary<string, object> { ["files"] = new[] { new { path = @"C:\models\" + id + ".gguf", active = true } }, ["port"] = 18235, ["context"] = 8192 };
        if (own is not null) doc["llama_server"] = own;
        doc["extra_args"] = args;
        File.WriteAllText(ProfilePath(id), JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string[] ArgsIn(string path) =>
        [.. JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetProperty("extra_args").EnumerateArray().Select(a => a.GetString()!)];

    private string ConfiguredServer() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(_home, "gatto.json"))).RootElement.GetProperty("llama_server").GetString()!;

    //every file under the home with its bytes, so a refusal can be shown to have written nothing
    private Dictionary<string, string> Snapshot() =>
        Directory.EnumerateFiles(_home, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(_home, f), f => Convert.ToHexString(File.ReadAllBytes(f)));

    //the network and the executables. every deed defaults to the path that succeeds, and each counts its calls
    private sealed class Fakes(EngineUpdateTests t)
    {
        public Dictionary<string, string?> Builds = new(StringComparer.OrdinalIgnoreCase);
        public string? LandedBuild = "11071";
        public string? FetchError;
        public bool OfferNothing;
        public int Offers, Fetches, Probes;
        public List<string> Probed = [];

        public EngineActs Acts() => new(
            () =>
            {
                Offers++;
                return OfferNothing ? null : new EngineFetchOffer(Path.GetDirectoryName(t.Landed)!, new EnginePair(
                    new EngineAsset($"llama-{Pin}-bin-win-vulkan-x64.zip", "https://example.invalid/z", "ab12", 31_851_232), null));
            },
            (_, _) =>
            {
                Fetches++;
                if (FetchError is { } e) return new EngineFetch(null, e);
                WriteServer(t.Landed);
                return new EngineFetch(t.Landed, null);
            },
            exe =>
            {
                Probes++;
                Probed.Add(exe);
                var build = string.Equals(exe, t.Landed, StringComparison.OrdinalIgnoreCase) ? LandedBuild
                    : Builds.TryGetValue(exe, out var b) ? b : "10737";
                return new ProbeResult(build is null ? ProbeShape.NotClassic : ProbeShape.ClassicServer, "", Build: build);
            });
    }

    private int _asks;
    private SelectSpec? _asked;

    private int Run(Fakes f, int? answer = 0, string? running = null)
    {
        var acts = f.Acts();
        var site = EngineSite.Probe(_home, acts.Probe, _ => running);
        var ctx = new CommandContext(_out, _home, true, Theme: null);
        return EngineUpdate.Run(ctx, ctx.Surface(), Gatto.Terminal.GlyphSet.Unicode, site, acts,
            spec => { _asks++; _asked = spec; _out.WriteLine("[question]"); return answer; });
    }

    [Fact]
    public void AN_OLDER_ENGINE_IS_REPLACED_AND_ONLY_THE_OLD_DIALECT_PROFILE_IS_REWRITTEN()
    {
        var newBefore = File.ReadAllBytes(ProfilePath("new-dialect"));
        var ownBefore = File.ReadAllBytes(ProfilePath("own-engine"));
        var oldBefore = File.ReadAllBytes(ProfilePath("old-dialect"));
        var f = new Fakes(this);

        Assert.Equal(0, Run(f));

        Assert.Equal(1, f.Fetches);
        Assert.Equal(Landed, ConfiguredServer());
        Assert.Equal(NewArgs, ArgsIn(ProfilePath("old-dialect")));
        Assert.Equal(oldBefore, File.ReadAllBytes(ProfilePath("old-dialect") + ".before-" + Pin));
        Assert.Equal(newBefore, File.ReadAllBytes(ProfilePath("new-dialect")));
        Assert.False(File.Exists(ProfilePath("new-dialect") + ".before-" + Pin));
        Assert.Equal(ownBefore, File.ReadAllBytes(ProfilePath("own-engine")));
        Assert.False(File.Exists(ProfilePath("own-engine") + ".before-" + Pin));
        Assert.True(File.Exists(OldServer));
        Assert.Contains("own-engine", Output);
        Assert.Contains(Doctor.Tilde(Path.GetDirectoryName(OldServer)!), Output);
    }

    //a second run finds the first run's backup and keeps it, so the only copy of what the user had is never overwritten
    [Fact]
    public void THE_BACKUP_IS_KEPT_ONCE()
    {
        var original = File.ReadAllBytes(ProfilePath("old-dialect"));
        Assert.Equal(0, Run(new Fakes(this)));
        Profile("old-dialect", ["-np", "1", "--mmap"], own: null);
        Config(OldServer);

        Assert.Equal(0, Run(new Fakes(this)));

        Assert.Equal(original, File.ReadAllBytes(ProfilePath("old-dialect") + ".before-" + Pin));
        Assert.Equal(["-np", "1", "-lm", "mmap"], ArgsIn(ProfilePath("old-dialect")));
    }

    //the landed build is probed before the first write, so a release that is not the pin changes nothing the user owns
    [Fact]
    public void A_LANDED_ENGINE_THAT_IS_NOT_THE_PIN_WRITES_NOTHING()
    {
        var before = Snapshot();
        var f = new Fakes(this) { LandedBuild = "11070" };

        Assert.Equal(1, Run(f));

        Assert.Equal(1, f.Fetches);
        Assert.Contains(Landed, f.Probed);
        var after = Snapshot();
        foreach (var (file, bytes) in before) Assert.Equal(bytes, after[file]);
        Assert.Equal(OldServer, ConfiguredServer());
    }

    [Fact]
    public void A_DIGEST_MISMATCH_WRITES_NOTHING()
    {
        var before = Snapshot();
        var f = new Fakes(this) { FetchError = "the download's digest does not match the one the release published." };

        Assert.Equal(1, Run(f));

        Assert.Equal(before, Snapshot());
        Assert.Contains("digest", Output);
    }

    [Fact]
    public void A_RUNNING_SERVER_IS_REFUSED_BEFORE_THE_QUESTION()
    {
        var before = Snapshot();
        var f = new Fakes(this);

        Assert.Equal(1, Run(f, running: "old-dialect"));

        Assert.Equal(before, Snapshot());
        Assert.Equal(0, _asks);
        Assert.Equal(0, f.Offers);
        Assert.Contains("gatto serve stop", Output);
    }

    //a folder under gatto's llama root whose name is not its own build was named by a hand. gatto never replaces a build a hand chose.
    [Fact]
    public void A_FORK_IS_REFUSED_WITH_ITS_FOLDER_NAMED()
    {
        var fork = Path.Combine(_home, "llama", "laguna-2bd55d0", "llama-server.exe");
        WriteServer(fork);
        Config(fork);
        var before = Snapshot();
        var f = new Fakes(this);

        Assert.Equal(1, Run(f));

        Assert.Equal(before, Snapshot());
        Assert.Equal(0, _asks);
        Assert.Contains("laguna-2bd55d0", Output);
    }

    [Fact]
    public void AN_UNREADABLE_BANNER_IS_REFUSED()
    {
        var before = Snapshot();
        var f = new Fakes(this);
        f.Builds[OldServer] = null;

        Assert.Equal(1, Run(f));

        Assert.Equal(before, Snapshot());
        Assert.Equal(0, _asks);
        Assert.Contains(OldServer, Output);
    }

    [Fact]
    public void THE_PINNED_ENGINE_WITH_EVERY_PROFILE_CURRENT_SAYS_ONE_LINE()
    {
        WriteServer(Landed);
        Config(Landed);
        Profile("old-dialect", NewArgs, own: null);
        var before = Snapshot();
        var f = new Fakes(this);

        Assert.Equal(0, Run(f));

        Assert.Equal(before, Snapshot());
        Assert.Equal(0, _asks);
        Assert.Equal($"the engine is {Pin}, the release gatto is tested with.", Output.Trim());
    }

    [Fact]
    public void A_NEWER_ENGINE_IS_NAMED_AND_NOTHING_IS_OFFERED()
    {
        var newer = Path.Combine(_home, "llama", "b11200", "llama-server.exe");
        WriteServer(newer);
        Config(newer);
        var before = Snapshot();
        var f = new Fakes(this);
        f.Builds[newer] = "11200";

        Assert.Equal(0, Run(f));

        Assert.Equal(before, Snapshot());
        Assert.Equal(0, _asks);
        Assert.Contains("b11200", Output);
    }

    [Fact]
    public void A_RELEASE_GITHUB_DOES_NOT_ANSWER_FOR_CHANGES_NOTHING()
    {
        var before = Snapshot();
        var f = new Fakes(this) { OfferNothing = true };

        Assert.Equal(1, Run(f));

        Assert.Equal(before, Snapshot());
        Assert.Equal(0, _asks);
        Assert.Equal(0, f.Fetches);
    }

    [Fact]
    public void DECLINING_CHANGES_NOTHING()
    {
        var before = Snapshot();
        var f = new Fakes(this);

        Assert.Equal(0, Run(f, answer: 1));

        Assert.Equal(before, Snapshot());
        Assert.Equal(0, f.Fetches);
    }

    //a landed folder that already reports the pin is used as it is, so a second run after a failed write downloads nothing
    [Fact]
    public void A_LANDED_PIN_IS_REUSED_WITHOUT_A_DOWNLOAD()
    {
        WriteServer(Landed);
        var f = new Fakes(this);

        Assert.Equal(0, Run(f));

        Assert.Equal(0, f.Offers);
        Assert.Equal(0, f.Fetches);
        Assert.Equal(Landed, ConfiguredServer());
    }

    private Task<int> Command(string latest, Fakes f, int? gattoAnswer = 0, string installed = "0.4.1")
    {
        var release = new Release(new UpdateState(DateTimeOffset.UnixEpoch, latest, false),
            [new ReleaseAsset("gatto-0.4.2-win-x64.zip", "https://example.invalid/z", "ab12", 38_000_000)], "https://example.invalid/notes");
        var gattoActs = new UpdateActs((_, _, _) => Task.FromResult<Release?>(release),
            (_, _, _, _, _) => Task.FromResult(Path.Combine("dl", "gatto.zip")),
            (_, _, _) => Task.FromResult<string?>(null), (_, _) => null, (_, _) => null);
        var acts = f.Acts();
        return UpdateCommand.RunAsync(new CommandContext(_out, _home, true, Theme: null), CancellationToken.None,
            new UpdateSite(@"C:\Programs\gatto", true, installed, [], false), gattoActs, _ => gattoAnswer,
            EngineSite.Probe(_home, acts.Probe, _ => null), acts, spec => { _asks++; _asked = spec; return 0; });
    }

    //a bare gatto update on the latest gatto still offers the engine
    [Fact]
    public async Task THE_ENGINE_PHASE_FOLLOWS_A_CURRENT_GATTO()
    {
        var f = new Fakes(this);

        Assert.Equal(0, await Command("v0.4.1", f));

        Assert.Equal(1, _asks);
        Assert.Equal(Landed, ConfiguredServer());
    }

    //an installed gatto ahead of the latest release still gets the engine question
    [Fact]
    public async Task THE_ENGINE_PHASE_FOLLOWS_A_GATTO_AHEAD_OF_THE_LATEST_RELEASE()
    {
        var f = new Fakes(this);

        Assert.Equal(0, await Command("v0.4.0", f));

        Assert.Contains("is older than this gatto", Output);
        Assert.Equal(1, _asks);
        Assert.Equal(Landed, ConfiguredServer());
    }

    //an installed release candidate fails the strict version parse, and the engine phase runs without that comparison
    [Fact]
    public async Task THE_ENGINE_PHASE_FOLLOWS_AN_INSTALLED_RELEASE_CANDIDATE()
    {
        var f = new Fakes(this);

        Assert.Equal(0, await Command("v0.5.2", f, installed: "0.5.3-rc"));

        Assert.Contains("couldn't compare versions", Output);
        Assert.Equal(1, _asks);
        Assert.Equal(Landed, ConfiguredServer());
    }

    //the process that replaced gatto still holds the previous pin, so it leaves the engine to the next run and says so
    [Fact]
    public async Task THE_ENGINE_PHASE_WAITS_FOR_THE_NEXT_RUN_AFTER_GATTO_WAS_REPLACED()
    {
        var before = Snapshot();
        var f = new Fakes(this);

        Assert.Equal(0, await Command("v0.4.2", f));

        Assert.Equal(0, _asks);
        Assert.Equal(0, f.Offers);
        Assert.Equal(before, Snapshot());
        Assert.Contains("run gatto update again", Output);
    }

    private string Lines => Output.Replace("\r\n", "\n");

    //the map is drawn before the question, one row per engine and one per model with what happens to it. the question itself has no default.
    [Fact]
    public void THE_MAP_BEFORE_THE_QUESTION_NAMES_EVERY_CHANGE()
    {
        Run(new Fakes(this), answer: 1);

        var spec = Assert.IsType<SelectSpec>(_asked);
        Assert.True(spec.NoInitialCursor);
        Assert.Equal($"Update llama.cpp to {Pin}?", spec.Question?.Text);
        Assert.Contains($"\nllama.cpp runs your models. gatto is tested with {Pin}.\n", Lines);
        Assert.Contains($"\n  now      b10737   {Doctor.Tilde(Path.GetDirectoryName(OldServer)!)}\n", Lines);
        Assert.Contains($"\n  update   {Pin}   {Doctor.Tilde(Path.GetDirectoryName(Landed)!)} (download 30.4 MB)\n", Lines);
        Assert.Contains($"\nmodels\n  old-dialect   settings updated for {Pin}, backup kept\n  own-engine    not touched, it uses its own llama.cpp\n\n[question]\n", Lines);
        Assert.DoesNotContain("new-dialect", Lines);
    }

    //the report starts one blank line under the question and gives each fact its own marked line, doctor's shape
    [Fact]
    public void THE_REPORT_OPENS_WITH_A_BLANK_LINE_AND_MARKS_EACH_FACT()
    {
        WriteServer(Landed);

        Assert.Equal(0, Run(new Fakes(this)));

        Assert.EndsWith(
            $"own-engine    not touched, it uses its own llama.cpp\n\n[question]\n\n"
            + $"\u2713 llama.cpp is now {Pin} ({Doctor.Tilde(Path.GetDirectoryName(Landed)!)})\n"
            + $"\u2713 old-dialect updated (backup: profile.json.before-{Pin})\n"
            + $"i own-engine not touched, it uses its own llama.cpp\n"
            + $"i the old llama.cpp b10737 is still at {Doctor.Tilde(Path.GetDirectoryName(OldServer)!)} (0.0 MB)\n",
            Lines);
    }
}
