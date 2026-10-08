using Gatto.Cli.Setup;
using Gatto.Cli.Setup.Tui;
using Gatto.Core.Acquire;
using Gatto.Core.Models;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup.Tui;

//a local row matches by its resolved path and a Hub row only by name, and the probes are read once per shelf
public class HaveMarksTests
{
    private static FoundModel OnDisk(string name) =>
        new(Path.Combine(@"C:\weights", name, name + "-Q4_K_M.gguf"), 4_000_000_000,
            new GgufHeader(GgufOutcome.Complete, null, "gemma3", name, 262144, null,
                BlockCount: 32, HeadCount: 32, HeadCountKv: 8, EmbeddingLength: 4096,
                KeyLength: 128, ValueLength: 128, ChatTemplate: null));

    private static ModelRow HubRow(string repo = "unsloth/gemma-4-26B-A4B-it",
        string file = "gemma-4-26B-A4B-it-Q4_K_M.gguf") =>
        ShelfRows.Of(repo, "unsloth", new HubQuant(file, 4_000_000_000, null),
            FitRegime.FitsGpu, 262144, false, Badge: null, Downloads: 5, Gated: false,
            Params: 25_200_000_000);

    //the mark vocabulary

    //the words and glyphs of the marks, spelled in one home
    [Fact]
    public void THE_MARKS_ARE_A_GLYPH_AND_A_WORD()
    {
        Assert.Equal("✓ added", HaveMarks.Text(HaveMark.Added, Gatto.Terminal.GlyphSet.Unicode));
        Assert.Equal("● loaded", HaveMarks.Text(HaveMark.Loaded, Gatto.Terminal.GlyphSet.Unicode));

        //the None arm draws a blank cell, since the absence is the statement
        Assert.Equal("", HaveMarks.Text(HaveMark.None, Gatto.Terminal.GlyphSet.Unicode));
        Assert.Equal("", HaveMarks.Glyph(HaveMark.None, Gatto.Terminal.GlyphSet.Unicode));
    }

    //no mark uses the retired =, and the matcher is fired at a string that has one before the real rows
    [Fact]
    public void NO_MARK_IS_SPELLED_WITH_THE_RETIRED_EQUALS()
    {
        static bool WearsEquals(string s) => s.Contains('=');

        Assert.True(WearsEquals("= loaded"), "the matcher cannot see the mark it is looking for");
        Assert.False(WearsEquals("● loaded"), "the matcher fires on ordinary mark text");

        foreach (var have in Enum.GetValues<HaveMark>())
        {
            Assert.False(WearsEquals(HaveMarks.Text(have, Gatto.Terminal.GlyphSet.Unicode)));
            Assert.False(WearsEquals(HaveMarks.Glyph(have, Gatto.Terminal.GlyphSet.Unicode)));
        }
    }

    //the legend names only the marks on screen, since a segment for a mark no row has reads as information
    [Fact]
    public void THE_LEGEND_NAMES_ONLY_THE_MARKS_THE_SHELF_CARRIES()
    {
        Assert.Null(HaveMarks.Legend([HaveMark.None, HaveMark.None], Gatto.Terminal.GlyphSet.Unicode));
        Assert.Equal("✓ added", HaveMarks.Legend([HaveMark.None, HaveMark.Added], Gatto.Terminal.GlyphSet.Unicode));
        Assert.Equal("● loaded", HaveMarks.Legend([HaveMark.Loaded], Gatto.Terminal.GlyphSet.Unicode));

        //the order is live first, which is the order the eye meets the marks and differs from the enum's
        Assert.Equal("● loaded · ✓ added", HaveMarks.Legend([HaveMark.Added, HaveMark.Loaded], Gatto.Terminal.GlyphSet.Unicode));
    }

    //the two questions, asked the two ways

    //a local row is matched by its resolved path, which is what a file on disk can answer
    [Fact]
    public void A_MODEL_ON_GATTOS_LIST_IS_ADDED_ON_THE_LOCAL_SHELF()
    {
        var mine = OnDisk("mine");
        var probes = new WizardProbes
        {
            Rows = [HubRow()],
            Found = [mine, OnDisk("stranger")],
            Existing = { [mine.Path] = "mine-model" },
        };

        var local = LocalShelfOf(probes);

        Assert.Equal(HaveMark.Added, local.Facts![0].Have);
        Assert.Equal(HaveMark.None, local.Facts![1].Have);
    }

    //a Hub row is matched by file name, since a repo listing has no local file to resolve
    [Fact]
    public void A_MODEL_ON_GATTOS_LIST_IS_ADDED_ON_THE_HUB_SHELF()
    {
        var probes = new WizardProbes
        {
            Rows = [HubRow(file: "already-here-Q4_K_M.gguf"), HubRow("unsloth/other", "other-Q4_K_M.gguf")],
            Named = { ["already-here-Q4_K_M.gguf"] = "already-here" },
        };

        var hub = HubShelfOf(probes);

        Assert.Equal(HaveMark.Added, hub.Facts![0].Have);
        Assert.Equal(HaveMark.None, hub.Facts![1].Have);
    }

    //loaded outranks added, and the fixture's model is both, since a row that is both must render one mark
    [Fact]
    public void THE_LOADED_MODEL_WEARS_THE_LOADED_MARK_NOT_THE_ADDED_ONE()
    {
        var serving = OnDisk("serving");
        var probes = new WizardProbes
        {
            Rows = [HubRow()],
            Found = [serving, OnDisk("idle")],
            Existing = { [serving.Path] = "serving-model" },
            Loaded = "serving-model",
        };

        var local = LocalShelfOf(probes);

        Assert.Equal(HaveMark.Loaded, local.Facts![0].Have);
        Assert.Equal(HaveMark.None, local.Facts![1].Have);
    }

