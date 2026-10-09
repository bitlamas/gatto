using Gatto.Cli.Setup;
using Gatto.Cli.Setup.Tui;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup.Tui;

//every press target is read off the frame that was painted, so a test asks what text a target covers rather than where layout put it
public class HitMapTests
{
    //the frame the face painted and the map it kept, with no key read, so the first paint is the one asserted on
    internal static (IReadOnlyList<string> Rows, HitMap Map) Painted(WizardScreen.Choice c, int width = 120, int height = 30,
        params ConsoleKeyInfo[] keys)
    {
        var rig = new WizardRig(width);
        rig.Surface.Height = height;
        var face = rig.TuiFace(keys);
        Assert.Throws<InvalidOperationException>(() => face.Choose(c));
        return (face.LastPainted, face.LastHits!);
    }

    private static IEnumerable<HitTarget> Of(HitMap map, HitKind kind) => map.Targets.Where(t => t.Tag.Kind == kind);

    [Theory]
    [InlineData("unified")]
    [InlineData("discrete")]
    public void EVERY_TABLE_ROW_IS_ONE_ROW_TARGET_HOLDING_ITS_NAME(string machine)
    {
        var shelf = ShelfFixtures.Shelf(machine);
        var (rows, map) = Painted(shelf);
        var shown = shelf.Shelf!.Rows.Count(r => rows.Any(p => p.Contains(r.Model, StringComparison.Ordinal)));
        Assert.True(shown > 1, "the fixture paints more than one row");
        for (var i = 0; i < shown; i++)
        {
            var t = Assert.Single(Of(map, HitKind.Row), x => x.Tag.Index == i);
            Assert.Contains(shelf.Shelf.Rows[i].Model, ShelfFixtures.TextAt(rows, t, map.Height));
        }
    }

    [Fact]
    public void THE_GROUPED_SHELF_INDEXES_ROWS_PAST_ITS_HEADINGS()
    {
        var shelf = ShelfFixtures.Shelf("discrete");
        Assert.True(Shelf.Grouped(shelf.Shelf!), "the discrete fixture draws both headings");
        var (rows, map) = Painted(shelf);
        Assert.DoesNotContain(Of(map, HitKind.Row), t => ShelfFixtures.TextAt(rows, t, map.Height).Contains("on the graphics card"));
        Assert.DoesNotContain(Of(map, HitKind.Row), t => ShelfFixtures.TextAt(rows, t, map.Height).Contains("in system memory"));
    }

    [Fact]
    public void A_WIDE_GLYPH_DOES_NOT_MOVE_THE_SPAN_OFF_ITS_TEXT()
    {
        var shelf = ShelfFixtures.Shelf("unified");
        var v = shelf.Shelf!;
        var wide = shelf with { Shelf = v with { Rows = [v.Rows[0] with { Model = "漢" + v.Rows[0].Model }, .. v.Rows.Skip(1)] } };
        var (rows, map) = Painted(wide);
        var t = Assert.Single(Of(map, HitKind.Row), x => x.Tag.Index == 0);
        var text = ShelfFixtures.TextAt(rows, t, map.Height);
        Assert.StartsWith("漢" + v.Rows[0].Model, text.TrimStart().TrimStart('❯', '›', ' '));
        //the row's cells end where its painted text ends, a span counted in characters stops one cell short
        var line = rows[t.Row];
        var left = line[..line.IndexOf('│')].TrimEnd();
        Assert.Equal(left, text.TrimEnd());
    }

    [Theory]
    [InlineData(24)]
    [InlineData(20)]
    public void TARGETS_READ_THEIR_TEXT_AFTER_THE_FIT_DROPS_BLANKS(int height)
    {
        var shelf = ShelfFixtures.Shelf("unified");
        var (rows, map) = Painted(shelf, height: height);
        Assert.NotEmpty(Of(map, HitKind.Row));
        foreach (var t in Of(map, HitKind.Row))
            Assert.Contains(shelf.Shelf!.Rows[t.Tag.Index].Model, ShelfFixtures.TextAt(rows, t, map.Height));
    }

