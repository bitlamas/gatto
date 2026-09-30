using System.Text.Json;
using System.Text.Json.Nodes;
using Gatto.Core.Acquire;
using Gatto.Roles;

namespace Gatto.Tests;

public class LoadModeDialectTests
{
    private static readonly LoadModeDialect D = LoadModeDialect.Load();

    private static int Pin => int.Parse(LlamaAssetSteering.PinnedRelease[1..]);

    private static JsonObject Profile(string name) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "profiles", name)))!.AsObject();

    private static string[] ArgsOf(JsonObject profile) =>
        [.. profile["extra_args"]!.AsArray().Select(n => n!.GetValue<string>())];

    //an engine bump brings a new help text, so it fails here until someone re-reads which flags the new build retired
    [Fact]
    public void THE_DIALECT_WAS_REVIEWED_AT_THE_ENGINE_PIN() =>
        Assert.Equal(LlamaAssetSteering.PinnedRelease, D.ReviewedRelease);

    //the two profiles a user moved by hand to release 11071 are the oracle. the rewrite of the first must produce the second and change nothing else in the file
    [Theory]
    [InlineData("qwen2.5-0.5b-instruct")]
    [InlineData("qwen3.6-35b-a3b")]
    public void THE_REWRITE_REPRODUCES_THE_HAND_MOVE_TO_THE_PIN(string model)
    {
        var before = Profile($"{model}.before-b11071.json");
        var after = Profile($"{model}.after-b11071.json");

        var rewritten = D.Rewrite(ArgsOf(before), Pin);
        before["extra_args"] = new JsonArray([.. rewritten.Select(a => (JsonNode)JsonValue.Create(a)!)]);

        Assert.Equal(after.ToJsonString(), before.ToJsonString());
    }

    //the mapping read from release 10703's deprecation notes, one row per retired flag. an empty list means the flag spelled the default and is dropped
    [Theory]
    [InlineData("--no-mmap", "-lm none")]
    [InlineData("--mmap", "-lm mmap")]
    [InlineData("--mlock", "-lm mlock")]
    [InlineData("-dio", "-lm direct-io")]
    [InlineData("--direct-io", "-lm direct-io")]
    [InlineData("-ndio", "")]
    [InlineData("--no-direct-io", "")]
    public void EACH_RETIRED_FLAG_BECOMES_ITS_REPLACEMENT(string flag, string replacement)
    {
        var expected = replacement.Length == 0 ? [] : replacement.Split(' ');
        Assert.Equal(["--jinja", .. expected, "-c", "8192"], D.Rewrite(["--jinja", flag, "-c", "8192"], Pin));
    }

    //the table holds exactly the seven flags release 11071 removed. a row added or lost without a review of the help text fails here
    [Fact]
    public void THE_TABLE_HOLDS_THE_SEVEN_FLAGS_THE_PIN_RETIRED() =>
        Assert.Equal(["--no-mmap", "--mmap", "--mlock", "-dio", "--direct-io", "-ndio", "--no-direct-io"],
            D.Retired.Select(r => r.Flag));

    //an engine older than the retirement still accepts the old flag, so a profile for it keeps it
    [Fact]
    public void A_BUILD_BEFORE_THE_RETIREMENT_KEEPS_THE_OLD_FLAG()
    {
        string[] args = ["-np", "1", "--no-mmap", "--jinja"];
        Assert.Equal(args, D.Rewrite(args, Pin - 1));
    }

    //a profile already in the pin's dialect comes back unchanged, so running the update twice rewrites nothing the second time
    [Fact]
    public void A_PROFILE_IN_THE_NEW_DIALECT_IS_LEFT_ALONE()
    {
        string[] args = ["-np", "1", "-fa", "on", "-lm", "mmap", "--lazy-mode", "on", "--jinja"];
        Assert.Equal(args, D.Rewrite(args, Pin));
    }

    //the scaffold writes its load mode through the same table, so a new profile never gets a flag the pinned build refuses
    [Fact]
    public void THE_SCAFFOLD_WRITES_NO_FLAG_THE_PIN_RETIRED()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gatto-dialect-" + Guid.NewGuid().ToString("N"));
        try
        {
            var id = ModelScaffold.Create(dir, Path.Combine(AppContext.BaseDirectory, "Fixtures", "tiny.gguf"), port: 1235);
            var args = Model.Load(dir, id).Profile.ExtraArgs;

            Assert.Equal(args, D.Rewrite(args, Pin));
            Assert.Equal(D.LoadMode("none"), args.SkipWhile(a => a != D.LoadModeFlag).Take(2));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }
}
