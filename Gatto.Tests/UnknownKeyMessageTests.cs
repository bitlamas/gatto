using Gatto.Core.Home;

namespace Gatto.Tests;

//an unknown key must name the keys that would be accepted. every call site here gets its own row, or the rule is only tested where a test happens to drive
public class UnknownKeyMessageTests
{
    private static string NewHome()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gatto-msg-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string LoadError(string configBody)
    {
        var home = NewHome();
        File.WriteAllText(Path.Combine(home, "gatto.json"), configBody);
        return Assert.Throws<GattoConfigException>(() => GattoConfig.Load(home)).Message;
    }

    private const string Eps = "\"endpoints\":{\"local\":{\"base_url\":\"http://x\"}},\"default_endpoint\":\"local\"";

    //site 1: the top level

    [Fact]
    public void SITE_1_TOP_LEVEL_NAMES_THE_TOP_LEVEL_KEYS()
    {
        var msg = LoadError($"{{{Eps},\"serach\":true}}");

        Assert.Contains("unknown key in gatto.json: serach", msg);

        //split the clause and compare sets, Contains-per-key only works while no key is a substring of another and nothing enforces that
        const string marker = "known keys: ";
        var at = msg.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(at >= 0, $"the refusal names no known keys: {msg}");
        var offered = msg[(at + marker.Length)..].Split(',').Select(s => s.Trim()).ToArray();

        Assert.Equal(GattoConfig.TopKeys, offered);
    }

    //site 2: an endpoint

    [Fact]
    public void SITE_2_AN_ENDPOINT_NAMES_THE_ENDPOINT_KEYS()
    {
        var msg = LoadError(
            "{\"endpoints\":{\"local\":{\"base_url\":\"http://x\",\"baseurl\":\"http://y\"}},\"default_endpoint\":\"local\"}");

        Assert.Contains("unknown key in gatto.json endpoint 'local': baseurl", msg);
        Assert.Contains("known keys: base_url, key_env, context, thinking", msg);
        //an endpoint's refusal must not name the top level's keys, one helper appending every array would leak them in
        Assert.DoesNotContain("default_endpoint", msg);
    }

    //sites 3-5: search and its two provider sections

    [Fact]
    public void SITE_3_SEARCH_NAMES_THE_SEARCH_KEYS()
    {
        var msg = LoadError($"{{{Eps},\"search\":{{\"provider\":\"ddg\"}}}}");

        Assert.Contains("unknown key in gatto.json search: provider", msg);
        Assert.Contains("known keys: providers, tavily, searxng", msg);
    }

    [Fact]
    public void SITE_4_SEARCH_TAVILY_NAMES_ITS_ONE_KEY()
    {
        var msg = LoadError($"{{{Eps},\"search\":{{\"tavily\":{{\"api_key\":\"x\"}}}}}}");

        Assert.Contains("unknown key in gatto.json search.tavily: api_key", msg);
        Assert.Contains("known keys: apiKey", msg);
        //apiKey is the only camelCase key, so a user typing api_key has to be shown that spelling
        Assert.DoesNotContain("providers", msg);
    }

    [Fact]
    public void SITE_5_SEARCH_SEARXNG_NAMES_ITS_ONE_KEY()
    {
        var msg = LoadError($"{{{Eps},\"search\":{{\"searxng\":{{\"base_url\":\"http://x\"}}}}}}");

        Assert.Contains("unknown key in gatto.json search.searxng: base_url", msg);
        Assert.Contains("known keys: url", msg);
    }

    //sites 6-7: the two gatto.json callers of the shared helper

    [Fact]
    public void SITE_6_CONTEXT_FILES_NAMES_ITS_KEYS_THROUGH_THE_SHARED_HELPER()
    {
        var msg = LoadError($"{{{Eps},\"context_files\":{{\"homes\":true}}}}");

        Assert.Contains("unknown key in gatto.json context_files: homes", msg);
        Assert.Contains("known keys: compat, home", msg);
    }

    [Fact]
    public void SITE_7_MEMORY_NAMES_ITS_KEYS_THROUGH_THE_SHARED_HELPER()
    {
        var msg = LoadError($"{{{Eps},\"memory\":{{\"budget\":10}}}}");

        Assert.Contains("unknown key in gatto.json memory: budget", msg);
        Assert.Contains("known keys: enabled, index_budget", msg);
    }

    //site 8: the same helper, outside gatto.json

    //changing the shared helper changes the model profile message too, though a profile's memory takes index_budget alone
    [Fact]
    public void SITE_8_A_MODEL_PROFILE_KEEPS_ITS_WHERE_AND_GAINS_ITS_LIST()
    {
        var models = NewHome();
        var dir = Path.Combine(models, "m1");
        Directory.CreateDirectory(dir);
        //a minimal valid profile plus the offending key, since files, port and context are required and a missing one fails with a different message
        File.WriteAllText(Path.Combine(dir, "profile.json"),
            """{"files": [{ "path": "C:\\w.gguf", "active": true }],"port":1235,"context":8192,"memory":{"enabled":false}}""");

        var msg = Assert.Throws<GattoConfigException>(() => Gatto.Roles.Model.Load(models, "m1")).Message;

        Assert.Contains("unknown key in model 'm1' profile at ", msg);   //the caller's where string stays in the message
        Assert.Contains("memory: enabled", msg);
        Assert.Contains("known keys: index_budget", msg);
        //the refusal must not offer gatto.json's memory list (its enabled key doesn't belong in a profile)
        Assert.DoesNotContain("known keys: enabled", msg);
    }

    //the project file's sentence, rendered from its arrays

    //the sentence is rendered from the arrays, and the keys it offers must be exactly the ones the reader takes
    [Fact]
    public void THE_PROJECT_FILES_SENTENCE_IS_RENDERED_FROM_ITS_ARRAYS()
    {
        var dir = NewHome();
        File.WriteAllText(Path.Combine(dir, ".gatto.json"), """{"memory":{"budget":1}}""");

        var msg = Assert.Throws<GattoConfigException>(() => ProjectFileConfig.TryReadMemoryEnabled(dir)).Message;

        Assert.Contains(".gatto.json accepts only ", msg);
        var offered = msg[(msg.IndexOf("accepts only ", StringComparison.Ordinal) + 13)..]
            .Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();

        Assert.Equal(
            new[] { "auto_compact", "context_files.compat", "memory.enabled", "wild" },
            offered.OrderBy(s => s, StringComparer.Ordinal).ToArray());
    }
}
