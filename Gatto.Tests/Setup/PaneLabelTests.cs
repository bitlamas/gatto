using Gatto.Cli.Setup;
using Gatto.Core.Acquire;
using Gatto.Core.Models;

namespace Gatto.Tests.Setup;

//inside one publisher's file list a label two files share carries what tells their repos apart, so Enter never takes a file the user cannot name
public class PaneLabelTests
{
    private static HubQuant Q(string name, long bytes) => new(name, bytes, null, Path: name);

    private static ModelRow Row(params RepoFiles[] repos) =>
        new("m", "qwen", 0, 9_000_000_000, null, "qwen35", 32768, Vision: false,
            [new PublisherOffer("mradermacher", repos, null, null, null, 10)], 0, null, FitRegime.DoesNotFit);

    private static IReadOnlyList<string> Labels(ModelRow row) =>
        [.. SetupFlow.PublishersOf(row, ShelfMachines.Vega)![0].Files.OrderBy(f => f.Bytes).Select(f => f.Label)];

    [Fact]
    public void A_LABEL_TWO_REPOS_SHARE_CARRIES_WHAT_THE_OTHER_REPO_LACKS()
    {
        var row = Row(
            new RepoFiles("mradermacher/m-GGUF", [Q("m.Q4_K_M.gguf", 5_100_000_000), Q("m.Q6_K.gguf", 7_000_000_000)], [], 2, 50),
            new RepoFiles("mradermacher/m-i1-GGUF", [Q("m.i1-Q4_K_M.gguf", 5_200_000_000), Q("m.i1-IQ4_XS.gguf", 4_600_000_000)], [], 2, 40));

        Assert.Equal(["IQ4_XS", "Q4_K_M", "Q4_K_M i1", "Q6_K"], Labels(row));
    }

    [Fact]
    public void ONE_REPO_KEEPS_ITS_BARE_TOKENS()
    {
        var row = Row(new RepoFiles("mradermacher/m-i1-GGUF",
            [Q("m.i1-Q4_K_M.gguf", 5_200_000_000), Q("m.i1-IQ4_XS.gguf", 4_600_000_000)], [], 2, 40));

        Assert.Equal(["IQ4_XS", "Q4_K_M"], Labels(row));
    }
}
