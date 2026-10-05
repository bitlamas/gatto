using System.Text.Json;
using Gatto.Core.Home;
using Gatto.Roles;

namespace Gatto.Tests;

public class RoleFileTests : IDisposable
{
    private readonly string _rolesDir = Directory.CreateTempSubdirectory("gatto-roles-").FullName;
    public void Dispose() => Directory.Delete(_rolesDir, recursive: true);

    private void Write(string name, string json) =>
        File.WriteAllText(Path.Combine(_rolesDir, $"{name}.json"), json);

    [Fact]
    public void A_ROLE_FILE_STILL_CARRYING_THE_RETIRED_PACK_KEY_FAILS_LOUD_AND_NAMES_IT()
    {
        //the allowlist already fails the retired pack key at load, so it needs no special case and no friendlier message
        Write("legacy", """{"pack":"qwen3.6-35b"}""");

        var ex = Assert.Throws<GattoConfigException>(() => RoleFile.Load(_rolesDir, "legacy"));

        Assert.Contains("unknown key", ex.Message);
        Assert.Contains("pack", ex.Message);
        Assert.Contains("legacy", ex.Message);
    }

    [Fact]
    public void ONE_MODEL_KEY_RESOLVES_ON_BOTH_ENDPOINT_KINDS_THROUGH_ONE_RESOLVER()
    {
        //one key and one resolver serve both endpoint kinds (the kind changes only the meaning of the resolved string)
        Write("coder", """{"model":"qwen3.6-35b"}""");
        var role = RoleFile.Load(_rolesDir, "coder");

        Assert.Equal("qwen3.6-35b", RoleFile.EffectiveModel(role, null, null));
        Assert.Equal("cli-wins", RoleFile.EffectiveModel(role, "cli-wins", null));
        Assert.Equal("qwen3.6-35b", RoleFile.EffectiveModel(role, null, "default-loses-to-the-role"));

        Write("bare", "{}");
        var bare = RoleFile.Load(_rolesDir, "bare");
        Assert.Equal("from-default-model", RoleFile.EffectiveModel(bare, null, "from-default-model"));

        //the removed twin must stay gone. a vestigial second resolver is a second home for drift.
        Assert.Null(typeof(RoleFile).GetMethod("EffectivePack"));
    }

    [Fact]
    public void Load_round_trips_every_field()
    {
        Write("coder", """
            {
              "model": "qwen3.6-35b",
              "endpoint": "local",
              "gates": ["grounding"],
              "checkpoints": true,
              "append": "discipline text",
              "thinking": "high",
              "sampling": { "temperature": 0.3 }
            }
            """);

        var role = RoleFile.Load(_rolesDir, "coder");

        Assert.Equal("coder", role.Name);
        Assert.Equal("qwen3.6-35b", role.Model);
        Assert.Equal("local", role.Endpoint);
        Assert.Equal(new[] { "grounding" }, role.Gates);
        Assert.True(role.Checkpoints);
        Assert.Equal("discipline text", role.Append);
        Assert.Equal(ThinkingLevel.High, role.ThinkingRequested);
    }

    [Fact]
    public void Load_leaves_thinking_null_when_key_absent()
    {
        //the loader maps an absent key to null, since the fallback needs model information the loader never sees
        Write("generalist", "{}");

        var role = RoleFile.Load(_rolesDir, "generalist");

        Assert.Null(role.ThinkingRequested);
    }

    [Fact]
    public void Load_bare_role_has_empty_gates_and_null_optionals()
    {
        Write("generalist", "{}");

        var role = RoleFile.Load(_rolesDir, "generalist");

        Assert.Empty(role.Gates);
        Assert.Null(role.Model);
        Assert.Null(role.Endpoint);
        Assert.False(role.Checkpoints);
        Assert.Null(role.Append);
    }

    [Fact]
    public void Load_accepts_empty_gates_array()
    {
        Write("bare", """{ "gates": [] }""");

        var role = RoleFile.Load(_rolesDir, "bare");

        Assert.Empty(role.Gates);
    }

    //sampling belongs to the model, so a role that still holds the key, well formed or not, loads without it and says nothing
    [Theory]
    [InlineData("""{ "sampling": { "temperature": 0.3, "top_p": 0.9 } }""")]
    [InlineData("""{ "sampling": "hot" }""")]
    [InlineData("""{ "sampling": { "model": "x" } }""")]
    public void Load_reads_a_role_that_still_holds_sampling_without_it(string json)
    {
        Write("coder", json);

        var role = RoleFile.Load(_rolesDir, "coder");

        Assert.Equal("coder", role.Name);
        Assert.DoesNotContain(typeof(RoleFile).GetProperties(), p => p.Name == "Sampling");
    }

    [Fact]
    public void Load_missing_file_names_the_path()
    {
        var ex = Assert.Throws<GattoConfigException>(() => RoleFile.Load(_rolesDir, "nosuch"));
        Assert.Contains("nosuch", ex.Message);
    }

