using System.Text.Json;
using Gatto.Core;
using Gatto.Core.Home;

namespace Gatto.Tests;

//an extension's own settings file beside its script, created on the first write and never on a read
public sealed class ExtensionSettingsTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-ext-settings-").FullName;

    public void Dispose() { try { Directory.Delete(_home, true); } catch { } }

    private ExtensionSettings New(string name = "alpha") => new(_home, name);

    private void Plant(string text, string name = "alpha")
    {
        Directory.CreateDirectory(Path.Combine(_home, "extensions"));
        File.WriteAllText(Path.Combine(_home, "extensions", name + ".json"), text);
    }

    [Fact]
    public void Set_creates_the_file_and_Get_reads_it_back()
    {
        var s = New();
        s.Set("owner", "gatto");
        s.Set("count", 3);
        s.Set("on", true);
        s.Set("nested", new Dictionary<string, int> { ["a"] = 1 });

        Assert.Equal(Path.Combine(_home, "extensions", "alpha.json"), s.Path);
        Assert.True(File.Exists(s.Path));
        var again = New();
        Assert.Equal("gatto", again.Get<string>("owner"));
        Assert.Equal(3, again.Get<int>("count"));
        Assert.True(again.Get<bool>("on"));
        Assert.Equal(1, again.Get<JsonElement>("nested").GetProperty("a").GetInt32());
    }

    [Fact]
    public void A_run_with_no_Set_creates_nothing()
    {
        var s = New();
        Assert.Null(s.Get<string>("owner"));
        Assert.Empty(s.Keys());
        s.Declare("on", SettingKind.Bool, "a toggle", false);

        Assert.False(File.Exists(s.Path));
        Assert.False(Directory.Exists(Path.Combine(_home, "extensions")));
    }

    [Fact]
    public void Set_with_null_removes_and_Remove_removes()
    {
        var s = New();
        s.Set("a", "1");
        s.Set("b", "2");
        s.Set("c", new Dictionary<string, string> { ["x"] = "y" });

        s.Set("a", null);
        s.Remove("b");

        Assert.Null(s.Get<string>("a"));
        Assert.Null(s.Get<string>("b"));
        Assert.Equal(["c"], s.Keys());
        Assert.Equal("""{"x":"y"}""", s.Get<JsonElement>("c").GetRawText().Replace(" ", "").Replace("\n", "").Replace("\r", ""));
    }

    [Fact]
    public void Keys_lists_present_keys_only_and_Declared_lists_declarations()
    {
        Plant("""{"loose":1}""");
        var s = New();
        s.Declare("on", SettingKind.Bool, "a toggle", false);

        Assert.Equal(["loose"], s.Keys());
        var d = Assert.Single(s.Declared);
        Assert.Equal(new SettingDeclaration("on", SettingKind.Bool, "a toggle", false), d);
    }

    [Fact]
    public void Defaults_is_reserved()
    {
        Plant("""{"defaults":{"grok":{"model":"m"}},"k":1}""");
        var s = New();

        Assert.Equal(["k"], s.Keys());
        var set = Assert.Throws<GattoConfigException>(() => s.Set("defaults", 1));
        Assert.Equal($"{s.Path} defaults is gatto's own key", set.Message);
        var remove = Assert.Throws<GattoConfigException>(() => s.Remove("defaults"));
        Assert.Equal($"{s.Path} defaults is gatto's own key", remove.Message);
    }

    [Fact]
    public void A_file_that_is_not_an_object_refuses_before_writing()
    {
        Plant("[1]");
        var s = New();

        var ex = Assert.Throws<GattoConfigException>(() => s.Set("k", 1));

        Assert.Equal($"{s.Path} must be a JSON object", ex.Message);
        Assert.Equal("[1]", File.ReadAllText(s.Path));
    }

    [Fact]
    public void Get_on_a_file_that_is_not_an_object_throws_the_same_message()
    {
        Plant("[1]");
        var s = New();

        Assert.Equal($"{s.Path} must be a JSON object", Assert.Throws<GattoConfigException>(() => s.Get<string>("k")).Message);
        Assert.Equal($"{s.Path} must be a JSON object", Assert.Throws<GattoConfigException>(() => s.Keys()).Message);
    }

    [Fact]
    public void A_file_that_is_not_json_names_the_file()
    {
        Plant("{not json");
        var s = New();

        var ex = Assert.Throws<GattoConfigException>(() => s.Get<string>("k"));

        Assert.StartsWith($"{s.Path} is not valid JSON: ", ex.Message);
    }

    [Fact]
    public void A_value_of_the_wrong_shape_names_the_key()
    {
        Plant("""{"count":"three"}""");
        var s = New();

        var ex = Assert.Throws<GattoConfigException>(() => s.Get<int>("count"));

        Assert.Equal($"{s.Path} count has the wrong type", ex.Message);
    }

    [Fact]
    public void A_held_target_throws_could_not_write_and_keeps_the_old_file()
    {
        Plant("""{"k":1}""");
        var s = New();

        using (new FileStream(s.Path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var ex = Assert.Throws<GattoConfigException>(() => s.Set("k", 2));
            Assert.StartsWith($"could not write {s.Path}: ", ex.Message);
        }

        Assert.Equal("""{"k":1}""", File.ReadAllText(s.Path));
    }

    [Fact]
    public void A_held_file_that_cannot_be_read_says_could_not_read()
    {
        Plant("""{"k":1}""");
        var s = New();

        using (new FileStream(s.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var ex = Assert.Throws<GattoConfigException>(() => s.Get<int>("k"));
            Assert.StartsWith($"could not read {s.Path}: ", ex.Message);
        }
    }

    [Fact]
    public void No_temp_file_survives_a_failed_write()
    {
        Plant("""{"k":1}""");
        var s = New();

        using (new FileStream(s.Path, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Throws<GattoConfigException>(() => s.Set("k", 2));

        Assert.Equal(["alpha.json"], Directory.GetFiles(Path.Combine(_home, "extensions")).Select(Path.GetFileName));
    }
}
