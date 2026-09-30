using Gatto.Core.Acquire;
using Gatto.Core.Tools;

namespace Gatto.Tests;

//every release tool shares one argument parser and prints the same banner, so the probe must refuse known siblings by name
public class LlamaSiblingTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gatto-sibling-").FullName;

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (Exception) { } }

    //plants a file with the name and no executable content, so a gate refusal and a real spawn stay two different results
    private string Plant(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "this is not a program");
        return path;
    }

    //the refusal must come from the name check before anything runs, since the planted file can't execute and a spawn would report Failed
    [Theory]
    [InlineData("llama-cli.exe")]
    [InlineData("llama-tokenize.exe")]
    [InlineData("llama-quantize.exe")]
    [InlineData("llama.exe")]
    public void A_KNOWN_SIBLING_IS_REFUSED_WITHOUT_EVER_BEING_RUN(string name)
    {
        var result = LlamaServerProbe.Run(Plant(name), TimeSpan.FromSeconds(5));

        Assert.Equal(ProbeShape.KnownSibling, result.Shape);
        Assert.Equal(name, result.SiblingName);
    }

    //a renamed copy of the server must still be probed, so the gate lists names. the planted file is not a program, so the spawn failure proves it passed the gate
    [Theory]
    [InlineData("my-server.exe")]
    [InlineData("llama-server-vulkan.exe")]
    [InlineData("server.exe")]
    public void A_NAME_THE_RELEASE_DOES_NOT_SHIP_STILL_REACHES_THE_PROBE(string name)
    {
        var result = LlamaServerProbe.Run(Plant(name), TimeSpan.FromSeconds(5));

        Assert.NotEqual(ProbeShape.KnownSibling, result.Shape);
    }

    //the list is the release minus the server, or the server's own name would refuse every correct setup
    [Fact]
    public void THE_SERVER_IS_NOT_A_SIBLING_OF_ITSELF() =>
        Assert.False(LlamaSiblings.Load().IsSibling(LlamaSiblings.ServerName));

    //the sibling list must move with the pinned release, or it names files that release lacks and misses its new ones
    [Fact]
    public void THE_LIST_WAS_READ_FROM_THE_RELEASE_GATTO_PINS() =>
        Assert.Equal(Gatto.Roles.LlamaAssetSteering.PinnedRelease, LlamaSiblings.Load().Release);

    //the floor comes from a real count, which keeps a barely-parsed resource from passing as a complete list
    [Fact]
    public void THE_LIST_IS_THE_WHOLE_RELEASE_NOT_A_HANDFUL()
    {
        var set = LlamaSiblings.Load();

        Assert.True(set.Executables.Count >= 20,
            $"only {set.Executables.Count} sibling names loaded, so the resource did not parse");
        Assert.NotEmpty(set.Generated);
    }

    //the cli's real banner reads as ClassicServer, so reading the output can't replace the name gate
    [Fact]
    public void THE_BANNER_ALONE_CANNOT_TELL_THE_CLI_FROM_THE_SERVER()
    {
        //this banner is measured from a real llama-cli.exe --version run
        const string cliBanner = "version: 10076 (305ba519a)\nbuilt with Clang 20.1.8 for Windows x86_64\n";

        var read = LlamaServerProbe.Interpret(0, "", cliBanner, timedOut: false, vcRuntimeAbsent: false);

        Assert.Equal(ProbeShape.ClassicServer, read.Shape);
    }
}
