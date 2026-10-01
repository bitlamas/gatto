using System.Text.Json;
using Gatto.Core.Home;
using Gatto.Extensions;

namespace Gatto.Tests;

//the shipped gatto.schema.json must match what the reader accepts, both ways round. one direction alone passes a subset schema or one that invents keys
public class ConfigSchemaTests
{
    private static JsonElement Root()
    {
        Assert.False(string.IsNullOrWhiteSpace(ConfigSchema.Text),
            "the embedded schema is empty — check the EmbeddedResource entry in Gatto.csproj");
        using var doc = JsonDocument.Parse(ConfigSchema.Text);
        return doc.RootElement.Clone();
    }

    //steps through a dotted path, a missing level names itself in the failure message
    private static JsonElement At(JsonElement root, string path)
    {
        var here = root;
        foreach (var seg in path.Split('.'))
        {
            Assert.True(here.TryGetProperty(seg, out var next),
                $"the schema has no '{seg}' under '{path}' — the level is missing entirely");
            here = next;
        }
        return here;
    }

    private static string[] PropertyNames(JsonElement root, string path) =>
        At(root, path).EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();

    private static string[] Sorted(string[] keys) =>
        keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();

    //each row pairs a reader level with the schema pointer that must describe it. a census fails when the reader gains a strict level with no row here
    public static TheoryData<string, string> Levels => new()
    {
        { "properties", nameof(GattoConfig.TopKeys) },
        { "properties.endpoints.additionalProperties.properties", nameof(GattoConfig.EndpointKeys) },
        { "properties.context_files.properties", nameof(GattoConfig.ContextFilesKeys) },
        { "properties.search.properties", nameof(GattoConfig.SearchKeys) },
        { "properties.search.properties.tavily.properties", nameof(GattoConfig.TavilyKeys) },
        { "properties.search.properties.searxng.properties", nameof(GattoConfig.SearxngKeys) },
        { "properties.memory.properties", nameof(GattoConfig.MemoryKeys) },
        { "properties.defaults.additionalProperties.properties", nameof(GattoConfig.DefaultsEntryKeys) },
    };

    private static string[] ArrayNamed(string name) => name switch
    {
        nameof(GattoConfig.TopKeys) => GattoConfig.TopKeys,
        nameof(GattoConfig.EndpointKeys) => GattoConfig.EndpointKeys,
        nameof(GattoConfig.ContextFilesKeys) => GattoConfig.ContextFilesKeys,
        nameof(GattoConfig.SearchKeys) => GattoConfig.SearchKeys,
        nameof(GattoConfig.TavilyKeys) => GattoConfig.TavilyKeys,
        nameof(GattoConfig.SearxngKeys) => GattoConfig.SearxngKeys,
        nameof(GattoConfig.MemoryKeys) => GattoConfig.MemoryKeys,
        nameof(GattoConfig.DefaultsEntryKeys) => GattoConfig.DefaultsEntryKeys,
        _ => throw new InvalidOperationException($"no reader array named '{name}'"),
    };

    [Theory]
    [MemberData(nameof(Levels))]
    public void SCHEMA_PROPERTIES_ARE_EXACTLY_THE_READERS_ALLOWLISTS(string pointer, string arrayName)
    {
        var described = PropertyNames(Root(), pointer);
        var accepted = Sorted(ArrayNamed(arrayName));

        //assert both directions separately, because the fixes differ. a key the reader accepts and the schema omits needs documenting, the reverse breaks a promise
        var undocumented = accepted.Except(described, StringComparer.Ordinal).ToArray();
        var invented = described.Except(accepted, StringComparer.Ordinal).ToArray();

        Assert.True(undocumented.Length == 0,
            $"{arrayName} accepts keys the schema does not describe at '{pointer}': "
            + string.Join(", ", undocumented));
        Assert.True(invented.Length == 0,
            $"the schema describes keys {arrayName} rejects at '{pointer}': " + string.Join(", ", invented));
    }

    //the enums follow the same claim as the property sets. the schema must not offer a value the reader refuses, nor hide one it accepts.
    [Theory]
    [InlineData("properties.theme.enum", nameof(GattoConfig.ValidThemes))]
    [InlineData("properties.search.properties.providers.items.enum", nameof(GattoConfig.ValidProviders))]
    public void SCHEMA_ENUMS_ARE_EXACTLY_THE_READERS_VALID_VALUES(string pointer, string arrayName)
    {
        var offered = At(Root(), pointer).EnumerateArray().Select(v => v.GetString()!)
            .OrderBy(v => v, StringComparer.Ordinal).ToArray();
        var accepted = Sorted(arrayName == nameof(GattoConfig.ValidThemes)
            ? GattoConfig.ValidThemes
            : GattoConfig.ValidProviders);

        Assert.Equal(accepted, offered);
    }

