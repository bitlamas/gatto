using System.Net;
using Gatto.Core.Acquire;

namespace Gatto.Tests;

//a Hub file is addressed by its path in the repo, folder included, since a URL from the bare name answers 404
public class HubRepoPathTests
{
    [Fact]
    public void A_PATH_KEEPS_ITS_SLASH_AND_ESCAPES_EACH_SEGMENT()
    {
        Assert.Equal("https://huggingface.co/unsloth/Q-GGUF/resolve/main/UD-IQ1_S/a%20b-00001-of-00003.gguf",
            HubUrl.Resolve("unsloth/Q-GGUF", "UD-IQ1_S/a b-00001-of-00003.gguf"));
        Assert.Equal("https://huggingface.co/unsloth/Q-GGUF/resolve/main/UD-IQ1_S/a.gguf?download=true",
            HubUrl.Download("unsloth/Q-GGUF", "UD-IQ1_S/a.gguf"));
        //each path segment is escaped, so a hash or a question mark inside one can't end the path early
        Assert.Equal("https://huggingface.co/unsloth/Q-GGUF/resolve/main/f%23x/a%3Fb.gguf",
            HubUrl.Resolve("unsloth/Q-GGUF", "f#x/a?b.gguf"));
    }

    [Theory]
    [InlineData("../other/x.gguf")]
    [InlineData("UD/../../x.gguf")]
    [InlineData("./x.gguf")]
    [InlineData("UD//x.gguf")]
    [InlineData("")]
    public void A_PATH_THAT_COULD_LEAVE_THE_REPO_IS_REFUSED(string path)
    {
        Assert.False(HubUrl.IsRepoPath(path));
        Assert.Throws<ArgumentException>(() => HubUrl.Resolve("unsloth/Q-GGUF", path));
    }

    [Fact]
    public async Task THE_TREE_KEEPS_THE_PATH_ON_THE_QUANT_AND_ON_EVERY_MEMBER()
    {
        const string tree = "["
            + "{\"type\":\"file\",\"path\":\"UD-IQ1_S/m-UD-IQ1_S-00002-of-00002.gguf\",\"size\":20,\"lfs\":{\"oid\":\"b\"}},"
            + "{\"type\":\"file\",\"path\":\"UD-IQ1_S/m-UD-IQ1_S-00001-of-00002.gguf\",\"size\":10,\"lfs\":{\"oid\":\"a\"}},"
            + "{\"type\":\"file\",\"path\":\"../escape-Q4_K_M.gguf\",\"size\":10,\"lfs\":{\"oid\":\"c\"}},"
            + "{\"type\":\"file\",\"path\":\"plain-Q8_0.gguf\",\"size\":30,\"lfs\":{\"oid\":\"d\"}}]";
        var client = new HubClient(new HttpClient(new TreeOnly(tree)) { Timeout = Timeout.InfiniteTimeSpan });

        var got = await client.TreeAsync("unsloth/m-GGUF", CancellationToken.None);

        var set = Assert.Single(got.Quants, q => q.ShardCount == 2);
        Assert.Equal("m-UD-IQ1_S-00001-of-00002.gguf", set.FileName);
        Assert.Equal("UD-IQ1_S/m-UD-IQ1_S-00001-of-00002.gguf", set.RepoPath);
        Assert.Equal(["UD-IQ1_S/m-UD-IQ1_S-00001-of-00002.gguf", "UD-IQ1_S/m-UD-IQ1_S-00002-of-00002.gguf"],
            set.Members.Select(m => m.RepoPath));
        Assert.Equal("plain-Q8_0.gguf", Assert.Single(got.Quants, q => q.ShardCount == 1).RepoPath);
        Assert.DoesNotContain(got.Quants, q => q.FileName.Contains("escape", StringComparison.Ordinal));
    }

    private sealed class TreeOnly(string tree) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(tree, System.Text.Encoding.UTF8, "application/json") });
    }
}
