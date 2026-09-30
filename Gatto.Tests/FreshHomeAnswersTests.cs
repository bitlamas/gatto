using System.Text.Json;
using Gatto.Core.Home;

namespace Gatto.Tests;

//start from a home a first launch scaffolds, then read the file $schema points at and require the six tokens a fresh session needs
public class FreshHomeAnswersTests
{
    //each token is a quoted json key, a bare url also matches inside base_url and a renamed key would pass
    private static readonly string[] WhatTheSessionNeeded =
        { "\"search\"", "\"providers\"", "\"tavily\"", "\"apiKey\"", "\"searxng\"", "\"url\"" };

    [Fact]
    public void A_FRESH_HOME_ANSWERS_HOW_DO_I_ADD_A_SEARCH_PROVIDER()
    {
        var home = Path.Combine(Path.GetTempPath(), "gatto-walk-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(home);
        GattoHome.EnsureInitialized(home);

        var configPath = Path.Combine(home, "gatto.json");
        var config = File.ReadAllText(configPath);

        //resolve $schema against the folder of the file that names it, a hard-coded name would stay green when the pointer has a typo
        using var doc = JsonDocument.Parse(config);
        Assert.True(doc.RootElement.TryGetProperty("$schema", out var crumb),
            "a fresh gatto.json carries no $schema, so a reader has nowhere to go from here");
        var target = crumb.GetString();
        Assert.False(string.IsNullOrWhiteSpace(target), "the breadcrumb is empty");

        var resolved = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(configPath)!, target!));
        Assert.True(File.Exists(resolved),
            $"the breadcrumb points at {target} and there is no file there: {resolved}");

        var answer = File.ReadAllText(resolved);
        var absent = WhatTheSessionNeeded.Where(t => !answer.Contains(t, StringComparison.Ordinal)).ToArray();
        Assert.True(absent.Length == 0,
            "following $schema from a fresh gatto.json reaches a document that cannot answer "
            + "\"how do I add a search provider\" — missing: " + string.Join(", ", absent));
    }

    //the schema must hold the shape as structure, a sentence would satisfy the text test and still leave a reader guessing
    [Fact]
    public void THE_ANSWER_IS_THE_SHAPE_NOT_A_MENTION_OF_IT()
    {
        var home = Path.Combine(Path.GetTempPath(), "gatto-walk2-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(home);
        GattoHome.EnsureInitialized(home);

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(home, "gatto.schema.json")));
        var search = doc.RootElement.GetProperty("properties").GetProperty("search").GetProperty("properties");

        var providers = search.GetProperty("providers").GetProperty("items").GetProperty("enum")
            .EnumerateArray().Select(v => v.GetString()).ToArray();
        Assert.Equal(new[] { "ddg", "tavily", "searxng" }, providers);

        Assert.True(search.GetProperty("tavily").GetProperty("properties").TryGetProperty("apiKey", out _));
        Assert.True(search.GetProperty("searxng").GetProperty("properties").TryGetProperty("url", out _));
    }
}