    //a model on gatto's list that is not the loaded one stays added, so loaded is not painted on everything
    [Fact]
    public void AND_A_MODEL_THAT_IS_MERELY_ADDED_STAYS_ADDED_WHILE_ANOTHER_IS_LOADED()
    {
        var idle = OnDisk("idle");
        var probes = new WizardProbes
        {
            Rows = [HubRow()],
            Found = [idle],
            Existing = { [idle.Path] = "idle-model" },
            Loaded = "some-other-model",
        };

        Assert.Equal(HaveMark.Added, LocalShelfOf(probes).Facts![0].Have);
    }

    //the probes are read once per shelf rather than once per row, since LoadedModelId is an HTTP round trip
    [Fact]
    public void THE_LOADED_PROBE_IS_ASKED_ONCE_PER_SHELF()
    {
        var probes = new WizardProbes
        {
            Rows = [HubRow("a/one", "one.gguf"), HubRow("a/two", "two.gguf"), HubRow("a/three", "three.gguf")],
            Found = [OnDisk("x"), OnDisk("y"), OnDisk("z")],
        };
        var flow = new SetupFlow(probes) { CanSwitchSource = true };

        probes.LoadedAsked = 0;
        var hub = Assert.IsType<WizardScreen.Choice>(flow.StartPastEngine());
        Assert.Equal(3, hub.Shelf!.Rows.Count);
        Assert.Equal(1, probes.LoadedAsked);

        probes.LoadedAsked = 0;
        var local = Assert.IsType<WizardScreen.Choice>(flow.Answer(ShelfControls.SourceAnswer()));
        Assert.Equal(3, local.Shelf!.Rows.Count);
        Assert.Equal(1, probes.LoadedAsked);
    }

    //fixtures

    //the model's id is set on the facts on both shelves, since the render tests build their own facts and would pass without it
    [Fact]
    public void THE_MODELS_ID_RIDES_THE_FACTS_ON_BOTH_SHELVES_NOT_ONLY_THE_MARK()
    {
        var mine = OnDisk("mine");
        var probes = new WizardProbes
        {
            Rows = [HubRow(file: "already-here-Q4_K_M.gguf")],
            Named = { ["already-here-Q4_K_M.gguf"] = "already-here" },
            Found = [mine],
            Existing = { [mine.Path] = "mine-model" },
        };

        Assert.Equal("already-here", HubShelfOf(probes).Facts![0].HaveId);
        Assert.Equal("mine-model", LocalShelfOf(probes).Facts![0].HaveId);
    }

    //a model from another repo does not mark the row, even when both repos publish the same file name
    [Fact]
    public void A_MODEL_FROM_ANOTHER_REPO_DOES_NOT_MARK_THIS_ROW()
    {
        var probes = new WizardProbes
        {
            Rows = [HubRow(repo: "publisher-b/model-GGUF", file: "model-Q4_K_M.gguf")],
            FromRepo = { ["publisher-a/model-GGUF"] = "mine" },
            Named = { ["model-Q4_K_M.gguf"] = "mine" },
        };

        var facts = HubShelfOf(probes).Facts![0];

        Assert.Equal(HaveMark.None, facts.Have);
        Assert.Null(facts.HaveId);
    }

    //a known match for the row above: the same fixture asked about its own repo marks, so a shelf that marks nothing fails
    [Fact]
    public void AND_THE_ROW_FROM_ITS_OWN_REPO_STILL_MARKS()
    {
        var probes = new WizardProbes
        {
            Rows = [HubRow(repo: "publisher-a/model-GGUF", file: "model-Q4_K_M.gguf")],
            FromRepo = { ["publisher-a/model-GGUF"] = "mine" },
            Named = { ["model-Q4_K_M.gguf"] = "mine" },
        };

        var facts = HubShelfOf(probes).Facts![0];

        Assert.Equal(HaveMark.Added, facts.Have);
        Assert.Equal("mine", facts.HaveId);
    }

    //an unmarked row has no id, since the field is the mark's evidence rather than a lookup the pane can use
    [Fact]
    public void AND_A_MODEL_YOU_DO_NOT_HAVE_CARRIES_NO_ID()
    {
        var probes = new WizardProbes { Rows = [HubRow(file: "stranger-Q4_K_M.gguf")] };

        var facts = HubShelfOf(probes).Facts![0];

        Assert.Equal(HaveMark.None, facts.Have);
        Assert.Null(facts.HaveId);
    }

    //the Hub shelf the flow opens on, asserting the shelf it reached so a helper cannot drive the wrong screen
    private static ShelfView HubShelfOf(WizardProbes probes)
    {
        var flow = new SetupFlow(probes) { CanSwitchSource = true };
        var hub = Assert.IsType<WizardScreen.Choice>(flow.StartPastEngine());
        Assert.Equal(ShelfSource.Hub, hub.Shelf!.Source);
        return hub.Shelf!;
    }

    private static ShelfView LocalShelfOf(WizardProbes probes)
    {
        var flow = new SetupFlow(probes) { CanSwitchSource = true };
        flow.StartPastEngine();
        var local = Assert.IsType<WizardScreen.Choice>(flow.Answer(ShelfControls.SourceAnswer()));
        Assert.Equal(ShelfSource.Local, local.Shelf!.Source);
        return local.Shelf!;
    }
}