    [Fact]
    public void A_FRAME_TALLER_THAN_THE_TERMINAL_SHIFTS_ITS_TARGETS_BY_THE_EXCESS()
    {
        var shelf = ShelfFixtures.Shelf("unified");
        foreach (var height in new[] { 8, 12 })
        {
            var (rows, map) = Painted(shelf, height: height);
            var excess = rows.Count - height;
            Assert.True(excess > 0, $"the frame overflows a terminal {height} rows high");
            foreach (var t in Of(map, HitKind.Row))
            {
                Assert.InRange(t.Row, 0, height - 1);
                Assert.Contains(shelf.Shelf!.Rows[t.Tag.Index].Model, rows[t.Row + excess]);
            }
            //the first model row was painted at this line, left of the divider since the pane's title names it too, and a line above the excess scrolled off the top
            var model = shelf.Shelf!.Rows[0].Model;
            var at = rows.ToList().FindIndex(r => r.IndexOf(model, StringComparison.Ordinal) is >= 0 and var i && i < r.IndexOf('│'));
            Assert.True(at >= 0, "the first row is painted left of the pane");
            Assert.Equal(at >= excess, Of(map, HitKind.Row).Any(t => t.Tag.Index == 0));
        }
    }

    //the open accordion's lines, each right of every table row and reading its own publisher or file
    [Fact]
    public void THE_OPEN_PANES_LINES_AND_FILES_ARE_TARGETS_RIGHT_OF_THE_TABLE()
    {
        var shelf = ShelfFixtures.Shelf("discrete");
        var (rows, map) = Painted(shelf, keys: ShelfFixtures.OpenKeys(shelf));
        var pubs = shelf.Shelf!.Facts![0].Publishers!;
        var tableEnd = Of(map, HitKind.Row).Max(t => t.LastCol);
        var lines = Of(map, HitKind.PaneLine).ToList();
        Assert.Equal(pubs.Count, lines.Count);
        foreach (var t in lines)
        {
            Assert.True(t.FirstCol > tableEnd);
            Assert.Contains(pubs[t.Tag.Index].Org, ShelfFixtures.TextAt(rows, t, map.Height));
            Assert.Equal(Region.Files, t.Tag.Area);
        }
        var files = Of(map, HitKind.PaneFile).ToList();
        Assert.NotEmpty(files);
        foreach (var t in files)
        {
            Assert.True(t.FirstCol > tableEnd);
            var label = Pane.Ordered(pubs[t.Tag.Index].Files)[t.Tag.File].Label;
            Assert.Contains(label, ShelfFixtures.TextAt(rows, t, map.Height));
        }
    }

    //a wide glyph in every name moves the divider, and a pane target still starts right after it. a map counted in characters starts one cell short
    [Fact]
    public void A_PANE_TARGET_RIGHT_OF_A_WIDE_GLYPH_STARTS_AFTER_THE_DIVIDER()
    {
        var shelf = ShelfFixtures.Shelf("discrete");
        var v = shelf.Shelf!;
        var wide = shelf with { Shelf = v with { Rows = [.. v.Rows.Select(r => r with { Model = "漢" + r.Model })] } };
        var (rows, map) = Painted(wide);
        var lines = Of(map, HitKind.PaneLine).ToList();
        Assert.NotEmpty(lines);
        foreach (var t in lines)
        {
            var line = rows[t.Row];
            Assert.Equal(UnicodeWidth.Of(line[..(line.IndexOf('│') + 2)]), t.FirstCol);
            Assert.Contains(v.Facts![0].Publishers![t.Tag.Index].Org, ShelfFixtures.TextAt(rows, t, map.Height));
        }
    }

    [Fact]
    public void THE_FOLDED_FRAMES_SHOWN_FILE_IS_A_TARGET()
    {
        var shelf = ShelfFixtures.Shelf("unified");
        Assert.Equal(0, Shelf.PaneWidth(shelf.Shelf!, 80, GlyphSet.Unicode));
        var (rows, map) = Painted(shelf, width: 80);
        var t = Assert.Single(Of(map, HitKind.FoldedFile));
        Assert.Equal(Region.Files, t.Tag.Area);
        var file = Pane.Ordered(shelf.Shelf!.Facts![0].Files!)[Pane.FileAt(shelf.Shelf.Facts[0].Files!, -1)];
        Assert.Contains(file.Label, ShelfFixtures.TextAt(rows, t, map.Height));
    }

    [Fact]
    public void UNDER_A_THE_DISCRETE_COUNT_ROW_JUMPS_TO_EACH_REGIME()
    {
        var (rows, map) = Painted(ShelfFixtures.Shelf("discrete", lifted: true));
        var jumps = Of(map, HitKind.RegimeJump).ToList();
        var gpu = Assert.Single(jumps, t => t.Tag.Regimes!.SetEquals([Gatto.Core.Models.FitRegime.FitsGpu]));
        var ram = Assert.Single(jumps, t => t.Tag.Regimes!.SetEquals([Gatto.Core.Models.FitRegime.FitsRamOnly]));
        Assert.Matches(@"^\d+ ", ShelfFixtures.TextAt(rows, gpu, map.Height));
        Assert.Matches(@"^\d+ ", ShelfFixtures.TextAt(rows, ram, map.Height));
    }

