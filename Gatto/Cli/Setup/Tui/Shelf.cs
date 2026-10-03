using Gatto.Core.Acquire;
using Gatto.Core.Hardware;
using Gatto.Terminal;

namespace Gatto.Cli.Setup.Tui;

//composed here and painted by ScreenPainter, so the shelf is a screen like any other rather than a second wizard
internal static class Shelf
{
    private const string Indent = "  ";

    //the digit column width: a digit, a period and a space up to nine, three blank cells past nine
    private const int NumberWidth = 3;

    //the mark where the keys are, ❯, and › for what is remembered, one pair the whole wizard shares
    private static string HereOf(GlyphSet g) => g.Prompt + " ";
    //a function of the set for HereOf's reason, since a const cannot read one
    private static string PickOf(GlyphSet g) => $"{g.Angle} ";
    private const string NoMark = "  ";

    private const int ParamsColumn = 6;
    private const int SizeQuantColumn = 14;
    private const int KindColumn = 8;
    private const int RunsColumn = 9;

    //everything the model column is measured against: the mark, the digits, the fixed columns and the gaps between them
    private const int Chrome =
        2 + NumberWidth
        + 2 + ParamsColumn
        + 2 + SizeQuantColumn
        + 2 + KindColumn
        + 1;

    //what the runs column adds when the machine has one
    private const int RunsChrome = 2 + RunsColumn;

    //eight cells for the Hub have-mark word, the width of ● loaded, the wider of the two
    private const int HaveColumn = 8;

    private const int HaveChrome = 2 + HaveColumn;

    //the LOCAL cell holds one glyph and the word moves to the pane, a word-wide cell left the pane too narrow
    private const int HaveGlyphChrome = 2 + 1;

    //what this row already is to the user. the facts were aligned to the rows at construction, so a mark can't pair with another name
    internal static HaveMark Have(ShelfView v, int i) =>
        v.Facts is { } fs && i < fs.Count ? fs[i].Have : HaveMark.None;

    private static bool AnyHaveMark(ShelfView v)
    {
        for (var i = 0; i < v.Rows.Count; i++) if (Have(v, i) != HaveMark.None) return true;
        return false;
    }

    //one tail column, and the have-mark outranks the fit mark in it. a shelf with no runs column pays for it only when a mark appears
    private static int TailChrome(ShelfView v) =>
        FitMarks.HasRunsColumn(v.Shape) ? RunsChrome
        : !AnyHaveMark(v) ? 0
        : v.Source == ShelfSource.Local ? HaveGlyphChrome
        : HaveChrome;

    //every row of the frame that is not a model, so a frame that gains or loses chrome changes this. the pane is out of the sum, it sits beside the table
    internal const int FrameRows = 15;

    //the row budget for this terminal height. zero or less means the height is unknown, so answer the plain default with at least one row
    public static int RowBudget(int height) =>
        height <= 0 ? HubSearch.DefaultRowBudget : Math.Max(1, height - FrameRows);

    //the families ladder and the publisher slot. the publisher is its own labelled control, since it selects a different axis than the family
    public static PaintedRow Chips(ShelfView v, int chip, Region focus, int width,
        GlyphSet? glyphs)
    {
        var g = glyphs ?? GlyphSet.Unicode;
        var focused = focus == Region.Families;
        var runs = new List<Run> { new(Indent) };
        var families = v.Families ?? [];

        for (var i = 0; i < families.Count; i++)
        {
            if (i > 0) runs.Add(new Run($" {g.Dot} ", RunInk.Dim));
            if (focused && i == chip)
            {
                runs.Add(new Run(HereOf(glyphs ?? GlyphSet.Unicode), RunInk.Accent));
                runs.Add(new Run(families[i], RunInk.Bright));
            }
            else
            {
                //a typed search lifts the family, so no chip is active while one is showing. the chips row reads Searched and the count line reads Lift
                var active = !v.Searched
                    && string.Equals(families[i], v.Family, StringComparison.OrdinalIgnoreCase);
                runs.Add(new Run(families[i], active ? RunInk.Accent : RunInk.Dim));
            }
        }

        if (v.Searched) runs.Add(new Run("   (lifted, you searched)", RunInk.Dim));

        //on a local shelf the folder takes the publisher slot, and the path is cut from the front like the pane's
        if (v.Source == ShelfSource.Local)
            return v.Folder is not { Length: > 0 } dir ? new PaintedRow(runs)
                //the local slot is never focused, it names a folder and opens nothing so it stays out of the ring
                : Slot(runs, "folder  ", Pane.PathTail(dir, FolderSlot, glyphs ?? GlyphSet.Unicode),
                       width, focused: false, g);

        if (!v.HasPublisherSlot || v.CuratedPublisher is not { Length: > 0 } pub)
            return new PaintedRow(runs);
        return Slot(runs, "publisher  ", pub, width, focus == Region.Publisher, g);
    }

