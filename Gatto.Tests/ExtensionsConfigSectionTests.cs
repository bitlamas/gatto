using System.Text.Json;
using Gatto.Cli;
using Gatto.Core.Home;

namespace Gatto.Tests;

//the extensions object is the one top-level key whose inner keys gatto does not validate, and the rest of gatto.json must stay strict beside it
public class ExtensionsConfigSectionTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-ext-cfg-").FullName;
    public void Dispose() { try { Directory.Delete(_home, true); } catch { } }

    private GattoConfig LoadWith(string extra)
    {
        File.WriteAllText(Path.Combine(_home, "gatto.json"),
            $"{{\"endpoints\":{{\"local\":{{\"base_url\":\"http://x\"}}}},\"default_endpoint\":\"local\"{extra}}}");
        return GattoConfig.Load(_home);
    }

    [Fact]
    public void AN_EXTENSIONS_OBJECT_IS_ACCEPTED_AND_ITS_INNER_KEYS_ARE_NOT_VALIDATED()
    {
        var cfg = LoadWith(""","extensions":{"tasks":{"owner":"gatto","anything":1}}""");
        Assert.Equal("gatto", cfg.Raw!.Value.GetProperty("extensions").GetProperty("tasks").GetProperty("owner").GetString());
    }

    [Theory]
    [InlineData(""","extensions":{"tasks":1}""", "gatto.json extensions.tasks must be an object")]
    [InlineData(""","extensions":{"tasks":{},"other":[]}""", "gatto.json extensions.other must be an object")]
    [InlineData(""","extensions":5""", "gatto.json has an \"extensions\" with the wrong type — must be an object")]
    public void AN_EXTENSIONS_VALUE_THAT_IS_NOT_AN_OBJECT_IS_REFUSED(string fragment, string expected)
    {
        var ex = Assert.Throws<GattoConfigException>(() => LoadWith(fragment));
        Assert.Equal(expected, ex.Message);
    }

    //the host refuses a key an extension names at the top level. the extensions object is the only way in, and a typo still fails loudly
    [Fact]
    public void AN_UNKNOWN_TOP_LEVEL_KEY_STILL_THROWS_BESIDE_AN_EXTENSIONS_OBJECT()
    {
        var ex = Assert.Throws<GattoConfigException>(() => LoadWith(""","extensions":{"tasks":{"owner":"x"}},"tasks":{"owner":"x"}"""));
        Assert.StartsWith("unknown key in gatto.json: tasks — known keys:", ex.Message);
    }

    [Fact]
    public void SECTION_READS_EXTENSIONS_FIRST_THEN_THE_TOP_LEVEL()
    {
        var raw = JsonDocument.Parse("{\"search\":{\"a\":1},\"extensions\":{\"search\":{\"a\":2},\"tasks\":{\"owner\":\"x\"}}}").RootElement.Clone();
        var section = GattoApp.SectionReader(raw);
        Assert.Equal(2, section("search")!.Value.GetProperty("a").GetInt32());
        Assert.Equal("x", section("tasks")!.Value.GetProperty("owner").GetString());
        Assert.Null(section("nothing"));
    }

    [Fact]
    public void SECTION_FALLS_BACK_TO_THE_TOP_LEVEL_WITHOUT_AN_EXTENSIONS_OBJECT()
    {
        var raw = JsonDocument.Parse("{\"search\":{\"a\":1}}").RootElement.Clone();
        var section = GattoApp.SectionReader(raw);
        Assert.Equal(1, section("search")!.Value.GetProperty("a").GetInt32());
        Assert.Null(section("tasks"));
        Assert.Null(GattoApp.SectionReader(null)("search"));
    }
}
