using Gatto.Cli.Setup;
using Gatto.Cli.Setup.Tui;
using Gatto.Core.Acquire;
using Gatto.Core.Hardware;
using Gatto.Core.Models;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup.Tui;

//a local row names its publisher and says it sees images only from a reading: the model's profile, the file's header, a projector beside it
public class LocalPublisherVisionTests
{
    private static FoundModel OnDisk(string name, string folder, string? quantizedBy = null) =>
        new(Path.Combine(folder, name + "-Q4_K_M.gguf"), 4_000_000_000,
            new GgufHeader(GgufOutcome.Complete, null, "gemma3", name, 262144, null,
                BlockCount: 32, HeadCount: 32, HeadCountKv: 8, EmbeddingLength: 4096,
                KeyLength: 128, ValueLength: 128, ChatTemplate: null, QuantizedBy: quantizedBy));

    private static ModelRow HubRow() =>
        ShelfRows.Of("unsloth/gemma-4-26B-A4B-it-GGUF", "unsloth", new HubQuant("gemma-4-26B-A4B-it-Q4_K_M.gguf", 4_000_000_000, null),
            FitRegime.FitsGpu, 262144, false, Badge: null, Downloads: 5, Gated: false, Params: 25_200_000_000);

    private static ShelfView LocalShelfOf(WizardProbes probes)
    {
        var flow = new SetupFlow(probes) { CanSwitchSource = true };
        flow.StartPastEngine();
        var local = Assert.IsType<WizardScreen.Choice>(flow.Answer(ShelfControls.SourceAnswer()));
        return local.Shelf!;
    }

    private static int RowOf(ShelfView v, string name) => v.Rows.ToList().FindIndex(r => r.Model == name);

    [Fact]
    public void THE_PUBLISHER_IS_THE_RECORDED_REPO_ELSE_THE_QUANTIZER_ELSE_NONE()
    {
        var fetched = OnDisk("fetched", @"C:\weights\fetched", quantizedBy: "someone-else");
        var quantized = OnDisk("quantized", @"C:\weights\quantized", quantizedBy: "bartowski");
        var bare = OnDisk("bare", @"C:\weights\bare");
        var probes = new WizardProbes
        {
            Rows = [HubRow()],
            Found = [fetched, quantized, bare],
            Existing = { [fetched.Path] = "fetched-model" },
            Added = { ["fetched-model"] = new AddedFacts("unsloth/fetched-GGUF", HasProjector: false) },
        };

        var v = LocalShelfOf(probes);

        Assert.Equal("unsloth", v.Facts![RowOf(v, "fetched")].Publisher);
        Assert.Equal("bartowski", v.Facts![RowOf(v, "quantized")].Publisher);
        Assert.Null(v.Facts![RowOf(v, "bare")].Publisher);
    }

    [Fact]
    public void VISION_COMES_FROM_THE_PROFILE_OR_A_PROJECTOR_IN_THE_MODELS_OWN_FOLDER()
    {
        var added = OnDisk("added", @"C:\weights\added");
        var alone = OnDisk("alone", @"C:\weights\alone");
        var shared = OnDisk("shared", @"C:\downloads");
        var neighbour = OnDisk("neighbour", @"C:\downloads");
        var plain = OnDisk("plain", @"C:\weights\plain");
        var probes = new WizardProbes
        {
            Rows = [HubRow()],
            Found = [added, alone, shared, neighbour, plain],
            Existing = { [added.Path] = "added-model" },
            Added = { ["added-model"] = new AddedFacts(null, HasProjector: true) },
            ProjectorsBeside = { alone.Path, shared.Path, neighbour.Path },
        };

        var v = LocalShelfOf(probes);

        Assert.True(v.Rows[RowOf(v, "added")].Vision);
        Assert.True(v.Rows[RowOf(v, "alone")].Vision);
        //a projector in a folder holding another model's files may be either model's, so it marks neither
        Assert.False(v.Rows[RowOf(v, "shared")].Vision);
        Assert.False(v.Rows[RowOf(v, "neighbour")].Vision);
        Assert.False(v.Rows[RowOf(v, "plain")].Vision);
    }

    [Fact]
    public void THE_LOCAL_PANE_NAMES_THE_PUBLISHER_AND_VISION_ONLY_WHEN_KNOWN()
    {
        var row = LocalShelf.Row(new LocalShelf.LocalGroup("m", [OnDisk("m", @"C:\w\m")]), OnDisk("m", @"C:\w\m"), null);
        var facts = new ModelFacts(LocalPath: @"C:\w\m\", FilesHere: "1 file", Publisher: "unsloth");
        const MachineShape shape = MachineShape.UnifiedWithShare;

        var known = Pane.Rows(row with { Vision = true }, facts, shape, 60, cursor: 0, focused: false, glyphs: null)
            .Select(r => r.Text.TrimEnd()).ToList();
        var unknown = Pane.Rows(row, facts with { Publisher = null }, shape, 60, cursor: 0, focused: false, glyphs: null)
            .Select(r => r.Text.TrimEnd()).ToList();

        Assert.Contains("by unsloth", known);
        Assert.Contains(known, r => r.Contains("vision", StringComparison.Ordinal));
        Assert.DoesNotContain(unknown, r => r.StartsWith("by ", StringComparison.Ordinal));
        Assert.DoesNotContain(unknown, r => r.Contains("vision", StringComparison.Ordinal) || r.Contains("text only", StringComparison.Ordinal));
    }

    //the quantizer is uploader text, so an escape or a newline in it never reaches the frame
    [Fact]
    public void A_QUANTIZER_WITH_CONTROL_CHARACTERS_IS_SANITIZED()
    {
        var odd = OnDisk("odd", @"C:\weights\odd", quantizedBy: "evil\u001b[31m\nrow");
        var v = LocalShelfOf(new WizardProbes { Rows = [HubRow()], Found = [odd] });

        var publisher = v.Facts![RowOf(v, "odd")].Publisher!;
        Assert.DoesNotContain('\u001b', publisher);
        Assert.DoesNotContain('\n', publisher);
    }
}
