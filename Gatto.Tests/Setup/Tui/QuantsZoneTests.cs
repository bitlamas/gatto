using Gatto.Cli.Setup;
using Gatto.Cli.Setup.Tui;
using Gatto.Core.Acquire;
using Gatto.Core.Hardware;
using Gatto.Core.Models;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup.Tui;

//map each quant with the same FitOf pricing the row uses, so the pane and the row can't disagree about fit
public class QuantsZoneTests
{
    private static HubQuant Q(string name, double gb) =>
        new(name, (long)(gb * 1024 * 1024 * 1024), null);

    private static readonly HubQuant[] Quants =
        [Q("m-Q8_0.gguf", 26.0), Q("m-Q4_K_M.gguf", 3.7), Q("m-Q6_K.gguf", 9.9)];

    //list the fixture in the repo's own order, heaviest first, so a smallest-first screen proves the renderer sorted
    private static ShelfRow Row(string id) => new(
        id, id.Split('/')[0], Quants[1], FitRegime.FitsGpu, 262144, false, null, 900, false,
        Params: 3_000_000_000, AllQuants: Quants, Arch: "qwen3", FileCount: 7);

    //the bigger card makes the 9.9 GB quant a GPU fit where the default calls it RAM-only, so the pricing claim can fail
    private static readonly HardwareSnapshot BigCard = new(34359738368, 34093496320, GpuKind.Discrete, 42949672960);

    private static WizardScreen.Choice Shelf(HardwareSnapshot? machine = null)
    {
        IReadOnlyList<ShelfRow> rows = [Row("unsloth/a"), Row("unsloth/b")];
        var probes = new WizardProbes
        {
            Rows = rows,
            Curated = "unsloth",
            Answer = _ => new HubSearchOutcome(rows, null, "unsloth", HiddenByFit: 0),
            //the default snapshot when the caller names no machine, so both arms build the same fixture and only the hardware differs
            Snapshot = machine ?? new HardwareSnapshot(34359738368, 34093496320, GpuKind.Discrete, 8589934592),
        };
        return Assert.IsType<WizardScreen.Choice>(new SetupFlow(probes).StartPastEngine());
    }

    private static ModelFacts Facts(WizardScreen.Choice c) => c.Shelf!.Facts![0];

    //assert the rendered pane, since the order and the mark belong to the renderer and a list check passes over a zone nobody drew
    [Fact]
    public void THE_PANE_DRAWS_ONE_ROW_PER_QUANT_SMALLEST_FIRST_WITH_THE_PICKED_ONE_MARKED()
    {
        var screen = Shelf();
        var rows = Pane.Rows(screen.Shelf!.Rows[0], Facts(screen), screen.Shelf!.Shape, 46,
            cursor: -1, focused: false, build: -1, focus: Region.List);

        var quantRows = rows.Select(r => r.Text)
            .SkipWhile(t => !t.Contains("files (", StringComparison.Ordinal))
            .Skip(1)
            .Where(t => t.Trim().Length > 0)
            .ToList();

        Assert.Equal(3, quantRows.Count);
        Assert.Contains("Q4_K_M", quantRows[0], StringComparison.Ordinal);
        Assert.Contains("Q6_K", quantRows[1], StringComparison.Ordinal);
        Assert.Contains("Q8_0", quantRows[2], StringComparison.Ordinal);
        //the picked quant shows the remembered mark, and the other two do not
        Assert.StartsWith(GlyphSet.Unicode.Angle, quantRows[0].TrimStart(), StringComparison.Ordinal);
        Assert.DoesNotContain(GlyphSet.Unicode.Angle, quantRows[1], StringComparison.Ordinal);
    }

    //each quant keeps its own fit regime, since readers pick by seeing which files their machine can hold
    [Fact]
    public void EVERY_QUANT_IS_PRICED_AND_THE_THREE_REGIMES_DIFFER()
    {
        var files = Facts(Shelf()).Files!;

        Assert.Equal(3, files.Count);
        Assert.Equal(FitRegime.FitsGpu, files.Single(f => f.Quant == "Q4_K_M").Fit);
        Assert.Equal(FitRegime.FitsRamOnly, files.Single(f => f.Quant == "Q6_K").Fit);
        Assert.Equal(FitRegime.DoesNotFit, files.Single(f => f.Quant == "Q8_0").Fit);
    }

