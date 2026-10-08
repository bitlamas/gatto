using Gatto.Cli.Setup;
using Gatto.Cli.Setup.Tui;
using Gatto.Core.Acquire;
using Gatto.Core.Models;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup.Tui;

//the shelf frame fits the terminal it is drawn in, the open publisher's files included, since the pane beside the table can be the taller column
public class ShelfHeightTests
{
    private const long GB = 1_073_741_824L;

    private static HubQuant Q(string name, double gb) => new(name, (long)(gb * GB), null, Path: name);

    //three publishers of six files each, so an open one draws its whole window with both edge counts. a mixed shelf puts half its rows in memory
    private static ModelRow Row(int i, bool mixed)
    {
        PublisherOffer Offer(string org)
        {
            var repo = new RepoFiles($"{org}/m{i}-GGUF", [.. new[] { "Q2_K", "Q3_K_M", "Q4_K_M", "Q5_K_M", "Q6_K", "Q8_0" }
                .Select((t, k) => Q($"m{i}-{t}.gguf", 2.0 + k))], [], 6, 10);
            var pick = new FileRef(org, repo.RepoId, $"m{i}-Q4_K_M.gguf");
            return new PublisherOffer(org, [repo], pick, repo.Quants[2], FitRegime.FitsGpu, 10);
        }
        return new ModelRow($"m{i}", "qwen", 0, 9_000_000_000, null, "qwen35", 262144, Vision: true,
            [Offer("unsloth"), Offer("bartowski"), Offer("lmstudio-community")], 0,
            new FileRef("unsloth", $"unsloth/m{i}-GGUF", $"m{i}-Q4_K_M.gguf"),
            mixed && i > 6 ? FitRegime.FitsRamOnly : FitRegime.FitsGpu, Structure: "dense");
    }

    private static WizardScreen.Choice OnTheShelf(int height, bool mixed)
    {
        var flow = new SetupFlow(new WizardProbes { Rows = [.. Enumerable.Range(1, 12).Select(i => Row(i, mixed))] })
        {
            CanSwitchSource = true,
            RowBudget = Shelf.RowBudget(height),
        };
        return Assert.IsType<WizardScreen.Choice>(flow.StartPastEngine());
    }

    private static ConsoleKeyInfo Key(ConsoleKey k) => new('\0', k, false, false, false);

    private static IEnumerable<ConsoleKeyInfo> OpenTheRowsPublisher(WizardScreen.Choice c)
    {
        var ring = Shelf.Regions(c.Shelf!, 120, 0, c.Door is not null).ToList();
        var tabs = (ring.IndexOf(Region.Files) - ring.IndexOf(Shelf.Opening(c.Shelf!)) + ring.Count) % ring.Count;
        return [.. Enumerable.Repeat(Key(ConsoleKey.Tab), tabs), Key(ConsoleKey.Enter)];
    }

    private static IReadOnlyList<string> Frame(int height, bool open, bool mixed = false)
    {
        var c = OnTheShelf(height, mixed);
        return WalkRender.SettledFrame(c, 120, script: open ? OpenTheRowsPublisher(c) : null, height: height).Rows;
    }

    private static string Dump(int height, IReadOnlyList<string> rows) =>
        $"at {height}: {rows.Count} rows\n" + string.Join("\n", rows);

    //at the default terminal every publisher, the open window and the count row are on screen
    [Fact]
    public void AT_120_BY_30_EVERYTHING_FITS()
    {
        var rows = Frame(30, open: true);

        Assert.True(rows.Count <= 30, Dump(30, rows));
        var mixed = Frame(30, open: true, mixed: true);
        Assert.True(mixed.Count <= 30, Dump(30, mixed));
        Assert.Contains(mixed, r => r.Contains("on the graphics card", StringComparison.Ordinal));
        Assert.Contains(rows, r => r.Contains("▾ unsloth", StringComparison.Ordinal));
        Assert.Contains(rows, r => r.Contains("… 1 heavier ↓", StringComparison.Ordinal));
        Assert.Contains(rows, r => r.Contains("lmstudio-community", StringComparison.Ordinal));
        Assert.Contains(rows, r => r.Contains(" of 12", StringComparison.Ordinal));
    }

    //at twenty rows the frame still fits with the publisher open, and the count row is still drawn. the group headings are rows too
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void AT_20_ROWS_THE_FRAME_FITS(bool open, bool mixed)
    {
        var rows = Frame(20, open, mixed);

        Assert.True(rows.Count <= 20, Dump(20, rows));
        Assert.Contains(rows, r => r.Contains(" of 12", StringComparison.Ordinal));
        if (open) Assert.Contains(rows, r => r.Contains("▾ unsloth", StringComparison.Ordinal));
    }
}