    //the folder slot's budget, 34 cells of path before the cut from the front starts
    private const int FolderSlot = 34;

    //one composer for both slots, so their spacing can't drift. a focused slot shows the mark and never a caret
    private static PaintedRow Slot(List<Run> runs, string label, string value, int width,
        bool focused, GlyphSet g)
    {
        //a focused slot draws the mark and no caret, the footer's Enter pick is the affordance the zone needs
        var mark = focused ? HereOf(g) : "";
        var left = string.Concat(runs.Select(r => r.Text));
        var right = $"{mark}{label}{value}";
        //the slot is right-aligned content, so the gap is measured from the inset width like the banner and the footer
        var gap = Math.Max(1, Margins.Inside(width) - UnicodeWidth.Of(left) - UnicodeWidth.Of(right));
        return new PaintedRow([
            .. runs,
            new Run(new string(' ', gap)),
            .. focused ? (IReadOnlyList<Run>)[new Run(mark, RunInk.Accent)] : [],
            new Run(label, RunInk.Dim),
            new Run(value, focused ? RunInk.Bright : RunInk.Plain)]);
    }

    //the empty block's own indent, deeper than the table's since it holds prose under a heading
    private const string EmptyIndent = "       ";

    //a local file name is capped at 24 cells with an ellipsis. a repo name never is, since it is an identifier the user may retype
    private const int LocalNameCap = 24;

    private static string Name(ShelfView v, ShelfRow r, GlyphSet g) =>
        v.Source == ShelfSource.Local ? TermText.TruncateCells(Name(v, r), LocalNameCap, g) : Name(v, r);

    //the model column takes the widest name on screen, after a local name is capped
    internal static int ModelWidth(ShelfView v, GlyphSet g) =>
        v.Rows.Count == 0 ? 0 : v.Rows.Max(r => UnicodeWidth.Of(Name(v, r, g)));

    //the local shelf leaves the params column out, so its column and gap come off what the model is measured against
    private static int ChromeOf(ShelfView v) =>
        v.Source == ShelfSource.Local ? Chrome - 2 - ParamsColumn : Chrome;

    //the table's share of the row, divider included. the size and quant column is counted at the width the rows are padded to rather than its constant
    internal static int LeftWidth(ShelfView v, GlyphSet g) =>
        ModelWidth(v, g) + ChromeOf(v) - SizeQuantColumn + SizeQuantWidth(v) + TailChrome(v);

    //the pane's column, or 0 when it would be narrower than Pane.MinWidth. the table's width decides it, so long names fold the shelf sooner
    internal static int PaneWidth(ShelfView v, int width, GlyphSet g)
    {
        //the divider and the space after it take two cells off the pane
        var pane = width - LeftWidth(v, g) - 2;
        return pane >= Pane.MinWidth ? pane : 0;
    }

