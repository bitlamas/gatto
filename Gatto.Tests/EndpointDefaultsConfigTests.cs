using Gatto.Core.Home;

namespace Gatto.Tests;

public class EndpointDefaultsConfigTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-defaults-").FullName;

    public void Dispose() { try { Directory.Delete(_home, true); } catch (IOException) { } }

    private GattoConfig Load(string json)
    {
        File.WriteAllText(Path.Combine(_home, "gatto.json"), json);
        return GattoConfig.Load(_home);
    }

    private const string Eps = "\"endpoints\":{\"local\":{},\"cloud\":{\"base_url\":\"https://x.test\"}}";

    [Fact]
    public void Each_endpoint_reads_its_own_entry()
    {
        var c = Load("{" + Eps + ",\"default_endpoint\":\"local\",\"defaults\":{\"local\":{\"model\":\"q\"},\"cloud\":{\"model\":\"c1\",\"effort\":{\"c1\":\"high\"}}}}");
        Assert.Equal("q", c.ModelDefaultFor("local"));
        Assert.Equal("c1", c.ModelDefaultFor("cloud"));
        Assert.Equal("high", c.EffortDefaultFor("cloud", "c1"));
        Assert.Null(c.EffortDefaultFor("cloud", "c2"));
        Assert.Equal("q", c.DefaultModel);
    }

    [Fact]
    public void The_legacy_key_reads_as_the_default_endpoints_entry()
    {
        var c = Load("{" + Eps + ",\"default_endpoint\":\"cloud\",\"default_model\":\"c1\"}");
        Assert.Equal("c1", c.ModelDefaultFor("cloud"));
        Assert.Null(c.ModelDefaultFor("local"));
    }

    [Fact]
    public void Both_keys_equal_load_and_both_different_refuse()
    {
        Assert.Equal("q", Load("{" + Eps + ",\"default_endpoint\":\"local\",\"default_model\":\"q\",\"defaults\":{\"local\":{\"model\":\"q\"}}}").DefaultModel);
        var ex = Assert.Throws<GattoConfigException>(() =>
            Load("{" + Eps + ",\"default_endpoint\":\"local\",\"default_model\":\"q\",\"defaults\":{\"local\":{\"model\":\"r\"}}}"));
        Assert.Contains("'q'", ex.Message);
        Assert.Contains("'r'", ex.Message);
    }

    [Fact]
    public void An_entry_for_an_endpoint_nobody_defines_loads_and_an_empty_entry_means_nothing_saved()
    {
        var c = Load("{" + Eps + ",\"default_endpoint\":\"local\",\"defaults\":{\"gone\":{\"model\":\"x\"},\"cloud\":{},\"local\":{\"model\":null}}}");
        Assert.Equal("x", c.ModelDefaultFor("gone"));
        Assert.Null(c.ModelDefaultFor("cloud"));
        Assert.Null(c.ModelDefaultFor("local"));
    }

    [Fact]
    public void A_default_endpoint_outside_gatto_json_loads_since_an_extension_may_contribute_it()
    {
        Assert.Equal("acme", Load("{" + Eps + ",\"default_endpoint\":\"acme\"}").DefaultEndpoint);
    }

    [Theory]
    [InlineData("{\"defaults\":[]}", "\"defaults\"")]
    [InlineData("{\"defaults\":{\"cloud\":\"x\"}}", "defaults.cloud")]
    [InlineData("{\"defaults\":{\"cloud\":{\"model\":3}}}", "defaults.cloud.model")]
    [InlineData("{\"defaults\":{\"cloud\":{\"effort\":{\"c1\":3}}}}", "defaults.cloud.effort.c1")]
    [InlineData("{\"defaults\":{\"cloud\":{\"colour\":\"red\"}}}", "colour")]
    public void A_bad_shape_refuses_and_names_where(string fragment, string named)
    {
        var json = "{" + Eps + ",\"default_endpoint\":\"local\"," + fragment[1..];
        var ex = Assert.Throws<GattoConfigException>(() => Load(json));
        Assert.Contains(named, ex.Message);
    }

    [Fact]
    public void A_configured_endpoint_keeps_its_models_in_order()
    {
        var c = Load("{\"endpoints\":{\"local\":{},\"cloud\":{\"base_url\":\"https://x.test\",\"models\":[\"b\",\"a\"]}},\"default_endpoint\":\"local\"}");
        Assert.Equal(new[] { "b", "a" }, c.Endpoints["cloud"].Models);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[\"a\",\"a\"]")]
    [InlineData("[\"\"]")]
    [InlineData("\"a\"")]
    public void A_models_list_that_is_empty_repeats_holds_a_blank_or_is_no_list_refuses(string models)
    {
        var ex = Assert.Throws<GattoConfigException>(() =>
            Load("{\"endpoints\":{\"cloud\":{\"base_url\":\"https://x.test\",\"models\":" + models + "}},\"default_endpoint\":\"cloud\"}"));
        Assert.Contains("models", ex.Message);
    }
}
