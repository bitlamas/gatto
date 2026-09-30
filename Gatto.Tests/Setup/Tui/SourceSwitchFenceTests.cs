using Gatto.Cli.Setup;
using Gatto.Core.Acquire;
using Gatto.Core.Models;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup.Tui;

//a sentence that names a key renders only where the key exists, so CanSwitchSource gates that one feature and nothing unrelated
public class SourceSwitchFenceTests
{
    private static FoundModel OnDisk(string name) =>
        new(Path.Combine(@"C:\weights", name, name + "-Q4_K_M.gguf"), 4_000_000_000,
            new GgufHeader(GgufOutcome.Complete, null, "gemma3", name, 262144, null,
                BlockCount: 32, HeadCount: 32, HeadCountKv: 8, EmbeddingLength: 4096,
                KeyLength: 128, ValueLength: 128, ChatTemplate: null));

    private static ShelfRow HubRow() => new(
        "unsloth/gemma-4-26B-A4B-it", "unsloth",
        new HubQuant("gemma-4-26B-A4B-it-Q4_K_M.gguf", 4_000_000_000, null),
        FitRegime.FitsGpu, 262144, false, Badge: null, Downloads: 5, Gated: false,
        Params: 25_200_000_000);

    //an outage with models on disk, the route to the one m away row
    private static WizardProbes Outage() => new()
    {
        Rows = [],
        Answer = _ => new HubSearchOutcome([], HubSearchCause.HubFailed),
        Found = [OnDisk("one"), OnDisk("two")],
    };

    //a healthy Hub with models on disk, the route to the discovery line
    private static WizardProbes Healthy() => new()
    {
        Rows = [HubRow()],
        Found = [OnDisk("one"), OnDisk("two")],
    };

    //every row both channels produce on both routes, plus the screens crossed, so the census is never empty
    private static (List<WizardRow> Rows, List<WizardScreen> Screens) Walk(bool canSwitchSource)
    {
        List<WizardRow> rows = [];
        List<WizardScreen> screens = [];

        foreach (var probes in new[] { Outage(), Healthy() })
        {
            var flow = new SetupFlow(probes) { CanSwitchSource = canSwitchSource };
            var screen = Take(flow, flow.StartPastEngine());

            if (screen is WizardScreen.Choice { Key: SetupFlow.DiscoveredKey })
                Take(flow, flow.Answer(SetupFlow.SearchInstead));
        }

        return (rows, screens);

        WizardScreen Take(SetupFlow f, WizardScreen s)
        {
            screens.Add(s);
            rows.AddRange(RowsOf(s));
            rows.AddRange(f.TakeNarration().SelectMany(i => i.Rows));
            return s;
        }
    }

    private static IEnumerable<WizardRow> RowsOf(WizardScreen s) => s switch
    {
        WizardScreen.Choice c => c.BodyRows ?? [],
        WizardScreen.Info i => i.Rows,
        WizardScreen.Ask a => a.BodyRows ?? [],
        WizardScreen.Terminal t => t.Rows,
        _ => [],
    };

    private static string TextOf(bool canSwitchSource) =>
        string.Join("\n", Walk(canSwitchSource).Rows.Select(r => r.Text));

    //the census covers the whole output rather than the two known sites, so a third sentence without the flag fails here
    [Fact]
    public void A_SENTENCE_THAT_NAMES_A_KEY_RENDERS_ONLY_WHERE_THE_KEY_EXISTS()
    {
        var plain = TextOf(canSwitchSource: false);
        var tui = TextOf(canSwitchSource: true);

        //check each sentence is present on the tui face first, since the assertions below are absences, and each wording needs its own match
        Assert.Contains("one m away", tui, StringComparison.Ordinal);
        Assert.Contains("m shows them", tui, StringComparison.Ordinal);

        Assert.DoesNotContain("one m away", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("m shows them", plain, StringComparison.Ordinal);
    }

    //a row's Keys mean its prose names a key, so a key-marked row on a face with no key loop is a defect
    [Fact]
    public void NO_ROW_CARRIES_KEY_MARKUP_ON_A_FACE_WITH_NO_KEY_LOOP()
    {
        Assert.DoesNotContain(Walk(canSwitchSource: false).Rows, r => r.Keys is { Count: > 0 });
        Assert.Contains(Walk(canSwitchSource: true).Rows, r => r.Keys is { Count: > 0 });
    }

    //prove both routes are reached, since each holds one of the two sentences and an empty census would satisfy every absence above
    [Fact]
    public void BOTH_WALKS_ACTUALLY_REACH_BOTH_SCREENS_THE_FENCE_IS_ABOUT()
    {
        foreach (var canSwitchSource in new[] { false, true })
        {
            var (rows, screens) = Walk(canSwitchSource);

            Assert.Contains("gatto couldn't reach Hugging Face",
                string.Join("\n", rows.Select(r => r.Text)), StringComparison.Ordinal);

            Assert.Contains(screens, s =>
                s is WizardScreen.Choice { Key: SetupFlow.SearchKey, Shelf.Rows.Count: > 0 });
        }
    }

    //the patterns must not match ordinary prose, or a matcher keyed on the letter m would refuse every screen gatto has
    [Fact]
    public void THE_PATTERNS_DO_NOT_MATCH_ORDINARY_TEXT()
    {
        var ordinary = TextOf(canSwitchSource: false);

        Assert.Contains("Nothing is wrong with this machine", ordinary, StringComparison.Ordinal);
        Assert.DoesNotContain("one m away", ordinary, StringComparison.Ordinal);
        Assert.DoesNotContain("m shows them", ordinary, StringComparison.Ordinal);
    }

    //this file is what the flow does with the answer, the face half of the rule sits with the row budget tests
}