    //the table, one row per model plus its header. the rows arrive ordered and are never sorted here, the order is settled elsewhere
    public static IReadOnlyList<PaintedRow> Table(ShelfView v, int row, bool focused,
        GlyphSet? glyphs)
    {
        var g = glyphs ?? GlyphSet.Unicode;
        var mw = ModelWidth(v, g);
        var runs = FitMarks.HasRunsColumn(v.Shape);
        //the local shelf has no params to show, so its header, cell and gap stay out and the model column takes the freed cells
        var paramsCol = v.Source != ShelfSource.Local;
        var tail = Math.Max(0, TailChrome(v) - 2);
        //the size-quant cell takes the widest pair shown, because Pane.Cells pads and never clips, so a narrow cell pushes every column after it
        var sqw = SizeQuantWidth(v);
        var sw = SizeWidth(v);
        var rows = new List<PaintedRow>
        {
            PaintedRow.Of(
                new string(' ', 2 + NumberWidth)
                + Pane.Cells("model", mw) + "  "
                + (paramsCol ? Pane.Right("params", ParamsColumn) + "  " : "")
                //the TUI shelf composes its own header, separate from ShelfTable's, so a fix to one header must be made in both
                + Pane.Cells($"size {(glyphs ?? GlyphSet.Unicode).Dot} quant", sqw) + "  "
                + Pane.Cells("kind", KindColumn)
                + (runs ? "  runs" : ""),
                RunInk.Dim),
        };

        for (var i = 0; i < v.Rows.Count; i++)
        {
            var r = v.Rows[i];
            var older = v.Facts is { } fs && i < fs.Count && fs[i].Older;
            //numbers stop at nine, past it the column is blank, since a tenth row with "10." would promise a key that is not there
            var number = i < 9
                ? $"{i + 1}. "
                : new string(' ', NumberWidth);
            var body = Pane.Cells(Name(v, r, g), mw) + "  "
                     + (paramsCol ? Pane.Right(ShelfTable.ParamsCell(r), ParamsColumn) + "  " : "")
                     + Pane.Cells(SizeQuant(r, sw), sqw) + "  "
                     + Pane.Cells(Structure(v, i), KindColumn);

            var cells = new List<Run>();
            if (i == row && focused)
            {
                cells.Add(new Run(HereOf(glyphs ?? GlyphSet.Unicode) + number + body, RunInk.Accent));
            }
            else if (i == row)
            {
                cells.Add(new Run(PickOf(g), RunInk.Dim));
                cells.Add(new Run(number + body, RunInk.Bright));
            }
            else
            {
                cells.Add(new Run(NoMark + number));
                cells.Add(new Run(body, older ? RunInk.Dim : RunInk.Plain));
            }

            //one tail column, and the have-mark outranks the fit mark since what the user already has changes what Enter means
            if (tail > 0)
            {
                var have = Have(v, i);
                cells.Add(new Run("  "));
                if (have != HaveMark.None)
                {
                    cells.Add(new Run(HaveMarks.Glyph(have, g), HaveMarks.Ink(have)));
                    //the LOCAL shelf takes the glyph alone, since the cell must stay one cell wide. its word is on the pane and in the count line's legend
                    cells.Add(new Run(
                        Pane.Cells(v.Source == ShelfSource.Local ? "" : " " + HaveMarks.Word(have), tail - 1),
                        RunInk.Dim));
                }
                else if (runs)
                {
                    var mark = FitMarks.Of(r.Fit, v.Shape, g);
                    cells.Add(new Run(mark.Glyph, mark.Ink));
                    cells.Add(new Run(Pane.Cells(mark.Word.Length > 0 ? " " + mark.Word : "", tail - 1),
                        RunInk.Dim));
                }
                else
                {
                    //an unmarked row on a shelf that has the column still owes it the cells, or the divider moves left on that row alone
                    cells.Add(new Run(new string(' ', tail)));
                }
            }

            rows.Add(new PaintedRow(cells));
        }

        //the rows the budget left off are counted, since a list that just stops reads as a complete one
        if (v.MoreBelow > 0)
            rows.Add(PaintedRow.Of(new string(' ', 2 + NumberWidth) + $"{g.Ellipsis} {v.MoreBelow} more below", RunInk.Dim));

        return rows;
    }

