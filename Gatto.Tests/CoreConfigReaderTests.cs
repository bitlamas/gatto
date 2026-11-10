using System.Text.Json;
using Gatto.Cli;
using Gatto.Core;
using Gatto.Core.Home;

namespace Gatto.Tests;

//gatto.json holds what core owns: a script reads core keys only, and an extensions object refuses like any retired key
public class CoreConfigReaderTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-core-cfg-").FullName;
    public void Dispose() { try { Directory.Delete(_home, true); } catch { } }

    private GattoConfig LoadWith(string extra)
    {
        File.WriteAllText(Path.Combine(_home, "gatto.json"),
            $"{{\"endpoints\":{{\"local\":{{\"base_url\":\"http://x\"}}}},\"default_endpoint\":\"local\"{extra}}}");
        return GattoConfig.Load(_home);
    }

    [Fact]
    public void A_gatto_json_with_extensions_refuses_at_load_naming_the_key()
    {
        var ex = Assert.Throws<GattoConfigException>(() => LoadWith(""","extensions":{"tasks":{"owner":"x"}}"""));

        Assert.StartsWith("unknown key in gatto.json: extensions", ex.Message);
        var dash = ex.Message.IndexOf('—');
        Assert.True(dash > 0, ex.Message);
        Assert.Contains("known keys", ex.Message[dash..]);
    }

    [Fact]
    public void Core_serves_a_top_level_key_and_null_for_a_name_TopKeys_lacks()
    {
        var raw = JsonDocument.Parse("""{"search":{"a":1},"tasks":{"owner":"x"}}""").RootElement.Clone();
        var core = GattoApp.CoreReader(raw);

        Assert.Equal(1, core("search")!.Value.GetProperty("a").GetInt32());
        Assert.Null(core("tasks"));
        Assert.Null(GattoApp.CoreReader(null)("search"));
    }

    [Fact]
    public void Core_serves_no_key_outside_TopKeys()
    {
        var names = GattoConfig.TopKeys.Concat(["tasks", "extensions", "grok"]).ToList();
        var raw = JsonDocument.Parse("{" + string.Join(",", names.Select(n => $"\"{n}\":1")) + "}").RootElement.Clone();
        var core = GattoApp.CoreReader(raw);

        foreach (var name in names)
            Assert.True(GattoConfig.TopKeys.Contains(name) == (core(name) is not null), name);
    }
}
