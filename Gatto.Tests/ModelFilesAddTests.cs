using System.Text.Json;
using Gatto.Core.Home;
using Gatto.Roles;

namespace Gatto.Tests;

//a new quant joins the list inactive, and a file whose quant label is already held or missing is refused
public class ModelFilesAddTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gatto-add-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string Models => Path.Combine(_dir, "models");
    private const string Q4 = @"C:\w\gemma-Q4_K_M.gguf";

    private void Existing(string id = "gemma")
    {
        Directory.CreateDirectory(Path.Combine(Models, id));
        File.WriteAllText(Path.Combine(Models, id, "profile.json"),
            $$"""
            {"files": [{"path": {{JsonSerializer.Serialize(Q4)}}, "quant": "Q4_K_M", "active": true}],
             "port": 1235, "context": 8192, "gpu_layers": 99}
            """);
    }

    private ModelProfile Reload(string id = "gemma") => Model.Load(Models, id).Profile;

    //the list grows, nothing else moves and the active file is unchanged
    [Fact]
    public void A_SECOND_QUANT_JOINS_THE_LIST_INACTIVE()
    {
        Existing();

        ModelFiles.Add(Models, "gemma", @"C:\w\gemma-Q6_K.gguf");

        var profile = Reload();
        Assert.Equal(2, profile.Files.Count);
        Assert.Equal("Q4_K_M", profile.Files.Single(f => f.Active).Quant);
        var added = profile.Files.Single(f => f.Quant == "Q6_K");
        Assert.False(added.Active);
        Assert.EndsWith("gemma-Q6_K.gguf", added.Path, StringComparison.OrdinalIgnoreCase);
    }

    //the profile is edited as a document, a round trip through the record would drop the user's tuning keys
    [Fact]
    public void AND_EVERY_OTHER_KEY_IS_UNTOUCHED()
    {
        Existing();
        var path = Path.Combine(Models, "gemma", "profile.json");

        ModelFiles.Add(Models, "gemma", @"C:\w\gemma-Q6_K.gguf");

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(1235, doc.RootElement.GetProperty("port").GetInt32());
        Assert.Equal(8192, doc.RootElement.GetProperty("context").GetInt32());
        Assert.Equal(99, doc.RootElement.GetProperty("gpu_layers").GetInt32());
    }

    //two files with one label make /model quant ambiguous, and the switch picks whichever it meets first
    [Fact]
    public void A_LABEL_THE_MODEL_ALREADY_HAS_IS_REFUSED()
    {
        Existing();

        var ex = Assert.Throws<GattoConfigException>(
            () => ModelFiles.Add(Models, "gemma", @"C:\w\other-Q4_K_M.gguf"));

        Assert.Contains("already has a file called 'Q4_K_M'", ex.Message, StringComparison.Ordinal);
        //the refusal must also name the options the model holds
        Assert.Contains("it holds Q4_K_M", ex.Message, StringComparison.Ordinal);
        Assert.Single(Reload().Files);
    }

    //an unlabelled file would be listed but unreachable, every /model quant verb addresses a file by its label
    [Fact]
    public void A_FILE_WITH_NO_QUANT_IN_ITS_NAME_IS_REFUSED()
    {
        Existing();

        var ex = Assert.Throws<GattoConfigException>(
            () => ModelFiles.Add(Models, "gemma", @"C:\w\mystery-weights.gguf"));

        Assert.Contains("no command could name", ex.Message, StringComparison.Ordinal);
        Assert.Single(Reload().Files);
    }

    //a repeat add of a listed file must be refused, a second entry would be two names for one file
    [Fact]
    public void A_FILE_ALREADY_LISTED_IS_REFUSED()
    {
        Existing();

        var ex = Assert.Throws<GattoConfigException>(() => ModelFiles.Add(Models, "gemma", Q4));

        Assert.Contains("already lists gemma-Q4_K_M.gguf", ex.Message, StringComparison.Ordinal);
        Assert.Single(Reload().Files);
    }

    //the known match for the refusal tests, so their unchanged counts can't pass against an add that never writes
    [Fact]
    public void AND_THE_LIST_REALLY_DOES_GROW_WHEN_THE_ADD_IS_LEGAL()
    {
        Existing();

        ModelFiles.Add(Models, "gemma", @"C:\w\gemma-Q6_K.gguf");

        Assert.Equal(2, Reload().Files.Count);
    }

    //the add must touch exactly one file, and the test sweeps the whole home so nothing else moves
    [Fact]
    public void THE_ADD_TOUCHES_EXACTLY_ONE_FILE_so_badges_and_every_other_sidecar_survive_it()
    {
        Existing();
        //the sweep roots at the home that holds the badge folder too, and the folder name comes from the production constant
        var audition = Path.Combine(_dir, Gatto.Core.Acquire.BadgeRegister.DirectoryName);
        Directory.CreateDirectory(audition);
        var badge = Path.Combine(audition, "gemma-Q4_K_M.json");
        File.WriteAllText(badge, "{\"schema\": 3}");
        var before = Directory.GetFiles(_dir, "*", SearchOption.AllDirectories)
            .ToDictionary(f => f, File.ReadAllText, StringComparer.OrdinalIgnoreCase);

        ModelFiles.Add(Models, "gemma", @"C:\w\gemma-Q6_K.gguf");

        var after = Directory.GetFiles(_dir, "*", SearchOption.AllDirectories)
            .ToDictionary(f => f, File.ReadAllText, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(before.Keys.OrderBy(k => k), after.Keys.OrderBy(k => k));
        var changed = after.Where(kv => before[kv.Key] != kv.Value).Select(kv => kv.Key).ToList();
        Assert.Equal([Path.Combine(Models, "gemma", "profile.json")], changed);
        //keep the explicit assertion, the badge's survival must be stated rather than implied
        Assert.Equal("{\"schema\": 3}", File.ReadAllText(badge));
    }

    //a model that will not load is refused before anything is written, the add goes through Model.Load
    [Fact]
    public void AN_UNREADABLE_MODEL_IS_REFUSED_BY_THE_LOADER()
    {
        Directory.CreateDirectory(Path.Combine(Models, "broken"));
        File.WriteAllText(Path.Combine(Models, "broken", "profile.json"), "{ not json");

        Assert.ThrowsAny<Exception>(() => ModelFiles.Add(Models, "broken", @"C:\w\gemma-Q6_K.gguf"));
    }
}