    //price the quants against the machine the screen reports, since the two fixtures answer differently on the 9.9 GB quant
    [Fact]
    public void THE_QUANTS_ARE_PRICED_AGAINST_THE_SCREENS_OWN_MACHINE()
    {
        Assert.Equal(FitRegime.FitsRamOnly, Facts(Shelf()).Files!.Single(f => f.Quant == "Q6_K").Fit);
        Assert.Equal(FitRegime.FitsGpu,
            Facts(Shelf(BigCard)).Files!.Single(f => f.Quant == "Q6_K").Fit);
    }

    //a read per row would let two rows disagree about the card, so the machine is read once per screen
    [Fact]
    public void THE_MACHINE_IS_READ_ONCE_PER_SCREEN_NOT_ONCE_PER_ROW()
    {
        static int ReadsFor(int rowCount)
        {
            IReadOnlyList<ShelfRow> rows =
                [.. Enumerable.Range(0, rowCount).Select(i => Row($"unsloth/m{i}"))];
            var probes = new WizardProbes
            {
                Rows = rows,
                Curated = "unsloth",
                Answer = _ => new HubSearchOutcome(rows, null, "unsloth", HiddenByFit: 0),
            };
            var screen = Assert.IsType<WizardScreen.Choice>(new SetupFlow(probes).StartPastEngine());
            Assert.Equal(rowCount, screen.Shelf!.Rows.Count);
            return probes.HardwareReads;
        }

        Assert.Equal(ReadsFor(2), ReadsFor(6));
    }

    //a row whose tree gave no quants must say nothing, since an unreadable tree and an empty one are different answers
    [Fact]
    public void A_ROW_WITH_NO_QUANTS_DRAWS_NO_ZONE()
    {
        IReadOnlyList<ShelfRow> rows =
            [Row("unsloth/a") with { AllQuants = null, FileCount = 0 }];
        var screen = Assert.IsType<WizardScreen.Choice>(new SetupFlow(new WizardProbes
        {
            Rows = rows,
            Curated = "unsloth",
            Answer = _ => new HubSearchOutcome(rows, null, "unsloth", HiddenByFit: 0),
        }).StartPastEngine());

        Assert.Null(Facts(screen).Files);
        Assert.Null(Facts(screen).FileCount);
    }

    //the Files zone joins the ring only when the row has files, so Tab never stops on an empty zone
    [Fact]
    public void THE_RING_GAINS_THE_FILES_ZONE_ONLY_WHEN_THERE_ARE_FILES()
    {
        var withFiles = Shelf();
        Assert.True(Gatto.Cli.Setup.Tui.Shelf.HasFiles(withFiles.Shelf!, 0));
        Assert.Contains(Region.Files,
            Gatto.Cli.Setup.Tui.Shelf.Regions(withFiles.Shelf!, 100, 0, withFiles.Door is not null));

        IReadOnlyList<ShelfRow> bare = [Row("unsloth/a") with { AllQuants = null }];
        var without = Assert.IsType<WizardScreen.Choice>(new SetupFlow(new WizardProbes
        {
            Rows = bare,
            Curated = "unsloth",
            Answer = _ => new HubSearchOutcome(bare, null, "unsloth", HiddenByFit: 0),
        }).StartPastEngine());

        Assert.False(Gatto.Cli.Setup.Tui.Shelf.HasFiles(without.Shelf!, 0));
        Assert.DoesNotContain(Region.Files,
            Gatto.Cli.Setup.Tui.Shelf.Regions(without.Shelf!, 100, 0, without.Door is not null));
    }

