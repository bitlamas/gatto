using Gatto.Cli.Setup;
using Gatto.Cli.Setup.Tui;
using Gatto.Core.Acquire;
using Gatto.Core.Models;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup.Tui;

//two files of one model are one row, and only the path tells which of them the server holds, so the row takes that file and wears the loaded mark
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
        Rows = [ShelfRows.Of("unsloth/gemma-4-e4b", "unsloth",
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

    [Fact]
    public void THE_ROW_TAKES_THE_SERVED_FILE_AND_WEARS_THE_LOADED_MARK()
    {
        var shelf = LocalShelfOf(Probes(loadedPath: Eighth));
        var row = Assert.Single(shelf.Rows);
        Assert.Equal("gemma-4-e4b-Q8_0.gguf", row.RowQuant!.FileName);
        Assert.Equal(HaveMark.Loaded, shelf.Facts![0].Have);
    }

    //move the server to the other file and the row's file must follow, or a pick keyed off scan order would pass
    [Fact]
    public void AND_THE_ROWS_FILE_FOLLOWS_THE_SERVER()
    {
        var shelf = LocalShelfOf(Probes(loadedPath: Quarter));
        Assert.Equal("gemma-4-e4b-Q4_K_M.gguf", Assert.Single(shelf.Rows).RowQuant!.FileName);
        Assert.Equal(HaveMark.Loaded, shelf.Facts![0].Have);
    }

    //a null LoadedModelPath marks nothing rather than marking against, so the row keeps the loaded mark
    [Fact]
    public void AND_A_PATH_NOBODY_COULD_READ_LEAVES_THE_MARK_ALONE()
    {
        var shelf = LocalShelfOf(Probes(loadedPath: null));
        Assert.Single(shelf.Rows);
        Assert.Equal(HaveMark.Loaded, shelf.Facts![0].Have);
    }

    //ask the server for LoadedModelPath once per shelf, the rows render the same either way and only a count can show it
    [Fact]
    public void THE_LOADED_FILE_IS_ASKED_ONCE_PER_SHELF()
    {
        var probes = Probes(loadedPath: Eighth);
        LocalShelfOf(probes);

        Assert.Equal(1, probes.LoadedPathAsked);
    }

    //the pane names the file in use, the one the row took
    [Fact]
    public void THE_PANE_NAMES_THE_ACTIVE_FILE()
    {
        var shelf = LocalShelfOf(Probes(loadedPath: Eighth));
        var have = Pane.Rows(shelf.Rows[0], shelf.Facts![0], shelf.Shape, 100, cursor: 0, focused: false)
            .Single(row => row.Text.Contains("loaded", StringComparison.Ordinal) || row.Text.Contains("added", StringComparison.Ordinal))
            .Text;
        Assert.Contains("the model you're on", have, StringComparison.Ordinal);
        Assert.DoesNotContain("not the file in use", have, StringComparison.Ordinal);
    }
}
