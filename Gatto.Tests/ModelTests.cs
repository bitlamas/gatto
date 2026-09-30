using System.Text.Json;
using Gatto.Core.Client;
using Gatto.Core.Home;
using Gatto.Roles;

namespace Gatto.Tests;

public class ModelTests : IDisposable
{
    private readonly string _modelsDir = Directory.CreateTempSubdirectory("gatto-models-").FullName;
    public void Dispose() => Directory.Delete(_modelsDir, recursive: true);

    private string ModelDir(string id)
    {
        var dir = Path.Combine(_modelsDir, id);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private void WriteProfile(string id, string json) =>
        File.WriteAllText(Path.Combine(ModelDir(id), "profile.json"), json);

    //loads a minimal valid profile with one extra key appended, for tests about that key.
    private Model LoadWith(string extra)
    {
        WriteProfile("p",
            "{\"files\": [{\"path\": \"C:\\\\m\\\\x.gguf\", \"active\": true}], \"port\": 1235, \"context\": 8192" + extra + "}");
        return Model.Load(_modelsDir, "p");
    }

    [Fact]
    public void Load_round_trips_every_field()
    {
        WriteProfile("qwen3.6-35b", """
            {
              "files": [{ "path": "C:\\models\\qwen3.6-35b.gguf", "active": true }],
              "port": 1235,
              "context": 32768,
              "gpu_layers": 40,
              "cache_type_k": "q8_0",
              "cache_type_v": "q8_0",
              "sampling": { "temperature": 0.7 },
              "thinking": { "high": { "reasoning_effort": "high" }, "none": null },
              "extra_args": ["--flash-attn", "on"],
              "seed": 12345
            }
            """);
        File.WriteAllText(Path.Combine(_modelsDir, "qwen3.6-35b", "system-append.md"), "model discipline text");
        File.WriteAllText(Path.Combine(_modelsDir, "qwen3.6-35b", "repair.json"), """{ "pattern": "retry" }""");
        File.WriteAllText(Path.Combine(_modelsDir, "qwen3.6-35b", "nudges.json"), """{ "append": "nudge text" }""");

        var model = Model.Load(_modelsDir, "qwen3.6-35b");

        Assert.Equal("qwen3.6-35b", model.Id);
        Assert.Equal("C:\\models\\qwen3.6-35b.gguf", model.Profile.ActivePath);
        Assert.Equal(1235, model.Profile.Port);
        Assert.Equal(32768, model.Profile.Context);
        Assert.Equal(40, model.Profile.GpuLayers);
        Assert.Equal("q8_0", model.Profile.CacheTypeK);
        Assert.Equal("q8_0", model.Profile.CacheTypeV);
        Assert.NotNull(model.Profile.Sampling);
        Assert.Equal(0.7, model.Profile.Sampling!.Value.GetProperty("temperature").GetDouble());
        Assert.NotNull(model.Profile.Thinking);
        Assert.True(model.Profile.Thinking!.ContainsKey("high"));
        Assert.Equal("high", model.Profile.Thinking["high"]!.Value.GetProperty("reasoning_effort").GetString());
        Assert.True(model.Profile.Thinking.ContainsKey("none"));
        Assert.Null(model.Profile.Thinking["none"]);
        Assert.Equal(new[] { "--flash-attn", "on" }, model.Profile.ExtraArgs);
        Assert.Equal(12345, model.Profile.Seed);
        Assert.Equal("model discipline text", model.SystemAppend);
        Assert.NotNull(model.Repair);
        Assert.Equal("retry", model.Repair!.Value.GetProperty("pattern").GetString());
        Assert.NotNull(model.Nudges);
        Assert.Equal("nudge text", model.Nudges!.Append);
    }

    [Fact]
    public void Load_reads_optional_llama_server_override()
    {
        //llama_server points a model at its own server binary, instead of the shared one
        WriteProfile("forked", """
            {
              "files": [{ "path": "m.gguf", "active": true }],
              "port": 1235,
              "context": 4096,
              "llama_server": "C:\\tools\\poolside-laguna\\llama-server.exe"
            }
            """);

        var model = Model.Load(_modelsDir, "forked");

        Assert.Equal("C:\\tools\\poolside-laguna\\llama-server.exe", model.Profile.LlamaServer);
    }

    [Fact]
    public void Load_reads_optional_api_key()
    {
        WriteProfile("keyed", """
            {
              "files": [{ "path": "m.gguf", "active": true }],
              "port": 1235,
              "context": 4096,
              "api_key": "s3cr3t-value"
            }
            """);

        var model = Model.Load(_modelsDir, "keyed");

        Assert.Equal("s3cr3t-value", model.Profile.ApiKey);
    }

    [Fact]
    public void Load_without_api_key_leaves_it_null()
    {
        //an api key is opt-in, leaving the field out is how a local model stays keyless
        WriteProfile("plain", """{ "files": [{ "path": "m.gguf", "active": true }], "port": 1235, "context": 4096 }""");

        Assert.Null(Model.Load(_modelsDir, "plain").Profile.ApiKey);
    }

    [Fact]
    public void Load_rejects_non_string_api_key()
    {
        WriteProfile("badkey", """{ "files": [{ "path": "m.gguf", "active": true }], "port": 1234, "context": 4096, "api_key": 7 }""");

        var ex = Assert.Throws<GattoConfigException>(() => Model.Load(_modelsDir, "badkey"));
        Assert.Contains("api_key", ex.Message);
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    public void Load_rejects_blank_api_key(string literal)
    {
        //a blank key is a config error, the server would keep the whitespace while the client's trimmed header 401s
        WriteProfile("blankkey", $$"""
            { "files": [{ "path": "m.gguf", "active": true }], "port": 1234, "context": 4096, "api_key": {{literal}} }
            """);

        var ex = Assert.Throws<GattoConfigException>(() => Model.Load(_modelsDir, "blankkey"));
        Assert.Contains("api_key", ex.Message);
    }

    [Fact]
    public void Load_trims_api_key()
    {
        //the stored key is trimmed (a header field value can't hold a key with edge whitespace)
        WriteProfile("padded", """
            { "files": [{ "path": "m.gguf", "active": true }], "port": 1234, "context": 4096, "api_key": "  s3cr3t-value  " }
            """);

        Assert.Equal("s3cr3t-value", Model.Load(_modelsDir, "padded").Profile.ApiKey);
    }

    //the sentinel must not turn up in the error, its uppercase and hyphens keep it clear of the id and path the message names
    [Theory]   //both spellings, plus --api-key-file, which gives the server a key the same way
    [InlineData("[\"--api-key\", \"LEAKED-VALUE\"]")]
    [InlineData("[\"--api-key=LEAKED-VALUE\"]")]
    [InlineData("[\"--api-key-file\", \"C:\\\\keys\\\\LEAKED-VALUE.txt\"]")]
    public void Load_rejects_an_api_key_flag_in_extra_args_when_api_key_is_also_set(string extraArgs)
    {
        //the last flag wins, the server takes the extra_args key and the client sends the field's, so every request 401s with no diagnostic
        WriteProfile("dupekey", $$"""
            {
              "files": [{ "path": "m.gguf", "active": true }], "port": 1234, "context": 4096,
              "api_key": "field-value",
              "extra_args": {{extraArgs}}
            }
            """);

        var ex = Assert.Throws<GattoConfigException>(() => Model.Load(_modelsDir, "dupekey"));
        Assert.Contains("extra_args", ex.Message);
        Assert.Contains("api_key", ex.Message);
        Assert.DoesNotContain("field-value", ex.Message);    //the field's value is a credential, it must not appear in the message
        Assert.DoesNotContain("LEAKED-VALUE", ex.Message);   //the credential inside the matched token must not appear either
    }

    [Theory]   //all three spellings, with no api_key field in the profile
    [InlineData("[\"--api-key\", \"LEAKED-VALUE\"]")]
    [InlineData("[\"--api-key=LEAKED-VALUE\"]")]
    [InlineData("[\"--api-key-file\", \"C:\\\\keys\\\\LEAKED-VALUE.txt\"]")]
    public void Load_rejects_an_api_key_flag_in_extra_args_when_no_api_key_field_is_set(string extraArgs)
    {
        //the server is armed with a key and the client sends none, so every request gets a 401
        WriteProfile("no-key-field", $$"""
            {
              "files": [{ "path": "m.gguf", "active": true }], "port": 1234, "context": 4096,
              "extra_args": {{extraArgs}}
            }
            """);

        var ex = Assert.Throws<GattoConfigException>(() => Model.Load(_modelsDir, "no-key-field"));
        Assert.Contains("extra_args", ex.Message);
        Assert.Contains("api_key", ex.Message);
        Assert.DoesNotContain("LEAKED-VALUE", ex.Message);
    }

    [Theory]   //json allows these in one string, llama-server wants flag and value as two tokens
    [InlineData("[\"--host 0.0.0.0\"]", "--host", "0.0.0.0")]
    [InlineData("[\"--host=0.0.0.0\"]", "--host", "0.0.0.0")]
    [InlineData("[\"--ctx-size=2048\"]", "--ctx-size", "2048")]
    public void Load_rejects_a_flag_and_value_crammed_into_one_extra_args_entry(
        string extraArgs, string expectedFlag, string expectedValue)
    {
        //a crammed entry fails the server start with an opaque llama-server error, so refuse it at load
        WriteProfile("crammed", $$"""
            {
              "files": [{ "path": "m.gguf", "active": true }], "port": 1234, "context": 4096,
              "extra_args": {{extraArgs}}
            }
            """);

        var ex = Assert.Throws<GattoConfigException>(() => Model.Load(_modelsDir, "crammed"));
        Assert.Contains("extra_args", ex.Message);
        //the message spells out both tokens as they go in, so it names the fix rather than the fault
        Assert.Contains($"\"{expectedFlag}\", \"{expectedValue}\"", ex.Message);
    }

    [Theory]   //a value that only looks flag-ish must load, a false refusal would brick a working model
    [InlineData("[\"--prompt\", \"-x y\"]")]              //the value starts with a dash.
    [InlineData("[\"--grammar\", \"- item = thing\"]")]   //the value adds a space and an equals sign
    [InlineData("[\"--alias\", \"-\"]")]                  //a single dash as value.
    [InlineData("[\"--foo\", \"--\"]")]                   //a double dash as value.
    [InlineData("[\"-ot exps=CPU\"]")]                    //a crammed short flag stays unchecked, by design.
    public void Load_does_not_mistake_a_dash_leading_VALUE_for_a_crammed_flag(string extraArgs)
    {
        //the guard checks long flags only, a miss leaves llama-server's own error and a wrong refusal breaks a working model
        WriteProfile("dashvalue", $$"""
            {
              "files": [{ "path": "m.gguf", "active": true }], "port": 1234, "context": 4096,
              "extra_args": {{extraArgs}}
            }
            """);

        var model = Model.Load(_modelsDir, "dashvalue");   //loading these must succeed.

        //the args reach llama-server verbatim, the guard only refuses or accepts them
        Assert.NotEmpty(model.Profile.ExtraArgs);
    }

    [Fact]
    public void Load_leaves_values_containing_equals_or_spaces_alone()
    {
        //only a token starting with a dash is in flag position, judging every token would refuse values llama.cpp accepts
        WriteProfile("valuesok", """
            {
              "files": [{ "path": "m.gguf", "active": true }], "port": 1234, "context": 4096,
              "extra_args": ["-ot", "exps=CPU", "--override-kv", "tokenizer.ggml.add_bos_token=bool:false",
                             "--chat-template-file", "C:\\templates\\my template.jinja"]
            }
            """);

        var model = Model.Load(_modelsDir, "valuesok");

        Assert.Equal(
            new[] { "-ot", "exps=CPU", "--override-kv", "tokenizer.ggml.add_bos_token=bool:false",
                    "--chat-template-file", @"C:\templates\my template.jinja" },
            model.Profile.ExtraArgs);
    }

    [Fact]
    public void Load_rejects_a_crammed_api_key_with_the_REDACTING_message_not_the_split_one()
    {
        //the api_key branch must run before the crammed-entry rule, which quotes the token and would print the credential
        WriteProfile("crammedkey", """
            {
              "files": [{ "path": "m.gguf", "active": true }], "port": 1234, "context": 4096,
              "extra_args": ["--api-key=LEAKED-VALUE"]
            }
            """);

        var ex = Assert.Throws<GattoConfigException>(() => Model.Load(_modelsDir, "crammedkey"));

        Assert.DoesNotContain("LEAKED-VALUE", ex.Message);
        Assert.Contains("api_key", ex.Message);   //the message came from the redacting branch rather than the crammed-entry splitter
    }

    [Theory]
    [InlineData("--api-key=REALSECRET", "--api-key")]
    [InlineData("--api-key-file=C:\\keys\\REALSECRET.txt", "--api-key-file")]
    public void Load_redacts_the_value_of_an_inline_api_key_flag_in_its_error(string token, string flagName)
    {
        //the =value spelling makes the token itself the credential, so redact the value and keep the flag name
        WriteProfile("inlinekey", $$"""
            {
              "files": [{ "path": "m.gguf", "active": true }], "port": 1234, "context": 4096,
              "extra_args": [{{JsonSerializer.Serialize(token)}}]
            }
            """);

        var ex = Assert.Throws<GattoConfigException>(() => Model.Load(_modelsDir, "inlinekey"));

        Assert.DoesNotContain("REALSECRET", ex.Message);
        Assert.Contains($"{flagName}=***", ex.Message);
    }

    [Fact]
    public void Load_still_accepts_unrelated_extra_args_next_to_an_api_key()
    {
        //the guard must not become a general extra_args ban
        WriteProfile("okkey", """
            {
              "files": [{ "path": "m.gguf", "active": true }], "port": 1234, "context": 4096,
              "api_key": "s3cr3t-value",
              "extra_args": ["--jinja", "-fa", "on"]
            }
            """);

        var profile = Model.Load(_modelsDir, "okkey").Profile;

        Assert.Equal("s3cr3t-value", profile.ApiKey);
        Assert.Equal(new[] { "--jinja", "-fa", "on" }, profile.ExtraArgs);
    }

    [Fact]
    public void Profile_ToString_lists_every_member_and_redacts_the_secret_ones()
    {
        //every public member must show in ToString, and a name holding Key or Secret must not print its value
        var profile = new ModelProfile(
            Files: [new ModelFile("m.gguf", null, true)], Port: 1, Context: 2, GpuLayers: 3,
            CacheTypeK: "q8_0", CacheTypeV: "q8_0",
            Sampling: null, Thinking: null, ExtraArgs: new[] { "--jinja" },
            Seed: 7, LlamaServer: @"C:\tools\llama-server.exe",
            ReasoningHistory: Gatto.Core.Loop.ReasoningHistory.None,
            ApiKey: "s3cr3t-value");

        var text = profile.ToString();

        foreach (var prop in typeof(ModelProfile).GetProperties(
                     System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            Assert.Contains(prop.Name, text);

            //only the property name is matched, so a secret named Token or Password prints in full. a new secret needs Key or Secret in its name
            if (!prop.Name.Contains("Key", StringComparison.OrdinalIgnoreCase)
                && !prop.Name.Contains("Secret", StringComparison.OrdinalIgnoreCase))
                continue;

            var value = prop.GetValue(profile)?.ToString();
            if (!string.IsNullOrEmpty(value))
                Assert.DoesNotContain(value, text);
        }
    }

    [Fact]
    public void Profile_ToString_survives_a_null_extra_args()
    {
        //a report is built inside exception and diagnostic paths, so ToString must never throw
        var profile = new ModelProfile(
            Files: [new ModelFile("m.gguf", null, true)], Port: 1, Context: 2, GpuLayers: null,
            CacheTypeK: null, CacheTypeV: null, Sampling: null, Thinking: null,
            ExtraArgs: null!, ApiKey: "s3cr3t-value");

        var text = profile.ToString();

        Assert.Contains("ExtraArgs = [0 args]", text);
        Assert.DoesNotContain("s3cr3t-value", text);
    }

    [Fact]
    public void Profile_ToString_never_renders_the_api_key()
    {
        //the synthesized ToString would print the key into any log line, exception message or failing assert, so the override exists
        WriteProfile("keyed2", """
            { "files": [{ "path": "m.gguf", "active": true }], "port": 1235, "context": 4096, "api_key": "s3cr3t-value" }
            """);

        var text = Model.Load(_modelsDir, "keyed2").Profile.ToString();

        Assert.DoesNotContain("s3cr3t-value", text);
        Assert.Contains("***", text);
    }

    [Theory]   //only all and none parse, a sliding window over the reasoning defeats the prefix cache
    [InlineData("all", Gatto.Core.Loop.ReasoningHistory.All)]
    [InlineData("none", Gatto.Core.Loop.ReasoningHistory.None)]
    public void Load_reads_reasoning_history(string value, Gatto.Core.Loop.ReasoningHistory expected)
    {
        WriteProfile("rh", $$"""
            { "files": [{ "path": "m.gguf", "active": true }], "port": 1234, "context": 4096, "reasoning_history": "{{value}}" }
            """);

        Assert.Equal(expected, Model.Load(_modelsDir, "rh").Profile.ReasoningHistory);
    }

    [Fact]
    public void Load_reasoning_history_defaults_to_all()
    {
        //the default is all, which keeps the prefix stable and keeps the model thinking
        WriteProfile("rhdefault", """{ "files": [{ "path": "m.gguf", "active": true }], "port": 1234, "context": 4096 }""");

        Assert.Equal(Gatto.Core.Loop.ReasoningHistory.All, Model.Load(_modelsDir, "rhdefault").Profile.ReasoningHistory);
    }

    [Theory]
    [InlineData("some")]
    [InlineData("recent")]   //a profile naming recent must fail at load rather than quietly fall back to another value
    public void Load_rejects_an_unknown_reasoning_history(string value)
    {
        WriteProfile("rhbad", $$"""
            { "files": [{ "path": "m.gguf", "active": true }], "port": 1234, "context": 4096, "reasoning_history": "{{value}}" }
            """);

        var ex = Assert.Throws<GattoConfigException>(() => Model.Load(_modelsDir, "rhbad"));
        Assert.Contains("reasoning_history", ex.Message);
        Assert.Contains("\"all\"", ex.Message);
    }

    [Theory]   //the default_effort field is the stored form of /effort, written by ModelDefaultEffort.Set and read back at composition time
    [InlineData("none", Gatto.Roles.ThinkingLevel.None)]
    [InlineData("low", Gatto.Roles.ThinkingLevel.Low)]
    [InlineData("xhigh", Gatto.Roles.ThinkingLevel.XHigh)]
    public void Load_reads_default_effort(string value, Gatto.Roles.ThinkingLevel expected)
    {
        WriteProfile("de", $$"""
            { "files": [{ "path": "m.gguf", "active": true }], "port": 1234, "context": 4096, "default_effort": "{{value}}" }
            """);

        Assert.Equal(expected, Model.Load(_modelsDir, "de").Profile.DefaultEffort);
    }

    [Fact]
    public void Load_rejects_an_invalid_default_effort()
    {
        WriteProfile("debad", """
            { "files": [{ "path": "m.gguf", "active": true }], "port": 1234, "context": 4096, "default_effort": "extreme" }
            """);

        var ex = Assert.Throws<GattoConfigException>(() => Model.Load(_modelsDir, "debad"));
        Assert.Contains("default_effort", ex.Message);
    }

    [Fact]
    public void Load_rejects_a_non_string_default_effort()
    {
        WriteProfile("denum", """
            { "files": [{ "path": "m.gguf", "active": true }], "port": 1234, "context": 4096, "default_effort": 3 }
            """);

        var ex = Assert.Throws<GattoConfigException>(() => Model.Load(_modelsDir, "denum"));
        Assert.Contains("default_effort", ex.Message);
    }

    [Fact]
    public void Load_minimal_profile_has_null_optionals_and_empty_extra_args()
    {
        WriteProfile("bare", """{ "files": [{ "path": "m.gguf", "active": true }], "port": 1234, "context": 4096 }""");

        var model = Model.Load(_modelsDir, "bare");

        Assert.Null(model.Profile.GpuLayers);
        Assert.Null(model.Profile.CacheTypeK);
        Assert.Null(model.Profile.CacheTypeV);
        Assert.Null(model.Profile.Sampling);
        Assert.Null(model.Profile.Thinking);
        Assert.Empty(model.Profile.ExtraArgs);
        Assert.Null(model.Profile.Seed);
        Assert.Null(model.Profile.LlamaServer);
        Assert.Null(model.Profile.MemoryIndexBudget);
        Assert.Null(model.Profile.DefaultEffort);
        Assert.Null(model.SystemAppend);
        Assert.Null(model.Repair);
        Assert.Null(model.Nudges);
    }

    [Fact]
    public void Load_rejects_non_integer_seed()
    {
        WriteProfile("badseed", """{ "files": [{ "path": "m.gguf", "active": true }], "port": 1234, "context": 4096, "seed": "abc" }""");
        var ex = Assert.Throws<GattoConfigException>(() => Model.Load(_modelsDir, "badseed"));
        Assert.Contains("seed", ex.Message);
    }

    [Fact]
    public void Load_missing_model_dir_throws_friendly_error()
    {
        var ex = Assert.Throws<GattoConfigException>(() => Model.Load(_modelsDir, "nosuch"));
        var expectedProfilePath = Path.Combine(_modelsDir, "nosuch", "profile.json");
        Assert.Contains("nosuch", ex.Message);
        Assert.Contains(expectedProfilePath, ex.Message);
    }

    [Fact]
    public void Load_model_dir_without_profile_json_throws_friendly_error()
    {
        ModelDir("empty-model");

        var ex = Assert.Throws<GattoConfigException>(() => Model.Load(_modelsDir, "empty-model"));
        var expectedProfilePath = Path.Combine(_modelsDir, "empty-model", "profile.json");
        Assert.Contains("empty-model", ex.Message);
        Assert.Contains(expectedProfilePath, ex.Message);
    }

    [Fact]
    public void Load_profile_missing_port_names_key_and_path()
    {
        WriteProfile("bad", """{ "files": [{ "path": "m.gguf", "active": true }], "context": 4096 }""");
        var path = Path.Combine(_modelsDir, "bad", "profile.json");

        var ex = Assert.Throws<GattoConfigException>(() => Model.Load(_modelsDir, "bad"));

        Assert.Contains("port", ex.Message);
        Assert.Contains(path, ex.Message);
    }

    [Fact]
    public void Load_profile_missing_files_names_key_and_path()
    {
        WriteProfile("bad", """{ "port": 1234, "context": 4096 }""");
        var ex = Assert.Throws<GattoConfigException>(() => Model.Load(_modelsDir, "bad"));
        Assert.Contains("files", ex.Message);
    }

    //a profile with the retired model_path is refused at load, and the error names the accepted keys
    [Fact]
    public void Load_REFUSES_a_profile_still_carrying_the_retired_model_path()
    {
        WriteProfile("old", """{ "model_path": "m.gguf", "port": 1234, "context": 4096 }""");

        var ex = Assert.Throws<GattoConfigException>(() => Model.Load(_modelsDir, "old"));

        Assert.Contains("model_path", ex.Message);
        Assert.Contains("unknown key", ex.Message);
    }

    //exactly one file must be active, and the zero-active and two-active errors must tell the reader to pick one or delete one
    [Fact]
    public void Load_refuses_a_files_list_that_cannot_say_which_weights_are_in_use()
    {
        WriteProfile("none", """
            { "files": [{ "path": "a.gguf" }, { "path": "b.gguf" }], "port": 1, "context": 2 }
            """);
        WriteProfile("both", """
            { "files": [{ "path": "a.gguf", "active": true }, { "path": "b.gguf", "active": true }],
              "port": 1, "context": 2 }
            """);

        var noActive = Assert.Throws<GattoConfigException>(() => Model.Load(_modelsDir, "none"));
        var twoActive = Assert.Throws<GattoConfigException>(() => Model.Load(_modelsDir, "both"));

        Assert.Contains("no active file", noActive.Message);
        Assert.Contains("2 active files", twoActive.Message);
        //the two failure messages must never converge into one sentence.
        Assert.NotEqual(noActive.Message, twoActive.Message);
    }

    //source is optional, and a profile without it is the ordinary local case
    [Fact]
    public void Load_reads_the_optional_source_and_leaves_it_null_when_absent()
    {
        WriteProfile("fetched", """
            { "files": [{ "path": "m.gguf", "quant": "Q4_K_M", "active": true }],
              "source": { "repo_id": "unsloth/gemma-4-26B-A4B-it", "file": "m.gguf" },
              "port": 1, "context": 2 }
            """);
        WriteProfile("adopted", """{ "files": [{ "path": "m.gguf", "active": true }], "port": 1, "context": 2 }""");

        var fetched = Model.Load(_modelsDir, "fetched");
        Assert.Equal("unsloth/gemma-4-26B-A4B-it", fetched.Profile.Source!.RepoId);
        Assert.Equal("Q4_K_M", fetched.Profile.Files[0].Quant);
        Assert.Null(Model.Load(_modelsDir, "adopted").Profile.Source);
    }

    [Fact]
    public void Load_profile_missing_context_names_key_and_path()
    {
        WriteProfile("bad", """{ "files": [{ "path": "m.gguf", "active": true }], "port": 1234 }""");
        var ex = Assert.Throws<GattoConfigException>(() => Model.Load(_modelsDir, "bad"));
        Assert.Contains("context", ex.Message);
    }

    [Fact]
    public void Load_rejects_unknown_profile_key()
    {
        WriteProfile("bad", """{ "files": [{ "path": "m.gguf", "active": true }], "port": 1234, "context": 4096, "surprise": 1 }""");
        var ex = Assert.Throws<GattoConfigException>(() => Model.Load(_modelsDir, "bad"));
        Assert.Contains("surprise", ex.Message);
    }

    [Fact]
    public void Profile_MemoryIndexBudget_Parsed()
    {
        WriteProfile("p", """
            { "files": [{ "path": "m.gguf", "active": true }], "port": 1234, "context": 4096, "memory": { "index_budget": 800 } }
            """);

        Assert.Equal(800, Model.Load(_modelsDir, "p").Profile.MemoryIndexBudget);
    }

    [Fact]
    public void Profile_Memory_UnknownKey_Throws()
    {
        //enabled is a global consent switch, so a profile must not set it per model
        WriteProfile("bad", """
            { "files": [{ "path": "m.gguf", "active": true }], "port": 1234, "context": 4096, "memory": { "enabled": true } }
            """);

        var ex = Assert.Throws<GattoConfigException>(() => Model.Load(_modelsDir, "bad"));
        Assert.Contains("enabled", ex.Message);
    }

    //the section reader is shared with gatto.json, so the whole message is pinned with Equal rather than Contains
    [Fact]
    public void Profile_Memory_ErrorsNameTheModelAndPath_NotGattoJson()
    {
        WriteProfile("wrongtype", """
            { "files": [{ "path": "m.gguf", "active": true }], "port": 1234, "context": 4096, "memory": 5 }
            """);
        WriteProfile("unknown", """
            { "files": [{ "path": "m.gguf", "active": true }], "port": 1234, "context": 4096, "memory": { "nope": 1 } }
            """);

        var wrongType = Assert.Throws<GattoConfigException>(() => Model.Load(_modelsDir, "wrongtype"));
        var unknown = Assert.Throws<GattoConfigException>(() => Model.Load(_modelsDir, "unknown"));

        Assert.Equal(
            $"model 'wrongtype' profile at {Path.Combine(_modelsDir, "wrongtype", "profile.json")} "
            + "has a \"memory\" with the wrong type — must be an object", wrongType.Message);
        //the expected message spells the accepted keys out by hand, a computed list could not tell the model keys from the global ones
        Assert.Equal(
            $"unknown key in model 'unknown' profile at {Path.Combine(_modelsDir, "unknown", "profile.json")} "
            + "memory: nope — known keys: index_budget", unknown.Message);
    }

    [Fact]
    public void Load_rejects_invalid_json_profile()
    {
        WriteProfile("broken", "{ not json");
        var ex = Assert.Throws<GattoConfigException>(() => Model.Load(_modelsDir, "broken"));
        Assert.Contains("broken", ex.Message);
    }

    [Fact]
    public void Load_rejects_non_object_profile_root()
    {
        WriteProfile("arr", "[1,2,3]");
        var ex = Assert.Throws<GattoConfigException>(() => Model.Load(_modelsDir, "arr"));
        Assert.Contains("JSON object", ex.Message);
    }

    [Fact]
    public void Load_system_append_picked_up()
    {
        WriteProfile("p", """{ "files": [{ "path": "m.gguf", "active": true }], "port": 1234, "context": 4096 }""");
        File.WriteAllText(Path.Combine(_modelsDir, "p", "system-append.md"), "model notes");

        var model = Model.Load(_modelsDir, "p");

        Assert.Equal("model notes", model.SystemAppend);
    }

    [Fact]
    public void Load_absent_nudges_repair_scorecard_is_valid()
    {
        WriteProfile("p", """{ "files": [{ "path": "m.gguf", "active": true }], "port": 1234, "context": 4096 }""");
        //nudges.json, repair.json and scorecard.json are optional, loading must not throw when they are missing

        var model = Model.Load(_modelsDir, "p");

        Assert.Null(model.Nudges);
        Assert.Null(model.Repair);
    }

    [Fact]
    public void Load_scorecard_json_present_is_ignored_without_error()
    {
        WriteProfile("p", """{ "files": [{ "path": "m.gguf", "active": true }], "port": 1234, "context": 4096 }""");
        File.WriteAllText(Path.Combine(_modelsDir, "p", "scorecard.json"), """{ "score": 42 }""");

        var model = Model.Load(_modelsDir, "p");

        Assert.NotNull(model);
    }

    [Fact]
    public void Load_rejects_wrong_typed_gpu_layers()
    {
        WriteProfile("bad", """{ "files": [{ "path": "m.gguf", "active": true }], "port": 1234, "context": 4096, "gpu_layers": "forty" }""");
        var ex = Assert.Throws<GattoConfigException>(() => Model.Load(_modelsDir, "bad"));
        Assert.Contains("gpu_layers", ex.Message);
    }

    [Fact]
    public void Load_rejects_wrong_typed_cache_type_k()
    {
        WriteProfile("bad", """{ "files": [{ "path": "m.gguf", "active": true }], "port": 1234, "context": 4096, "cache_type_k": 5 }""");
        var ex = Assert.Throws<GattoConfigException>(() => Model.Load(_modelsDir, "bad"));
        Assert.Contains("cache_type_k", ex.Message);
    }

    [Fact]
    public void Load_rejects_wrong_typed_sampling()
    {
        WriteProfile("bad", """{ "files": [{ "path": "m.gguf", "active": true }], "port": 1234, "context": 4096, "sampling": "hot" }""");
        var ex = Assert.Throws<GattoConfigException>(() => Model.Load(_modelsDir, "bad"));
        Assert.Contains("sampling", ex.Message);
    }

    [Fact]
    public void Load_rejects_reserved_key_in_profile_sampling()
    {
        WriteProfile("bad", """{ "files": [{ "path": "m.gguf", "active": true }], "port": 1234, "context": 4096, "sampling": { "model": "x" } }""");
        var path = Path.Combine(_modelsDir, "bad", "profile.json");
        var ex = Assert.Throws<GattoConfigException>(() => Model.Load(_modelsDir, "bad"));
        Assert.Contains("bad", ex.Message);
        Assert.Contains(path, ex.Message);
        Assert.Contains("model", ex.Message);
    }

    [Fact]
    public void Load_rejects_reserved_key_in_thinking_map_value()
    {
        WriteProfile("bad", """{ "files": [{ "path": "m.gguf", "active": true }], "port": 1234, "context": 4096, "thinking": { "high": { "stream": true } } }""");
        var path = Path.Combine(_modelsDir, "bad", "profile.json");
        var ex = Assert.Throws<GattoConfigException>(() => Model.Load(_modelsDir, "bad"));
        Assert.Contains("bad", ex.Message);
        Assert.Contains(path, ex.Message);
        Assert.Contains("stream", ex.Message);
    }

    [Fact]
    public void Load_rejects_wrong_typed_thinking()
    {
        WriteProfile("bad", """{ "files": [{ "path": "m.gguf", "active": true }], "port": 1234, "context": 4096, "thinking": "high" }""");
        var ex = Assert.Throws<GattoConfigException>(() => Model.Load(_modelsDir, "bad"));
        Assert.Contains("thinking", ex.Message);
    }

    [Fact]
    public void Load_rejects_invalid_thinking_level_key()
    {
        WriteProfile("bad", """{ "files": [{ "path": "m.gguf", "active": true }], "port": 1234, "context": 4096, "thinking": { "maximum": {} } }""");
        var ex = Assert.Throws<GattoConfigException>(() => Model.Load(_modelsDir, "bad"));
        Assert.Contains("maximum", ex.Message);
    }

    [Fact]
    public void Load_rejects_wrong_typed_thinking_value()
    {
        WriteProfile("bad", """{ "files": [{ "path": "m.gguf", "active": true }], "port": 1234, "context": 4096, "thinking": { "high": 5 } }""");
        var ex = Assert.Throws<GattoConfigException>(() => Model.Load(_modelsDir, "bad"));
        Assert.Contains("thinking", ex.Message);
        Assert.Contains("high", ex.Message);
    }

    [Fact]
    public void Load_thinking_map_distinguishes_null_value_from_absent_key()
    {
        WriteProfile("p", """{ "files": [{ "path": "m.gguf", "active": true }], "port": 1234, "context": 4096, "thinking": { "high": null } }""");

        var model = Model.Load(_modelsDir, "p");

        Assert.True(model.Profile.Thinking!.ContainsKey("high"));
        Assert.Null(model.Profile.Thinking["high"]);
        Assert.False(model.Profile.Thinking.ContainsKey("low"));
    }

    [Fact]
    public void Load_empty_thinking_map_is_valid()
    {
        WriteProfile("p", """{ "files": [{ "path": "m.gguf", "active": true }], "port": 1234, "context": 4096, "thinking": {} }""");

        var model = Model.Load(_modelsDir, "p");

        Assert.NotNull(model.Profile.Thinking);
        Assert.Empty(model.Profile.Thinking!);
    }

    [Fact]
    public void Load_empty_extra_args_is_valid()
    {
        WriteProfile("p", """{ "files": [{ "path": "m.gguf", "active": true }], "port": 1234, "context": 4096, "extra_args": [] }""");

        var model = Model.Load(_modelsDir, "p");

        Assert.Empty(model.Profile.ExtraArgs);
    }

    [Fact]
    public void Load_rejects_non_string_extra_args_entry()
    {
        WriteProfile("bad", """{ "files": [{ "path": "m.gguf", "active": true }], "port": 1234, "context": 4096, "extra_args": [1] }""");
        var ex = Assert.Throws<GattoConfigException>(() => Model.Load(_modelsDir, "bad"));
        Assert.Contains("extra_args", ex.Message);
    }

    [Fact]
    public void Load_rejects_invalid_nudges_json()
    {
        WriteProfile("p", """{ "files": [{ "path": "m.gguf", "active": true }], "port": 1234, "context": 4096 }""");
        var nudgesPath = Path.Combine(_modelsDir, "p", "nudges.json");
        File.WriteAllText(nudgesPath, "{ not json");

        var ex = Assert.Throws<GattoConfigException>(() => Model.Load(_modelsDir, "p"));
        Assert.Contains("nudges.json", ex.Message);
        Assert.Contains(nudgesPath, ex.Message);
    }

    [Fact]
    public void Load_rejects_nudges_with_unknown_key_naming_model_and_path()
    {
        WriteProfile("p", """{ "files": [{ "path": "m.gguf", "active": true }], "port": 1234, "context": 4096 }""");
        var nudgesPath = Path.Combine(_modelsDir, "p", "nudges.json");
        File.WriteAllText(nudgesPath, """{ "gate": "grounding" }""");   //the json parses, only the key name is wrong

        var ex = Assert.Throws<GattoConfigException>(() => Model.Load(_modelsDir, "p"));

        Assert.Contains("p", ex.Message);
        Assert.Contains(nudgesPath, ex.Message);
        Assert.Contains("gate", ex.Message);   //the message names the model and path and keeps the text Nudges.Parse produced
    }

    [Fact]
    public void Load_rejects_invalid_repair_json()
    {
        WriteProfile("p", """{ "files": [{ "path": "m.gguf", "active": true }], "port": 1234, "context": 4096 }""");
        var repairPath = Path.Combine(_modelsDir, "p", "repair.json");
        File.WriteAllText(repairPath, "{ not json");

        var ex = Assert.Throws<GattoConfigException>(() => Model.Load(_modelsDir, "p"));
        Assert.Contains("repair.json", ex.Message);
        Assert.Contains(repairPath, ex.Message);
    }

    [Fact]
    public void Sampling_thinking_and_repair_survive_after_source_document_disposed()
    {
        WriteProfile("p", """
            {
              "files": [{ "path": "m.gguf", "active": true }],
              "port": 1234,
              "context": 4096,
              "sampling": { "temperature": 0.5 },
              "thinking": { "high": { "x": 1 } }
            }
            """);
        File.WriteAllText(Path.Combine(_modelsDir, "p", "repair.json"), """{ "pattern": "retry" }""");

        var model = Model.Load(_modelsDir, "p");
        GC.Collect();
        GC.WaitForPendingFinalizers();

        Assert.Equal(0.5, model.Profile.Sampling!.Value.GetProperty("temperature").GetDouble());
        Assert.Equal(1, model.Profile.Thinking!["high"]!.Value.GetProperty("x").GetInt32());
        Assert.Equal("retry", model.Repair!.Value.GetProperty("pattern").GetString());
    }

    [Fact]
    public void ListIds_returns_sorted_ids_with_profile_json()
    {
        WriteProfile("mistral", """{ "files": [{ "path": "m.gguf", "active": true }], "port": 1234, "context": 4096 }""");
        WriteProfile("Qwen", """{ "files": [{ "path": "m.gguf", "active": true }], "port": 1234, "context": 4096 }""");

        var ids = Model.ListIds(_modelsDir);

        Assert.Equal(new[] { "mistral", "Qwen" }, ids);
    }

    [Fact]
    public void ListIds_skips_directories_without_profile_json()
    {
        WriteProfile("valid", """{ "files": [{ "path": "m.gguf", "active": true }], "port": 1234, "context": 4096 }""");
        ModelDir("no-profile");

        var ids = Model.ListIds(_modelsDir);

        Assert.Equal(new[] { "valid" }, ids);
    }

    [Fact]
    public void ListIds_missing_models_dir_returns_empty_without_throwing()
    {
        var missing = Path.Combine(_modelsDir, "does-not-exist");
        Assert.Empty(Model.ListIds(missing));
    }

    [Fact]
    public void MatchesLoaded_NullProbe_IsUnknown()
    {
        var model = ModelWith(@"C:\models\a.gguf");
        Assert.Null(Model.MatchesLoaded(model, null));
    }

    //a probe with no model_path reads as unknown, and the catch-all keeps that answer rather than an explicit check
    [Fact]
    public void MatchesLoaded_ProbeWithNoModelPath_IsUnknown_never_a_mismatch()
    {
        var model = ModelWith(@"C:\models\a.gguf");

        Assert.Null(Model.MatchesLoaded(model, new LoadedModel(null, 55555)));
        //null and false are different answers here, the probe must not say the weights are wrong
        Assert.NotEqual(false, Model.MatchesLoaded(model, new LoadedModel(null, 55555)));
    }

    [Fact]
    public void MatchesLoaded_SamePath_IsTrue()
    {
        var model = ModelWith(@"C:\models\a.gguf");
        Assert.True(Model.MatchesLoaded(model, new LoadedModel(@"C:\models\a.gguf", 4096)));
    }

    [Fact]
    public void MatchesLoaded_DifferentCaseAndSeparators_IsTrue()
    {
        var model = ModelWith(@"C:\models\sub\..\a.gguf");
        Assert.True(Model.MatchesLoaded(model, new LoadedModel(@"c:/MODELS/A.GGUF", 4096)));
    }

    [Fact]
    public void MatchesLoaded_DifferentModel_IsFalse()
    {
        var model = ModelWith(@"C:\models\a.gguf");
        Assert.False(Model.MatchesLoaded(model, new LoadedModel(@"C:\models\b.gguf", 4096)));
    }

    [Fact]
    public void MatchesLoaded_UnnormalizablePath_IsUnknown()
    {
        var model = ModelWith(@"C:\models\a.gguf");
        Assert.Null(Model.MatchesLoaded(model, new LoadedModel("\0bad|path", 4096)));
    }

    //a minimal model with just the file path, the only field MatchesLoaded reads
    private static Model ModelWith(string modelPath) => new(
        "test",
        new ModelProfile([new ModelFile(modelPath, null, true)], 1235, 4096, null, null, null, null, null, Array.Empty<string>()),
        null, null, null);

    [Fact]
    public void AutoServe_IS_TRISTATE_and_absent_means_never_asked()
    {
        //a plain bool cannot tell "not yet asked" from "asked and refused", which is the difference between a question and a nag
        Assert.Null(LoadWith("").Profile.AutoServe);
        Assert.True(LoadWith(",\"auto_serve\": true").Profile.AutoServe);
        Assert.False(LoadWith(",\"auto_serve\": false").Profile.AutoServe);
    }

    [Fact]
    public void AutoServe_IN_PROFILEKEYS_or_the_model_it_is_written_to_stops_loading()
    {
        //loading refuses unknown keys, so auto_serve must stay on the accepted-key list or gatto writes a profile it then cannot load
        var model = LoadWith(",\"auto_serve\": true");

        Assert.True(model.Profile.AutoServe);
    }

    [Fact]
    public void AutoServe_THAT_IS_NOT_A_BOOL_is_a_config_error_never_a_silent_no()
    {
        var ex = Assert.Throws<GattoConfigException>(() => LoadWith(",\"auto_serve\": \"yes\""));

        Assert.Contains("auto_serve", ex.Message, StringComparison.Ordinal);
        Assert.Contains("true or false", ex.Message, StringComparison.Ordinal);
    }



    //auto_serve is a tri-state, false is the veto, and nothing writes it on the user's behalf

}
