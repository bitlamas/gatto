using Gatto.Core.Client;
using Gatto.Core.Home;
using Gatto.Roles;
using System.Text.Json;

namespace Gatto.Tests;

//the formatter is pure, no live server and no serve.json reads, so the tests feed it a fabricated LoadedModel
public class ServeStatusJsonTests
{
    [Fact]
    public void Loaded_model_emits_all_known_fields()
    {
        var loaded = new LoadedModel(@"C:\models\gemma-4-26B-A4B-it-Q6_K.gguf", 131072);
        var json = ServeStatusJson.Build(loaded, "gemma-model", matchesModel: true);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(@"C:\models\gemma-4-26B-A4B-it-Q6_K.gguf", root.GetProperty("model_path").GetString());
        Assert.Equal(131072, root.GetProperty("n_ctx").GetInt32());
        Assert.Equal("gemma-model", root.GetProperty("model_id").GetString());
        Assert.True(root.GetProperty("matches_model").GetBoolean());
        Assert.Equal("Q6_K", root.GetProperty("quant").GetString());
        Assert.Equal(SessionStore.SchemaVersion, root.GetProperty("schema_version").GetInt32());
    }

    [Fact]
    public void THE_WIRE_OBJECT_HAS_NO_KEY_LEFT_THAT_SAYS_PACK()
    {
        //assert the whole key set, a rename that stops halfway leaves the object speaking two vocabularies
        var loaded = new LoadedModel(@"C:\models\a-Q4_K_S.gguf", 8192);
        var json = ServeStatusJson.Build(loaded, "some-model", true);
        using var doc = JsonDocument.Parse(json);
        var names = doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();

        Assert.DoesNotContain(names, n => n.Contains("pack", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("model_id", names);
        Assert.Contains("matches_model", names);
        //the two keys do not collide, model_id names the setup and model_path the weights file
        Assert.Contains("model_path", names);
    }

    [Fact]
    public void Never_emits_n_gpu_layers_or_fallback()
    {
        var loaded = new LoadedModel(@"C:\models\a-Q4_K_S.gguf", 8192);
        var json = ServeStatusJson.Build(loaded, "p", matchesModel: false);
        using var doc = JsonDocument.Parse(json);
        var names = doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();

        Assert.DoesNotContain("n_gpu_layers", names);
        Assert.DoesNotContain("fallback", names);
        Assert.DoesNotContain("oom", names);
    }

    [Fact]
    public void Unparseable_filename_omits_quant_entirely()
    {
        var loaded = new LoadedModel(@"C:\models\my-custom-weights.gguf", 4096);
        var json = ServeStatusJson.Build(loaded, "p", matchesModel: null);
        using var doc = JsonDocument.Parse(json);

        Assert.False(doc.RootElement.TryGetProperty("quant", out _));
    }

    [Fact]
    public void Null_loaded_model_nulls_model_path_n_ctx_and_omits_quant()
    {
        var json = ServeStatusJson.Build(loaded: null, modelId: "p", matchesModel: null);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(JsonValueKind.Null, root.GetProperty("model_path").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("n_ctx").ValueKind);
        Assert.False(root.TryGetProperty("quant", out _));
        Assert.Equal(JsonValueKind.Null, root.GetProperty("matches_model").ValueKind);
    }

    [Fact]
    public void Null_n_ctx_on_a_known_model_stays_null_not_omitted()
    {
        var loaded = new LoadedModel(@"C:\m.gguf", null);
        var json = ServeStatusJson.Build(loaded, "p", matchesModel: true);
        using var doc = JsonDocument.Parse(json);

        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("n_ctx").ValueKind);
    }

    [Fact]
    public void Null_model_id_nulls_model_id()
    {
        var loaded = new LoadedModel(@"C:\m.gguf", 4096);
        var json = ServeStatusJson.Build(loaded, modelId: null, matchesModel: null);
        using var doc = JsonDocument.Parse(json);

        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("model_id").ValueKind);
    }

    [Theory]
    [InlineData(@"C:\models\deepseek-v4-flash-IQ2_XXS.gguf", "IQ2_XXS")]
    [InlineData(@"C:\models\minimax-m2.7-reap-Q4_K_S.gguf", "Q4_K_S")]
    [InlineData(@"C:\models\model.Q8_0.gguf", "Q8_0")]
    [InlineData(@"C:\models\model-F16.gguf", "F16")]
    [InlineData(@"C:\models\model-bf16.gguf", "BF16")]
    public void Quant_parses_from_common_gguf_naming_conventions(string path, string expected)
    {
        var loaded = new LoadedModel(path, 4096);
        var json = ServeStatusJson.Build(loaded, "p", matchesModel: true);
        using var doc = JsonDocument.Parse(json);

        Assert.Equal(expected, doc.RootElement.GetProperty("quant").GetString());
    }
}
