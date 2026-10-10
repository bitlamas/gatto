using System.Text.Json;
using Gatto.Cli.Setup;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup;

//with no base_url on local, the shelf asks the default model's own port what is loaded, the address a launch would talk to
public sealed class LoadedModelProbeTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-lmp-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_home, true); } catch (Exception) { }
    }

    [Fact]
    public async Task A_LOCAL_ENDPOINT_WITHOUT_BASE_URL_is_probed_on_the_DEFAULT_MODELS_PORT()
    {
        await using var server = new FakeOpenAiServer();
        var weights = Path.Combine(_home, "m.gguf");
        File.WriteAllText(weights, "x");
        var dir = Path.Combine(_home, "models", "test-model");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "profile.json"),
            $$"""{"files":[{"path":{{JsonSerializer.Serialize(weights)}},"active":true}],"port":{{new Uri(server.BaseUrl).Port}},"context":8192}""");
        File.WriteAllText(Path.Combine(_home, "gatto.json"),
            """{"endpoints":{"local":{}},"default_endpoint":"local","defaults":{"local":{"model":"test-model"}}}""");
        server.PropsResponse = new FakeResponse(
            Body: $$$"""{"model_path":{{{JsonSerializer.Serialize(weights)}}},"default_generation_settings":{"n_ctx":8192}}""");

        using var probes = new LiveSetupProbes(_home, glyphs: GlyphSet.Unicode);

        Assert.Equal("test-model", probes.LoadedModelId());
        Assert.Equal(Path.GetFullPath(weights), Path.GetFullPath(probes.LoadedModelPath()!));
    }
}