    [Fact]
    public void ON_THE_UNIFIED_MACHINE_FITS_IS_ONE_JUMP_TO_EITHER_REGIME()
    {
        var (rows, map) = Painted(ShelfFixtures.Shelf("unified", lifted: true));
        var fits = Assert.Single(Of(map, HitKind.RegimeJump), t => ShelfFixtures.TextAt(rows, t, map.Height).Contains("fits"));
        Assert.True(fits.Tag.Regimes!.SetEquals([Gatto.Core.Models.FitRegime.FitsGpu, Gatto.Core.Models.FitRegime.FitsRamOnly]));
    }

    [Fact]
    public void THE_HUBS_SHOW_ALL_CLAUSE_ACTS_AS_A()
    {
        var shelf = ShelfFixtures.Shelf("discrete");
        var (rows, map) = Painted(shelf);
        var t = Assert.Single(Of(map, HitKind.Clause), x => x.Tag.Key == "a");
        Assert.Equal("a show all", ShelfFixtures.TextAt(rows, t, map.Height));
    }

    [Fact]
    public void THE_LOCAL_SHOWS_ALL_CLAUSE_ACTS_AS_A()
    {
        var shelf = ShelfFixtures.Shelf("discrete");
        var v = shelf.Shelf! with { Source = ShelfSource.Local, HiddenOlder = 2 };
        var (rows, map) = Painted(shelf with { Shelf = v });
        var t = Assert.Single(Of(map, HitKind.Clause), x => x.Tag.Key == "a");
        Assert.EndsWith("a shows all", ShelfFixtures.TextAt(rows, t, map.Height));
    }

    [Fact]
    public void THE_M_SHOWS_THEM_CLAUSE_ACTS_AS_M()
    {
        var shelf = ShelfFixtures.Shelf("discrete");
        var row = new WizardRow("gatto also found 2 models already on this machine, m shows them", Tone: RowTone.Aside, Keys: ["m"]);
        var (rows, map) = Painted(shelf with { BodyRows = [row] });
        var t = Assert.Single(Of(map, HitKind.Clause), x => x.Tag.Key == "m");
        Assert.Equal("m shows them", ShelfFixtures.TextAt(rows, t, map.Height));
    }

    [Fact]
    public void THE_PARAMS_HEADER_IS_ITS_OWN_TARGET()
    {
        var (rows, map) = Painted(ShelfFixtures.Shelf("discrete"));
        var t = Assert.Single(Of(map, HitKind.ParamsHeader));
        Assert.StartsWith("params", ShelfFixtures.TextAt(rows, t, map.Height));
    }

    [Fact]
    public void EVERY_FOOTER_WORD_BUT_THE_ARROWS_IS_ONE_KEY_TARGET()
    {
        var (rows, map) = Painted(ShelfFixtures.Shelf("discrete"));
        var keys = Of(map, HitKind.FooterKey).ToList();
        Assert.NotEmpty(keys);
        Assert.DoesNotContain(keys, t => ShelfFixtures.TextAt(rows, t, map.Height).Contains("move"));
        var esc = Assert.Single(keys, t => t.Tag.Key == "Esc");
        Assert.Matches(@"^Esc (back|leave)$", ShelfFixtures.TextAt(rows, esc, map.Height));
        var tab = Assert.Single(keys, t => t.Tag.Key == "Tab");
        Assert.Equal("Tab area", ShelfFixtures.TextAt(rows, tab, map.Height));
    }

    [Fact]
    public void THE_DOOR_AND_EACH_CHIP_READ_THEIR_OWN_TEXT()
    {
        var shelf = ShelfFixtures.Shelf("discrete");
        var (rows, map) = Painted(shelf);
        var door = Assert.Single(Of(map, HitKind.Door));
        Assert.Equal(Region.Search, door.Tag.Area);
        Assert.Contains(shelf.Door!, ShelfFixtures.TextAt(rows, door, map.Height));
        var families = shelf.Shelf!.Families!;
        var chips = Of(map, HitKind.Chip).ToList();
        Assert.Equal(families.Count, chips.Count);
        foreach (var t in chips)
        {
            Assert.Equal(families[t.Tag.Index], ShelfFixtures.TextAt(rows, t, map.Height));
            Assert.Equal(Region.Families, t.Tag.Area);
        }
    }

