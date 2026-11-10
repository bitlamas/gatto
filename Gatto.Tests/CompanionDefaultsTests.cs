using System.Text.Json;
using Gatto.Core;
using Gatto.Core.Home;

namespace Gatto.Tests;

//a contributed endpoint's defaults live in its extension's companion file, a configured endpoint's in gatto.json, and a reader with no owner scans
public sealed class CompanionDefaultsTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-companion-").FullName;

    public void Dispose() { try { Directory.Delete(_home, true); } catch { } }

    private string GattoJson => Path.Combine(_home, "gatto.json");

    private void WriteConfig(string endpoints = """{"local":{},"cloud":{"base_url":"https://cloud.example.test/v1"}}""") =>
        File.WriteAllText(GattoJson, $$"""{"endpoints":{{endpoints}},"default_endpoint":"local"}""");

    private void Companion(string name, string json)
    {
        Directory.CreateDirectory(Path.Combine(_home, "extensions"));
        File.WriteAllText(Path.Combine(_home, "extensions", name + ".json"), json);
    }

    private JsonElement Read(string name) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(_home, "extensions", name + ".json"))).RootElement;

    [Fact]
    public void A_contributed_endpoint_reads_and_writes_its_companion_and_leaves_gatto_json_byte_equal()
    {
        WriteConfig();
        var before = File.ReadAllBytes(GattoJson);

        CompanionDefaults.SetModel(_home, "grok", "hosted", "m1");
        CompanionDefaults.SetEffort(_home, "grok", "hosted", "m1", "high");

        var entry = Read("hosted").GetProperty("defaults").GetProperty("grok");
        Assert.Equal("m1", entry.GetProperty("model").GetString());
        Assert.Equal("high", entry.GetProperty("effort").GetProperty("m1").GetString());
        Assert.Equal("m1", CompanionDefaults.Model(_home, null, "grok", "hosted"));
        Assert.Equal("high", CompanionDefaults.Effort(_home, null, "grok", "hosted", "m1"));
        Assert.Equal(before, File.ReadAllBytes(GattoJson));
        Assert.Equal(Path.Combine(_home, "extensions", "hosted.json"), CompanionDefaults.CompanionPath(_home, "hosted"));
    }

    [Fact]
    public void A_reader_with_an_owner_never_touches_gatto_json()
    {
        Companion("hosted", """{"defaults":{"grok":{"model":"m1"}}}""");

        Assert.Equal("m1", CompanionDefaults.Model(_home, null, "grok", "hosted"));
        Assert.Null(CompanionDefaults.Effort(_home, null, "grok", "hosted", "m1"));
    }

    [Fact]
    public void A_configured_endpoint_still_writes_defaults_in_gatto_json()
    {
        WriteConfig();

        CompanionDefaults.SetModel(_home, "cloud", null, "c1");
        CompanionDefaults.SetEffort(_home, "cloud", null, "c1", "low");

        var entry = JsonDocument.Parse(File.ReadAllText(GattoJson)).RootElement.GetProperty("defaults").GetProperty("cloud");
        Assert.Equal("c1", entry.GetProperty("model").GetString());
        Assert.Equal("c1", CompanionDefaults.Model(_home, null, "cloud", null));
        Assert.Equal("low", CompanionDefaults.Effort(_home, GattoConfig.Load(_home), "cloud", null, "c1"));
        Assert.False(Directory.Exists(Path.Combine(_home, "extensions")) && Directory.GetFiles(Path.Combine(_home, "extensions"), "*.json").Length > 0);
    }

    [Fact]
    public void Two_endpoints_of_one_extension_have_two_rows()
    {
        CompanionDefaults.SetModel(_home, "fast", "hosted", "f1");
        CompanionDefaults.SetModel(_home, "deep", "hosted", "d1");

        Assert.Equal("f1", CompanionDefaults.Model(_home, null, "fast", "hosted"));
        Assert.Equal("d1", CompanionDefaults.Model(_home, null, "deep", "hosted"));
        var defaults = Read("hosted").GetProperty("defaults");
        Assert.Equal(["fast", "deep"], defaults.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public void Scan_finds_the_one_companion_for_an_unconfigured_endpoint_and_Model_reads_it()
    {
        WriteConfig();
        Companion("hosted", """{"defaults":{"grok":{"model":"m1"}}}""");
        Companion("other", """{"owner":"x"}""");

        var found = Assert.Single(CompanionDefaults.Scan(_home, "grok"));

        Assert.Equal(Path.Combine(_home, "extensions", "hosted.json"), found.File);
        Assert.Equal("m1", found.Model);
        Assert.Equal("m1", CompanionDefaults.Model(_home, null, "grok", null));
    }

    [Fact]
    public void Scan_with_two_companions_returns_both_in_ordinal_file_order_and_Model_returns_null()
    {
        WriteConfig();
        Companion("beta", """{"defaults":{"grok":{"model":"b1"}}}""");
        Companion("alpha", """{"defaults":{"grok":{"model":"a1"}}}""");

        var found = CompanionDefaults.Scan(_home, "grok");

        Assert.Equal(["alpha.json", "beta.json"], found.Select(f => Path.GetFileName(f.File)));
        Assert.Null(CompanionDefaults.Model(_home, null, "grok", null));
    }

    [Fact]
    public void Scan_skips_a_file_that_is_not_json_and_Broken_names_it()
    {
        WriteConfig();
        Companion("alpha", "{not json");
        Companion("beta", "[1]");
        Companion("hosted", """{"defaults":{"grok":{"model":"m1"}}}""");

        Assert.Equal("m1", Assert.Single(CompanionDefaults.Scan(_home, "grok")).Model);
        var broken = CompanionDefaults.Broken(_home);
        Assert.Equal(["alpha.json", "beta.json"], broken.Select(b => Path.GetFileName(b.File)));
        Assert.StartsWith(Path.Combine(_home, "extensions", "alpha.json") + " is not valid JSON: ", broken[0].Problem);
        Assert.Equal(Path.Combine(_home, "extensions", "beta.json") + " must be a JSON object", broken[1].Problem);
    }

    [Fact]
    public void A_writer_with_no_owner_for_an_unconfigured_endpoint_throws()
    {
        WriteConfig();
        const string message = "endpoint 'nowhere' is not configured in gatto.json and no extension owns it here";

        Assert.Equal(message, Assert.Throws<GattoConfigException>(() => CompanionDefaults.SetModel(_home, "nowhere", null, "m")).Message);
        Assert.Equal(message, Assert.Throws<GattoConfigException>(() => CompanionDefaults.SetEffort(_home, "nowhere", null, "m", "high")).Message);
    }

    [Fact]
    public void An_effort_for_another_model_is_null()
    {
        CompanionDefaults.SetEffort(_home, "grok", "hosted", "m1", "high");

        Assert.Null(CompanionDefaults.Effort(_home, null, "grok", "hosted", "m2"));
    }

    [Fact]
    public void An_entry_that_is_not_an_object_names_the_file()
    {
        Companion("hosted", """{"defaults":{"grok":"m1"}}""");

        var ex = Assert.Throws<GattoConfigException>(() => CompanionDefaults.Model(_home, null, "grok", "hosted"));

        Assert.Equal($"{Path.Combine(_home, "extensions", "hosted.json")} defaults.grok must be an object", ex.Message);
    }
}
