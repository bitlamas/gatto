using Gatto.Core.Home;
using Gatto.Roles;

namespace Gatto.Tests;

//the write of default_effort into profile.json must leave every other key alone
public class ModelDefaultEffortTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gatto-default-effort-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private const string Profile = """
        {
          "files": [{ "path": "C:\\w\\m.gguf", "active": true }],
          "port": 1235,
          "context": 8192,
          "sampling": { "temperature": 0.7 },
          "extra_args": ["--flash-attn", "on"],
          "api_key": "s3cr3t-value"
        }
        """;

    private string Write(string json = Profile)
    {
        var dir = Path.Combine(_dir, "m");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "profile.json"), json);
        return _dir;
    }

    private ModelProfile Reload() => Model.Load(_dir, "m").Profile;

    [Fact]
    public void SET_WRITES_THE_LEVEL()
    {
        ModelDefaultEffort.Set(Write(), "m", ThinkingLevel.XHigh);

        Assert.Equal(ThinkingLevel.XHigh, Reload().DefaultEffort);
    }

    [Fact]
    public void SET_OVERWRITES_A_PREVIOUS_DEFAULT()
    {
        var dir = Write();
        ModelDefaultEffort.Set(dir, "m", ThinkingLevel.Low);
        ModelDefaultEffort.Set(dir, "m", ThinkingLevel.Medium);

        Assert.Equal(ThinkingLevel.Medium, Reload().DefaultEffort);
    }

    //every other key survives the write, the same rule the files-list tests hold
    [Fact]
    public void AND_EVERY_OTHER_KEY_SURVIVES_THE_WRITE()
    {
        ModelDefaultEffort.Set(Write(), "m", ThinkingLevel.XHigh);

        var p = Reload();
        Assert.Equal(0.7, p.Sampling!.Value.GetProperty("temperature").GetDouble());
        Assert.Equal(new[] { "--flash-attn", "on" }, p.ExtraArgs);
        Assert.Equal("s3cr3t-value", p.ApiKey);
        Assert.Equal(8192, p.Context);
        Assert.Equal(1235, p.Port);
        Assert.Equal(@"C:\w\m.gguf", p.ActivePath);
    }

    [Fact]
    public void SET_ON_AN_UNKNOWN_MODEL_THROWS_THE_LOADERS_OWN_REFUSAL()
    {
        var ex = Assert.Throws<GattoConfigException>(() => ModelDefaultEffort.Set(_dir, "nope", ThinkingLevel.High));
        Assert.Contains("nope", ex.Message);
    }

    //the profile goes through Model.Load first, a profile the loader refuses must not be edited into another broken one
    [Fact]
    public void SET_ON_A_BROKEN_PROFILE_THROWS_BEFORE_WRITING()
    {
        var dir = Write("""{ "port": 1235, "context": 8192 }""");   //the profile has no files key, the loader refuses it

        Assert.Throws<GattoConfigException>(() => ModelDefaultEffort.Set(dir, "m", ThinkingLevel.High));
    }
}
