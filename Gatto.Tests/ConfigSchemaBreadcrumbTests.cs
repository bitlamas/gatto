using System.Text.Json;
using Gatto.Core.Home;

namespace Gatto.Tests;

//the scaffold names the schema as the first key, so a reader sees it on line one. every test here asserts that position
public class ConfigSchemaBreadcrumbTests
{
    private static string NewHome()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gatto-crumb-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string FirstKey(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateObject().First().Name;
    }

    [Fact]
    public void THE_SCAFFOLD_NAMES_THE_SCHEMA_ON_LINE_ONE()
    {
        var home = NewHome();
        GattoHome.EnsureInitialized(home);
        var text = File.ReadAllText(Path.Combine(home, "gatto.json"));

        Assert.Equal("$schema", FirstKey(text));

        using var doc = JsonDocument.Parse(text);
        Assert.Equal("./" + ConfigSchema.FileName,
            doc.RootElement.GetProperty("$schema").GetString());
    }

    //adding the $schema breadcrumb must not break the scaffold, a fresh home still loads under the strict reader
    [Fact]
    public void THE_SCAFFOLD_STILL_PARSES_UNDER_THE_STRICT_READER()
    {
        var home = NewHome();
        GattoHome.EnsureInitialized(home);

        var cfg = GattoConfig.Load(home);

        Assert.Equal("local", cfg.DefaultEndpoint);
        Assert.True(cfg.Endpoints.ContainsKey("local"));
    }

    //a rewrite must keep $schema as the first key. if this fails, fix the writer rather than relax the test
    [Fact]
    public void A_WRITE_LEAVES_THE_BREADCRUMB_FIRST()
    {
        var home = NewHome();
        GattoHome.EnsureInitialized(home);
        var path = Path.Combine(home, "gatto.json");

        GattoConfigWriter.SetEndpointDefaultModel(home, "local", "some-model");

        var after = File.ReadAllText(path);
        Assert.Equal("$schema", FirstKey(after));
        using var doc = JsonDocument.Parse(after);
        Assert.Equal("some-model", doc.RootElement.GetProperty("defaults").GetProperty("local").GetProperty("model").GetString());
    }

    //every writer gets a row of its own, a passing row for one proves nothing about another
    [Theory]
    [InlineData("SetLlamaServer")]
    [InlineData("SetDefaultEndpoint")]
    [InlineData("UpsertEndpoint")]
    [InlineData("SetConsentKey")]
    [InlineData("SetWeightsRoot")]
    public void EVERY_WRITER_LEAVES_THE_BREADCRUMB_FIRST(string writer)
    {
        var home = NewHome();
        GattoHome.EnsureInitialized(home);

        switch (writer)
        {
            case "SetLlamaServer": GattoConfigWriter.SetLlamaServer(home, @"C:\llama\llama-server.exe"); break;
            case "SetDefaultEndpoint": GattoConfigWriter.SetDefaultEndpoint(home, "local"); break;
            case "UpsertEndpoint": GattoConfigWriter.UpsertEndpoint(home, "local", "http://127.0.0.1:1235", 8192); break;
            case "SetConsentKey": GattoConfigWriter.SetConsentKey(home, "update_check", true); break;
            //the census test pins this row, a new mutator forces a new row here
            case "SetWeightsRoot": GattoConfigWriter.SetWeightsRoot(home, @"D:\weights"); break;
            default: throw new InvalidOperationException($"no writer named '{writer}'");
        }

        Assert.Equal("$schema", FirstKey(File.ReadAllText(Path.Combine(home, "gatto.json"))));
    }

    //the theory cannot prove its own coverage, a sixth mutator would pass every row. add a public static method to the writer and this test fails naming it
    [Fact]
    public void EVERY_MUTATOR_ON_THE_WRITER_HAS_A_ROW_ABOVE()
    {
        var src = File.ReadAllText(Path.Combine(
            Census.SourceTree.RepoRoot(), "Gatto", "Core", "Home", "GattoConfigWriter.cs"));

        var mutators = System.Text.RegularExpressions.Regex
            .Matches(src, @"public static void (\w+)\(")
            .Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal).ToArray();

        Assert.Equal(
            new[]
            {
                "SetConsentKey", "SetDefaultEndpoint", "SetEndpointDefaultModel", "SetEndpointEffort",
                "SetLlamaServer", "SetWeightsRoot", "UpsertEndpoint",
            },
            mutators);
    }

    //a hand-added $schema key must load like a scaffolded one. the reader accepting the key and the scaffold writing it are two claims
    [Fact]
    public void AN_OLDER_HOME_THAT_GAINED_THE_KEY_BY_HAND_STILL_LOADS()
    {
        var home = NewHome();
        File.WriteAllText(Path.Combine(home, "gatto.json"),
            """
            {
              "$schema": "./gatto.schema.json",
              "endpoints": { "local": { "base_url": "http://127.0.0.1:1235" } },
              "default_endpoint": "local",
              "regions": "off"
            }
            """);

        var cfg = GattoConfig.Load(home);

        Assert.Equal("local", cfg.DefaultEndpoint);
    }
}
