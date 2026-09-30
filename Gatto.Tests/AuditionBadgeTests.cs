using Gatto.Core.Acquire;
using Gatto.Roles;
using Gatto.Roles.Audition;

namespace Gatto.Tests;

//the badge is keyed on the gguf name, and the shelf looks up by repo id. the tests drive the two-argument Write the CLI calls, taking the id off the stamp.
public class AuditionBadgeTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-f24-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch (Exception) { } //a failed cleanup must not fail the test.
        GC.SuppressFinalize(this);
    }

    private static AuditionVerdict Verdict(string file, string? repoId) => new(
        Pass: true, Disqualified: false,
        Tasks: [],
        Stamp: new AuditionStamp(
            GattoBuild: "abc1234", ModelFileName: file, Quant: "Q6_K", Context: 4096,
            SamplingNote: "defaults", ThinkingNote: "", Server: null, RepoId: repoId),
        WallClock: TimeSpan.FromSeconds(9), DecodeTokS: 27.9);

    //the register is keyed by file name for the wizard and the local shelf. the lookup under the stem must find nothing, or a reader that matches anything passes.
    [Fact]
    public void THE_LOCAL_ROAD_READS_THE_BADGE_THE_WIZARD_WROTE()
    {
        const string File = "Ministral-3-3B-Q6_K.gguf";
        BadgeWriter.Write(_home, File, Verdict(File, "unsloth/Ministral-3-3B-GGUF"),
            "unsloth/Ministral-3-3B-GGUF");

        var probes = new Gatto.Cli.Setup.LiveSetupProbes(_home,
            glyphs: Gatto.Terminal.GlyphSet.Unicode);

        Assert.Equal(File, probes.BadgeForFile(File)?.ModelKey);
        Assert.Null(probes.BadgeForFile("Ministral-3-3B-Q6_K"));
    }

    //a fetched model has a repo id, and the shelf looks up only by it. a badge written under the file name alone is not found
    [Fact]
    public void A_CLI_AUDITION_WRITES_THE_REPO_ID_THE_SHELF_LOOKS_UP_BY()
    {
        BadgeWriter.Write(_home, Verdict("Qwen3.5-4B-Q6_K.gguf", "unsloth/Qwen3.5-4B-GGUF"));

        Assert.NotNull(BadgeRegister.LookupByRepoId(_home, "unsloth/Qwen3.5-4B-GGUF"));
    }

    //a model adopted from disk has no repo id, so the field stays null. the badge is still found under the file name, the key that every model has
    [Fact]
    public void A_MODEL_ADOPTED_FROM_DISK_STILL_WRITES_A_BADGE_WITH_NO_REPO_ID()
    {
        BadgeWriter.Write(_home, Verdict("local-thing.gguf", repoId: null));

        var badge = BadgeRegister.Lookup(_home, "local-thing.gguf");
        Assert.NotNull(badge);
        Assert.Null(badge!.RepoId);
    }

    //the stamp reads the repo id from the measured profile. test both profiles, since source is absent on a disk-adopted profile
    [Theory]
    [InlineData("unsloth/Qwen3.5-4B-GGUF")]
    [InlineData(null)]
    public void THE_STAMP_CARRIES_THE_PROFILES_OWN_REPO_ID(string? repoId)
    {
        var models = Path.Combine(_home, "models");
        var dir = Path.Combine(models, "m");
        Directory.CreateDirectory(dir);
        var gguf = Path.Combine(dir, "w.gguf");
        File.WriteAllText(gguf, "");
        var source = repoId is null
            ? ""
            : $$"""
                "source": { "repo_id": {{System.Text.Json.JsonSerializer.Serialize(repoId)}},
                            "file": "w.gguf" },
                """;
        File.WriteAllText(Path.Combine(dir, "profile.json"), $$"""
            {
              "port": 1235,
              "context": 4096,
              {{source}}
              "files": [ { "path": {{System.Text.Json.JsonSerializer.Serialize(gguf)}}, "active": true } ]
            }
            """);

        var stamp = AuditionRunner.StampFor(Model.Load(models, "m"), loaded: null);

        Assert.Equal(repoId, stamp.RepoId);
    }
}
