using Gatto.Cli.Setup;
using Gatto.Cli.Setup.Tui;
using Gatto.Core.Acquire;
using Gatto.Core.Hardware;
using Gatto.Core.Models;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup.Tui;

//the quants window's height must not depend on the file count, a taller frame loses its top in the panel. every check compares two frames at the same height
public class InSessionLayoutHoldsTests
{
    private static ShelfRow Row() => new(
        RepoId: "unsloth/gemma-4-26B", Publisher: "unsloth",
        PickedQuant: new HubQuant("Q4_K_M.gguf", 16_900_000_000, null),
        Fit: FitRegime.FitsGpu, NativeCtx: 262144, Vision: false, Badge: null,
        Downloads: 900, Gated: false, Params: 26_000_000_000, Arch: "gemma3");

    private static WizardScreen.Choice Screen(int files, MachineShape shape)
    {
        var pane = Enumerable.Range(0, files)
            .Select(i => new PaneFile("Q" + i, 4_000_000_000L + i, FitRegime.FitsGpu))
            .ToList();
        var shelf = new ShelfView([Row()], "unsloth", shape, Total: 1,
            Families: Families.Load().Ladder, Family: "all",
            Facts: [new ModelFacts(Files: pane)]);
        return new WizardScreen.Choice(SetupFlow.SearchKey, SetupFlow.ModelTitleFor(inSession: true),
            [new ChoiceOption("0", "unsloth/gemma-4-26B")],
            Shelf: shelf, Door: SetupFlow.ShelfDoorPlaceholderOf(GlyphSet.Unicode))
        {
            Strip = WalkSection.For(SetupFlow.SearchKey, SetupPath.Llama, inSession: true),
        };
    }

    private static IReadOnlyList<string> Frame(int files, int height, MachineShape shape)
    {
        var surface = new RecordingSurface { Width = 100, Height = height };
        var face = new TuiWizardSurface(surface, new Keys([Esc, Esc]),
            new Theme(new TermCaps(true, true)), version: "0.5.0", build: "1a2b3c4", nowMs: () => 0,
            //the clock must answer at once and nowMs stays frozen, an armed chord's bounded wait would otherwise hang here
            clock: _ => new Ready());
        try { face.Choose(Screen(files, shape)); }
        catch (InvalidOperationException) { }
        return face.LastPainted;
    }

    private sealed class Ready : Gatto.Repl.IPollClock
    {
        public bool WaitForKey(TimeSpan budget) => true;
        public long ElapsedMs => 0;
    }

    private static readonly ConsoleKeyInfo Esc = new('\0', ConsoleKey.Escape, false, false, false);

    private sealed class Keys(IEnumerable<ConsoleKeyInfo> k) : Gatto.Terminal.IKeySource
    {
        private readonly Queue<ConsoleKeyInfo> _q = new(k);
        public bool KeyAvailable => false;
        public ConsoleKeyInfo ReadKey() => _q.Count > 0 ? _q.Dequeue()
            : throw new InvalidOperationException("the script ran dry");
    }

    //sweep the heights a REPL panel hands over, one height passes on a build that only varies where the shedder bites
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TWO_FILES_AND_THIRTY_THREE_DRAW_THE_SAME_NUMBER_OF_ROWS(bool unified)
    {
        var shape = unified ? MachineShape.UnifiedWithShare : MachineShape.Discrete;
        foreach (var height in new[] { 0, 14, 18, 22, 26, 30, 40 })
        {
            var few = Frame(2, height, shape);
            var many = Frame(33, height, shape);

            Assert.Equal(few.Count, many.Count);
        }
    }

    //assert the strip's presence in each frame, two frames that both lost it would be equal and satisfy the row above
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void THE_STRIP_IS_DRAWN_WHATEVER_THE_FILE_COUNT(bool unified)
    {
        var shape = unified ? MachineShape.UnifiedWithShare : MachineShape.Discrete;
        foreach (var height in new[] { 0, 14, 18, 22, 26, 30, 40 })
        foreach (var files in new[] { 2, 33 })
            Assert.Contains(Frame(files, height, shape),
                r => r.Contains("model", StringComparison.Ordinal)
                  && r.Contains("check", StringComparison.Ordinal));
    }

