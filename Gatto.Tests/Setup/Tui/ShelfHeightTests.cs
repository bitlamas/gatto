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

    //the local shelf's grouped row, two folders with the row's folder open, counts its pane in the frame as the Hub's accordion does
    [Fact]
    public void AT_20_ROWS_THE_LOCAL_GROUPED_FRAME_FITS()
    {
        var (flow, _) = ShelfFixtures.Flow("unified", ShelfFixtures.Entry.Model, found: ShelfFixtures.GroupedScan());
        var local = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.CtlSource));
        var rows = WalkRender.SettledFrame(local, 120, script: ShelfFixtures.OpenKeys(local), height: 20).Rows;

        Assert.True(rows.Count <= 20, Dump(20, rows));
        Assert.Contains(rows, r => r.Contains(" of 1", StringComparison.Ordinal));
        Assert.Contains(rows, r => r.Contains('\u25be'));
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

    //every row is reachable: the window follows the cursor down and back up, and the frame keeps its height and its chrome
    [Fact]
    public void THE_LIST_SCROLLS_WITH_THE_CURSOR_AND_THE_FRAME_KEEPS_ITS_HEIGHT()
    {
        const int height = 28;
        var flow = new SetupFlow(new WizardProbes { Rows = [.. Enumerable.Range(1, 30).Select(i => Row(i, mixed: false))] })
        {
            CanSwitchSource = true,
            RowBudget = int.MaxValue,
        };
        var c = Assert.IsType<WizardScreen.Choice>(flow.StartPastEngine());
        var fresh = WalkRender.SettledFrame(c, 120, height: height).Rows;
        var down = WalkRender.SettledFrame(c, 120, height: height,
            script: Enumerable.Repeat(Key(ConsoleKey.DownArrow), 20)).Rows;
        var back = WalkRender.SettledFrame(c, 120, height: height,
            script: [.. Enumerable.Repeat(Key(ConsoleKey.DownArrow), 20), .. Enumerable.Repeat(Key(ConsoleKey.UpArrow), 15)]).Rows;

        //the frame is the same height at the top, scrolled down and scrolled back, so no spacer or rule was squeezed out
        Assert.Equal(fresh.Count, down.Count);
        Assert.Equal(fresh.Count, back.Count);
        Assert.True(down.Count <= height, Dump(height, down));
        Assert.Contains(fresh, r => r.Contains("1–13 of 30", StringComparison.Ordinal));

        Assert.Contains(down, r => r.Contains("❯ m21 ", StringComparison.Ordinal));
        Assert.Contains(down, r => r.Contains("… 9 more above", StringComparison.Ordinal));
        Assert.Contains(down, r => r.Contains("… 9 more below", StringComparison.Ordinal));
        Assert.Contains(down, r => r.Contains("10–21 of 30", StringComparison.Ordinal));
        Assert.DoesNotContain(down, r => r.Contains(" m9 ", StringComparison.Ordinal));

        //moving back up holds the window until the cursor reaches its top row
        Assert.Contains(back, r => r.Contains("❯ m6 ", StringComparison.Ordinal));
        Assert.Contains(back, r => r.Contains("6–17 of 30", StringComparison.Ordinal));
    }

    //a grouped shelf scrolled into one group still names it, and the frame keeps its height on both sides of the boundary
    [Fact]
    public void A_GROUPED_SHELF_SCROLLS_WITH_ITS_HEADINGS_AND_KEEPS_ITS_HEIGHT()
    {
        const int height = 28;
        var flow = new SetupFlow(new WizardProbes { Rows = [.. Enumerable.Range(1, 30).Select(i => Row(i, mixed: true))] })
        {
            CanSwitchSource = true,
            RowBudget = int.MaxValue,
        };
        var c = Assert.IsType<WizardScreen.Choice>(flow.StartPastEngine());
        var fresh = WalkRender.SettledFrame(c, 120, height: height).Rows;
        var down = WalkRender.SettledFrame(c, 120, height: height,
            script: Enumerable.Repeat(Key(ConsoleKey.DownArrow), 20)).Rows;

        Assert.True(fresh.Count <= height, Dump(height, fresh));
        Assert.Equal(fresh.Count, down.Count);
        Assert.Contains(fresh, r => r.Contains("on the graphics card", StringComparison.Ordinal));
        Assert.Contains(down, r => r.Contains("❯ m21 ", StringComparison.Ordinal));
        Assert.Contains(down, r => r.Contains("in system memory", StringComparison.Ordinal));
        Assert.DoesNotContain(down, r => r.Contains("on the graphics card", StringComparison.Ordinal));
    }

    //a press on a scrolled window names the row on the full shelf, so it answers the model drawn under the pointer
    [Fact]
    public void A_PRESS_ON_A_SCROLLED_WINDOW_ANSWERS_THE_ROW_UNDER_THE_POINTER()
    {
        var flow = new SetupFlow(new WizardProbes { Rows = [.. Enumerable.Range(1, 30).Select(i => Row(i, mixed: false))] })
        {
            CanSwitchSource = true,
            RowBudget = int.MaxValue,
        };
        var c = Assert.IsType<WizardScreen.Choice>(flow.StartPastEngine()) with { AllowBack = false };
        var downs = Enumerable.Repeat(Key(ConsoleKey.DownArrow), 20).ToArray();
        var m16 = ShelfFixtures.Target(c, HitKind.Row, index: 15, height: 28, keys: downs);

        var rig = new WizardRig(120);
        rig.Surface.Height = 28;
        var face = rig.TuiFaceEvents([.. downs.Select(k => (object)new KeyEvent(k)), .. RigMouse.On(m16), .. RigMouse.On(m16)]);
        Assert.Equal(c.Options[15].Key, face.Choose(c));
    }

    //the TUI face windows the shelf itself, so it asks the flow for every row rather than a page it cannot scroll past
    [Fact]
    public void THE_TUI_FACE_ASKS_THE_FLOW_FOR_EVERY_ROW()
    {
        Assert.Equal(int.MaxValue, WalkRender.RowBudget(120, 28));
    }
}
