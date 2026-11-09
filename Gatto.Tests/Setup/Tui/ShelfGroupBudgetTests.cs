using Gatto.Cli.Setup;
using Gatto.Cli.Setup.Shelves;
using Gatto.Core.Acquire;
using Gatto.Core.Models;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup.Tui;

//a window too short for the shelf keeps the one model row the floor promises and its heading, however many groups the shelf holds
public class ShelfGroupBudgetTests
{
    private const long GB = 1_073_741_824L;

    private static readonly FitRegime[] Order =
        [FitRegime.FitsGpu, FitRegime.FitsSplit, FitRegime.FitsRamOnly, FitRegime.DoesNotFit];

    //twelve rows spread over the first n fits, in the engine's order
    private static FitRegime FitOf(int i, int n) => Order[(i - 1) * n / 12];

    private static ModelRow HubRow(int i, FitRegime fit)
    {
        PublisherOffer Offer(string org)
        {
            var repo = new RepoFiles($"{org}/m{i}-GGUF", [.. new[] { "Q2_K", "Q3_K_M", "Q4_K_M", "Q5_K_M", "Q6_K", "Q8_0" }
                .Select((t, k) => new HubQuant($"m{i}-{t}.gguf", (long)((2.0 + k) * GB), null, Path: $"m{i}-{t}.gguf"))], [], 6, 10);
            var pick = new FileRef(org, repo.RepoId, $"m{i}-Q4_K_M.gguf");
            return new PublisherOffer(org, [repo], pick, repo.Quants[2], FitRegime.FitsGpu, 10);
        }
        return new ModelRow($"m{i}", "qwen", 0, 9_000_000_000, null, "qwen35", 262144, Vision: true,
            [Offer("unsloth"), Offer("bartowski"), Offer("lmstudio-community")], 0,
            new FileRef("unsloth", $"unsloth/m{i}-GGUF", $"m{i}-Q4_K_M.gguf"), fit, Structure: "dense");
    }

    private static SetupScreen.Choice Hub(int groups)
    {
        var rows = Enumerable.Range(1, 12).Select(i => HubRow(i, FitOf(i, groups))).ToList();
        var flow = new SetupFlow(new FakeSetupProbes { Rows = rows }) { CanSwitchSource = true, RowBudget = int.MaxValue };
        return Assert.IsType<SetupScreen.Choice>(flow.StartPastEngine());
    }

    //the local shelf is the one that holds a fourth group, the files too big for the machine
    private static SetupScreen.Choice Local(int groups)
    {
        var rows = Enumerable.Range(1, 12).Select(i =>
            ShelfRows.Of($"org/m{i}", "org", new HubQuant($"m{i}-Q4_K_M.gguf", 5 * GB, null), FitOf(i, groups), Model: $"m{i}")).ToList();
        return new SetupScreen.Choice(SetupFlow.SearchKey, "Which file?",
            [.. rows.Select((r, i) => new ChoiceOption(i.ToString(), r.Model))],
            Shelf: new ShelfView(rows, Gatto.Core.Hardware.MachineShape.Discrete, Total: 12,
                Families: ["all"], Family: "all", Source: ShelfSource.Local, Folder: @"C:\models"),
            Door: SetupFlow.ShelfDoorPlaceholderOf(GlyphSet.Unicode));
    }

    public static TheoryData<string, int> Shelves => new()
    {
        { "hub", 2 }, { "hub", 3 }, { "local", 2 }, { "local", 3 }, { "local", 4 },
    };

    private static SetupScreen.Choice Of(string source, int groups) => source == "hub" ? Hub(groups) : Local(groups);

    private static int ModelRows(SetupScreen.Choice c, int height) =>
        ShelfFixtures.Targets(c, 120, height).Count(t => t.Tag.Kind == HitKind.Row);

    //a heading opens its row in the table column, with the pane beside it
    private static int Headings(IReadOnlyList<string> frame, ShelfSource source) =>
        frame.Count(r => Order.Any(f => r.TrimStart().StartsWith(Shelf.GroupHeading(f, source), StringComparison.Ordinal)));

    //10 rows is under any shelf's chrome, so the floor holds: one model row, and its group named
    [Theory]
    [MemberData(nameof(Shelves))]
    public void A_window_under_the_chrome_shows_one_model_row_and_its_heading(string source, int groups)
    {
        var c = Of(source, groups);
        var frame = WalkRender.SettledFrame(c, 120, height: 10).Rows;

        Assert.Equal(1, ModelRows(c, 10));
        Assert.Equal(1, Headings(frame, c.Shelf!.Source));
    }

    //once the window holds the one-row frame, the frame fits it at every taller height
    [Theory]
    [MemberData(nameof(Shelves))]
    public void A_window_that_holds_the_one_row_frame_holds_every_taller_frame(string source, int groups)
    {
        var c = Of(source, groups);
        var floor = WalkRender.SettledFrame(c, 120, height: 10).Rows.Count;

        for (var h = floor; h <= 44; h++)
            Assert.True(WalkRender.SettledFrame(c, 120, height: h).Rows.Count <= h, $"at {h} the frame is taller");
    }

    //the open publisher is the taller column only when the window has room, so a short window draws the same frame with it open
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void A_publisher_open_on_a_short_window_does_not_add_a_row(int groups)
    {
        var c = Hub(groups);
        var shut = WalkRender.SettledFrame(c, 120, height: 10).Rows;
        var open = WalkRender.SettledFrame(c, 120, script: ShelfFixtures.OpenKeys(c), height: 10).Rows;

        Assert.Equal(shut.Count, open.Count);
    }
}
