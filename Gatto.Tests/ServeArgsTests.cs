using System.Globalization;
using System.Text.Json;
using Gatto.Roles;

namespace Gatto.Tests;

public class ServeArgsTests
{
    private static JsonElement ParseObject(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    private static ModelProfile Minimal(string? mmproj = null) => new(
        Files: [new ModelFile(@"C:\models\m.gguf", null, true)], Port: 1235, Context: 8192,
        GpuLayers: null, CacheTypeK: null, CacheTypeV: null,
        Sampling: null, Thinking: null, ExtraArgs: Array.Empty<string>(),
        MmProj: mmproj);

    [Fact]
    public void No_mmproj_leaves_the_argv_byte_identical()
    {
        //a model without mmproj must compose the same argv as before
        Assert.DoesNotContain("--mmproj", ServeArgs.Compose(Minimal()));
    }

    [Fact]
    public void An_mmproj_path_is_emitted_as_its_own_flag_and_value()
    {
        var argv = ServeArgs.Compose(Minimal(@"C:\models\mmproj-gemma-BF16.gguf")).ToList();
        var i = argv.IndexOf("--mmproj");
        Assert.True(i >= 0, "the flag must be emitted");
        //the path stays its own element, a pre-joined argument with spaces splits in two and the server rejects it
        Assert.Equal(@"C:\models\mmproj-gemma-BF16.gguf", argv[i + 1]);
    }

    [Fact]
    public void Compose_full_profile_pins_exact_argv_in_order()
    {
        var profile = new ModelProfile(
            Files: [new ModelFile(@"C:\models\qwen3.6-35b.gguf", null, true)],
            Port: 1235,
            Context: 32768,
            GpuLayers: 40,
            CacheTypeK: "q8_0",
            CacheTypeV: "q8_0",
            Sampling: ParseObject("""{ "temperature": 0.7, "top_p": 0.9, "top_k": 40, "min_p": 0.05 }"""),
            Thinking: null,
            ExtraArgs: new[] { "--flash-attn", "on" });

        var argv = ServeArgs.Compose(profile);

        Assert.Equal(new[]
        {
            "-m", @"C:\models\qwen3.6-35b.gguf",
            "--port", "1235",
            "-c", "32768",
            "--slots",
            "--n-gpu-layers", "40",
            "-ctk", "q8_0",
            "-ctv", "q8_0",
            "--temp", "0.7",
            "--top-p", "0.9",
            "--top-k", "40",
            "--min-p", "0.05",
            "--host", "127.0.0.1",
            "--flash-attn", "on",
        }, argv);
    }

    [Fact]
    public void Compose_minimal_profile_emits_required_flags_and_slots()
    {
        var profile = new ModelProfile(
            Files: [new ModelFile(@"C:\models\tiny.gguf", null, true)],
            Port: 8080,
            Context: 4096,
            GpuLayers: null,
            CacheTypeK: null,
            CacheTypeV: null,
            Sampling: null,
            Thinking: null,
            ExtraArgs: Array.Empty<string>());

        var argv = ServeArgs.Compose(profile);

        //the --slots flag is always present, and no --seed appears without a seed
        Assert.Equal(new[]
        {
            "-m", @"C:\models\tiny.gguf",
            "--port", "8080",
            "-c", "4096",
            "--slots",
            "--host", "127.0.0.1",
        }, argv);
        Assert.DoesNotContain("--seed", argv);
    }

    [Fact]
    public void Compose_with_seed_emits_seed_flag_right_after_slots()
    {
        var profile = new ModelProfile(
            Files: [new ModelFile(@"C:\models\tiny.gguf", null, true)],
            Port: 8080,
            Context: 4096,
            GpuLayers: null,
            CacheTypeK: null,
            CacheTypeV: null,
            Sampling: null,
            Thinking: null,
            ExtraArgs: Array.Empty<string>(),
            Seed: 42);

        var argv = ServeArgs.Compose(profile);

        Assert.Equal(new[]
        {
            "-m", @"C:\models\tiny.gguf",
            "--port", "8080",
            "-c", "4096",
            "--slots",
            "--seed", "42",
            "--host", "127.0.0.1",
        }, argv);
    }

    [Fact]
    public void Compose_appends_extra_args_last_so_user_wins_on_conflict()
    {
        var profile = new ModelProfile(
            Files: [new ModelFile("m.gguf", null, true)],
            Port: 1,
            Context: 2,
            GpuLayers: 10,
            CacheTypeK: null,
            CacheTypeV: null,
            Sampling: ParseObject("""{ "temperature": 0.5 }"""),
            Thinking: null,
            ExtraArgs: new[] { "--n-gpu-layers", "99", "--temp", "1.0" });

        var argv = ServeArgs.Compose(profile);

        Assert.Equal(new[]
        {
            "-m", "m.gguf",
            "--port", "1",
            "-c", "2",
            "--slots",
            "--n-gpu-layers", "10",
            "--temp", "0.5",
            "--host", "127.0.0.1",
            "--n-gpu-layers", "99",
            "--temp", "1.0",
        }, argv);
    }

    [Fact]
    public void Compose_gpu_layers_zero_still_emits_the_flag()
    {
        var profile = new ModelProfile(
            Files: [new ModelFile("m.gguf", null, true)],
            Port: 1,
            Context: 2,
            GpuLayers: 0,
            CacheTypeK: null,
            CacheTypeV: null,
            Sampling: null,
            Thinking: null,
            ExtraArgs: Array.Empty<string>());

        var argv = ServeArgs.Compose(profile);

        Assert.Equal(new[]
        {
            "-m", "m.gguf",
            "--port", "1",
            "-c", "2",
            "--slots",
            "--n-gpu-layers", "0",
            "--host", "127.0.0.1",
        }, argv);
    }

    [Fact]
    public void Compose_cache_type_k_without_v_emits_only_k()
    {
        var profile = new ModelProfile(
            Files: [new ModelFile("m.gguf", null, true)],
            Port: 1,
            Context: 2,
            GpuLayers: null,
            CacheTypeK: "q8_0",
            CacheTypeV: null,
            Sampling: null,
            Thinking: null,
            ExtraArgs: Array.Empty<string>());

        var argv = ServeArgs.Compose(profile);

        Assert.Equal(new[]
        {
            "-m", "m.gguf",
            "--port", "1",
            "-c", "2",
            "--slots",
            "-ctk", "q8_0",
            "--host", "127.0.0.1",
        }, argv);
    }

    [Fact]
    public void Compose_empty_sampling_object_emits_no_sampling_flags()
    {
        var profile = new ModelProfile(
            Files: [new ModelFile("m.gguf", null, true)],
            Port: 1,
            Context: 2,
            GpuLayers: null,
            CacheTypeK: null,
            CacheTypeV: null,
            Sampling: ParseObject("{}"),
            Thinking: null,
            ExtraArgs: Array.Empty<string>());

        var argv = ServeArgs.Compose(profile);

        Assert.Equal(new[]
        {
            "-m", "m.gguf",
            "--port", "1",
            "-c", "2",
            "--slots",
            "--host", "127.0.0.1",
        }, argv);
    }

    [Fact]
    public void Compose_ignores_unknown_sampling_keys()
    {
        var profile = new ModelProfile(
            Files: [new ModelFile("m.gguf", null, true)],
            Port: 1,
            Context: 2,
            GpuLayers: null,
            CacheTypeK: null,
            CacheTypeV: null,
            Sampling: ParseObject("""{ "temperature": 0.7, "repeat_penalty": 1.1, "seed": 42 }"""),
            Thinking: null,
            ExtraArgs: Array.Empty<string>());

        var argv = ServeArgs.Compose(profile);

        Assert.Equal(new[]
        {
            "-m", "m.gguf",
            "--port", "1",
            "-c", "2",
            "--slots",
            "--temp", "0.7",
            "--host", "127.0.0.1",
        }, argv);
    }

    [Fact]
    public void Compose_ignores_wrong_typed_sampling_value_rather_than_emit_it()
    {
        var profile = new ModelProfile(
            Files: [new ModelFile("m.gguf", null, true)],
            Port: 1,
            Context: 2,
            GpuLayers: null,
            CacheTypeK: null,
            CacheTypeV: null,
            Sampling: ParseObject("""{ "temperature": "hot", "top_p": 0.9 }"""),
            Thinking: null,
            ExtraArgs: Array.Empty<string>());

        var argv = ServeArgs.Compose(profile);

        //a sampling value of the wrong type is skipped, its well-typed sibling still emits
        Assert.Equal(new[]
        {
            "-m", "m.gguf",
            "--port", "1",
            "-c", "2",
            "--slots",
            "--top-p", "0.9",
            "--host", "127.0.0.1",
        }, argv);
    }

    [Fact]
    public void Compose_renders_integer_sampling_value_without_trailing_zero()
    {
        var profile = new ModelProfile(
            Files: [new ModelFile("m.gguf", null, true)],
            Port: 1,
            Context: 2,
            GpuLayers: null,
            CacheTypeK: null,
            CacheTypeV: null,
            Sampling: ParseObject("""{ "temperature": 1 }"""),
            Thinking: null,
            ExtraArgs: Array.Empty<string>());

        var argv = ServeArgs.Compose(profile);

        Assert.Equal(new[]
        {
            "-m", "m.gguf",
            "--port", "1",
            "-c", "2",
            "--slots",
            "--temp", "1",
            "--host", "127.0.0.1",
        }, argv);
    }

    [Fact]
    public void Compose_formats_sampling_numbers_invariantly_under_comma_decimal_culture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR"); //a culture whose decimal separator is a comma.
            var profile = new ModelProfile(
                Files: [new ModelFile("m.gguf", null, true)],
                Port: 1,
                Context: 2,
                GpuLayers: null,
                CacheTypeK: null,
                CacheTypeV: null,
                Sampling: ParseObject("""{ "temperature": 0.7 }"""),
                Thinking: null,
                ExtraArgs: Array.Empty<string>());

            var argv = ServeArgs.Compose(profile);

            Assert.Contains("0.7", argv);
            Assert.DoesNotContain("0,7", argv);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Compose_emits_host_127_0_0_1_as_a_contiguous_pair()
    {
        var profile = new ModelProfile(
            Files: [new ModelFile("m.gguf", null, true)],
            Port: 1,
            Context: 2,
            GpuLayers: null,
            CacheTypeK: null,
            CacheTypeV: null,
            Sampling: null,
            Thinking: null,
            ExtraArgs: Array.Empty<string>());

        var argv = ServeArgs.Compose(profile);

        var hostIndex = argv.ToList().IndexOf("--host");
        Assert.True(hostIndex >= 0, "expected --host to be present in argv");
        Assert.True(hostIndex + 1 < argv.Count, "expected a value to follow --host");
        Assert.Equal("127.0.0.1", argv[hostIndex + 1]);
    }

    [Fact]
    public void Compose_places_host_before_every_extra_arg()
    {
        var profile = new ModelProfile(
            Files: [new ModelFile("m.gguf", null, true)],
            Port: 1,
            Context: 2,
            GpuLayers: null,
            CacheTypeK: null,
            CacheTypeV: null,
            Sampling: null,
            Thinking: null,
            ExtraArgs: new[] { "--flash-attn", "on", "--ubatch-size", "512" });

        var argv = ServeArgs.Compose(profile).ToList();

        var hostIndex = argv.IndexOf("--host");
        Assert.True(hostIndex >= 0, "expected --host to be present in argv");

        //check every extra arg sits after --host, using the earliest IndexOf so the assertion cannot pass vacuously
        foreach (var extra in profile.ExtraArgs)
        {
            var extraIndex = argv.IndexOf(extra);
            Assert.True(extraIndex >= 0, $"expected extra arg '{extra}' to be present in argv");
            Assert.True(
                hostIndex < extraIndex,
                $"expected --host (index {hostIndex}) to precede ExtraArgs element '{extra}' (index {extraIndex})");
        }
    }

    [Fact]
    public void Compose_model_extra_args_host_override_still_wins_by_position()
    {
        //the server takes the last --host, so gatto puts its safe default first and keeps the model's own token
        var profile = new ModelProfile(
            Files: [new ModelFile("m.gguf", null, true)],
            Port: 1,
            Context: 2,
            GpuLayers: null,
            CacheTypeK: null,
            CacheTypeV: null,
            Sampling: null,
            Thinking: null,
            ExtraArgs: new[] { "--host", "0.0.0.0" });

        var argv = ServeArgs.Compose(profile);

        Assert.Equal(new[]
        {
            "-m", "m.gguf",
            "--port", "1",
            "-c", "2",
            "--slots",
            "--host", "127.0.0.1",
            "--host", "0.0.0.0",
        }, argv);
    }

    [Fact]
    public void Compose_without_api_key_emits_no_api_key_flag()
    {
        //a model that says nothing about keys composes the same argv as before, a default key appearing on its own is the bug
        var profile = new ModelProfile(
            Files: [new ModelFile("m.gguf", null, true)],
            Port: 1,
            Context: 2,
            GpuLayers: null,
            CacheTypeK: null,
            CacheTypeV: null,
            Sampling: null,
            Thinking: null,
            ExtraArgs: new[] { "--jinja" });

        var argv = ServeArgs.Compose(profile);

        Assert.DoesNotContain("--api-key", argv);
        Assert.Equal(new[]
        {
            "-m", "m.gguf",
            "--port", "1",
            "-c", "2",
            "--slots",
            "--host", "127.0.0.1",
            "--jinja",
        }, argv);
    }

    [Fact]
    public void Compose_with_api_key_emits_the_flag_with_the_exact_value()
    {
        var profile = new ModelProfile(
            Files: [new ModelFile("m.gguf", null, true)],
            Port: 1,
            Context: 2,
            GpuLayers: null,
            CacheTypeK: null,
            CacheTypeV: null,
            Sampling: null,
            Thinking: null,
            ExtraArgs: Array.Empty<string>(),
            ApiKey: "s3cr3t-value");

        var argv = ServeArgs.Compose(profile, apiKeyFile: @"C:\home\models\p\api-key.txt");

        Assert.Equal(new[]
        {
            "-m", "m.gguf",
            "--port", "1",
            "-c", "2",
            "--slots",
            "--host", "127.0.0.1",
            "--api-key-file", @"C:\home\models\p\api-key.txt",
        }, argv);

        //the credential itself must appear nowhere in the command line.
        Assert.DoesNotContain("s3cr3t-value", string.Join(" ", argv), StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_refuses_to_run_a_keyed_model_without_a_key_file()
    {
        //refuse a keyed model with no key file, a fallback to --api-key with the value would leak it
        var profile = new ModelProfile(
            Files: [new ModelFile("m.gguf", null, true)], Port: 1, Context: 2, GpuLayers: null,
            CacheTypeK: null, CacheTypeV: null, Sampling: null, Thinking: null,
            ExtraArgs: Array.Empty<string>(), ApiKey: "s3cr3t-value");

        var ex = Assert.Throws<InvalidOperationException>(() => ServeArgs.Compose(profile));
        Assert.Contains("apiKeyFile", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cr3t-value", ex.Message, StringComparison.Ordinal);   //the error message must not leak the credential either.
    }

    [Fact]
    public void Compose_without_an_api_key_is_byte_identical_whether_or_not_a_path_is_offered()
    {
        //a model with no key composes the same argv whether or not a key file path is offered
        var profile = new ModelProfile(
            Files: [new ModelFile("m.gguf", null, true)], Port: 1, Context: 2, GpuLayers: null,
            CacheTypeK: null, CacheTypeV: null, Sampling: null, Thinking: null,
            ExtraArgs: Array.Empty<string>());

        Assert.Equal(ServeArgs.Compose(profile), ServeArgs.Compose(profile, @"C:\unused.txt"));
        Assert.DoesNotContain("--api-key-file", ServeArgs.Compose(profile));
    }

    [Fact]
    public void Compose_places_api_key_before_every_extra_arg()
    {
        //gatto-composed flags come before every extra arg, the loader refuses a key passed through ExtraArgs
        var profile = new ModelProfile(
            Files: [new ModelFile("m.gguf", null, true)],
            Port: 1,
            Context: 2,
            GpuLayers: null,
            CacheTypeK: null,
            CacheTypeV: null,
            Sampling: null,
            Thinking: null,
            ExtraArgs: new[] { "--jinja", "--no-mmap" },
            ApiKey: "composed");

        var argv = ServeArgs.Compose(profile, apiKeyFile: @"C:\home\models\p\api-key.txt").ToList();

        Assert.Equal(new[]
        {
            "-m", "m.gguf",
            "--port", "1",
            "-c", "2",
            "--slots",
            "--host", "127.0.0.1",
            "--api-key-file", @"C:\home\models\p\api-key.txt",
            "--jinja", "--no-mmap",
        }, argv);

        var keyIndex = argv.IndexOf("--api-key-file");
        var firstExtraIndex = argv.IndexOf(profile.ExtraArgs[0]);
        Assert.True(keyIndex >= 0 && keyIndex < firstExtraIndex,
            $"expected --api-key-file (index {keyIndex}) to precede the first ExtraArgs element (index {firstExtraIndex})");
    }

    private static ModelProfile WithExtras(string[] extraArgs, string? apiKey = null) =>
        new(Files: [new ModelFile(@"C:\models\m.gguf", null, true)], Port: 1235, Context: 4096, GpuLayers: null,
            CacheTypeK: null, CacheTypeV: null, Sampling: null, Thinking: null,
            ExtraArgs: extraArgs, ApiKey: apiKey);

    [Fact]
    public void ExposureWarning_fires_when_host_is_widened_and_no_key_is_set()
    {
        var w = ServeArgs.ExposureWarning(WithExtras(new[] { "--host", "0.0.0.0" }));

        Assert.NotNull(w);
        Assert.Contains("0.0.0.0", w);
        Assert.Contains("api_key", w);
    }

    [Fact]
    public void ExposureWarning_is_silent_when_the_widened_server_is_keyed()
    {
        //widening the bind is legitimate once the server authenticates, which is the reason the api key exists
        Assert.Null(ServeArgs.ExposureWarning(WithExtras(new[] { "--host", "0.0.0.0" }, apiKey: "s3cr3t")));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]
    [InlineData("LocalHost")]
    [InlineData("::1")]
    [InlineData("[::1]")]
    [InlineData("127.5.5.5")]   //the whole 127 block is loopback
    public void ExposureWarning_is_silent_for_every_loopback_spelling(string host)
    {
        Assert.Null(ServeArgs.ExposureWarning(WithExtras(new[] { "--host", host })));
    }

    [Fact]
    public void ExposureWarning_is_silent_when_the_model_never_touches_host()
    {
        //the composer itself emits a loopback host here, so there is nothing to warn about.
        Assert.Null(ServeArgs.ExposureWarning(WithExtras(new[] { "--flash-attn", "on" })));
    }

    [Fact]
    public void ExposureWarning_reads_the_LAST_host_because_llama_server_does()
    {
        //the server keeps the last host flag, judging the first would diagnose an address it never bound
        Assert.Null(ServeArgs.ExposureWarning(WithExtras(new[] { "--host", "0.0.0.0", "--host", "127.0.0.1" })));

        var w = ServeArgs.ExposureWarning(WithExtras(new[] { "--host", "127.0.0.1", "--host", "0.0.0.0" }));
        Assert.NotNull(w);
        Assert.Contains("0.0.0.0", w);
    }

    //no --host=value case here, the loader refuses that spelling so this method never sees it

    [Fact]
    public void ExposureWarning_treats_an_unparseable_host_as_exposed()
    {
        //an unparseable host is treated as exposed, a false warning costs one line where a missed one leaves the server open
        Assert.NotNull(ServeArgs.ExposureWarning(WithExtras(new[] { "--host", "my-lan-box.local" })));
    }

    [Fact]
    public void ExposureWarning_ignores_a_trailing_host_flag_with_no_value()
    {
        //a trailing --host with no value must not index past the end of the list
        Assert.Null(ServeArgs.ExposureWarning(WithExtras(new[] { "--host" })));
    }
}