    [Fact]
    public void Load_missing_roles_directory_names_the_path()
    {
        var missing = Path.Combine(_rolesDir, "does-not-exist");
        var ex = Assert.Throws<GattoConfigException>(() => RoleFile.Load(missing, "generalist"));
        Assert.Contains("generalist", ex.Message);
    }

    [Fact]
    public void Load_rejects_invalid_json()
    {
        Write("broken", "{ not json");
        var ex = Assert.Throws<GattoConfigException>(() => RoleFile.Load(_rolesDir, "broken"));
        Assert.Contains("broken", ex.Message);
    }

    [Fact]
    public void Load_rejects_non_object_root()
    {
        Write("arr", "[1,2,3]");
        var ex = Assert.Throws<GattoConfigException>(() => RoleFile.Load(_rolesDir, "arr"));
        Assert.Contains("JSON object", ex.Message);
    }

    [Fact]
    public void Load_rejects_unknown_key()
    {
        Write("weird", """{ "surprise": 1 }""");
        var ex = Assert.Throws<GattoConfigException>(() => RoleFile.Load(_rolesDir, "weird"));
        Assert.Contains("surprise", ex.Message);
    }

    [Fact]
    public void Load_rejects_wrong_typed_gates()
    {
        Write("bad", """{ "gates": "grounding" }""");
        var ex = Assert.Throws<GattoConfigException>(() => RoleFile.Load(_rolesDir, "bad"));
        Assert.Contains("gates", ex.Message);
    }

    [Fact]
    public void Load_rejects_non_string_gate_entry()
    {
        Write("bad", """{ "gates": [1] }""");
        var ex = Assert.Throws<GattoConfigException>(() => RoleFile.Load(_rolesDir, "bad"));
        Assert.Contains("gates", ex.Message);
    }

    [Fact]
    public void Load_rejects_wrong_typed_checkpoints()
    {
        Write("bad", """{ "checkpoints": "yes" }""");
        var ex = Assert.Throws<GattoConfigException>(() => RoleFile.Load(_rolesDir, "bad"));
        Assert.Contains("checkpoints", ex.Message);
    }

    [Fact]
    public void Load_rejects_wrong_typed_thinking()
    {
        Write("bad", """{ "thinking": 3 }""");
        var ex = Assert.Throws<GattoConfigException>(() => RoleFile.Load(_rolesDir, "bad"));
        Assert.Contains("thinking", ex.Message);
    }

    [Fact]
    public void Load_rejects_wrong_typed_model()
    {
        Write("bad", """{ "model": 5 }""");
        var ex = Assert.Throws<GattoConfigException>(() => RoleFile.Load(_rolesDir, "bad"));
        Assert.Contains("model", ex.Message);
    }



    [Fact]
    public void ListNames_returns_sorted_stems()
    {
        Write("oracle", "{}");
        Write("Coder", "{}");
        Write("generalist", "{}");

        var names = RoleFile.ListNames(_rolesDir);

        Assert.Equal(new[] { "Coder", "generalist", "oracle" }, names);
    }

    [Fact]
    public void ListNames_missing_directory_returns_empty()
    {
        var missing = Path.Combine(_rolesDir, "does-not-exist");
        Assert.Empty(RoleFile.ListNames(missing));
    }

    [Fact]
    public void ListNames_ignores_non_json_files()
    {
        Write("generalist", "{}");
        File.WriteAllText(Path.Combine(_rolesDir, "readme.txt"), "not a role");

        var names = RoleFile.ListNames(_rolesDir);

        Assert.Equal(new[] { "generalist" }, names);
    }

    //coverage for shipped-role materialization lives in ShippedExtensionsTests
}

public class NudgesTests
{
    [Fact]
    public void Parse_reads_full_shape()
    {
        using var doc = JsonDocument.Parse("""
            {
              "gates": ["grounding", "citation_ledger"],
              "append": "nudge discipline",
              "thinking_cap": "low"
            }
            """);

        var nudges = Nudges.Parse(doc.RootElement);

        Assert.Equal(new[] { "grounding", "citation_ledger" }, nudges.Gates);
        Assert.Equal("nudge discipline", nudges.Append);
        Assert.Equal(ThinkingLevel.Low, nudges.ThinkingCap);
    }

    [Fact]
    public void Parse_reads_empty_shape()
    {
        using var doc = JsonDocument.Parse("{}");

        var nudges = Nudges.Parse(doc.RootElement);

        Assert.Empty(nudges.Gates);
        Assert.Null(nudges.Append);
        Assert.Null(nudges.ThinkingCap);
    }

    [Fact]
    public void Parse_rejects_unknown_key()
    {
        using var doc = JsonDocument.Parse("""{ "surprise": 1 }""");
        var ex = Assert.Throws<GattoConfigException>(() => Nudges.Parse(doc.RootElement));
        Assert.Contains("surprise", ex.Message);
    }

    [Fact]
    public void Parse_rejects_non_object_root()
    {
        using var doc = JsonDocument.Parse("[1,2,3]");
        var ex = Assert.Throws<GattoConfigException>(() => Nudges.Parse(doc.RootElement));
        Assert.Contains("JSON object", ex.Message);
    }
}
