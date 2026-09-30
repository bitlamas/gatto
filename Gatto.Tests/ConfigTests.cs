using Gatto.Core.Home;
using Gatto.Tests.Support;
using System.Text.Json;

namespace Gatto.Tests;

[Collection(SubstDriveCollection.Name)]
public class ConfigTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-home-").FullName;
    public void Dispose() => Directory.Delete(_home, recursive: true);

    [Fact]
    public void EnsureInitialized_creates_layout_and_never_overwrites()
    {
        GattoHome.EnsureInitialized(_home);
        Assert.True(Directory.Exists(Path.Combine(_home, "sessions")));
        Assert.True(Directory.Exists(Path.Combine(_home, "extensions")));
        Assert.True(Directory.Exists(Path.Combine(_home, "models")));
        Assert.True(Directory.Exists(Path.Combine(_home, "roles")));
        Assert.True(File.Exists(Path.Combine(_home, "gatto.json")));

        File.WriteAllText(Path.Combine(_home, "gatto.json"), "{\"endpoints\":{\"local\":{\"base_url\":\"http://x\"}},\"default_endpoint\":\"local\"}");
        GattoHome.EnsureInitialized(_home);   //this second call must leave the edited config alone.
        Assert.Contains("http://x", File.ReadAllText(Path.Combine(_home, "gatto.json")));
    }

    [Fact]
    public void Fresh_home_scaffolds_without_a_default_model()
    {
        GattoHome.EnsureInitialized(_home);
        var json = File.ReadAllText(Path.Combine(_home, "gatto.json"));
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.TryGetProperty("default_model", out _),
            "a fresh home must be explicitly unconfigured, not wrongly configured (spec 5.1)");
        //the scaffold keeps its starter local endpoint even with no default_model
        Assert.True(doc.RootElement.TryGetProperty("endpoints", out var eps));
        Assert.True(eps.TryGetProperty("local", out _));
    }

    private void WriteReasoningConfig(string reasoningClause) =>
        File.WriteAllText(Path.Combine(_home, "gatto.json"),
            "{\"endpoints\":{\"local\":{\"base_url\":\"http://x\"}},\"default_endpoint\":\"local\"" + reasoningClause + "}");

    [Fact]
    public void Reasoning_defaults_to_collapsed()
    {
        WriteReasoningConfig("");
        Assert.Equal(ReasoningMode.Collapsed, GattoConfig.Load(_home).ReasoningMode);
    }

    [Theory]
    [InlineData("collapsed", ReasoningMode.Collapsed)]
    [InlineData("expanded", ReasoningMode.Expanded)]
    public void Reasoning_accepts_new_values(string v, ReasoningMode expected)
    {
        WriteReasoningConfig($",\"reasoning\":\"{v}\"");
        Assert.Equal(expected, GattoConfig.Load(_home).ReasoningMode);
    }

    [Fact]
    public void Mouse_defaults_to_true_and_wheel_lines_to_3()
    {
        WriteReasoningConfig("");
        var c = GattoConfig.Load(_home);
        Assert.True(c.Mouse);
        Assert.Equal(3, c.WheelLines);
    }

    [Fact]
    public void Mouse_and_wheel_lines_parse()
    {
        WriteReasoningConfig(",\"mouse\":false,\"wheel_lines\":5");
        var c = GattoConfig.Load(_home);
        Assert.False(c.Mouse);
        Assert.Equal(5, c.WheelLines);
    }

    [Fact]
    public void Wheel_lines_below_1_throws()
    {
        WriteReasoningConfig(",\"wheel_lines\":0");
        Assert.Throws<GattoConfigException>(() => GattoConfig.Load(_home));
    }

    [Fact]
    public void Mouse_wrong_type_throws()
    {
        WriteReasoningConfig(",\"mouse\":\"yes\"");
        Assert.Throws<GattoConfigException>(() => GattoConfig.Load(_home));
    }

    [Fact]
    public void Mouse_true_with_alt_screen_false_is_NOT_an_error()   //this combination is legal and simply gives no mouse, so loading must not throw.
    {
        WriteReasoningConfig(",\"mouse\":true,\"alt_screen\":false");
        var c = GattoConfig.Load(_home);
        Assert.True(c.Mouse);
        Assert.False(c.AltScreen);
    }

    [Fact]
    public void Copy_on_select_defaults_to_false()
    {
        WriteReasoningConfig("");
        Assert.False(GattoConfig.Load(_home).CopyOnSelect);
    }

    private void WriteAutoCompactConfig(string clause) =>
        File.WriteAllText(Path.Combine(_home, "gatto.json"),
            "{\"endpoints\":{\"local\":{\"base_url\":\"http://x\"}},\"default_endpoint\":\"local\"" + clause + "}");

    [Fact]
    public void AutoCompact_Absent_DefaultsToPoint8()
    {
        WriteAutoCompactConfig("");
        Assert.Equal(0.8, GattoConfig.Load(_home).AutoCompact);
    }

    [Fact]
    public void AutoCompact_False_Disables()
    {
        WriteAutoCompactConfig(",\"auto_compact\": false");
        Assert.Null(GattoConfig.Load(_home).AutoCompact);
    }

    [Theory]
    [InlineData("0.5", 0.5)]
    [InlineData("0.95", 0.95)]
    public void AutoCompact_BoundaryValues_Accepted(string raw, double expected)
    {
        WriteAutoCompactConfig($",\"auto_compact\": {raw}");
        Assert.Equal(expected, GattoConfig.Load(_home).AutoCompact);
    }

    [Theory]
    [InlineData("0.49")]
    [InlineData("0.96")]
    [InlineData("true")]       //a JSON true must be rejected, coercing it to 1.0 would read as 100%
    [InlineData("\"0.8\"")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[]")]
    public void AutoCompact_Invalid_RejectsNamingTheKey(string raw)
    {
        WriteAutoCompactConfig($",\"auto_compact\": {raw}");
        var ex = Assert.Throws<GattoConfigException>(() => GattoConfig.Load(_home));
        Assert.Contains("auto_compact", ex.Message);
    }

    [Fact]
    public void Copy_on_select_parses()
    {
        WriteReasoningConfig(",\"copy_on_select\":true");
        Assert.True(GattoConfig.Load(_home).CopyOnSelect);
    }

    [Fact]
    public void Copy_on_select_wrong_type_throws()
    {
        WriteReasoningConfig(",\"copy_on_select\":\"yes\"");
        Assert.Throws<GattoConfigException>(() => GattoConfig.Load(_home));
    }

    [Fact]
    public void Copy_on_select_true_with_mouse_false_is_NOT_an_error()   //the pair is legal, and copy-on-select is simply a no-op when the mouse is off.
    {
        WriteReasoningConfig(",\"copy_on_select\":true,\"mouse\":false");
        var c = GattoConfig.Load(_home);
        Assert.True(c.CopyOnSelect);
        Assert.False(c.Mouse);
    }

    [Theory]   //legacy values must throw and name the new value, a silent map or a warning is not enough
    [InlineData("show", "expanded")]
    [InlineData("hide", "collapsed")]
    public void Reasoning_legacy_values_throw_with_rename_hint(string legacy, string mapped)
    {
        WriteReasoningConfig($",\"reasoning\":\"{legacy}\"");
        var ex = Assert.Throws<GattoConfigException>(() => GattoConfig.Load(_home));
        Assert.Contains(mapped, ex.Message);
    }

    [Fact]
    public void Reasoning_rejects_unknown_value()
    {
        WriteReasoningConfig(",\"reasoning\":\"bogus\"");
        var ex = Assert.Throws<GattoConfigException>(() => GattoConfig.Load(_home));
        Assert.Contains("collapsed", ex.Message);
        Assert.Contains("expanded", ex.Message);
    }

    //a fresh home must have no GATTO.md, and context collection there finds no files. a home without that file is legal, the row pins the clean state
    [Fact]
    public void EnsureInitialized_writes_no_GATTO_md_and_the_walk_finds_nothing()
    {
        GattoHome.EnsureInitialized(_home);

        var path = Path.Combine(_home, "GATTO.md");
        Assert.False(File.Exists(path));

        //assert the home default's own path in both directions, the temp directory's ancestors are real user folders. an isolated drive root keeps unrelated files out
        using var root = SubstRoot.Mount();
        var project = root.Dir("nowalk");

        static bool Same(string a, string b) =>
            string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

        File.WriteAllText(path, "# a home file the user wrote");
        Assert.Contains(ContextFiles.Collect(project, compat: false, path), f => Same(f.Path, path));

        File.Delete(path);
        Assert.DoesNotContain(ContextFiles.Collect(project, compat: false, path), f => Same(f.Path, path));
    }

    //an existing GATTO.md must be left alone, the check catches a future tidy-up that deletes it
    [Fact]
    public void EnsureInitialized_never_touches_an_existing_GATTO_md()
    {
        GattoHome.EnsureInitialized(_home);
        var path = Path.Combine(_home, "GATTO.md");
        var sentinel = "# my hand-edited project context\nDo not touch this file.";
        File.WriteAllText(path, sentinel);

        GattoHome.EnsureInitialized(_home);   //this call must leave the user's file unchanged.
        Assert.Equal(sentinel, File.ReadAllText(path));

        GattoHome.EnsureInitialized(_home);   //the second call must also leave the file byte-for-byte identical.
        Assert.Equal(sentinel, File.ReadAllText(path));
    }

    [Fact]
    public void Load_parses_endpoints_and_defaults()
    {
        //the scaffold must not name a model, DefaultModel stays null on a fresh home
        GattoHome.EnsureInitialized(_home);
        var cfg = GattoConfig.Load(_home);
        Assert.Equal("local", cfg.DefaultEndpoint);
        Assert.Null(cfg.DefaultModel);
        Assert.Equal("http://127.0.0.1:1235", cfg.Endpoints["local"].BaseUrl);
    }

    [Theory]
    [InlineData("on")]
    [InlineData("off")]
    public void Load_parses_think_default(string value)
    {
        Directory.CreateDirectory(_home);
        File.WriteAllText(Path.Combine(_home, "gatto.json"),
            "{\"endpoints\":{\"local\":{\"base_url\":\"http://x\"}},\"default_endpoint\":\"local\",\"think\":\"" + value + "\"}");
        Assert.Equal(value, GattoConfig.Load(_home).Think);
    }

    [Fact]
    public void Load_absent_think_isNull()
    {
        GattoHome.EnsureInitialized(_home);
        Assert.Null(GattoConfig.Load(_home).Think);
    }

    [Fact]
    public void Load_altScreen_and_dumpOnExit_default_on_and_off()
    {
        GattoHome.EnsureInitialized(_home);
        var cfg = GattoConfig.Load(_home);
        Assert.True(cfg.AltScreen);
        Assert.False(cfg.DumpOnExit);
    }

    [Theory]
    [InlineData("false", false, true)]
    [InlineData("true", true, true)]
    public void Load_parses_altScreen(string json, bool expected, bool _)
    {
        Directory.CreateDirectory(_home);
        File.WriteAllText(Path.Combine(_home, "gatto.json"),
            "{\"endpoints\":{\"local\":{\"base_url\":\"http://x\"}},\"default_endpoint\":\"local\",\"alt_screen\":" + json + "}");
        Assert.Equal(expected, GattoConfig.Load(_home).AltScreen);
    }

    [Fact]
    public void Load_parses_dumpOnExit_true()
    {
        Directory.CreateDirectory(_home);
        File.WriteAllText(Path.Combine(_home, "gatto.json"),
            "{\"endpoints\":{\"local\":{\"base_url\":\"http://x\"}},\"default_endpoint\":\"local\",\"dump_on_exit\":true}");
        Assert.True(GattoConfig.Load(_home).DumpOnExit);
    }

    //the key is off unless the file turns it on, a stop on every quit would end the server under a second window
    [Fact]
    public void Load_stopServerOnExit_defaults_off_and_parses_true()
    {
        GattoHome.EnsureInitialized(_home);
        Assert.False(GattoConfig.Load(_home).StopServerOnExit);
        File.WriteAllText(Path.Combine(_home, "gatto.json"),
            """{"endpoints":{"local":{"base_url":"http://x"}},"default_endpoint":"local","stop_server_on_exit":true}""");
        Assert.True(GattoConfig.Load(_home).StopServerOnExit);
    }

    [Fact]
    public void Load_rejects_stopServerOnExit_of_the_wrong_type()
    {
        Directory.CreateDirectory(_home);
        File.WriteAllText(Path.Combine(_home, "gatto.json"),
            """{"endpoints":{"local":{"base_url":"http://x"}},"default_endpoint":"local","stop_server_on_exit":"yes"}""");
        var ex = Assert.Throws<GattoConfigException>(() => GattoConfig.Load(_home));
        Assert.Contains("stop_server_on_exit", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_rejects_altScreen_of_the_wrong_type()
    {
        Directory.CreateDirectory(_home);
        File.WriteAllText(Path.Combine(_home, "gatto.json"),
            "{\"endpoints\":{\"local\":{\"base_url\":\"http://x\"}},\"default_endpoint\":\"local\",\"alt_screen\":\"yes\"}");
        var ex = Assert.Throws<GattoConfigException>(() => GattoConfig.Load(_home));
        Assert.Contains("alt_screen", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_rejects_invalid_think()
    {
        Directory.CreateDirectory(_home);
        File.WriteAllText(Path.Combine(_home, "gatto.json"),
            """{"endpoints":{"local":{"base_url":"http://x"}},"default_endpoint":"local","think":"maybe"}""");
        var ex = Assert.Throws<GattoConfigException>(() => GattoConfig.Load(_home));
        Assert.Contains("think", ex.Message);
    }

    [Fact]
    public void Load_rejects_unknown_keys()
    {
        Directory.CreateDirectory(_home);
        File.WriteAllText(Path.Combine(_home, "gatto.json"),
            "{\"endpoints\":{\"local\":{\"base_url\":\"http://x\"}},\"default_endpoint\":\"local\",\"surprise\":1}");
        var ex = Assert.Throws<GattoConfigException>(() => GattoConfig.Load(_home));
        Assert.Contains("surprise", ex.Message);
    }

    [Fact]
    public void Load_rejects_default_endpoint_not_defined()
    {
        Directory.CreateDirectory(_home);
        File.WriteAllText(Path.Combine(_home, "gatto.json"),
            "{\"endpoints\":{\"local\":{\"base_url\":\"http://x\"}},\"default_endpoint\":\"cloud\"}");
        var ex = Assert.Throws<GattoConfigException>(() => GattoConfig.Load(_home));
        Assert.Contains("cloud", ex.Message);
    }

    [Fact]
    public void Load_missing_file_names_the_path()
    {
        var ex = Assert.Throws<GattoConfigException>(() => GattoConfig.Load(_home));
        Assert.Contains("gatto.json", ex.Message);
    }

    [Fact]
    public void Load_rejects_non_object_root()
    {
        Directory.CreateDirectory(_home);
        File.WriteAllText(Path.Combine(_home, "gatto.json"), "[1,2,3]");
        var ex = Assert.Throws<GattoConfigException>(() => GattoConfig.Load(_home));
        Assert.Contains("JSON object", ex.Message);
    }

    [Fact]
    public void Load_rejects_unknown_endpoint_key()
    {
        Directory.CreateDirectory(_home);
        File.WriteAllText(Path.Combine(_home, "gatto.json"),
            "{\"endpoints\":{\"local\":{\"base_url\":\"http://x\",\"portt\":1}},\"default_endpoint\":\"local\"}");
        var ex = Assert.Throws<GattoConfigException>(() => GattoConfig.Load(_home));
        Assert.Contains("portt", ex.Message);
    }

    [Fact]
    public void Load_rejects_wrong_typed_context()
    {
        Directory.CreateDirectory(_home);
        File.WriteAllText(Path.Combine(_home, "gatto.json"),
            "{\"endpoints\":{\"local\":{\"base_url\":\"http://x\",\"context\":\"big\"}},\"default_endpoint\":\"local\"}");
        var ex = Assert.Throws<GattoConfigException>(() => GattoConfig.Load(_home));
        Assert.Contains("wrong type", ex.Message);
    }

    [Fact]
    public void Load_rejects_endpoint_value_that_is_not_an_object()
    {
        Directory.CreateDirectory(_home);
        File.WriteAllText(Path.Combine(_home, "gatto.json"),
            "{\"endpoints\":{\"local\":\"http://x\"},\"default_endpoint\":\"local\"}");
        var ex = Assert.Throws<GattoConfigException>(() => GattoConfig.Load(_home));
        Assert.Contains("must be a JSON object", ex.Message);
    }

    [Fact]
    public void Load_rejects_fractional_context()
    {
        Directory.CreateDirectory(_home);
        File.WriteAllText(Path.Combine(_home, "gatto.json"),
            "{\"endpoints\":{\"local\":{\"base_url\":\"http://x\",\"context\":1.5}},\"default_endpoint\":\"local\"}");
        var ex = Assert.Throws<GattoConfigException>(() => GattoConfig.Load(_home));
        Assert.Contains("wrong type", ex.Message);
    }

    [Fact]
    public void Load_rejects_scheme_less_base_url()
    {
        Directory.CreateDirectory(_home);
        File.WriteAllText(Path.Combine(_home, "gatto.json"),
            "{\"endpoints\":{\"local\":{\"base_url\":\"127.0.0.1:1235\"}},\"default_endpoint\":\"local\"}");
        var ex = Assert.Throws<GattoConfigException>(() => GattoConfig.Load(_home));
        Assert.Contains("absolute http", ex.Message);
    }

    [Fact]
    public void Load_parses_endpoint_thinking_map_including_explicit_null()
    {
        Directory.CreateDirectory(_home);
        File.WriteAllText(Path.Combine(_home, "gatto.json"),
            """{"endpoints":{"local":{"base_url":"http://x","thinking":{"high":{"reasoning_effort":"high"},"none":null}}},"default_endpoint":"local"}""");
        var cfg = GattoConfig.Load(_home);

        var thinking = cfg.Endpoints["local"].Thinking;
        Assert.NotNull(thinking);
        Assert.Equal("high", thinking!["high"]!.Value.GetProperty("reasoning_effort").GetString());
        Assert.True(thinking.ContainsKey("none"));
        Assert.Null(thinking["none"]);
    }

    [Fact]
    public void Load_rejects_endpoint_thinking_wrong_type()
    {
        Directory.CreateDirectory(_home);
        File.WriteAllText(Path.Combine(_home, "gatto.json"),
            """{"endpoints":{"local":{"base_url":"http://x","thinking":"nope"}},"default_endpoint":"local"}""");
        var ex = Assert.Throws<GattoConfigException>(() => GattoConfig.Load(_home));
        Assert.Contains("thinking", ex.Message);
    }

    [Fact]
    public void Load_endpoint_thinking_empty_map_parses_as_empty_not_null()
    {
        Directory.CreateDirectory(_home);
        File.WriteAllText(Path.Combine(_home, "gatto.json"),
            """{"endpoints":{"local":{"base_url":"http://x","thinking":{}}},"default_endpoint":"local"}""");
        var cfg = GattoConfig.Load(_home);

        Assert.NotNull(cfg.Endpoints["local"].Thinking);
        Assert.Empty(cfg.Endpoints["local"].Thinking!);
    }

    [Fact]
    public void Load_rejects_endpoint_thinking_value_wrong_type()
    {
        Directory.CreateDirectory(_home);
        File.WriteAllText(Path.Combine(_home, "gatto.json"),
            """{"endpoints":{"local":{"base_url":"http://x","thinking":{"high":"nope"}}},"default_endpoint":"local"}""");
        var ex = Assert.Throws<GattoConfigException>(() => GattoConfig.Load(_home));
        Assert.Contains("high", ex.Message);
    }

    [Fact]
    public void Load_local_endpoint_without_base_url_loads_with_null_base_url()
    {
        Directory.CreateDirectory(_home);
        File.WriteAllText(Path.Combine(_home, "gatto.json"),
            """{"endpoints":{"local":{}},"default_endpoint":"local"}""");
        var cfg = GattoConfig.Load(_home);
        Assert.Null(cfg.Endpoints["local"].BaseUrl);
    }

    [Fact]
    public void Load_non_local_endpoint_without_base_url_still_throws()
    {
        Directory.CreateDirectory(_home);
        File.WriteAllText(Path.Combine(_home, "gatto.json"),
            """{"endpoints":{"cloud":{}},"default_endpoint":"cloud"}""");
        var ex = Assert.Throws<GattoConfigException>(() => GattoConfig.Load(_home));
        Assert.Contains("cloud", ex.Message);
        Assert.Contains("base_url", ex.Message);
    }

    [Fact]
    public void Load_llama_server_and_context_files_default_when_absent()
    {
        Directory.CreateDirectory(_home);
        File.WriteAllText(Path.Combine(_home, "gatto.json"),
            "{\"endpoints\":{\"local\":{\"base_url\":\"http://x\"}},\"default_endpoint\":\"local\"}");
        var cfg = GattoConfig.Load(_home);
        Assert.Null(cfg.LlamaServer);
        Assert.False(cfg.ContextCompat);
        Assert.True(cfg.ContextHome);
    }

    [Fact]
    public void Load_parses_llama_server_and_context_files_when_present()
    {
        Directory.CreateDirectory(_home);
        File.WriteAllText(Path.Combine(_home, "gatto.json"),
            """{"endpoints":{"local":{"base_url":"http://x"}},"default_endpoint":"local","llama_server":"C:\\llama\\llama-server.exe","context_files":{"compat":true,"home":false}}""");
        var cfg = GattoConfig.Load(_home);
        Assert.Equal("C:\\llama\\llama-server.exe", cfg.LlamaServer);
        Assert.True(cfg.ContextCompat);
        Assert.False(cfg.ContextHome);
    }

    //the old key use_default must fail as an unknown key, with no alias. the rejection proves nothing unless the renamed key home is accepted in the same file
    [Fact]
    public void Load_rejects_the_old_use_default_key_with_no_alias()
    {
        Directory.CreateDirectory(_home);
        File.WriteAllText(Path.Combine(_home, "gatto.json"),
            """{"endpoints":{"local":{"base_url":"http://x"}},"default_endpoint":"local","context_files":{"use_default":false}}""");
        var ex = Assert.Throws<GattoConfigException>(() => GattoConfig.Load(_home));
        Assert.Contains("unknown key in gatto.json context_files: use_default", ex.Message);
    }

    [Fact]
    public void Load_accepts_the_home_key_the_old_one_was_renamed_to()
    {
        Directory.CreateDirectory(_home);
        File.WriteAllText(Path.Combine(_home, "gatto.json"),
            """{"endpoints":{"local":{"base_url":"http://x"}},"default_endpoint":"local","context_files":{"home":false}}""");
        Assert.False(GattoConfig.Load(_home).ContextHome);
    }

    [Fact]
    public void Load_context_files_home_of_the_wrong_type_names_the_key()
    {
        Directory.CreateDirectory(_home);
        File.WriteAllText(Path.Combine(_home, "gatto.json"),
            """{"endpoints":{"local":{"base_url":"http://x"}},"default_endpoint":"local","context_files":{"home":"yes"}}""");
        var ex = Assert.Throws<GattoConfigException>(() => GattoConfig.Load(_home));
        Assert.Contains("context_files.home", ex.Message);
    }

    [Fact]
    public void Load_rejects_unknown_context_files_key()
    {
        Directory.CreateDirectory(_home);
        File.WriteAllText(Path.Combine(_home, "gatto.json"),
            """{"endpoints":{"local":{"base_url":"http://x"}},"default_endpoint":"local","context_files":{"surprise":1}}""");
        var ex = Assert.Throws<GattoConfigException>(() => GattoConfig.Load(_home));
        Assert.Contains("surprise", ex.Message);
    }

    [Fact]
    public void Load_rejects_wrong_typed_llama_server()
    {
        Directory.CreateDirectory(_home);
        File.WriteAllText(Path.Combine(_home, "gatto.json"),
            "{\"endpoints\":{\"local\":{\"base_url\":\"http://x\"}},\"default_endpoint\":\"local\",\"llama_server\":1}");
        var ex = Assert.Throws<GattoConfigException>(() => GattoConfig.Load(_home));
        Assert.Contains("llama_server", ex.Message);
    }

    [Fact]
    public void Load_rejects_wrong_typed_default_model()
    {
        Directory.CreateDirectory(_home);
        File.WriteAllText(Path.Combine(_home, "gatto.json"),
            "{\"endpoints\":{\"local\":{\"base_url\":\"http://x\"}},\"default_endpoint\":\"local\",\"default_model\":1235}");
        var ex = Assert.Throws<GattoConfigException>(() => GattoConfig.Load(_home));
        Assert.Equal("gatto.json has a \"default_model\" with the wrong type — must be a string", ex.Message);
    }

    [Fact]
    public void Load_rejects_reserved_key_in_endpoint_thinking_map_value()
    {
        Directory.CreateDirectory(_home);
        File.WriteAllText(Path.Combine(_home, "gatto.json"),
            """{"endpoints":{"local":{"base_url":"http://x","thinking":{"high":{"stream":true}}}},"default_endpoint":"local"}""");
        var ex = Assert.Throws<GattoConfigException>(() => GattoConfig.Load(_home));
        Assert.Contains("local", ex.Message);
        Assert.Contains("stream", ex.Message);
    }

    [Fact]
    public void Load_theme_defaults_to_auto_when_absent()
    {
        Directory.CreateDirectory(_home);
        File.WriteAllText(Path.Combine(_home, "gatto.json"),
            "{\"endpoints\":{\"local\":{\"base_url\":\"http://x\"}},\"default_endpoint\":\"local\"}");
        var cfg = GattoConfig.Load(_home);
        Assert.Equal("auto", cfg.Theme);
    }

    [Theory]
    [InlineData("dark")] [InlineData("light")] [InlineData("auto")]
    public void Load_parses_valid_theme(string theme)
    {
        Directory.CreateDirectory(_home);
        File.WriteAllText(Path.Combine(_home, "gatto.json"),
            $"{{\"endpoints\":{{\"local\":{{\"base_url\":\"http://x\"}}}},\"default_endpoint\":\"local\",\"theme\":\"{theme}\"}}");
        var cfg = GattoConfig.Load(_home);
        Assert.Equal(theme, cfg.Theme);
    }

    [Fact]
    public void Load_rejects_invalid_theme_and_lists_valid_values()
    {
        Directory.CreateDirectory(_home);
        File.WriteAllText(Path.Combine(_home, "gatto.json"),
            "{\"endpoints\":{\"local\":{\"base_url\":\"http://x\"}},\"default_endpoint\":\"local\",\"theme\":\"neon\"}");
        var ex = Assert.Throws<GattoConfigException>(() => GattoConfig.Load(_home));
        Assert.Contains("theme", ex.Message);
        Assert.Contains("dark", ex.Message);
        Assert.Contains("light", ex.Message);
        Assert.Contains("auto", ex.Message);
    }

    [Fact]
    public void Load_rejects_wrong_typed_theme()
    {
        Directory.CreateDirectory(_home);
        File.WriteAllText(Path.Combine(_home, "gatto.json"),
            "{\"endpoints\":{\"local\":{\"base_url\":\"http://x\"}},\"default_endpoint\":\"local\",\"theme\":1}");
        var ex = Assert.Throws<GattoConfigException>(() => GattoConfig.Load(_home));
        Assert.Contains("theme", ex.Message);
    }

    [Fact]
    public void Load_theme_is_a_recognized_top_key()
    {
        Directory.CreateDirectory(_home);
        File.WriteAllText(Path.Combine(_home, "gatto.json"),
            "{\"endpoints\":{\"local\":{\"base_url\":\"http://x\"}},\"default_endpoint\":\"local\",\"theme\":\"dark\"}");
        var cfg = GattoConfig.Load(_home);
        Assert.Equal("dark", cfg.Theme);
    }

    [Theory]
    [InlineData("\"off\"")] [InlineData("\"on\"")] [InlineData("\"sideways\"")] [InlineData("true")] [InlineData("1")]
    public void Load_regions_key_accepted_and_ignored_regardless_of_value(string rawValue)
    {
        //the regions key is inert, any old gatto.json with that key of any value or type must load rather than fail startup
        Directory.CreateDirectory(_home);
        File.WriteAllText(Path.Combine(_home, "gatto.json"),
            $"{{\"endpoints\":{{\"local\":{{\"base_url\":\"http://x\"}}}},\"default_endpoint\":\"local\",\"regions\":{rawValue}}}");
        GattoConfig.Load(_home);   //the key stays in the allowlist here, and is simply never read.
    }

    private GattoConfig LoadWith(string extra)
    {
        Directory.CreateDirectory(_home);
        File.WriteAllText(Path.Combine(_home, "gatto.json"),
            $"{{\"endpoints\":{{\"local\":{{\"base_url\":\"http://x\"}}}},\"default_endpoint\":\"local\"{extra}}}");
        return GattoConfig.Load(_home);
    }

    [Fact]
    public void Search_absent_yields_default_chain()
    {
        var cfg = LoadWith("");
        Assert.Equal(new[] { "ddg" }, cfg.Search.Providers);
        Assert.Null(cfg.Search.TavilyApiKey);
        Assert.Null(cfg.Search.SearxngUrl);
    }

    [Fact]
    public void Search_full_section_parses()
    {
        var cfg = LoadWith(
            ""","search":{"providers":["searxng","ddg","tavily"],"tavily":{"apiKey":"test-key-x"},"searxng":{"url":"http://10.0.0.50:8888/"}}""");
        Assert.Equal(new[] { "searxng", "ddg", "tavily" }, cfg.Search.Providers);
        Assert.Equal("test-key-x", cfg.Search.TavilyApiKey);
        Assert.Equal("http://10.0.0.50:8888/", cfg.Search.SearxngUrl);
    }

    [Fact]
    public void Search_providers_absent_defaults_to_ddg_but_settings_still_parse()
    {
        var cfg = LoadWith(""","search":{"tavily":{"apiKey":"test-key-x"}}""");
        Assert.Equal(new[] { "ddg" }, cfg.Search.Providers);
        Assert.Equal("test-key-x", cfg.Search.TavilyApiKey);
    }

    [Theory]
    [InlineData(""","search":{"providers":["bing"]}""", "invalid provider")]
    [InlineData(""","search":{"providers":[]}""", "must not be empty")]
    [InlineData(""","search":{"providers":["ddg","ddg"]}""", "listed twice")]
    [InlineData(""","search":{"providers":["tavily"]}""", "search.tavily.apiKey")]
    [InlineData(""","search":{"providers":["searxng"]}""", "search.searxng.url")]
    [InlineData(""","search":{"searxng":{"url":"not-a-url"},"providers":["searxng"]}""", "absolute http(s)")]
    [InlineData(""","search":{"banana":1}""", "unknown key")]
    [InlineData(""","search":3""", "wrong type")]
    [InlineData(""","search":{"providers":"ddg"}""", "must be an array")]
    [InlineData(""","search":{"tavily":{"apiKey":7},"providers":["tavily"]}""", "wrong type")]
    public void Search_invalid_sections_throw_friendly(string extra, string fragment)
    {
        var ex = Assert.Throws<GattoConfigException>(() => LoadWith(extra));
        Assert.Contains(fragment, ex.Message);
    }

    [Fact]
    public void Raw_root_is_retained_for_section_lookup()
    {
        var cfg = LoadWith(""","search":{"providers":["ddg"]}""");
        Assert.True(cfg.Raw!.Value.TryGetProperty("search", out var section));
        Assert.Equal(JsonValueKind.Object, section.ValueKind);
    }

    //a temp home whose gatto.json holds the given json
    private static string HomeWith(string json)
    {
        var home = Directory.CreateTempSubdirectory("gatto-home-").FullName;
        File.WriteAllText(Path.Combine(home, "gatto.json"), json);
        return home;
    }

    [Fact]
    public void Load_WeightsRoot_IsParsed()
    {
        var home = HomeWith("""
            {"endpoints":{"local":{"base_url":"http://127.0.0.1:1235"}},"default_endpoint":"local",
             "weights_root":"C:\\weights"}
            """);
        Assert.Equal(@"C:\weights", GattoConfig.Load(home).WeightsRoot);
    }

    //the old models_dir key has no fallback to weights_root, it fails as an unknown key and the error names weights_root
    [Fact]
    public void Load_ModelsDir_IsNotAKeyAnyMore()
    {
        var home = HomeWith("""
            {"endpoints":{"local":{"base_url":"http://127.0.0.1:1235"}},"default_endpoint":"local",
             "models_dir":"C:\\weights"}
            """);
        var ex = Assert.Throws<GattoConfigException>(() => GattoConfig.Load(home));
        Assert.Contains("models_dir", ex.Message, StringComparison.Ordinal);
        Assert.Contains("weights_root", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_WeightsRoot_Absent_IsNull()
    {
        var home = HomeWith("""{"endpoints":{"local":{"base_url":"http://127.0.0.1:1235"}},"default_endpoint":"local"}""");
        Assert.Null(GattoConfig.Load(home).WeightsRoot);
    }

    [Fact]
    public void Load_WeightsRoot_WrongType_ThrowsFriendly()
    {
        var home = HomeWith("""
            {"endpoints":{"local":{"base_url":"http://127.0.0.1:1235"}},"default_endpoint":"local","weights_root":42}
            """);
        var ex = Assert.Throws<GattoConfigException>(() => GattoConfig.Load(home));
        Assert.Contains("weights_root", ex.Message);
    }

    [Fact]
    public void Memory_DefaultsOn_AndTheBudgetIsUNSET_NotAConstant()
    {
        //null means unset here, that is the contract. a default would make a chosen 1000 look like no choice, so DerivedMemoryBudget supplies the effective number
        var c = LoadWith("");
        Assert.True(c.MemoryEnabled);
        Assert.Null(c.MemoryIndexBudget);
    }

    [Fact]
    public void Memory_ExplicitBudget_StillWinsOverTheDerivedDefault()
    {
        var c = LoadWith(""","memory":{"index_budget":250}""");
        Assert.Equal(250, c.MemoryIndexBudget);
    }

    [Fact]
    public void Memory_Disabled()
    {
        var c = LoadWith(""","memory":{"enabled":false}""");
        Assert.False(c.MemoryEnabled);
    }

    [Fact]
    public void Memory_BudgetOverride()
    {
        var c = LoadWith(""","memory":{"index_budget":2500}""");
        Assert.Equal(2500, c.MemoryIndexBudget);
    }

    [Fact]
    public void Memory_UnknownKey_Throws()
    {
        var ex = Assert.Throws<GattoConfigException>(() => LoadWith(""","memory":{"budgetz":1}"""));
        Assert.Contains("budgetz", ex.Message);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("\"big\"")]
    [InlineData("true")]
    public void Memory_BadBudget_Throws(string raw)
    {
        var ex = Assert.Throws<GattoConfigException>(() => LoadWith($$""","memory":{"index_budget":{{raw}}}"""));
        Assert.Contains("must be a number >= 1", ex.Message);
    }

    [Fact]
    public void Memory_WrongType_Throws()
    {
        var ex = Assert.Throws<GattoConfigException>(() => LoadWith(""","memory":5"""));
        Assert.Contains("must be an object", ex.Message);
    }

    //assert whole messages, a substring check stays green even when the error names the wrong place
    [Theory]
    [InlineData(""","memory":5""", "gatto.json has a \"memory\" with the wrong type — must be an object")]
    [InlineData(""","context_files":5""", "gatto.json has a \"context_files\" with the wrong type — must be an object")]
    //the expected message is spelled out by hand. a shared source cannot catch a wrong key list
    [InlineData(""","memory":{"nope":1}""", "unknown key in gatto.json memory: nope — known keys: enabled, index_budget")]
    [InlineData(""","context_files":{"nope":1}""", "unknown key in gatto.json context_files: nope — known keys: compat, home")]
    public void Sections_ReportTheirOwnFileAndName_Exactly(string fragment, string expected)
    {
        var ex = Assert.Throws<GattoConfigException>(() => LoadWith(fragment));
        Assert.Equal(expected, ex.Message);
    }

    [Fact]
    public void Journal_KeyIsGone_AndNowFailsAsAnUnknownKey()
    {
        //the top level is strict, an old config with a journal key must fail by name
        var ex = Assert.Throws<GattoConfigException>(() => LoadWith(""","journal":true"""));
        //assert the prefix only, pinning the full key list would duplicate the TopKeys pin and go red on unrelated key additions
        Assert.StartsWith("unknown key in gatto.json: journal — known keys:", ex.Message);
    }
}

//the reader of the consent key must ship with its writer, or a strict reader breaks the home the writer wrote.
public class UpdateCheckKeyTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-uck-").FullName;
    public void Dispose() { try { Directory.Delete(_home, true); } catch { } }

    private Gatto.Core.Home.GattoConfig Load(string json)
    {
        File.WriteAllText(Path.Combine(_home, "gatto.json"), json);
        return Gatto.Core.Home.GattoConfig.Load(_home);
    }

    private const string Base = "\"endpoints\":{\"local\":{\"base_url\":\"http://x\"}},\"default_endpoint\":\"local\"";

    [Fact]
    public void ABSENT_MEANS_NO_not_not_yet()
    {
        //a check nobody agreed to must never run. null is the third state, and no later code path may read it as permission.
        Assert.Null(Load("{" + Base + "}").UpdateCheck);
    }

    [Fact]
    public void THE_KEY_ROUND_TRIPS_BOTH_WAYS()
    {
        Assert.True(Load("{" + Base + ",\"update_check\":true}").UpdateCheck);
        Assert.False(Load("{" + Base + ",\"update_check\":false}").UpdateCheck);
    }

    [Fact]
    public void THE_WRITER_AND_THE_READER_SHIP_TOGETHER_or_the_home_bricks()
    {
        //the reader throws on any unknown key, a writer adding a key the reader lacks writes a config it refuses. the test asserts the round trip end to end
        Load("{" + Base + "}");
        Gatto.Core.Home.GattoConfigWriter.SetConsentKey(_home, "update_check", true);

        Assert.True(Gatto.Core.Home.GattoConfig.Load(_home).UpdateCheck);
    }

    [Fact]
    public void A_NON_BOOLEAN_IS_A_CONFIG_ERROR_never_a_silent_no()
    {
        var ex = Assert.Throws<Gatto.Core.Home.GattoConfigException>(
            () => Load("{" + Base + ",\"update_check\":\"yes\"}"));

        Assert.Contains("update_check", ex.Message, StringComparison.Ordinal);
    }
}

//the allowlist, the shipped schema and the reader must arrive in one commit, a partial change stops a home from launching
public class GlyphsKeyTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-glyphs-").FullName;
    public void Dispose() { try { Directory.Delete(_home, true); } catch { } }

    private Gatto.Core.Home.GattoConfig Load(string json)
    {
        File.WriteAllText(Path.Combine(_home, "gatto.json"), json);
        return Gatto.Core.Home.GattoConfig.Load(_home);
    }

    private const string Base = "\"endpoints\":{\"local\":{\"base_url\":\"http://x\"}},\"default_endpoint\":\"local\"";

    //an absent glyphs key means auto, the host decides. theme takes the same default
    [Fact]
    public void ABSENT_MEANS_AUTO()
    {
        Assert.Equal("auto", Load("{" + Base + "}").Glyphs);
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("unicode")]
    [InlineData("ascii")]
    public void THE_THREE_VALID_VALUES_ARE_READ(string value)
    {
        Assert.Equal(value, Load("{" + Base + ",\"glyphs\":\"" + value + "\"}").Glyphs);
    }

    //an invalid value must be rejected and name the valid three. a silent fallback would draw unreadable glyphs on the host the user edited the file to fix
    [Fact]
    public void AN_INVALID_VALUE_IS_REFUSED_AND_NAMES_THE_VALID_ONES()
    {
        var ex = Assert.Throws<Gatto.Core.Home.GattoConfigException>(
            () => Load("{" + Base + ",\"glyphs\":\"emoji\"}"));

        Assert.Contains("glyphs", ex.Message, StringComparison.Ordinal);
        Assert.Contains("auto, unicode, ascii", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void THE_WRONG_TYPE_IS_REFUSED()
    {
        var ex = Assert.Throws<Gatto.Core.Home.GattoConfigException>(
            () => Load("{" + Base + ",\"glyphs\":true}"));

        Assert.Contains("glyphs", ex.Message, StringComparison.Ordinal);
    }
}