    //pin the ring's whole order, since a membership check passes on any arrangement of the same zones
    [Fact]
    public void THE_RINGS_ORDER_IS_FAMILIES_PUBLISHER_LIST_FILES_SEARCH()
    {
        var screen = Shelf();

        Assert.Equal(
            [Region.Families, Region.Publisher, Region.List, Region.Files, Region.Search],
            Gatto.Cli.Setup.Tui.Shelf.Regions(screen.Shelf!, 100, 0, screen.Door is not null));
    }

    //assert the row's own PickedQuant, since every consumer downstream reads it (a pick that changed only the pane would agree with itself)
    [Fact]
    public void PICKING_A_QUANT_IN_THE_PANE_CHANGES_THE_ROWS_OWN_QUANT()
    {
        IReadOnlyList<ShelfRow> rows = [Row("unsloth/a")];
        var flow = new SetupFlow(new WizardProbes
        {
            Rows = rows,
            Curated = "unsloth",
            Answer = _ => new HubSearchOutcome(rows, null, "unsloth", HiddenByFit: 0),
        });
        var shelf = Assert.IsType<WizardScreen.Choice>(flow.StartPastEngine());

        Assert.Equal("m-Q4_K_M.gguf", shelf.Shelf!.Rows[0].PickedQuant.FileName);

        //the pick is the row's option key plus the quant label. the pane orders lightest-first and the repo order differs, so an index would name another file
        var next = flow.Answer(ShelfControls.PickAnswer(shelf.Options[0].Key, "Q6_K"));

        //the next screen names the chosen file only, since a screen naming both would read as a choice still to make
        var text = string.Join(" | ", next switch
        {
            WizardScreen.Choice c => new[] { c.Question }
                .Concat((c.BodyRows ?? []).Select(r => r.Text))
                .Concat(c.Options.Select(o => o.Label)),
            WizardScreen.Ask a => (a.BodyRows ?? []).Select(r => r.Text),
            _ => [],
        });

        Assert.Contains("Q6_K", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Q4_K_M", text, StringComparison.Ordinal);
    }

    //the zone lists only files Pick would accept, since a listed sidecar would become the row's quant (the files count still sees it)
    [Fact]
    public void A_SIDECAR_IS_NOT_OFFERED_AS_A_QUANT_THOUGH_THE_COUNT_STILL_SEES_IT()
    {
        //0.3 GB beside 7 billion parameters is far under any real quantization, the exact shape the size floor must reject
        var sidecar = Q("stories15M-q4_0.gguf", 0.3);
        IReadOnlyList<ShelfRow> rows =
        [
            Row("ggml-org/models") with
            {
                AllQuants = [.. Quants, sidecar],
                Params = 7_000_000_000,
                FileCount = 8,
            },
        ];
        var screen = Assert.IsType<WizardScreen.Choice>(new SetupFlow(new WizardProbes
        {
            Rows = rows,
            Curated = "unsloth",
            Answer = _ => new HubSearchOutcome(rows, null, "unsloth", HiddenByFit: 0),
        }).StartPastEngine());

        var files = Facts(screen).Files!;

        Assert.Equal(3, files.Count);
        Assert.DoesNotContain(files, f => f.Quant.Contains("q4_0", StringComparison.OrdinalIgnoreCase));
        //the heading counts every file in the repo, including the one refused as an offer
        Assert.Equal(8, Facts(screen).FileCount);
    }

    //a file with no readable quant token draws no row, since picks match by label and an unnamed entry could never be one
    [Fact]
    public void A_FILE_WITH_NO_QUANT_TOKEN_DRAWS_NO_ROW()
    {
        IReadOnlyList<ShelfRow> rows =
            [Row("unsloth/a") with { AllQuants = [.. Quants, Q("model.gguf", 4.2)] }];
        var screen = Assert.IsType<WizardScreen.Choice>(new SetupFlow(new WizardProbes
        {
            Rows = rows,
            Curated = "unsloth",
            Answer = _ => new HubSearchOutcome(rows, null, "unsloth", HiddenByFit: 0),
        }).StartPastEngine());

        var files = Facts(screen).Files!;

        Assert.Equal(3, files.Count);
        Assert.All(files, f => Assert.NotEqual("", f.Quant));
    }
}
