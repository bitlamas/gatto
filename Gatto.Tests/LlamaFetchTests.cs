using System.IO.Compression;
using Gatto.Cli;

namespace Gatto.Tests;

//every extraction failure must remove the destination folder too, or the next sweep offers a half-extracted engine as the tested release
public class LlamaFetchTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("gatto-fetch-").FullName;

    public void Dispose() { try { Directory.Delete(_root, true); } catch (Exception) { } }

    private string Into => Path.Combine(_root, "into");

    //builds a zip from entry names and bodies, and the entry names use forward slashes like a real release archive
    private string Zip(string name, params (string Entry, string Body)[] entries)
    {
        var path = Path.Combine(_root, name);
        using var fs = new FileStream(path, FileMode.Create);
        using var archive = new ZipArchive(fs, ZipArchiveMode.Create);
        foreach (var (entry, body) in entries)
        {
            using var w = new StreamWriter(archive.CreateEntry(entry).Open());
            w.Write(body);
        }
        return path;
    }

    //extraction flattens the inner folder a real archive nests its payload in, so the destination holds no folder nobody named
    [Fact]
    public void THE_ARCHIVES_INNER_FOLDER_IS_FLATTENED()
    {
        var zip = Zip("engine.zip",
            ("llama-b10076-bin-win-vulkan-x64/llama-server.exe", "server"),
            ("llama-b10076-bin-win-vulkan-x64/ggml-vulkan.dll", "lib"),
            ("llama-b10076-bin-win-vulkan-x64/llama-cli.exe", "cli"));

        var got = LlamaFetch.Extract(zip, Into);

        Assert.Null(got.Error);
        Assert.Equal(Path.Combine(Into, "llama-server.exe"), got.ServerPath);
        Assert.True(File.Exists(Path.Combine(Into, "ggml-vulkan.dll")), "the companions did not come with it");
        Assert.False(Directory.Exists(Path.Combine(Into, "llama-b10076-bin-win-vulkan-x64")),
            "the inner folder survived");
    }

    //a flat archive has no prefix, so the strip loop must strip nothing
    [Fact]
    public void A_FLAT_ARCHIVE_NEEDS_NO_FLATTENING()
    {
        var got = LlamaFetch.Extract(Zip("flat.zip", ("llama-server.exe", "server")), Into);

        Assert.Null(got.Error);
        Assert.True(File.Exists(Path.Combine(Into, "llama-server.exe")));
    }

    //every entry is checked before a single byte is written, so a malicious archive can't leave its harmless half on disk
    [Theory]
    [InlineData("../escaped.txt")]
    [InlineData("llama-b10076/../../escaped.txt")]
    [InlineData("..\\escaped.txt")]
    public void AN_ENTRY_THAT_ESCAPES_THE_FOLDER_REFUSES_EVERYTHING(string escape)
    {
        var zip = Zip("evil.zip", ("llama-server.exe", "server"), (escape, "pwned"));

        var got = LlamaFetch.Extract(zip, Into);

        Assert.NotNull(got.Error);
        Assert.Null(got.ServerPath);
        //nothing on disk survives the refusal
        Assert.False(Directory.Exists(Into), "a refused fetch left its folder behind");
        Assert.False(File.Exists(Path.Combine(_root, "escaped.txt")), "the escaping entry was written");
    }

    //the fence is compared with a trailing separator, or a sibling folder whose name starts with the destination name passes the prefix check
    [Fact]
    public void A_SIBLING_FOLDER_WITH_A_LONGER_NAME_IS_STILL_OUTSIDE()
    {
        var zip = Zip("evil2.zip", ("llama-server.exe", "server"), ("../intoevil/x.txt", "pwned"));

        Assert.NotNull(LlamaFetch.Extract(zip, Into).Error);
        Assert.False(Directory.Exists(Path.Combine(_root, "intoevil")));
    }

    //the archive must hold exactly one server, so zero is the wrong release and two would force a silent choice into the config
    [Fact]
    public void AN_ARCHIVE_WITH_NO_SERVER_IS_REFUSED_AND_SAYS_SO()
    {
        var got = LlamaFetch.Extract(Zip("empty.zip", ("llama-cli.exe", "cli")), Into);

        Assert.Contains("0 copies", got.Error!, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Into));
    }

    [Fact]
    public void AN_ARCHIVE_WITH_TWO_SERVERS_IS_REFUSED_AND_SAYS_SO()
    {
        var zip = Zip("two.zip", ("a/llama-server.exe", "one"), ("b/llama-server.exe", "two"));

        var got = LlamaFetch.Extract(zip, Into);

        Assert.Contains("2 copies", got.Error!, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Into));
    }

    //a non-archive must come back as an error, since the caller is a setup screen and no exception may escape
    [Fact]
    public void A_FILE_THAT_IS_NOT_AN_ARCHIVE_IS_REFUSED_RATHER_THAN_THROWN()
    {
        var junk = Path.Combine(_root, "junk.zip");
        File.WriteAllText(junk, "not a zip");

        var got = LlamaFetch.Extract(junk, Into);

        Assert.NotNull(got.Error);
        Assert.Null(got.ServerPath);
    }

    //the fetch folder path is composed in one place, so the fetch and the engine sweep can't disagree about its shape
    [Fact]
    public void THE_FETCH_LANDS_WHERE_THE_SWEEP_LOOKS() =>
        Assert.Equal(Path.Combine(@"C:\home", "llama", "b10076"), LlamaFetch.DirFor(@"C:\home", "b10076"));

    //the release url must point at the pinned tag, since a fetch that followed latest would install a build nothing tested
    [Fact]
    public void THE_RELEASE_READ_IS_THE_PIN_NOT_LATEST()
    {
        var url = LlamaFetch.ReleaseUrl(Gatto.Roles.LlamaAssetSteering.PinnedRelease);

        Assert.Contains("ggml-org/llama.cpp", url, StringComparison.Ordinal);
        Assert.EndsWith("/tags/" + Gatto.Roles.LlamaAssetSteering.PinnedRelease, url, StringComparison.Ordinal);
        Assert.DoesNotContain("latest", url, StringComparison.Ordinal);
    }

    private static Gatto.Cli.Setup.EngineAsset A(string name) =>
        new(name, "https://example.invalid/" + name, new string('a', 64), 1);

    //records the get and extract steps in the order they happened
    private sealed class Recorder
    {
        public List<string> Steps { get; } = [];
        public HashSet<string> Refuse { get; init; } = [];

        public Gatto.Cli.Landing Get(Gatto.Cli.Setup.EngineAsset a)
        {
            Steps.Add("get " + a.ZipName);
            return Refuse.Contains(a.ZipName) ? new(null, "no digest") : new(a.ZipName + ".zip", null);
        }

        public EngineFetch Extract(string zip, bool isServer)
        {
            Steps.Add("extract " + zip);
            return Refuse.Contains(zip) ? new(null, "bad archive") : new(isServer ? "server.exe" : "dir", null);
        }
    }

    private static readonly Gatto.Cli.Setup.EnginePair Pair =
        new(A("llama-cuda"), A("cudart"));

    //both halves must arrive and verify before either is extracted, or the folder ends up with a server and no runtime
    [Fact]
    public void EVERY_HALF_VERIFIES_BEFORE_ANY_HALF_IS_EXTRACTED()
    {
        var rec = new Recorder();

        LlamaFetch.Land(Pair, rec.Get, rec.Extract);

        Assert.Equal(
            ["get llama-cuda", "get cudart", "extract llama-cuda.zip", "extract cudart.zip"],
            rec.Steps);
    }

    //an unverified companion must extract nothing, since extracting the server first would still report the same error
    [Fact]
    public void A_COMPANION_THAT_WILL_NOT_VERIFY_EXTRACTS_NOTHING_AT_ALL()
    {
        var rec = new Recorder { Refuse = ["cudart"] };

        var got = LlamaFetch.Land(Pair, rec.Get, rec.Extract);

        Assert.Null(got.ServerPath);
        Assert.Equal("no digest", got.Error);
        Assert.DoesNotContain(rec.Steps, s => s.StartsWith("extract", StringComparison.Ordinal));
    }

    //a server that fails verification must stop the companion fetch, since those bytes would be spent on a fetch that already failed
    [Fact]
    public void A_SERVER_THAT_WILL_NOT_VERIFY_NEVER_FETCHES_THE_COMPANION()
    {
        var rec = new Recorder { Refuse = ["llama-cuda"] };

        Assert.Equal("no digest", LlamaFetch.Land(Pair, rec.Get, rec.Extract).Error);
        Assert.Equal(["get llama-cuda"], rec.Steps);
    }

    //the pair must report the server's path, or a folder would be written into the llama_server config field
    [Fact]
    public void THE_SERVERS_PATH_IS_WHAT_THE_PAIR_REPORTS()
    {
        var rec = new Recorder();

        Assert.Equal("server.exe", LlamaFetch.Land(Pair, rec.Get, rec.Extract).ServerPath);
    }

    //a pair without a companion is the ordinary machine and must not wait for one that does not exist.
    [Fact]
    public void A_PAIR_WITH_NO_COMPANION_IS_JUST_THE_SERVER()
    {
        var rec = new Recorder();

        LlamaFetch.Land(new(A("llama-vulkan"), null), rec.Get, rec.Extract);

        Assert.Equal(["get llama-vulkan", "extract llama-vulkan.zip"], rec.Steps);
    }

    //the companion's archive must hold no server, since accepting one would let the wrong archive decide which binary the config names
    [Fact]
    public void A_COMPANION_CARRYING_A_SERVER_IS_REFUSED_AND_TAKES_THE_FOLDER_WITH_IT()
    {
        var zip = Zip("bad-companion.zip", ("inner/llama-server.exe", "no"), ("inner/cudart64_12.dll", "runtime"));
        var into = Path.Combine(_root, "engine");
        Directory.CreateDirectory(into);
        File.WriteAllText(Path.Combine(into, "already-here.txt"), "x");

        var got = LlamaFetch.Extract(zip, into, expectServer: false);

        Assert.Null(got.ServerPath);
        Assert.Contains("1 copies", got.Error!, StringComparison.Ordinal);
        Assert.Contains("expected 0", got.Error!, StringComparison.Ordinal);
        Assert.False(Directory.Exists(into));
    }

    //the companion reports the folder it extracted into, since demanding a server would refuse every correct runtime archive
    [Fact]
    public void A_COMPANION_FLATTENS_INTO_THE_SAME_FOLDER_AND_REPORTS_IT()
    {
        var zip = Zip("companion.zip", ("cudart-bin/cudart64_12.dll", "runtime"), ("cudart-bin/cublas64_12.dll", "more"));
        var into = Path.Combine(_root, "engine2");

        var got = LlamaFetch.Extract(zip, into, expectServer: false);

        Assert.Null(got.Error);
        Assert.Equal(Path.GetFullPath(into), got.ServerPath);
        Assert.True(File.Exists(Path.Combine(into, "cudart64_12.dll")));
        Assert.True(File.Exists(Path.Combine(into, "cublas64_12.dll")));
        //the inner folder must not survive
        Assert.False(Directory.Exists(Path.Combine(into, "cudart-bin")));
    }

    //a companion archive with no inner folder must still extract cleanly.
    [Fact]
    public void A_FLAT_COMPANION_LANDS_TOO()
    {
        var zip = Zip("flat-companion.zip", ("cudart64_12.dll", "runtime"));
        var into = Path.Combine(_root, "engine3");

        Assert.Null(LlamaFetch.Extract(zip, into, expectServer: false).Error);
        Assert.True(File.Exists(Path.Combine(into, "cudart64_12.dll")));
    }
}