    //the count line: the position and the hidden buckets by name. a bucket with a key says so, the one without stays quiet about keys
    public static string CountLine(ShelfView v, GlyphSet? glyphs)
    {
        var g = glyphs ?? GlyphSet.Unicode;
        //a zero total means the producer did not count, so report the rows in hand, smaller than the truth and never wrong
        var total = Math.Max(v.Total, v.Rows.Count + v.MoreBelow);
        //the Range glyph, since this is a range's typography rather than a not-run mark
        var position = $"1{g.Range}{v.Rows.Count} of {total}";

        //the LOCAL shelf's legend, and only the local shelf's, since its cells are bare glyphs. the marks it lists come from the rows on screen
        var legend = v.Source == ShelfSource.Local
            ? HaveMarks.Legend(Enumerable.Range(0, v.Rows.Count).Select(i => Have(v, i)), g)
            : null;

        if (v.ShowAll)
            return Tail(g, position, "older and too-big rows shown, a hides them", legend);

        var hidden = HiddenClause(v.HiddenNewer, v.HiddenOlder, v.HiddenByFit, g);

        //the family clause stays out of the hidden list. that filter is lifted with "all", so its number can't sit under a sentence naming "a"
        var family = FamilyClause(v.HiddenByFamily);

        return Tail(g, position, hidden, family, legend);
    }

    //what another family chip is holding, and "all" shows it. both the chip's sentence and the count line end in this clause
    internal static string? FamilyClause(int hiddenByFamily) =>
        hiddenByFamily > 0 ? $"{hiddenByFamily} in other families, all shows them" : null;

    //what the shelf is holding back and the key that shows it, newest first and never summed. null when nothing is held back
    internal static string? HiddenClause(int newer, int older, int tooBig, GlyphSet g)
    {
        var parts = new List<string>();
        if (newer > 0) parts.Add($"{newer} newer");
        if (older > 0) parts.Add($"{older} older");
        if (tooBig > 0) parts.Add($"{tooBig} too big");
        return parts.Count == 0 ? null : $"{string.Join($" {g.Dot} ", parts)}, a shows all";
    }

    //the count line's clauses joined by the one separator, skipping the empty ones, so a fourth clause is not a rewrite of the third
    private static string Tail(GlyphSet g, string position, params string?[] clauses) =>
        string.Join($" {g.Dot} ", new[] { position }.Concat(clauses.Where(c => c is { Length: > 0 }))!);

    //the whole middle of the screen: chips, the table beside its pane, the count line. file is -1 for wherever the knee is
    public static IReadOnlyList<PaintedRow> Body(
        ShelfView v, int row, int chip, int file, Region focus, int width, int build = -1,
        GlyphSet? glyphs = null)
    {
        var g = glyphs ?? GlyphSet.Unicode;
        //the facts list must line up with the rows. a copy that sets only Rows keeps the old facts, and the readers below guard the index so a short list goes unnoticed
        if (v.Facts is { } aligned && aligned.Count != v.Rows.Count)
            throw new ArgumentException(
                $"a shelf carries one ModelFacts per row: {v.Rows.Count} rows, {aligned.Count} facts",
                nameof(v));

        //a view with no families has no family default to report, so the chips row is left out rather than drawn as bare indent
        var rows = v.NothingFetched
            ? []
            : new List<PaintedRow>
            {
                Chips(v, chip, focus, width, glyphs),
                PaintedRow.Of(""),
            };

        //an empty shelf keeps the chips row above, because a search that found nothing still has a family default to report
        if (v.Rows.Count == 0)
        {
            foreach (var line in v.Empty ?? [])
                rows.Add(PaintedRow.Of(line.Length == 0 ? "" : EmptyIndent + line));
            return rows;
        }

        var table = Table(v, row, focus == Region.List, glyphs);
        var pane = PaneWidth(v, width, g);

        if (pane > 0 && row >= 0 && row < v.Rows.Count)
        {
            var facts = v.Facts is { } fs && row < fs.Count ? fs[row] : null;
            //the files block keeps its height on a shelf where some row has files. a shelf with none draws it for no row, so the frame cannot move
            var anyFiles = v.Facts is { } all && all.Any(f => f.Files is { Count: > 0 });
            rows.AddRange(Beside(
                table,
                Pane.Rows(v.Rows[row], facts, v.Shape, pane, file, focus == Region.Files, build, focus, glyphs,
                    holdFilesBlock: anyFiles),
                LeftWidth(v, g),
                //the divider accents for either pane zone, since a divider lit only for the files would say the keys are elsewhere
                focus is Region.Files or Region.Builds, g));
        }
        else
        {
            rows.AddRange(table);
        }

        rows.Add(PaintedRow.Of(""));
        rows.Add(PaintedRow.Of(Indent + CountLine(v, g), RunInk.Dim));

        //below the pane's threshold the facts move under the count line, since the count belongs with the table it describes
        if (pane == 0 && row >= 0 && row < v.Rows.Count)
            rows.AddRange(Pane.Fold(v.Rows[row],
                v.Facts is { } ff && row < ff.Count ? ff[row] : null,
                v.Shape, width, focus == Region.Files, file, g));

        return rows;
    }

