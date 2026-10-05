using Gatto.Cli.Setup;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup;

//the check at the end of setup sends the model the connect road settled on, a server that validates the field must see a name
public class ConnectProveModelTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-prove-model-").FullName;
    public void Dispose() { try { Directory.Delete(_home, recursive: true); } catch { } }

    [Fact]
    public async Task The_check_names_the_served_model_in_its_request()
    {
        await using var server = new FakeOpenAiServer();
        File.WriteAllText(Path.Combine(_home, "gatto.json"), $$$"""
            {"endpoints":{"theirs":{"base_url":"{{{server.BaseUrl}}}"}},
             "default_endpoint":"theirs","default_model":"served-x"}
            """);

        new LiveSetupProbes(_home, glyphs: GlyphSet.Unicode).ProveIt(modelId: null);

        Assert.Equal("served-x", server.LastRequestBody!.Value.GetProperty("model").GetString());
    }
}
