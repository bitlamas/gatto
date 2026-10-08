using Gatto.Cli.Setup;
using Gatto.Cli.Setup.Tui;
using Gatto.Core.Acquire;
using Gatto.Core.Hardware;
using Gatto.Core.Models;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup.Tui;

//the pane as the landing page draws it: the model name, one facts line, and every publisher as a closed accordion line that opens one at a time
public class PaneAccordionTests
{
    private const long GB = 1_073_741_824L;

    private static HubQuant Q(string name, double gb) => new(name, (long)(gb * GB), "sha-" + name, Path: name);

    //unsloth is the row's publisher with six files, bartowski holds a static and an i1 repo with a Q4_K_M each, ggml-org has no pick
    private static ModelRow Row()
    {
        var uns = new RepoFiles("unsloth/m-GGUF", [Q("m-Q4_K_M.gguf", 5.0), Q("m-Q6_K.gguf", 7.0), Q("m.gguf", 6.0),
            Q("m-Q2_K.gguf", 3.0), Q("m-Q3_K_M.gguf", 4.0), Q("m-Q8_0.gguf", 9.0)], [], 6, 900);
        var bStatic = new RepoFiles("bartowski/m-GGUF", [Q("m-Q4_K_M.gguf", 5.1)], [], 1, 50);
        var bI1 = new RepoFiles("bartowski/m-i1-GGUF", [Q("m.i1-Q4_K_M.gguf", 5.2), Q("m.i1-IQ4_XS.gguf", 4.6)], [], 2, 40);
        var ggml = new RepoFiles("ggml-org/m-GGUF", [Q("m-IQ2_XXS.gguf", 2.4)], [], 1, 10);
        var rowFile = new FileRef("unsloth", "unsloth/m-GGUF", "m-Q4_K_M.gguf");
        return new ModelRow("m", "qwen", 0, 9_000_000_000, null, "qwen35", 262144, Vision: true,
        [
            new PublisherOffer("unsloth", [uns], rowFile, uns.Quants[0], FitRegime.FitsGpu, 900),
            new PublisherOffer("bartowski", [bStatic, bI1], new FileRef("bartowski", "bartowski/m-i1-GGUF", "m.i1-Q4_K_M.gguf"),
                bI1.Quants[0], FitRegime.FitsGpu, 90),
            new PublisherOffer("ggml-org", [ggml], null, null, null, 10),
        ], 0, rowFile, FitRegime.FitsGpu, Structure: "dense");
    }

    private static (SetupFlow Flow, WizardScreen.Choice Shelf) OnTheShelf(ModelRow? row = null)
    {
        var flow = new SetupFlow(new WizardProbes { Rows = [row ?? Row()] }) { CanSwitchSource = true };
        return (flow, Assert.IsType<WizardScreen.Choice>(flow.StartPastEngine()));
    }

    private static ConsoleKeyInfo Key(ConsoleKey k) => new('\0', k, false, false, false);

    private static IEnumerable<ConsoleKeyInfo> TabToFiles(WizardScreen.Choice c)
    {
        var ring = Gatto.Cli.Setup.Tui.Shelf.Regions(c.Shelf!, 120, 0, c.Door is not null).ToList();
        var steps = (ring.IndexOf(Region.Files) - ring.IndexOf(Gatto.Cli.Setup.Tui.Shelf.Opening(c.Shelf!)) + ring.Count) % ring.Count;
        return Enumerable.Repeat(Key(ConsoleKey.Tab), steps);
    }

    private static string Pane(WizardScreen.Choice c, IEnumerable<ConsoleKeyInfo> keys) =>
        string.Join("\n", WalkRender.SettledFrame(c, 120, script: keys).Rows);

    [Fact]
    public void THE_FACTS_LINE()
    {
        var (_, c) = OnTheShelf(Row() with { Active = 3_000_000_000, Structure = "MoE" });
        var text = Pane(c, []);
        Assert.Contains("◈ vision · reads 3B/token · context 262,144", text, StringComparison.Ordinal);

        var (_, plain) = OnTheShelf(Row() with { Vision = false, NativeCtx = null, Active = null, Structure = null });
        var bare = Pane(plain, []);
        Assert.DoesNotContain("vision", bare, StringComparison.Ordinal);
        Assert.DoesNotContain("context", bare, StringComparison.Ordinal);
    }

