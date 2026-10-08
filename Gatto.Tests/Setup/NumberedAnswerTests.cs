using Gatto.Cli.Setup;
using Gatto.Core.Acquire;
using Gatto.Core.Models;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup;

//each shelf arm must check its own bounds, an out-of-range number gets a named refusal with the screen and its row count
public class NumberedAnswerTests
{
    private static ModelRow HubRow(string id) => ShelfRows.Of(
        id, "unsloth", new HubQuant(id + "-Q4_K_M.gguf", 4_000_000_000, null),
        FitRegime.FitsGpu, 262144, false, Badge: null, Downloads: 5, Gated: false,
        Params: 25_200_000_000);

    private static FoundModel OnDisk(string name) =>
        new(Path.Combine(@"C:\weights", name, name + "-Q4_K_M.gguf"), 4_000_000_000,
            new GgufHeader(GgufOutcome.Complete, null, "gemma3", name, 262144, null,
                BlockCount: 32, HeadCount: 32, HeadCountKv: 8, EmbeddingLength: 4096,
                KeyLength: 128, ValueLength: 128, ChatTemplate: null));

    //answer a one-row screen with row 3, the refusal must name the screen and the true row count instead of a short array
    [Fact]
    public void AN_OUT_OF_RANGE_NUMBER_REFUSES_BY_NAME_RATHER_THAN_INDEXING()
    {
        var flow = new SetupFlow(new WizardProbes
        {
            Rows = [HubRow("unsloth/one")],
            Found = [OnDisk("only-one")],
        });
        flow.StartPastEngine();

        var boom = Assert.Throws<InvalidOperationException>(() => flow.Answer("3"));

        Assert.Contains(SetupFlow.DiscoveredKey, boom.Message, StringComparison.Ordinal);
        Assert.Contains("1 rows", boom.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Index was out of range", boom.Message, StringComparison.Ordinal);
    }

    //the refusal's count must come from the list the answer reached, and the two lists differ in length on purpose
    [Fact]
    public void THE_COUNT_IN_THE_REFUSAL_IS_THE_LIST_THAT_WAS_CONSULTED()
    {
        var flow = new SetupFlow(new WizardProbes
        {
            Rows = [HubRow("unsloth/a"), HubRow("unsloth/b"), HubRow("unsloth/c")],
            Found = [OnDisk("one")],
        });
        flow.StartPastEngine();

        //the local screen holds one row, the Hub shelf behind it holds three
        var local = Assert.Throws<InvalidOperationException>(() => flow.Answer("2"));
        Assert.Contains("1 rows", local.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("3 rows", local.Message, StringComparison.Ordinal);

        //the Hub shelf is up now, 9 is out of its range
        flow.Answer(ShelfControls.SourceAnswer());
        var hub = Assert.Throws<InvalidOperationException>(() => flow.Answer("9"));
        Assert.Contains(SetupFlow.SearchKey, hub.Message, StringComparison.Ordinal);
        Assert.Contains("3 rows", hub.Message, StringComparison.Ordinal);
    }

    //a malformed number must take the same named refusal path, with the same key space and the same sentence
    [Theory]
    [InlineData("-1")]
    [InlineData("nonsense")]
    [InlineData("")]
    public void A_MALFORMED_NUMBER_REFUSES_THE_SAME_WAY(string key)
    {
        var flow = new SetupFlow(new WizardProbes { Rows = [HubRow("unsloth/one")], Found = [OnDisk("one")] });
        flow.StartPastEngine();

        var boom = Assert.Throws<InvalidOperationException>(() => flow.Answer(key));

        Assert.Contains("is not one of its", boom.Message, StringComparison.Ordinal);
    }

    //an in-range number must still pick its row, an arm that refuses everything would satisfy the other tests
    [Fact]
    public void AN_IN_RANGE_NUMBER_STILL_PICKS_ITS_ROW()
    {
        var flow = new SetupFlow(new WizardProbes
        {
            Rows = [HubRow("unsloth/one")],
            Found = [OnDisk("first"), OnDisk("second")],
        });
        flow.StartPastEngine();

        flow.Answer("1");

        Assert.Contains("second", flow.Selected!.Path, StringComparison.Ordinal);
    }
}