    [Fact]
    public void AN_ESCAPE_ROW_ANSWERS_ITS_OPTION()
    {
        var shelf = ShelfFixtures.Shelf("discrete");
        var withEscape = shelf with { Options = [.. shelf.Options, new ChoiceOption("escape-key", "Skip this step")] };
        var (rows, map) = Painted(withEscape);
        var t = Assert.Single(Of(map, HitKind.EscapeRow));
        Assert.Equal("escape-key", t.Tag.Answer);
        Assert.Contains("Skip this step", ShelfFixtures.TextAt(rows, t, map.Height));
    }

    //every key word any footer builder draws maps to a key, so a new footer word cannot ship with a press that does nothing
    [Fact]
    public void EVERY_FOOTER_KEY_THE_WIZARD_DRAWS_MAPS_TO_A_KEY()
    {
        var maps = new List<HitMap>
        {
            Painted(ShelfFixtures.Shelf("discrete")).Map,
            Painted(ShelfFixtures.Shelf("discrete", lifted: true)).Map,
            Painted(new WizardScreen.Choice("pick", "Which one?", [new("a", "One"), new("b", "Two")], Unnumbered: true, SwitchesSource: true, Door: "type…")).Map,
            Painted(new WizardScreen.Choice("nodefault", "Which one?", [new("a", "One"), new("b", "Two")], NoDefault: true)).Map,
            Painted(new WizardScreen.Choice("keys", "Go on?", [new("y", "continue"), new("n", "stop")], KeysOnly: true)).Map,
            Painted(new WizardScreen.Choice("watch", "Fetching", [new("p", "pause", Press: "p"), new("s", "skip", Press: "Space"), new("x", "stop")],
                Watching: true, KeysOnly: true)).Map,
            AskMap(new WizardScreen.Ask("ask", "Where?", _ => null, "a folder…", new AskOffer("here", "C:\\m"))),
            AskMap(new WizardScreen.Ask("ask2", "Where?", _ => null, "a folder…")),
        };
        var keys = maps.SelectMany(m => Of(m, HitKind.FooterKey)).Select(t => t.Tag.Key!).Distinct().ToList();
        Assert.Contains("Esc", keys);
        Assert.Contains("Space", keys);
        foreach (var k in keys) Assert.True(FooterKeys.KeyOf(k) is not null, $"footer key {k} maps to no key");
    }

    private static HitMap AskMap(WizardScreen.Ask a)
    {
        var rig = new WizardRig(120);
        rig.Surface.Height = 30;
        var face = rig.TuiFace();
        Assert.Throws<InvalidOperationException>(() => face.Ask(a));
        return face.LastHits;
    }

    [Fact]
    public void THE_LAST_CELL_OF_A_SPAN_IS_A_HIT()
    {
        var tag = new HitTag(HitKind.Row, Index: 0);
        var map = HitMap.Of([new PaintedRow([new Run("abcd", Tag: tag)])], 20, 0);
        Assert.NotNull(map.At(3, 0));
        Assert.Null(map.At(4, 0));
    }

    [Fact]
    public void A_CLAMPED_RUN_KEEPS_ITS_TAG()
    {
        var tag = new HitTag(HitKind.Row, Index: 3);
        var row = new PaintedRow([new Run("abcdef", Tag: tag)]).Clamp(3);
        Assert.Equal("abc", row.Text);
        Assert.Same(tag, row.Runs[0].Tag);
    }

    [Fact]
    public void ADJACENT_RUNS_WITH_ONE_TAG_ARE_ONE_TARGET_AND_A_FOOTER_STRING_IS_A_KEY()
    {
        var tag = new HitTag(HitKind.Row, Index: 0);
        var map = HitMap.Of([new PaintedRow([new Run("ab", Tag: tag), new Run("cd", Tag: tag), new Run(" "), new Run("Esc", Tag: "Esc")])], 20, 0);
        Assert.Equal(2, map.Targets.Count);
        Assert.Equal((0, 3), (map.Targets[0].FirstCol, map.Targets[0].LastCol));
        Assert.Equal(new HitTag(HitKind.FooterKey, Key: "Esc"), map.Targets[1].Tag);
        Assert.Equal(map.Targets[1], map.At(6, 0));
        Assert.Null(map.At(4, 0));
    }
}
