using Gatto.Roles;

namespace Gatto.Tests;

//a model holds several files, so the check must index all of them and not just the active one
public class ModelFilesAreAllConfiguredTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-allfiles-").FullName;
    public void Dispose() { try { Directory.Delete(_home, true); } catch { } }

    private string Models => Path.Combine(_home, "models");
    private string Weights => Path.Combine(_home, "weights");

    private (string Active, string Idle) TwoFileModel(string id = "gemma")
    {
        Directory.CreateDirectory(Weights);
        var active = Path.Combine(Weights, "gemma-Q4_K_M.gguf");
        var idle = Path.Combine(Weights, "gemma-Q6_K.gguf");
        File.WriteAllBytes(active, Gguf("gemma"));
        File.WriteAllBytes(idle, Gguf("gemma"));

        Directory.CreateDirectory(Path.Combine(Models, id));
        File.WriteAllText(Path.Combine(Models, id, "profile.json"),
            $$"""
            {"files": [{"path": {{Json(active)}}, "quant": "Q4_K_M", "active": true},
                       {"path": {{Json(idle)}}, "quant": "Q6_K", "active": false}],
             "port": 1235, "context": 8192}
            """);
        return (active, idle);
    }

    private static string Json(string path) => System.Text.Json.JsonSerializer.Serialize(path);

    //two quants of one model share general.name, which is what makes the id collide
    private static byte[] Gguf(string name) => GgufTestBytes.SyntheticHeader(kv =>
    {
        kv.Str("general.architecture", "gemma3");
        kv.Str("general.name", name);
        kv.U32("gemma3.context_length", 4096);
    });

    //an idle file inside a model must not appear unconfigured, or the shelf offers a file gatto already holds
    [Fact]
    public void AN_IDLE_FILE_IS_NOT_UNCONFIGURED()
    {
        var (active, idle) = TwoFileModel();

        var loose = ModelScaffold.FindUnconfigured(Weights, Models);

        Assert.DoesNotContain(idle, loose, StringComparer.OrdinalIgnoreCase);
        //plant a loose file the sweep must find, or a sweep returning nothing passes the negative
        Assert.DoesNotContain(active, loose, StringComparer.OrdinalIgnoreCase);
        var third = Path.Combine(Weights, "someone-else-Q4_K_M.gguf");
        File.WriteAllBytes(third, Gguf("someone-else"));
        Assert.Contains(third, ModelScaffold.FindUnconfigured(Weights, Models), StringComparer.OrdinalIgnoreCase);
    }

    //a null answer sends the flow to the collision screen for a file the model already lists
    [Fact]
    public void AN_IDLE_FILE_ALREADY_HAS_ITS_MODEL()
    {
        var (_, idle) = TwoFileModel();

        Assert.Equal("gemma", ModelScaffold.ModelFor(Models, idle));
    }

    //the model lookup and the collision check must agree, or the flow asks the user to choose between a model and itself
    [Fact]
    public void AND_AN_IDLE_FILE_IS_NOT_A_COLLISION()
    {
        var (_, idle) = TwoFileModel();

        Assert.Null(ModelScaffold.CollidingModel(Models, idle));
    }
}
