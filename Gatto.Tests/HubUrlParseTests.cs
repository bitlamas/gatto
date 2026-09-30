using Gatto.Core.Acquire;

namespace Gatto.Tests;

//one input row serves three grammars, so typed text is classified before anything acts on it. falling through to search is normal
public class HubUrlParseTests
{
    private static HubRef Parsed(string text)
    {
        Assert.True(HubUrl.TryParse(text, out var reference), $"expected `{text}` to classify");
        return reference;
    }

    private static void IsSearch(string text) =>
        Assert.False(HubUrl.TryParse(text, out _), $"expected `{text}` to fall through to a search");

    //the bare repo id is the most common paste into the typed field.
    [Fact]
    public void A_BARE_REPO_ID_IS_A_MODEL()
    {
        var model = Assert.IsType<HubRef.Model>(Parsed("unsloth/gemma-4-e4b-it-GGUF"));

        Assert.Equal("unsloth/gemma-4-e4b-it-GGUF", model.RepoId);
        Assert.Null(model.File);
    }

    //every url form that names a repo with no file resolves to that repo. a revision segment is skipped, since gatto fetches from main
    [Theory]
    [InlineData("https://huggingface.co/unsloth/x-GGUF")]
    [InlineData("https://huggingface.co/unsloth/x-GGUF/")]
    [InlineData("https://huggingface.co/unsloth/x-GGUF/tree/main")]
    //a tree url opened into a folder still names the repo. a four-segment path cannot prove that a file exists
    [InlineData("https://huggingface.co/unsloth/x-GGUF/tree/main/Q4")]
    [InlineData("https://www.huggingface.co/unsloth/x-GGUF")]
    [InlineData("http://huggingface.co/unsloth/x-GGUF")]
    [InlineData("  https://huggingface.co/unsloth/x-GGUF  ")]
    public void A_HUB_URL_WITHOUT_A_FILE_IS_THE_REPO(string url)
    {
        var model = Assert.IsType<HubRef.Model>(Parsed(url));

        Assert.Equal("unsloth/x-GGUF", model.RepoId);
        Assert.Null(model.File);
    }

    //a browser shows /blob/ and a download link gives /resolve/, and a user pastes either, so both must name the same file
    [Theory]
    [InlineData("https://huggingface.co/unsloth/x-GGUF/blob/main/model-Q4_K_M.gguf")]
    [InlineData("https://huggingface.co/unsloth/x-GGUF/resolve/main/model-Q4_K_M.gguf")]
    [InlineData("https://huggingface.co/unsloth/x-GGUF/resolve/main/model-Q4_K_M.gguf?download=true")]
    public void A_BLOB_OR_RESOLVE_URL_NAMES_THE_FILE(string url)
    {
        var model = Assert.IsType<HubRef.Model>(Parsed(url));

        Assert.Equal("unsloth/x-GGUF", model.RepoId);
        Assert.Equal("model-Q4_K_M.gguf", model.File);
    }

    //a sharded file lives in a folder, so the stored file name keeps its path inside the repo.
    [Fact]
    public void A_FILE_IN_A_FOLDER_KEEPS_ITS_PATH()
    {
        var model = Assert.IsType<HubRef.Model>(
            Parsed("https://huggingface.co/o/x/blob/main/Q4/model-00001-of-00003.gguf"));

        Assert.Equal("Q4/model-00001-of-00003.gguf", model.File);
    }

    //a collection holds many models, and gatto cannot pick one. it classifies as its own kind, so a valid link is never failed as a bad search.
    [Theory]
    [InlineData("https://huggingface.co/collections/unsloth/gemma-4-68a1b2c3")]
    [InlineData("https://huggingface.co/collections/unsloth")]
    public void A_COLLECTION_IS_ITS_OWN_ANSWER(string url) =>
        Assert.IsType<HubRef.Collection>(Parsed(url));

    //the host must be checked through Uri rather than by string prefix, or a look-alike host enters the request path. such lines fall through to search
    [Theory]
    [InlineData("https://huggingface.co.evil.test/unsloth/x-GGUF")]
    [InlineData("https://evil.test/huggingface.co/unsloth/x-GGUF")]
    [InlineData("https://huggingface.co@evil.test/unsloth/x-GGUF")]
    [InlineData("https://nothuggingface.co/unsloth/x-GGUF")]
    [InlineData("https://hub.huggingface.co/unsloth/x-GGUF")]
    public void A_LOOKALIKE_HOST_IS_NOT_THE_HUB(string url) => IsSearch(url);

    //url ids pass the same shape rule as typed ids, so a malformed id classifies as search and never reaches the request path.
    [Theory]
    [InlineData("https://huggingface.co/%2e%2e/%2e%2e/etc")]
    [InlineData("https://huggingface.co/org/na me")]
    [InlineData("https://huggingface.co/org")]
    public void A_URL_CANNOT_SMUGGLE_A_MALFORMED_ID(string url) => IsSearch(url);

    //the Uri type resolves dot segments before the path is read, so the id arrives normalised. urls are built from that id
    [Fact]
    public void A_DOT_SEGMENT_IS_RESOLVED_AND_CANNOT_LEAVE_THE_HOST()
    {
        var model = Assert.IsType<HubRef.Model>(Parsed("https://huggingface.co/../../etc/passwd"));

        Assert.Equal("etc/passwd", model.RepoId);
        Assert.DoesNotContain("..", model.RepoId, StringComparison.Ordinal);
        //this assert checks only that the builder receives the normalised id on the hub host, since escaping changes nothing in etc/passwd
        Assert.StartsWith("https://huggingface.co/etc/passwd",
            HubUrl.Tree(model.RepoId), StringComparison.Ordinal);
    }

    //text that is neither a URL nor a repo id parses as a search. this is the common path, and the parser must not special-case it.
    [Theory]
    [InlineData("gemma 4")]
    [InlineData("qwen3 coder")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ftp://huggingface.co/o/x")]
    [InlineData("just/one/slash/too/many is not an id")]
    public void ANYTHING_ELSE_IS_A_SEARCH(string text) => IsSearch(text);

    //null is a search too, because an untouched row is not an error.
    [Fact]
    public void NULL_IS_A_SEARCH() => Assert.False(HubUrl.TryParse(null, out _));

    //the parser and the url builder must agree, since two spellings of the grammar drift and the client then cannot fetch what was accepted
    [Fact]
    public void WHAT_THE_BUILDER_WRITES_THE_PARSER_READS()
    {
        var built = HubUrl.Resolve("unsloth/x-GGUF", "model-Q4_K_M.gguf");
        var download = HubUrl.Download("unsloth/x-GGUF", "model-Q4_K_M.gguf");
        var tree = HubUrl.Tree("unsloth/x-GGUF");

        foreach (var url in new[] { built, download, tree })
        {
            var model = Assert.IsType<HubRef.Model>(Parsed(url));
            Assert.Equal("unsloth/x-GGUF", model.RepoId);
        }

        Assert.Equal("model-Q4_K_M.gguf", ((HubRef.Model)Parsed(built)).File);
        Assert.Equal("model-Q4_K_M.gguf", ((HubRef.Model)Parsed(download)).File);
        Assert.Null(((HubRef.Model)Parsed(tree)).File);
    }
}