    //the levels table cannot prove its own coverage, a new strict level with no row stays undescribed. the scan of *Keys arrays is a proxy for that coverage
    [Fact]
    public void EVERY_STRICT_LEVEL_IN_THE_READER_HAS_A_ROW_HERE()
    {
        //the scan covers all of Core\Home, an array declared in a new file cannot slip past
        var arrays = Directory
            .EnumerateFiles(Path.Combine(Census.SourceTree.RepoRoot(), "Gatto", "Core", "Home"), "*.cs")
            .SelectMany(f => System.Text.RegularExpressions.Regex
                .Matches(File.ReadAllText(f), @"internal static readonly string\[\] (\w+Keys)\b")
                .Select(m => m.Groups[1].Value))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal).ToArray();

        var covered = Levels.Select(row => (string)row[1]!)
            .OrderBy(n => n, StringComparer.Ordinal).ToArray();

        Assert.Equal(covered, arrays);
    }

    //the fixture gives $schema a number, the reader must ignore the value. a well-formed string would show only that the key is accepted
    [Fact]
    public void THE_SCHEMA_KEY_IS_ACCEPTED_AND_ITS_VALUE_IS_NEVER_READ()
    {
        var home = NewHome();
        File.WriteAllText(Path.Combine(home, "gatto.json"),
            """{"$schema":12345,"endpoints":{"local":{"base_url":"http://x"}},"default_endpoint":"local"}""");

        var cfg = GattoConfig.Load(home);   //the load must not throw, the value is never resolved or type-checked
        Assert.Equal("local", cfg.DefaultEndpoint);
    }

    //the schema must never sit under Gatto\Shipped\, that manifest keeps deletions and preserves edited copies
    [Fact]
    public void THE_SCHEMA_IS_NOT_A_MANIFEST_TRACKED_SHIPPED_FILE()
    {
        var offenders = ShippedExtensions.Files.Keys
            .Where(k => k.EndsWith(".schema.json", StringComparison.Ordinal)).ToArray();

        Assert.True(offenders.Length == 0,
            "a schema is embedded under Gatto\\Shipped\\ and would be materialized by "
            + "ShippedExtensions.EnsureWritten, which makes a deletion stick and preserves a user edit. "
            + "R3 rules plain overwrite. Move it back to Gatto\\Core\\Home\\: " + string.Join(", ", offenders));
    }

    //a non-empty string is not enough. the embedded schema must parse and declare its JSON Schema draft.
    [Fact]
    public void THE_SCHEMA_DECLARES_ITS_DIALECT()
    {
        var root = Root();
        Assert.Equal("https://json-schema.org/draft/2020-12/schema", root.GetProperty("$schema").GetString());
        Assert.False(root.GetProperty("additionalProperties").GetBoolean());
    }

    //the default_publisher enum must come from the allowlist in Core\Acquire, a copy drifts on the first new org. without the enum, no editor can complete the key
    [Fact]
    public void THE_PUBLISHER_ENUM_IS_EXACTLY_THE_DATED_ALLOWLIST()
    {
        var offered = At(Root(), "properties.default_publisher.enum")
            .EnumerateArray().Select(v => v.GetString()!)
            .OrderBy(v => v, StringComparer.Ordinal).ToArray();
        var accepted = Sorted([.. Gatto.Core.Acquire.UploaderAllowlist.Load().Orgs]);

        Assert.Equal(accepted, offered);
    }

    //the regions key stays described and marked deprecated, dropping it makes an editor flag a config gatto loads without complaint
    [Fact]
    public void REGIONS_IS_DESCRIBED_AND_MARKED_DEPRECATED()
    {
        var regions = At(Root(), "properties.regions");
        Assert.True(regions.GetProperty("deprecated").GetBoolean());
    }

    //the weights_root description must name both jobs, fetched into and scanned. this text renders in the manual, the user reads it
    [Fact]
    public void THE_WEIGHTS_ROOTS_DESCRIPTION_NAMES_BOTH_JOBS()
    {
        var described = At(Root(), "properties.weights_root.description").GetString()!;

        Assert.Contains("fetched into", described, StringComparison.Ordinal);
        Assert.Contains("scans", described, StringComparison.Ordinal);
    }

    //the retired models_dir key must be absent from the schema. an offered key the reader rejects tells the user to write something that will not load
    [Fact]
    public void AND_THE_OLD_KEY_IS_NOT_IN_THE_SCHEMA()
    {
        Assert.DoesNotContain("models_dir", ConfigSchema.Text, StringComparison.Ordinal);
    }

    private static string NewHome()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gatto-schema-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }
}
