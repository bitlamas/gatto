using System.Text.Json;
using Gatto.Cli;
using Gatto.Roles.Models;
using Gatto.Terminal;

namespace Gatto.Tests;

//what the /model settings row and the /effort sentence row read from a model's own map
public class ModelLevelsTests
{
    private static Dictionary<string, JsonElement?> Map(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement?>>(json)!;

    //the shape the dev home's levelled profiles hold, keys written out of the enum's order on purpose
    private static readonly string Levelled =
        """{"xhigh": {"chat_template_kwargs": {"enable_thinking": true, "reasoning_effort": "xhigh"}}, "none": {"chat_template_kwargs": {"enable_thinking": false}}, "medium": {"reasoning_effort": "medium"}, "low": {"reasoning_effort": "low"}}""";

    private const string ToggleMap = """{"none": {}, "high": {"reasoning_effort": "high"}}""";

    [Fact]
    public void A_levelled_map_offers_its_levels_in_the_enums_order()
    {
        var (levels, _) = ModelLevels.For(Map(Levelled), null, knownToggle: false, thinkOnByDefault: true);

        Assert.Equal(["none", "low", "medium", "xhigh"], levels);
    }

    [Fact]
    public void With_no_saved_default_the_row_shows_medium_and_a_saved_level_off_the_map_shows_where_it_lands()
    {
        Assert.Equal("medium", ModelLevels.For(Map(Levelled), null, false, true).Default);
        Assert.Equal("medium", ModelLevels.For(Map(Levelled), ThinkingLevel.High, false, true).Default);
        Assert.Equal("xhigh", ModelLevels.For(Map(Levelled), ThinkingLevel.Max, false, true).Default);
    }

    [Fact]
    public void A_toggle_map_offers_none_and_on_and_its_default_is_one_of_them()
    {
        var (levels, saved) = ModelLevels.For(Map(ToggleMap), ThinkingLevel.None, false, true);
        Assert.Equal(ModelLevels.Toggle, levels);
        Assert.Equal("none", saved);
        Assert.Equal("on", ModelLevels.For(Map(ToggleMap), null, false, thinkOnByDefault: true).Default);
    }

    //a map-less model's toggle is the server's sniff, so only the model in use can say it has one
    [Fact]
    public void A_map_less_model_has_levels_only_when_its_toggle_is_known()
    {
        Assert.Equal((null, null), ModelLevels.For(null, ThinkingLevel.Max, knownToggle: false, thinkOnByDefault: true));
        Assert.Equal((ModelLevels.Toggle, "on"), ModelLevels.For(null, ThinkingLevel.Max, knownToggle: true, thinkOnByDefault: true));
    }

    [Fact]
    public void On_writes_the_models_own_on_level_and_a_named_level_writes_itself()
    {
        Assert.Equal(ThinkingLevel.High, ModelLevels.Resolve(Map(ToggleMap), "on"));
        Assert.Equal(ThinkingLevel.None, ModelLevels.Resolve(Map(ToggleMap), "none"));
        Assert.Equal(ThinkingLevel.Max, ModelLevels.Resolve(null, "on"));
        Assert.Equal(ThinkingLevel.Medium, ModelLevels.Resolve(Map(Levelled), "medium"));
    }

    [Fact]
    public void The_sentence_prints_the_fragment_as_it_is_sent()
    {
        var map = Map(Levelled);
        string Of(ThinkingLevel l)
        {
            var (body, suffix) = Thinking.ResolveEntry(map, l);
            return ModelLevels.Sentence(body, suffix, GlyphSet.Unicode);
        }

        Assert.Equal("reasoning_effort: medium", Of(ThinkingLevel.Medium));
        Assert.Equal("""chat_template_kwargs: {"enable_thinking":true,"reasoning_effort":"xhigh"}""", Of(ThinkingLevel.XHigh));
    }

    //a suffix is sent with every message, so a body of only a suffix still sends something
    [Fact]
    public void A_suffix_prints_and_only_an_entry_sending_nothing_says_so()
    {
        var map = Map("""{"none": {"prompt_suffix": "/no_think"}, "low": {}, "high": {"reasoning_effort": "high", "prompt_suffix": "/think"}}""");
        string Of(ThinkingLevel l)
        {
            var (body, suffix) = Thinking.ResolveEntry(map, l);
            return ModelLevels.Sentence(body, suffix, GlyphSet.Unicode);
        }

        Assert.Equal("prompt_suffix: /no_think", Of(ThinkingLevel.None));
        Assert.Equal(ModelLevels.NothingSent, Of(ThinkingLevel.Low));
        Assert.Equal("reasoning_effort: high · prompt_suffix: /think", Of(ThinkingLevel.High));
    }
}