    //two columns with a divider between them, as many rows as the taller side has. the divider accents when the keys are in the pane
    private static IReadOnlyList<PaintedRow> Beside(
        IReadOnlyList<PaintedRow> left, IReadOnlyList<PaintedRow> right, int leftWidth, bool paneFocused,
        GlyphSet g)
    {
        var rows = new List<PaintedRow>();
        for (var i = 0; i < Math.Max(left.Count, right.Count); i++)
        {
            var l = i < left.Count ? left[i] : PaintedRow.Of("");
            var r = i < right.Count ? right[i] : PaintedRow.Of("");
            var gap = Math.Max(0, leftWidth - UnicodeWidth.Of(l.Text));
            rows.Add(new PaintedRow([
                .. l.Runs,
                new Run(new string(' ', gap)),
                new Run($"{g.Box.Vertical}", paneFocused ? RunInk.Accent : RunInk.Dim),
                new Run(" "),
                .. r.Runs]));
        }
        return rows;
    }

    //where the keys start. the door for a search that found nothing or a shelf with no ladder, the chips row for a chip
    public static Region Opening(ShelfView v) =>
        v.Rows.Count > 0 ? Region.List
            : !v.Searched && v.Families is { Count: > 0 } ? Region.Families
            : Region.Search;

    //the shelf's Tab ring, empty regions left out, and the search region only where the screen takes typing
    public static IReadOnlyList<Region> Regions(ShelfView v, int width, int row, bool hasDoor)
    {
        //the test is no families rather than no rows, an empty search still has a ladder and keeps all five keys
        if (v.NothingFetched) return [Region.Search];

        //the strip is not a Tab stop, and the slot sits between the families and the list as the chips row reads
        List<Region> ring = v.HasPublisherSlot
            ? [Region.Families, Region.Publisher, Region.List]
            : [Region.Families, Region.List];
        if (HasFiles(v, row)) ring.Add(Region.Files);
        if (HasBuilds(v, row)) ring.Add(Region.Builds);
        //the search region joins the ring only where there is a door, since a zone offering nothing is a Tab stop that changes nothing
        if (hasDoor) ring.Add(Region.Search);
        return ring;
    }

    internal static bool HasFiles(ShelfView v, int row) =>
        v.Facts is { } fs && row >= 0 && row < fs.Count && fs[row].Files is { Count: > 0 };

    //more than one build, the same count the pane draws on, since a zone with one build is a Tab stop that changes nothing
    internal static bool HasBuilds(ShelfView v, int row) =>
        v.Facts is { } fs && row >= 0 && row < fs.Count && fs[row].Builds is { Count: > 1 };

