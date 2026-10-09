using Gatto.Cli.Setup;
using Gatto.Cli.Setup.Tui;
using Gatto.Core.Acquire;
using Gatto.Core.Hardware;
using Gatto.Terminal;
using Gatto.Tests.Fakes;
using Gatto.Tests.Setup.Tui;

namespace Gatto.Tests.Setup;

//a local row holds its model's files in the pane, one folder per line, priced once and chosen by path
public class LocalGroupedRowTests
{
    private static (SetupFlow Flow, WizardScreen.Choice Shelf) Local(params (string, string?, string?, int)[] files)
    {
        var probes = new WizardProbes
        {
            Llama = @"C:\llama\llama-server.exe",
            Snapshot = ShelfFixtures.Snapshot("unified"),
            Found = ShelfFixtures.LocalScan(files),
        };
        var flow = new SetupFlow(probes) { CanSwitchSource = true };
        flow.StartAtModelSegment();
        return (flow, Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.CtlSource)));
    }

    private static readonly (string, string?, string?, int)[] TwoFolders =
        [(@"C:\a\x-Q4_K_M.gguf", "llama", "8B", 5), (@"D:\b\x-Q8_0.gguf", "llama", "8B", 9)];

    private static readonly (string, string?, string?, int)[] OneFolderTwoQuants =
        [(@"C:\m\x-Q4_K_M.gguf", "llama", "8B", 5), (@"C:\m\x-Q8_0.gguf", "llama", "8B", 9)];

    [Fact]
    public void A_TWO_FOLDER_ROW_DRAWS_TWO_FOLDER_LINES_WITH_THE_ROWS_FOLDER_MARKED()
    {
        var (_, shelf) = Local(TwoFolders);
        var row = Assert.Single(shelf.Shelf!.Rows);
        Assert.Equal("x", row.Model);
        Assert.Equal(2, shelf.Shelf.Facts![0].Publishers!.Count);
        var (rows, map) = HitMapTests.Painted(shelf);
        var lines = map.Targets.Where(t => t.Tag.Kind == HitKind.PaneLine).ToList();
        Assert.Equal(2, lines.Count);
        var marked = Assert.Single(lines, t => ShelfFixtures.TextAt(rows, t).StartsWith('\u203a'));
        Assert.Equal(row.RowPublisher, marked.Tag.Index);
        Assert.Contains(@"C:\a", ShelfFixtures.TextAt(rows, marked));   //the band's Q4_K_M is the row's file, in C:\a
    }

    [Fact]
    public void A_ONE_FOLDER_ROW_DRAWS_ITS_LINE_OPEN()
    {
        var (_, shelf) = Local(OneFolderTwoQuants);
        var (rows, map) = HitMapTests.Painted(shelf);
        Assert.Equal(2, map.Targets.Count(t => t.Tag.Kind == HitKind.PaneFile));
        Assert.Contains('\u25be', ShelfFixtures.TextAt(rows, map.Targets.Single(t => t.Tag.Kind == HitKind.PaneLine)));
    }

    [Fact]
    public void ENTER_ON_THE_SECOND_FILE_IN_THE_PANE_ADOPTS_THAT_FILES_PATH()
    {
        var (flow, shelf) = Local(OneFolderTwoQuants);
        var ring = Shelf.Regions(shelf.Shelf!, 120, 0, shelf.Door is not null).ToList();
        var tabs = (ring.IndexOf(Region.Files) - ring.IndexOf(Region.List) + ring.Count) % ring.Count;
        var (answer, _) = WalkRender.Answered(shelf, 120,
            [.. Enumerable.Repeat(WizardRig.Tab, tabs), WizardRig.Down, WizardRig.Down, WizardRig.Enter]);
        flow.Answer(answer!);
        Assert.Equal(@"C:\m\x-Q8_0.gguf", flow.Selected!.Path);
    }

    [Fact]
    public void WITH_A_CHIP_LIT_THE_SHOWN_ROW_ADOPTS_ITS_OWN_FILE()
    {
        var (flow, _) = Local((@"C:\a\one-Q4_K_M.gguf", "qwen3", "8B", 5), (@"C:\b\two-Q4_K_M.gguf", "gemma3", "4B", 3));
        var family = Families.Load().FamilyOf("gemma3")!;
        var lit = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.CtlFamily + family));
        var row = Assert.Single(lit.Shelf!.Rows);
        Assert.Equal("two", row.Model);
        flow.Answer(lit.Options[0].Key);
        Assert.Equal(@"C:\b\two-Q4_K_M.gguf", flow.Selected!.Path);
    }

    [Fact]
    public void A_PARTIAL_SETS_LINE_SAYS_HOW_MANY_FILES_ARE_HERE()
    {
        var probes = new WizardProbes
        {
            Llama = @"C:\llama\llama-server.exe",
            Snapshot = ShelfFixtures.Snapshot("unified"),
        };
        var scan = ShelfFixtures.LocalScan((@"C:\m\x-Q6_K-00001-of-00003.gguf", "llama", "8B", 3), (@"C:\m\x-Q4_K_M.gguf", "llama", "8B", 5));
        probes.Found = [scan[0] with { ShardsPresent = 2 }, scan[1]];
        var flow = new SetupFlow(probes) { CanSwitchSource = true };
        flow.StartAtModelSegment();
        var shelf = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.CtlSource));
        var (rows, _) = HitMapTests.Painted(shelf);
        Assert.Contains(rows, r => r.Contains("2 of 3 files here"));
    }

    [Fact]
    public void THE_COUNT_ROW_COUNTS_ROWS_NOT_FILES()
    {
        var (_, shelf) = Local(
            (@"C:\a\x-Q4_K_M.gguf", "llama", "8B", 5), (@"C:\a\x-Q8_0.gguf", "llama", "8B", 9), (@"D:\b\x-Q6_K.gguf", "llama", "8B", 7),
            (@"C:\a\y-Q4_K_M.gguf", "qwen3", "4B", 3), (@"C:\a\y-Q8_0.gguf", "qwen3", "4B", 5),
            (@"C:\a\z-Q4_K_M.gguf", "gemma3", "12B", 8), (@"C:\a\z-Q5_K_M.gguf", "gemma3", "12B", 9));
        Assert.Equal(3, shelf.Shelf!.Rows.Count);
        Assert.StartsWith("1\u20133 of 3", Shelf.CountLine(shelf.Shelf, GlyphSet.Unicode));
    }

    [Fact]
    public void THE_FOLDED_FRAME_SHOWS_THE_ROWS_FILE()
    {
        var (_, shelf) = Local(OneFolderTwoQuants);
        //a short name leaves room for the pane at 80, so the fold is read at the widest width where the pane goes
        var width = Enumerable.Range(40, 60).Last(w => Shelf.PaneWidth(shelf.Shelf!, w, GlyphSet.Unicode) == 0);
        var (rows, map) = HitMapTests.Painted(shelf, width: width);
        var folded = Assert.Single(map.Targets, t => t.Tag.Kind == HitKind.FoldedFile);
        Assert.Contains("Q4_K_M", ShelfFixtures.TextAt(rows, folded));
    }

    //the live probe reads the models folder on every ask, so one local shelf asks once per file
    [Fact]
    public void ONE_SHELF_ASKS_EACH_FILES_MODEL_ONCE()
    {
        var probes = new WizardProbes
        {
            Llama = @"C:\llama\llama-server.exe",
            Snapshot = ShelfFixtures.Snapshot("unified"),
            Found = ShelfFixtures.LocalScan(TwoFolders),
        };
        var flow = new SetupFlow(probes) { CanSwitchSource = true };
        flow.StartAtModelSegment();
        probes.ExistingAsked.Clear();
        flow.Answer(SetupFlow.CtlSource);
        Assert.All(ShelfFixtures.LocalScan(TwoFolders), f => Assert.Equal(1, probes.ExistingAsked.GetValueOrDefault(f.Path)));
    }

    [Fact]
    public void A_LABEL_ENDING_IN_A_NEWLINE_NEVER_REACHES_THE_FRAME()
    {
        var (_, shelf) = ShelfFixtures.LocalFlow((@"C:\m\x-Q4_K_M.gguf", "llama", "8B\n", 5));
        var (rows, _) = HitMapTests.Painted(shelf);
        Assert.DoesNotContain(rows, r => r.Contains('\n') || r.Contains('\u001b'));
    }

    //a label wider than the Hub's widest cell widens the column, so every row keeps its columns under the header
    [Fact]
    public void A_NINE_CELL_LABEL_KEEPS_THE_TABLE_ALIGNED()
    {
        var (_, shelf) = ShelfFixtures.LocalFlow((@"C:\m\big-Q4_K_M.gguf", "qwen35moe", "397B-A17B", 5), (@"C:\m\small-Q4_K_M.gguf", "llama", "8B", 3));
        var (rows, _) = HitMapTests.Painted(shelf);
        var left = rows.Where(r => r.Contains('\u2502')).Select(r => r[..r.IndexOf('\u2502')]).ToList();
        Assert.True(left.Count >= 3);
        Assert.Single(left.Select(l => l.IndexOf("Q4_K_M", StringComparison.Ordinal)).Where(i => i >= 0).Distinct());
    }

    //the folded frame steps through the row's folder, starting on the row's file
    [Fact]
    public void AT_THE_FOLDED_WIDTH_THE_OTHER_QUANT_OF_A_ROW_CAN_BE_CHOSEN()
    {
        var (flow, shelf) = ShelfFixtures.LocalFlow((@"C:\m\gemma-4-26B-A4B-it-Q4_K_M.gguf", "llama", "8B", 5), (@"C:\m\gemma-4-26B-A4B-it-Q8_0.gguf", "llama", "8B", 9));
        Assert.Equal(0, Shelf.PaneWidth(shelf.Shelf!, 80, GlyphSet.Unicode));
        var ring = Shelf.Regions(shelf.Shelf!, 80, 0, shelf.Door is not null).ToList();
        var tabs = (ring.IndexOf(Region.Files) - ring.IndexOf(Region.List) + ring.Count) % ring.Count;
        var (answer, _) = WalkRender.Answered(shelf, 80,
            [.. Enumerable.Repeat(WizardRig.Tab, tabs), new ConsoleKeyInfo('\0', ConsoleKey.RightArrow, false, false, false), WizardRig.Enter]);
        flow.Answer(answer!);
        Assert.Equal(@"C:\m\gemma-4-26B-A4B-it-Q8_0.gguf", flow.Selected!.Path);
    }

    [Fact]
    public void AT_THE_FOLDED_WIDTH_ENTER_CHOOSES_THE_ROWS_FILE()
    {
        var (flow, shelf) = ShelfFixtures.LocalFlow((@"C:\m\gemma-4-26B-A4B-it-Q8_0.gguf", "llama", "8B", 9), (@"C:\m\gemma-4-26B-A4B-it-Q4_K_M.gguf", "llama", "8B", 5));
        var ring = Shelf.Regions(shelf.Shelf!, 80, 0, shelf.Door is not null).ToList();
        var tabs = (ring.IndexOf(Region.Files) - ring.IndexOf(Region.List) + ring.Count) % ring.Count;
        var (answer, _) = WalkRender.Answered(shelf, 80, [.. Enumerable.Repeat(WizardRig.Tab, tabs), WizardRig.Enter]);
        flow.Answer(answer!);
        Assert.Equal(@"C:\m\gemma-4-26B-A4B-it-Q4_K_M.gguf", flow.Selected!.Path);
    }

    [Fact]
    public void A_ROW_NO_RULE_CHOSE_DRAWS_NO_PICK_MARK_ON_ITS_FILE()
    {
        var (_, shelf) = ShelfFixtures.LocalFlow((@"C:\m\x-Q3_K_M.gguf", "llama", null, 4), (@"C:\m\x-Q2_K.gguf", "llama", null, 3));
        var (rows, map) = HitMapTests.Painted(shelf);
        var files = map.Targets.Where(t => t.Tag.Kind == HitKind.PaneFile).ToList();
        Assert.NotEmpty(files);
        Assert.DoesNotContain(files, t => ShelfFixtures.TextAt(rows, t).StartsWith('\u203a'));
    }

    [Fact]
    public void A_FILES_FIT_ON_ITS_PANE_LINE_IS_ITS_FIT_AS_A_ROW_FILE()
    {
        var (_, shelf) = Local(TwoFolders);
        var hw = HardwareClassifier.Classify(ShelfFixtures.Snapshot("unified"));
        var files = ShelfFixtures.LocalScan(TwoFolders);
        var pubs = shelf.Shelf!.Facts![0].Publishers!;
        foreach (var f in files)
        {
            var line = pubs.SelectMany(p => p.Files).Single(pf => pf.File!.Path == Path.GetFileName(f.Path)
                && pf.File.RepoId == Path.GetDirectoryName(f.Path));
            Assert.Equal(LocalShelf.FitOf(f, hw, 4096), line.Fit);
        }
        Assert.Equal(LocalShelf.FitOf(files[0], hw, 4096), shelf.Shelf.Rows[0].Fit);
    }
}