    //equal row counts can hide a shifted body, so the table's start row is the property rather than a proxy
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void THE_TABLE_STARTS_ON_THE_SAME_ROW_WHATEVER_THE_FILE_COUNT(bool unified)
    {
        var shape = unified ? MachineShape.UnifiedWithShare : MachineShape.Discrete;
        foreach (var height in new[] { 0, 14, 18, 22, 26, 30, 40 })
        {
            var few = Frame(2, height, shape);
            var many = Frame(33, height, shape);

            Assert.Equal(IndexOfTable(few), IndexOfTable(many));
            Assert.True(IndexOfTable(few) >= 0, "neither frame drew the table at all");
        }
    }

    private static int IndexOfTable(IReadOnlyList<string> rows)
    {
        for (var i = 0; i < rows.Count; i++)
            if (rows[i].Contains("gemma-4-26B", StringComparison.Ordinal)) return i;
        return -1;
    }

    //a row with no files must not change the frame, the block is sized per shelf rather than per row
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_ROW_WITH_NO_FILES_DOES_NOT_MOVE_THE_FRAME(bool unified)
    {
        var shape = unified ? MachineShape.UnifiedWithShare : MachineShape.Discrete;
        var mixed = new ShelfView([Row(), Row() with { RepoId = "unsloth/no-files" }],
            "unsloth", shape, Total: 2, Families: Families.Load().Ladder, Family: "all",
            Facts: [
                new ModelFacts(Files: [
                    new PaneFile("Q4_K_M", 4_000_000_000, FitRegime.FitsGpu),
                    new PaneFile("Q6_K", 6_000_000_000, FitRegime.FitsGpu)]),
                new ModelFacts(Files: null),
            ]);

        var onFiles = Shelf.Body(mixed, row: 0, chip: 0, file: -1, Region.List, 100,
            glyphs: GlyphSet.Unicode).Count;
        var onNone = Shelf.Body(mixed, row: 1, chip: 0, file: -1, Region.List, 100,
            glyphs: GlyphSet.Unicode).Count;

        Assert.Equal(onFiles, onNone);
    }

    //a shelf with no file data anywhere draws no block, six blank rows per row would hide models in a short panel
    [Fact]
    public void A_SHELF_WITH_NO_FILES_ANYWHERE_SPENDS_NOTHING_ON_THE_BLOCK()
    {
        var bare = new ShelfView([Row(), Row() with { RepoId = "unsloth/two" }],
            "unsloth", MachineShape.Discrete, Total: 2,
            Families: Families.Load().Ladder, Family: "all");
        var withFiles = bare with
        {
            Facts = [
                new ModelFacts(Files: [new PaneFile("Q4_K_M", 4_000_000_000, FitRegime.FitsGpu)]),
                new ModelFacts(Files: null),
            ],
        };

        var bareRows = Shelf.Body(bare, row: 0, chip: 0, file: -1, Region.List, 100,
            glyphs: GlyphSet.Unicode).Count;
        var mixedRows = Shelf.Body(withFiles, row: 1, chip: 0, file: -1, Region.List, 100,
            glyphs: GlyphSet.Unicode).Count;

        Assert.True(bareRows < mixedRows,
            $"the bare shelf spent the block anyway: {bareRows} rows against {mixedRows}");
    }

    //pin the window's height at the source, so a variable window fails here with a message about the window
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(33)]
    public void THE_QUANTS_WINDOW_IS_ALWAYS_THE_SAME_HEIGHT(int files)
    {
        var pane = Pane.Rows(Row(),
            new ModelFacts(Files: [.. Enumerable.Range(0, files)
                .Select(i => new PaneFile("Q" + i, 4_000_000_000L + i, FitRegime.FitsGpu))]),
            MachineShape.UnifiedWithShare, width: 40, cursor: 0);

        var oneFile = Pane.Rows(Row(),
            new ModelFacts(Files: [new PaneFile("Q0", 4_000_000_000L, FitRegime.FitsGpu)]),
            MachineShape.UnifiedWithShare, width: 40, cursor: 0);

        Assert.Equal(oneFile.Count, pane.Count);
    }
}
