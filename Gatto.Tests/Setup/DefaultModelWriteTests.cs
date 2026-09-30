using Gatto.Cli.Setup;
using Gatto.Core.Home;

namespace Gatto.Tests.Setup;

//only an id the user confirmed overwrites an existing default, a scaffold id writes where none exists. a run ending before a choice still leaves a default.
public class DefaultModelWriteTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-defmodel-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_home, true); } catch (Exception) { }
    }

    private string Gguf()
    {
        var gguf = Path.Combine(_home, "tiny.gguf");
        if (!File.Exists(gguf))
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "tiny.gguf"), gguf);
        return gguf;
    }

    private string? DefaultIn() => GattoConfig.Load(_home).DefaultModel;

    private void GiveItADefault(string id)
    {
        GattoHome.EnsureInitialized(_home);
        GattoConfigWriter.SetDefaultModel(_home, id);
        Assert.Equal(id, DefaultIn());   //the fixture proves the seeded default really reads back.
    }

    [Fact]
    public void AN_EXPLICIT_CHOICE_ALWAYS_WRITES_EVEN_OVER_AN_EXISTING_DEFAULT()
    {
        GiveItADefault("qwen3.5");

        Assert.Null(WriteSetApply.Apply(_home,
            new WriteSet { DefaultModel = "gemma-picked-on-purpose" }, out _));

        //the connect and reuse paths put a confirmed id here, and refusing it would break a setup re-run.
        Assert.Equal("gemma-picked-on-purpose", DefaultIn());
    }

    [Fact]
    public void THE_SCAFFOLD_FALLBACK_WRITES_ONLY_INTO_A_HOME_WITH_NO_DEFAULT()
    {
        GiveItADefault("qwen3.5");

        Assert.Null(WriteSetApply.Apply(_home,
            new WriteSet { CreateModel = new WriteSet.Model(Gguf(), Port: 41235, Context: 4096) },
            out var scaffolded));

        Assert.NotNull(scaffolded);
        //adding a model on a configured machine must not repoint what a bare launch starts.
        Assert.Equal("qwen3.5", DefaultIn());
    }

    //the if-absent field writes only when no default exists. the code in Apply can't see which flow gave the value, so the full run decides.
    [Fact]
    public void THE_IF_ABSENT_FIELD_DOES_NOT_OVERWRITE()
    {
        GiveItADefault("qwen3.5");

        Assert.Null(WriteSetApply.Apply(_home,
            new WriteSet { DefaultModelIfAbsent = "gemma-from-the-belt" }, out _));

        Assert.Equal("qwen3.5", DefaultIn());
    }

    [Fact]
    public void THE_IF_ABSENT_FIELD_WRITES_WHEN_THERE_IS_NONE()
    {
        GattoHome.EnsureInitialized(_home);
        Assert.Null(DefaultIn());

        Assert.Null(WriteSetApply.Apply(_home,
            new WriteSet { DefaultModelIfAbsent = "gemma-from-the-belt" }, out _));

        Assert.Equal("gemma-from-the-belt", DefaultIn());
    }

    [Fact]
    public void THE_SCAFFOLD_FALLBACK_STILL_WRITES_WHEN_THERE_IS_NONE()
    {
        GattoHome.EnsureInitialized(_home);
        Assert.Null(DefaultIn());   //assert the home has no default, so the fixture is proven rather than assumed.

        Assert.Null(WriteSetApply.Apply(_home,
            new WriteSet { CreateModel = new WriteSet.Model(Gguf(), Port: 41235, Context: 4096) },
            out var scaffolded));

        //a run that ends at the check must still leave a config pointed at a model.
        Assert.Equal(scaffolded, DefaultIn());
    }
}
