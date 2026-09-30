using Gatto.Cli.Setup;
using Gatto.Cli.Setup.Tui;
using Gatto.Core.Acquire;
using Gatto.Core.Models;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup.Tui;

//only the path tells the two files of one id apart, so the sibling file must not read as loaded
public class SiblingFileNotLoadedTests
{
    private const string Folder = @"C:\weights\gemma-4-e4b";
    private const string Quarter = Folder + @"\gemma-4-e4b-Q4_K_M.gguf";
    private const string Eighth = Folder + @"\gemma-4-e4b-Q8_0.gguf";
    private const string Id = "gemma-4-e4b";

    private static FoundModel OnDisk(string path) =>
        new(path, 4_000_000_000,
            new GgufHeader(GgufOutcome.Complete, null, "gemma3", Id, 262144, null,
                BlockCount: 32, HeadCount: 32, HeadCountKv: 8, EmbeddingLength: 4096,
                KeyLength: 128, ValueLength: 128, ChatTemplate: null));

    //both files answer the same id, since a profile's files all resolve to their profile
    private static WizardProbes Probes(string? loadedPath) => new()
    {
        Rows = [new("unsloth/gemma-4-e4b", "unsloth",
            new HubQuant("gemma-4-e4b-Q4_K_M.gguf", 4_000_000_000, null),
            FitRegime.FitsGpu, 262144, false, Badge: null, Downloads: 5, Gated: false,
            Params: 4_000_000_000)],
        Found = [OnDisk(Quarter), OnDisk(Eighth)],
        Existing = { [Quarter] = Id, [Eighth] = Id },
        Loaded = Id,
        LoadedPath = loadedPath,
    };

    private static ShelfView LocalShelfOf(WizardProbes probes)
    {
        var flow = new SetupFlow(probes) { CanSwitchSource = true };
        flow.StartPastEngine();
        var local = Assert.IsType<WizardScreen.Choice>(flow.Answer(ShelfControls.SourceAnswer()));
        Assert.Equal(ShelfSource.Local, local.Shelf!.Source);
        return local.Shelf!;
    }

    //read the mark by file name, the row order is the scan's and an assertion on an index breaks when the shelf sorts
    private static HaveMark MarkFor(ShelfView v, string fileName)
    {
        var at = v.Rows
            .Select((r, i) => (r, i))
            .Single(x => x.r.PickedQuant.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase))
            .i;
        return v.Facts![at].Have;
    }

    [Fact]
    public void ONLY_THE_ACTIVE_FILE_WEARS_THE_LOADED_MARK()
    {
        var shelf = LocalShelfOf(Probes(loadedPath: Eighth));

        Assert.Equal(HaveMark.Loaded, MarkFor(shelf, "gemma-4-e4b-Q8_0.gguf"));
        Assert.Equal(HaveMark.OtherFile, MarkFor(shelf, "gemma-4-e4b-Q4_K_M.gguf"));
    }

    //move the server to the other file and the marks must swap, or a fix keyed off row order would pass
    [Fact]
    public void AND_THE_MARKS_SWAP_WHEN_THE_SERVER_MOVES()
    {
        var shelf = LocalShelfOf(Probes(loadedPath: Quarter));

        Assert.Equal(HaveMark.Loaded, MarkFor(shelf, "gemma-4-e4b-Q4_K_M.gguf"));
        Assert.Equal(HaveMark.OtherFile, MarkFor(shelf, "gemma-4-e4b-Q8_0.gguf"));
    }

    //a null LoadedModelPath marks nothing rather than marking against, so both rows keep the loaded mark
    [Fact]
    public void AND_A_PATH_NOBODY_COULD_READ_LEAVES_THE_MARK_ALONE()
    {
        var shelf = LocalShelfOf(Probes(loadedPath: null));

        Assert.Equal(HaveMark.Loaded, MarkFor(shelf, "gemma-4-e4b-Q4_K_M.gguf"));
        Assert.Equal(HaveMark.Loaded, MarkFor(shelf, "gemma-4-e4b-Q8_0.gguf"));
    }

    //ask the server for LoadedModelPath once per shelf, two rows render the same either way and only a count can show it
    [Fact]
    public void THE_LOADED_FILE_IS_ASKED_ONCE_PER_SHELF()
    {
        var probes = Probes(loadedPath: Eighth);
        LocalShelfOf(probes);

        Assert.Equal(1, probes.LoadedPathAsked);
    }

    //the rendered half

    private static string HaveRowOf(ShelfView v, string fileName)
    {
        var at = v.Rows
            .Select((r, i) => (r, i))
            .Single(x => x.r.PickedQuant.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase))
            .i;
        return Pane.Rows(v.Rows[at], v.Facts![at], v.Shape, 100,
                cursor: at, focused: false, build: -1, focus: Region.List)
            .Single(row => row.Text.Contains("added", StringComparison.Ordinal)
                        || row.Text.Contains("loaded", StringComparison.Ordinal))
            .Text;
    }

    //the column cannot show the difference, so the pane names the active file and the sibling row does not
    [Fact]
    public void THE_PANE_NAMES_THE_ACTIVE_FILE_AND_ONLY_THE_ACTIVE_FILE()
    {
        var shelf = LocalShelfOf(Probes(loadedPath: Eighth));

        var live = HaveRowOf(shelf, "gemma-4-e4b-Q8_0.gguf");
        var other = HaveRowOf(shelf, "gemma-4-e4b-Q4_K_M.gguf");

        Assert.Contains("the model you're on", live, StringComparison.Ordinal);
        Assert.DoesNotContain("not the file in use", live, StringComparison.Ordinal);

        Assert.Contains("not the file in use", other, StringComparison.Ordinal);
        Assert.DoesNotContain("the model you're on", other, StringComparison.Ordinal);
    }

    //the sibling row draws the added glyph, and the check reads the rendered row rather than the enum
    [Fact]
    public void AND_THE_SIBLING_ROW_DRAWS_THE_ADDED_GLYPH_NOT_THE_LIVE_DOT()
    {
        var g = Gatto.Terminal.GlyphSet.Unicode;
        var shelf = LocalShelfOf(Probes(loadedPath: Eighth));

        var other = HaveRowOf(shelf, "gemma-4-e4b-Q4_K_M.gguf");

        Assert.StartsWith(g.Ok, other, StringComparison.Ordinal);
        Assert.DoesNotContain(g.Loaded, other, StringComparison.Ordinal);
    }
}