    [Fact]
    public void EVERY_PUBLISHER_CLOSED_AT_FIRST()
    {
        var (_, c) = OnTheShelf();
        var text = Pane(c, []);
        Assert.Contains("publishers (3)", text, StringComparison.Ordinal);
        Assert.Matches(@"› ▸ unsloth\s+5\.0 GB Q4_K_M\s+✓ (GPU|fits)", text);
        Assert.Matches(@"  ▸ bartowski\s+5\.2 GB Q4_K_M", text);
        Assert.Matches(@"  ▸ ggml-org\s+no file fits", text);
        Assert.DoesNotMatch("▾ (unsloth|bartowski|ggml-org)", text);
    }

    //from unsloth's pick, the third of its six files, four downs pass its last three files and reach bartowski's line
    private static ConsoleKeyInfo[] ToBartowski => [.. Enumerable.Repeat(Key(ConsoleKey.DownArrow), 4)];

    //an Enter on a closed publisher opens it and closes the other, and the window opens on that publisher's own pick
    [Fact]
    public void ENTER_OPENS_ONE_AT_A_TIME()
    {
        var (_, c) = OnTheShelf();
        var first = Pane(c, [.. TabToFiles(c), Key(ConsoleKey.Enter)]);
        Assert.Contains("▾ unsloth", first, StringComparison.Ordinal);
        Assert.Contains("❯ Q4_K_M", first, StringComparison.Ordinal);
        Assert.Contains("… 2 lighter ↑", first, StringComparison.Ordinal);
        Assert.Contains("… 1 heavier ↓", first, StringComparison.Ordinal);
        Assert.DoesNotContain("Q8_0", first, StringComparison.Ordinal);

        var second = Pane(c, [.. TabToFiles(c), Key(ConsoleKey.Enter), .. ToBartowski, Key(ConsoleKey.Enter)]);
        Assert.Contains("▾ bartowski", second, StringComparison.Ordinal);
        Assert.Contains("▸ unsloth", second, StringComparison.Ordinal);
        Assert.Contains("❯ Q4_K_M", second, StringComparison.Ordinal);
    }

    //the row's publisher opens on the row's own file, the one the table shows
    [Fact]
    public void THE_PANE_OPENS_ON_THE_ROWS_FILE()
    {
        var row = Row();
        var chosen = new FileRef("unsloth", "unsloth/m-GGUF", "m-Q6_K.gguf");
        var (_, c) = OnTheShelf(row with { RowFile = chosen });
        var text = Pane(c, [.. TabToFiles(c), Key(ConsoleKey.Enter)]);
        Assert.Contains("❯ Q6_K", text, StringComparison.Ordinal);
    }

    //a file whose name declares no quant is listed by its name, since the floor decides only the pick
    [Fact]
    public void EVERY_FILE_IS_LISTED()
    {
        var (_, c) = OnTheShelf();
        var text = Pane(c, [.. TabToFiles(c), Key(ConsoleKey.Enter), Key(ConsoleKey.DownArrow)]);
        Assert.Contains("m.gguf", text, StringComparison.Ordinal);
    }

    //an Enter on a file chooses that file by reference, here the second repo's Q4_K_M of a two-repo publisher
    [Fact]
    public void ENTER_ON_A_FILE_CHOOSES_IT()
    {
        var (flow, c) = OnTheShelf();
        var (answer, _) = WalkRender.Answered(c, 120,
            [.. TabToFiles(c), Key(ConsoleKey.Enter), .. ToBartowski, Key(ConsoleKey.Enter), Key(ConsoleKey.Enter)]);
        var picked = ShelfControls.Unpick(answer!);
        Assert.NotNull(picked);
        Assert.Equal(new FileRef("bartowski", "bartowski/m-i1-GGUF", "m.i1-Q4_K_M.gguf"), picked!.Value.File);

        flow.Answer(answer!);
        Assert.Equal("bartowski/m-i1-GGUF", flow.Picked!.RowFile!.RepoId);
    }
}
