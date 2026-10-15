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
    public void A_CHECKPOINTS_KEY_IS_UNKNOWN()
    {
        Write("bad", """{ "checkpoints": true }""");

        var ex = Assert.Throws<GattoConfigException>(() => RoleFile.Load(_rolesDir, "bad"));

        Assert.Equal("unknown key in role 'bad': checkpoints", ex.Message);
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
        Assert.Null(role.Append);
        Assert.False(role.Raw);
    }

    [Fact]
    public void RAW_ROUND_TRIPS()
    {
        Write("raw", """{ "raw": true }""");
        Write("cooked", """{ "raw": false }""");

        Assert.True(RoleFile.Load(_rolesDir, "raw").Raw);
        Assert.False(RoleFile.Load(_rolesDir, "cooked").Raw);
    }

    [Fact]
    public void A_WRONG_TYPED_RAW_FAILS_WITH_THE_KEY()
    {
        Write("bad", """{ "raw": "yes" }""");

        var ex = Assert.Throws<GattoConfigException>(() => RoleFile.Load(_rolesDir, "bad"));

        Assert.Contains("\"raw\"", ex.Message);
        Assert.Contains("must be true or false", ex.Message);
    }

    [Fact]
    public void THE_CALLABLE_KEYS_ROUND_TRIP()
    {
        Write("callee", """{ "description": "d", "tools": ["read_file", "grep"], "turns": 3, "context_files": false }""");

        var role = RoleFile.Load(_rolesDir, "callee");

        Assert.Equal("d", role.Description);
        Assert.Equal(new[] { "read_file", "grep" }, role.Tools);
        Assert.Equal(3, role.Turns);
        Assert.False(role.ContextFiles);
    }

    [Fact]
    public void A_BARE_ROLE_HAS_THE_CALLABLE_DEFAULTS()
    {
        Write("bare", "{}");

        var role = RoleFile.Load(_rolesDir, "bare");

        Assert.Null(role.Description);
        Assert.Null(role.Tools);
        Assert.Equal(10, role.Turns);
        Assert.True(role.ContextFiles);
    }

    [Fact]
    public void AN_EMPTY_TOOLS_ARRAY_LOADS_AS_NONE()
    {
        Write("mute", """{ "tools": [] }""");

        var role = RoleFile.Load(_rolesDir, "mute");

        Assert.NotNull(role.Tools);
        Assert.Empty(role.Tools!);
    }

    [Fact]
    public void A_TOOLS_VALUE_THAT_IS_NOT_AN_ARRAY_IS_REFUSED()
    {
        Write("bad", """{ "tools": "grep" }""");

        var ex = Assert.Throws<GattoConfigException>(() => RoleFile.Load(_rolesDir, "bad"));

        Assert.Contains("'bad'", ex.Message);
        Assert.Contains("\"tools\" of \"grep\"", ex.Message);
    }

    [Fact]
    public void A_NULL_DESCRIPTION_LOADS_AS_UNSET()
    {
        Write("quiet", """{ "description": null }""");

        Assert.Null(RoleFile.Load(_rolesDir, "quiet").Description);
    }

    [Fact]
    public void A_SEVENTH_TOOL_NAME_IS_REFUSED()
    {
        Write("bad", """{ "tools": ["read_file", "frobnicate"] }""");

        var ex = Assert.Throws<GattoConfigException>(() => RoleFile.Load(_rolesDir, "bad"));

        Assert.Contains("'bad'", ex.Message);
        Assert.Contains("\"tools\"", ex.Message);
        Assert.Contains("frobnicate", ex.Message);
        foreach (var allowed in new[] { "read_file", "write_file", "edit_file", "glob", "grep", "shell" })
            Assert.Contains(allowed, ex.Message);
    }

    [Fact]
    public void RUN_AGENT_IN_TOOLS_IS_REFUSED()
    {
        Write("bad", """{ "tools": ["run_agent"] }""");

        var ex = Assert.Throws<GattoConfigException>(() => RoleFile.Load(_rolesDir, "bad"));

        Assert.Contains("'bad'", ex.Message);
        Assert.Contains("\"tools\"", ex.Message);
        Assert.Contains("run_agent", ex.Message);
    }

    [Fact]
    public void A_DUPLICATE_TOOL_IS_REFUSED()
    {
        Write("bad", """{ "tools": ["grep", "grep"] }""");

        var ex = Assert.Throws<GattoConfigException>(() => RoleFile.Load(_rolesDir, "bad"));

        Assert.Contains("'bad'", ex.Message);
        Assert.Contains("\"tools\"", ex.Message);
        Assert.Contains("twice", ex.Message);
        Assert.Contains("grep", ex.Message);
    }

    [Fact]
    public void TURNS_ZERO_IS_REFUSED()
    {
        Write("bad", """{ "turns": 0 }""");

        var ex = Assert.Throws<GattoConfigException>(() => RoleFile.Load(_rolesDir, "bad"));

        Assert.Contains("'bad'", ex.Message);
        Assert.Contains("\"turns\" of 0", ex.Message);
    }

    [Theory]
    [InlineData("\"ten\"")]
    [InlineData("1.5")]
    public void TURNS_NOT_AN_INTEGER_IS_REFUSED(string value)
    {
        Write("bad", $$"""{ "turns": {{value}} }""");

        var ex = Assert.Throws<GattoConfigException>(() => RoleFile.Load(_rolesDir, "bad"));

        Assert.Contains("'bad'", ex.Message);
        Assert.Contains($"\"turns\" of {value}", ex.Message);
    }

    [Fact]
    public void A_WRONG_TYPED_DESCRIPTION_IS_REFUSED()
    {
        Write("bad", """{ "description": 3 }""");

        var ex = Assert.Throws<GattoConfigException>(() => RoleFile.Load(_rolesDir, "bad"));

        Assert.Contains("'bad'", ex.Message);
        Assert.Contains("\"description\" of 3", ex.Message);
    }

    [Fact]
    public void A_WRONG_TYPED_CONTEXT_FILES_IS_REFUSED()
    {
        Write("bad", """{ "context_files": "no" }""");

        var ex = Assert.Throws<GattoConfigException>(() => RoleFile.Load(_rolesDir, "bad"));

        Assert.Contains("'bad'", ex.Message);
        Assert.Contains("\"context_files\" of \"no\"", ex.Message);
    }

    [Fact]
    public void A_MULTILINE_DESCRIPTION_IS_REFUSED()
    {
        //the tool lists each description on one line
        Write("bad", """{ "description": "one\ntwo" }""");

        var ex = Assert.Throws<GattoConfigException>(() => RoleFile.Load(_rolesDir, "bad"));

        Assert.Contains("'bad'", ex.Message);
        Assert.Contains("\"description\" of \"one\\ntwo\"", ex.Message);
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
