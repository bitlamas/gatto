using System.Text.Json;
using Gatto.Core.Client;
using Gatto.Core.Home;
using Gatto.Roles;

namespace Gatto.Tests;

public class ModelSwitchTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-ms-home-").FullName;
    private readonly string _modelsDir = Directory.CreateTempSubdirectory("gatto-ms-models-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch { }
        try { Directory.Delete(_modelsDir, recursive: true); } catch { }
    }

    private void CreateModel(string modelId, string modelPath = "model.gguf", int port = 1235, int context = 4096)
    {
        var modelDir = Path.Combine(_modelsDir, modelId);
        Directory.CreateDirectory(modelDir);

        var profileJson = $$"""
            {
              "files": [{ "path": "{{modelPath.Replace("\\", "\\\\")}}", "active": true }],
              "port": {{port}},
              "context": {{context}}
            }
            """;
        File.WriteAllText(Path.Combine(modelDir, "profile.json"), profileJson);
    }

    private void CreateGattoJson(string defaultModel = "qwen3.6-35b")
    {
        var json = $$"""
            {
              "endpoints": {
                "local": {
                  "base_url": "http://127.0.0.1:1235"
                }
              },
              "default_endpoint": "local",
              "default_model": "{{defaultModel}}"
            }
            """;
        File.WriteAllText(Path.Combine(_home, "gatto.json"), json);
    }

    private RoleFile MinimalRole()
        => new("test", "qwen3.6-35b", null, Array.Empty<string>(), false, null, ThinkingLevel.Medium);

    //deciding and persisting are separate steps, with the serving change between them. a write failure belongs to Persist, and the nothing-changed claim to Decide

    [Fact]
    public void Persist_CorruptGattoJson_ReportsTheFailureWithoutAStackTrace()
    {
        CreateModel("qwen3.6-35b");
        File.WriteAllText(Path.Combine(_home, "gatto.json"), "{not valid json");

        var problem = ModelSwitch.Persist(_home, "local", "qwen3.6-35b");

        Assert.NotNull(problem);
        Assert.Contains("persist", problem);
        Assert.DoesNotContain("Exception", problem);   //the failure reads as a sentence, with no exception text in it
    }

    [Fact]
    public void Persist_MissingGattoJson_ReportsTheFailureWithoutAStackTrace()
    {
        CreateModel("qwen3.6-35b");
        File.Delete(Path.Combine(_home, "gatto.json"));

        var problem = ModelSwitch.Persist(_home, "local", "qwen3.6-35b");

        Assert.NotNull(problem);
        Assert.Contains("persist", problem);
        Assert.DoesNotContain("Exception", problem);
    }

    [Fact]
    public void Decide_TOUCHES_NOTHING_ON_DISK_so_the_caller_can_change_serving_first()
    {
        //deciding writes nothing, the caller can still change the server before the write
        CreateModel("qwen3.6-35b");
        var configPath = Path.Combine(_home, "gatto.json");
        //the home has no gatto.json, and deciding must leave it that way
        Assert.False(File.Exists(configPath));

        var outcome = ModelSwitch.Decide(_modelsDir, _home, "qwen3.6-35b", MinimalRole(),
            Array.Empty<(string, string)>(), null, "http://127.0.0.1:1235");

        Assert.True(outcome.Success);
        Assert.False(File.Exists(configPath));
    }

    //a switch rebuilds the whole prompt, the model line names the model switched to
    [Fact]
    public void A_SWITCH_RECOMPOSES_THE_MODEL_LINE_FOR_THE_NEW_MODEL()
    {
        CreateModel("qwen3.6-35b");
        var outcome = ModelSwitch.Decide(_modelsDir, _home, "qwen3.6-35b", MinimalRole(),
            Array.Empty<(string, string)>(), null, "http://127.0.0.1:1235", cwd: @"C:\proj");

        Assert.True(outcome.Success);
        Assert.Contains("\nModel: qwen3.6-35b.\nWorking directory: C:\\proj.", outcome.SystemText, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeProbe_UnknownLoadedFile_FallsBackToFileName()
    {
        CreateModel("qwen3.6-35b", "C:\\models\\qwen.gguf");
        var armedModel = Model.Load(_modelsDir, "qwen3.6-35b");

        var unknownPath = "C:\\some\\other\\path\\mysterious-model-2.5-100b.gguf";
        var loaded = new LoadedModel(unknownPath, 4096);

        var mismatch = ModelSwitch.DescribeProbe(armedModel, loaded, _modelsDir);

        Assert.NotNull(mismatch);
        Assert.Equal("mysterious-model-2.5-100b", mismatch!.Value.Name);
        //the caller may offer /model {name} only when OnTheShelf is true
        Assert.False(mismatch.Value.OnTheShelf);
    }

    [Fact]
    public void DescribeProbe_KnownLoadedFile_ReturnsModelId()
    {
        CreateModel("qwen3.6-35b", "C:\\models\\qwen.gguf");
        CreateModel("gemma-4-26b", "C:\\models\\gemma.gguf");
        var armedModel = Model.Load(_modelsDir, "qwen3.6-35b");

        var loaded = new LoadedModel("C:\\models\\qwen.gguf", 4096);

        var mismatch = ModelSwitch.DescribeProbe(armedModel, loaded, _modelsDir);

        Assert.Null(mismatch);
    }

    [Fact]
    public void DescribeProbe_LoadedFileClaimedByOtherModel_UsesModelId()
    {
        CreateModel("qwen3.6-35b", "C:\\models\\qwen.gguf");
        CreateModel("gemma-4-26b", "C:\\models\\gemma.gguf");
        var armedModel = Model.Load(_modelsDir, "qwen3.6-35b");

        var loaded = new LoadedModel("C:\\models\\gemma.gguf", 4096);

        var mismatch = ModelSwitch.DescribeProbe(armedModel, loaded, _modelsDir);

        Assert.NotNull(mismatch);
        Assert.Equal("gemma-4-26b", mismatch!.Value.Name);
        //a shelf model claimed these weights, without this case a flag stuck at false still passes both tests
        Assert.True(mismatch.Value.OnTheShelf);
    }

    [Fact]
    public void DescribeProbe_NoLoadedModel_ReturnsNull()
    {
        CreateModel("qwen3.6-35b", "C:\\models\\qwen.gguf");
        var armedModel = Model.Load(_modelsDir, "qwen3.6-35b");

        var mismatch = ModelSwitch.DescribeProbe(armedModel, null, _modelsDir);

        //a server that named no model is unknown, which is not a mismatch
        Assert.Null(mismatch);
    }
}
