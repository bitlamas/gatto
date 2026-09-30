using System.Text.Json;
using Gatto.Core.Home;
using Gatto.Tests.Census;

namespace Gatto.Tests;

//schema description strings are public prose, since they ship in the binary, the public mirror and the site manual at once
public class SchemaDescriptionsArePublicProse
{
    private static IEnumerable<(string Path, string Text)> Descriptions()
    {
        using var doc = JsonDocument.Parse(ConfigSchema.Text);
        var found = new List<(string, string)>();

        void Walk(JsonElement node, string path)
        {
            if (node.ValueKind != JsonValueKind.Object) return;
            if (node.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String)
                found.Add((path, d.GetString()!));
            foreach (var prop in node.EnumerateObject())
                if (prop.Value.ValueKind is JsonValueKind.Object)
                    Walk(prop.Value, path.Length == 0 ? prop.Name : $"{path}.{prop.Name}");
        }

        Walk(doc.RootElement, "");
        return found;
    }

    [Fact]
    public void EVERY_DESCRIPTION_IN_THE_SCHEMA_IS_PUBLIC_PROSE()
    {
        var failures = new List<string>();
        foreach (var (path, text) in Descriptions())
            foreach (var name in PublicProseStyle.Check(text))
                failures.Add($"{path} — {name} — {text}");

        Assert.True(failures.Count == 0,
            "schema descriptions must follow the public prose style:\n  " + string.Join("\n  ", failures));
    }

    //a scan that finds nothing reports a clean schema forever, so this pins the reached paths and a minimum count
    [Fact]
    public void THE_WALK_REACHES_EVERY_LEVEL_OF_THE_SCHEMA()
    {
        var paths = Descriptions().Select(d => d.Path).ToArray();

        Assert.Contains("properties.theme", paths);                                   //sample a description at the top level.
        Assert.Contains("properties.search.properties.tavily.properties.apiKey", paths); //sample a description three levels deep, so a scan that stops early fails here.
        Assert.Contains("properties.endpoints.additionalProperties.properties.base_url", paths);
        Assert.True(paths.Length >= 25, $"only {paths.Length} descriptions found; the walk is short");
    }
}

//each row is a sentence that breaks exactly one rule, so a validator that lost a rule fails here
public class PublicProseStyleTests
{
    [Theory]
    [InlineData("a line with an em dash — like this", "an em dash")]
    [InlineData("This opens with a capital", "a capitalised opening word")]
    [InlineData("ruled on 2026-08-29 and never revisited", "a date")]
    public void EACH_RULE_CATCHES_ITS_OWN_VIOLATION(string text, string expected)
    {
        Assert.Contains(expected, PublicProseStyle.Check(text));
    }

    [Fact]
    public void A_CLEAN_SENTENCE_PASSES()
    {
        //a clean sentence must pass, or a validator that flagged everything would still satisfy every other row
        Assert.Empty(PublicProseStyle.Check("lines scrolled per wheel notch."));
    }

    //a context size is not a date, so the pattern must stay narrow, since a bare year match would flag any number
    [Theory]
    [InlineData("2048 tokens is the floor")]
    [InlineData("the default port is 2024")]
    public void A_NUMBER_THAT_IS_NOT_A_DATE_IS_NOT_A_DATE(string text)
    {
        Assert.DoesNotContain("a date", PublicProseStyle.Check(text));
    }

    //pin every rule name, so a rule dropped in the extraction fails here by name
    [Fact]
    public void THE_EXTRACTION_KEPT_EVERY_RULE()
    {
        Assert.Equal(
            new[] { "a capitalised opening word", "a date", "an em dash" },
            PublicProseStyle.Violations.Select(v => v.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }
}