    //m is labelled with its destination. atList is what Esc does on the list, since the shelf cannot know where it is
    public static IReadOnlyList<FooterKey> Keys(FocusRing ring, ShelfSource source,
        string atList = "leave", bool searchKey = false, GlyphSet? glyphs = null,
        bool lift = false, bool back = false, bool sourceKey = true) =>
    [
        //every key is read off the ring, so the row cannot advertise one that goes nowhere. the two hints hold their shed rank and go first
        .. ring.HasSecondArea ? (IReadOnlyList<FooterKey>)[new("Tab", "area", Shed: 2)] : [],
        .. ring.Has(Region.List)
            ? (IReadOnlyList<FooterKey>)[new((glyphs ?? GlyphSet.Unicode).ArrowsKey, "move", Shed: 1)]
            : [],
        //the word on Enter follows what Enter does in that zone, so the row says choose, pick or next as the keys move
        .. ring.Has(Region.List)
            //three words for three deeds: choose commits and moves on, pick commits and stays, next moves on with what is already committed
            ? (IReadOnlyList<FooterKey>)[new("Enter",
                ring.Current is Region.Files ? "choose"
                    : ring.Current is Region.Builds or Region.Publisher ? "pick"
                    : "next")]
            : [],
        //the m key is offered only where no typing door takes the letters, otherwise it would be typed rather than answered
        .. sourceKey ? (IReadOnlyList<FooterKey>)[new("m", source == ShelfSource.Local ? "hub" : "local")] : [],
        //d searches, offered on the hub shelf only, and the key arm reads the same predicate OffersD
        .. searchKey ? (IReadOnlyList<FooterKey>)[new("d", "search")] : [],
        //a lifts the fit filter, and it is offered only where OffersLift says so. the count line promises the key, this row answers it
        .. lift ? (IReadOnlyList<FooterKey>)[new("a", "all sizes")] : [],
        //b goes back only where there is a back to go to, so the key is not drawn after the write pause
        .. back ? (IReadOnlyList<FooterKey>)[new("b", "back")] : [],
        //the Esc key is not a region and always has a deed, so it survives every collapse. m leads d, so the two keys that change the view come first
        new("Esc", ring.EscVerb(atList)),
    ];

    //drops the publisher prefix while every row shares one publisher, a second one makes it what tells rows apart
    private static string Name(ShelfView v, ShelfRow r) =>
        v.Rows.All(x => string.Equals(x.Publisher, r.Publisher, StringComparison.OrdinalIgnoreCase))
        && r.RepoId.StartsWith(r.Publisher + "/", StringComparison.OrdinalIgnoreCase)
            ? r.RepoId[(r.Publisher.Length + 1)..]
            : r.RepoId;

    //the ruled column, or what the widest row needs. a cell narrower than its content pushes the columns after it, since Pane.Cells pads and never clips
    private static int SizeQuantWidth(ShelfView v) =>
        v.Rows.Count == 0
            ? SizeQuantColumn
            : Math.Max(SizeQuantColumn, v.Rows.Max(r => UnicodeWidth.Of(SizeQuant(r, SizeWidth(v)))));

    //the widest size shown, so every quant token starts in one column
    private static int SizeWidth(ShelfView v) =>
        v.Rows.Count == 0 ? 0 : v.Rows.Max(r => UnicodeWidth.Of(SearchRow.Gb(r.PickedQuant.Bytes)));

    //an unconventional filename shows the size alone, the token rule lives in QuantToken.Of
    private static string SizeQuant(ShelfRow r, int sizeWidth)
    {
        var gb = SearchRow.Gb(r.PickedQuant.Bytes);
        return QuantToken.Of(r.PickedQuant.FileName) is { Length: > 0 } q ? $"{Pane.Cells(gb, sizeWidth)} {q}" : gb;
    }

    //the structure cell is view data, read from the producer's facts. an empty cell means the producer said nothing about that row
    private static string Structure(ShelfView v, int i) =>
        v.Facts is { } fs && i < fs.Count ? fs[i].Structure ?? "" : "";
}
