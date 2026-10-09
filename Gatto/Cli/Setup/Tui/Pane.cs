using Gatto.Core.Acquire;
using Gatto.Core.Hardware;
using Gatto.Core.Models;
using Gatto.Terminal;

namespace Gatto.Cli.Setup.Tui;

//one file as the pane lists it: the quant, its size, the fit verdict FitArithmetic reached and the file it names. a file with no token has a null quant and is still listed
internal readonly record struct PaneFile(string? Quant, long Bytes, FitRegime Fit, FileRef? File = null,
    string? Tag = null)   //what tells this file's repo from another holding the same label, null when nothing collides
{
    //the quant, or the file's own name when its name declares none, and the repo's tag when it has one
    public string Label => (Quant ?? (File is { } f ? f.Path[(f.Path.LastIndexOf('/') + 1)..] : ""))
        + (Tag is { Length: > 0 } t ? " " + t : "");

    //a label two repos share takes the parts of each repo name the others lack. an i1 file reads Q4_K_M i1 and the static one stays Q4_K_M
    public static IReadOnlyList<PaneFile> Tagged(IReadOnlyList<PaneFile> files) =>
        [.. files.Select(file =>
        {
            var repos = files.Where(o => o.Label == file.Label && o.File is { } of && file.File is { } ff
                    && !string.Equals(of.RepoId, ff.RepoId, StringComparison.OrdinalIgnoreCase))
                .Select(o => o.File!.RepoId).ToList();
            if (repos.Count == 0 || file.File is not { } mine) return file;
            var others = repos.SelectMany(Parts).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var own = Parts(mine.RepoId).Where(p => !others.Contains(p)).ToList();
            return own.Count == 0 ? file : file with { Tag = string.Join("-", own) };
        })];

    private static IEnumerable<string> Parts(string repoId) => repoId[(repoId.IndexOf('/') + 1)..].Split('-');
}

//one publisher as the pane lists it: every file it holds, priced, and the file it would take, or null when none fits
internal sealed record PanePublisher(string Org, IReadOnlyList<PaneFile> Files, FileRef? Pick,
    bool Marked = true);   //false when no rule chose the pick, so its file carries no pick mark

//one line of the publisher list: a publisher's own line when File is -1, otherwise one of the open publisher's files in size order
internal readonly record struct PaneLine(int Publisher, int File);

//what the frame knows about one model beyond the listing, as data. no field is derived here, and an absent one renders nothing

//the file count and the file list stay, since a lazy producer fills them in v0.5.1
internal sealed record ModelFacts(
    string? Structure = null,
    (long Total, long Active)? Experts = null,
    int? FileCount = null,
    IReadOnlyList<PaneFile>? Files = null,
    IReadOnlyList<PanePublisher>? Publishers = null,
    string? LocalPath = null,
    string? FilesHere = null,
    HaveMark Have = HaveMark.None,
    string? HaveId = null);

//the pane draws one model's facts, keyed by the cursor. every line states a fact, and only the footer's fewer params = faster speaks for the column
internal static class Pane
{
    //the pane's threshold: below this the facts move under the table. taken from the mock, where w_pane >= 28 is what shelf() tests
    public const int MinWidth = 28;

    //the one context window gatto prices a row at. moving it is a separate change, since the fit arithmetic has three live callers
    public const int KvContext = 4096;

    //below this the pane compacts: the have-row drops the model id
    private const int CompactWidth = 36;

    //the fewest cells a cut id keeps, ellipsis included, below which the line drops the id rather than show a stub
    private const int MinIdCells = 8;

    //how many files the window shows at once: three, with the knee at the top so the file gatto would pick opens the list
    private const int Window = 3;

    //the words say gatto has not measured it, since unverified reads as a verdict on the model
    internal const string NotMeasured = "not measured by gatto yet";

    //the pane for one row, padded to width. a hub row lists its publishers closed, and one opens at a time so nobody steps through thirty quants
    public static IReadOnlyList<PaintedRow> Rows(
        ModelRow r, ModelFacts? facts, MachineShape shape, int width,
        int cursor = -1, bool focused = false, int open = -1,
        GlyphSet? glyphs = null, bool holdFilesBlock = false, int maxRows = 0)
    {
        var g = glyphs ?? GlyphSet.Unicode;
        var f = facts ?? new ModelFacts();
        //a pane taller than the rows the frame left beside the table would push the frame past the terminal
        if (f.LocalPath is not null)
        {
            var local = LocalRows(r, f, shape, width, g, cursor, open, focused, maxRows);
            return maxRows > 0 ? [.. local.Take(maxRows)] : local;
        }

        var rows = new List<PaintedRow>();
        foreach (var line in Wrap(Name(r), width)) rows.Add(PaintedRow.Of(line, RunInk.Bright));
        if (f.Have != HaveMark.None) rows.Add(HaveRow(r, f, width, g));
        rows.AddRange(FactsLines(r, f, width, g));
        rows.Add(PaintedRow.Of(""));

        if (f.Publishers is { Count: > 0 } pubs)
        {
            rows.Add(PaintedRow.Of($"publishers ({pubs.Count})", RunInk.Dim));
            var haveQuant = f.Have != HaveMark.None ? QuantToken.Of(SearchRow.RowFileName(r)) : null;
            var block = PublisherRows(pubs, r.RowPublisher, shape, width, cursor, open, focused, f, haveQuant, g,
                out var focusRow, out var openRow);
            //the name, the facts and the heading stay, and the publisher lines scroll to keep the cursor's line in view, with its publisher's line when both fit
            var keep = maxRows > 0 ? Math.Max(1, maxRows - rows.Count) : block.Count;
            var start = Math.Clamp(focusRow - keep / 2, 0, Math.Max(0, block.Count - keep));
            if (openRow >= 0 && openRow < focusRow && focusRow - openRow < keep) start = Math.Min(start, openRow);
            rows.AddRange(block.Skip(start).Take(keep));
        }
        //a row with no publishers priced holds the block's height, or the frame moves as the cursor passes. the held lines are blank, since nothing was read
        else if (holdFilesBlock)
        {
            for (var i = 0; i < Window + 3; i++) rows.Add(PaintedRow.Of(""));
        }

        return [.. rows.Take(maxRows > 0 ? maxRows : rows.Count).Select(row => Pad(row, width))];
    }

    //the local pane, where the model is the file that is here, so the file block is two lines and nothing opens
    private static IReadOnlyList<PaintedRow> LocalRows(ModelRow r, ModelFacts f, MachineShape shape, int width, GlyphSet g,
        int cursor = -1, int open = -1, bool focused = false, int maxRows = 0)
    {
        var rows = new List<PaintedRow>();

        //the name is wrapped rather than clamped. a repo name is something the user may have to type, and its tail is the part that tells two apart
        foreach (var line in Wrap(Name(r), width)) rows.Add(PaintedRow.Of(line, RunInk.Bright));

        //a local row shows the folder path, a hub row shows the publisher. the path is tail-cut at a separator, so half a folder name cannot read as a different folder
        rows.Add(f.LocalPath is { Length: > 0 } dir
            ? PaintedRow.Of(PathTail(dir, width, g), RunInk.Dim)
            : PaintedRow.Of($"by {r.RowOffer?.Org}", RunInk.Dim));

        //what the user already has: the pane's first fact, since it is about the user rather than about the model
        if (f.Have != HaveMark.None) rows.Add(HaveRow(r, f, width, g));

        rows.Add(PaintedRow.Of(""));

        if (r.NativeCtx is > 0 and { } ctx)
            rows.Add(new PaintedRow([
                new Run("context up to ", RunInk.Plain),
                new Run(ctx.ToString("N0", System.Globalization.CultureInfo.InvariantCulture), RunInk.Accent)]));

        rows.Add(new PaintedRow(Reads(r, f, g)));

        //the pane says which architecture this row is, on every row that has one. the family chips group by a fact the table does not show
        if (r.Arch is { Length: > 0 } arch)
            rows.Add(new PaintedRow([new Run("arch      ", RunInk.Dim), new Run(arch)]));

        //a row with no measurement says so. the badge words come from SearchRow's own helper rather than a second wording
        rows.Add(r.Badge is { } b
            //the bare badge words here, since this row paints its own mark and the full sentence would put a second glyph on the row
            ? new PaintedRow([new Run(g.Ok, RunInk.Ok),
                              new Run(" " + SearchRow.BadgeWordsBare(b), RunInk.Dim)])
            : new PaintedRow([new Run(g.Bad + " " + NotMeasured, RunInk.Dim)]));

        rows.Add(PaintedRow.Of(""));

        //each folder is a line as a publisher is on the Hub pane, and the lines scroll to keep the cursor's line in view
        if (f.Publishers is { Count: > 0 } pubs)
        {
            var block = PublisherRows(pubs, r.RowPublisher, shape, width, cursor, open, focused, f, null, g,
                out var focusRow, out var openRow, folders: true);
            //in a short frame the facts give way first, the folder lines are what the keys act on. the name and the folder path stay
            var head = (f.Have != HaveMark.None ? 1 : 0) + Wrap(Name(r), width).Count + 1;
            if (maxRows > 0 && rows.Count + block.Count > maxRows)
            {
                var forBlock = Math.Min(block.Count, Math.Max(1, maxRows - head - 1));
                rows.RemoveRange(Math.Min(rows.Count, Math.Max(head, maxRows - forBlock)), Math.Max(0, rows.Count - Math.Max(head, maxRows - forBlock)));
            }
            var keep = maxRows > 0 ? Math.Max(1, maxRows - rows.Count) : block.Count;
            var start = Math.Clamp(focusRow - keep / 2, 0, Math.Max(0, block.Count - keep));
            if (openRow >= 0 && openRow < focusRow && focusRow - openRow < keep) start = Math.Min(start, openRow);
            rows.AddRange(block.Skip(start).Take(keep));
            return [.. rows.Select(row => Pad(row, width))];
        }

        //the quant is a fact here rather than a choice
        rows.Add(new PaintedRow([
            new Run($"file {g.Dot} {g.Angle} ", RunInk.Dim),
            new Run(LocalQuantAndSize(r)),
        ]));
        var mark = FitMarks.Of(r.Fit, shape, g);
        rows.Add(new PaintedRow([
            new Run((f.FilesHere ?? "") + "  ", RunInk.Dim),
            new Run(mark.Glyph, mark.Ink),
            new Run(mark.Word.Length > 0 ? " " + mark.Word : "", RunInk.Dim),
        ]));
        return [.. rows.Select(row => Pad(row, width))];
    }

    //vision, what one token reads and the context, each part only when the row knows it. a narrow pane breaks the line between parts
    private static IReadOnlyList<PaintedRow> FactsLines(ModelRow r, ModelFacts f, int width, GlyphSet g)
    {
        var parts = new List<IReadOnlyList<Run>>();
        if (r.Vision) parts.Add([new Run($"{g.Vision} vision", RunInk.Dim)]);
        if (ReadsPerToken(r, f) is { } reads)
            parts.Add([new Run("reads ", RunInk.Dim), new Run(reads, RunInk.Accent), new Run("/token", RunInk.Dim)]);
        if (r.NativeCtx is > 0 and { } ctx)
            parts.Add([new Run("context ", RunInk.Dim),
                       new Run(ctx.ToString("N0", System.Globalization.CultureInfo.InvariantCulture), RunInk.Accent)]);

        var lines = new List<PaintedRow>();
        var runs = new List<Run>();
        foreach (var part in parts)
        {
            var joined = runs.Count == 0 ? [.. part] : (List<Run>)[.. runs, new Run($" {g.Dot} ", RunInk.Dim), .. part];
            if (runs.Count > 0 && UnicodeWidth.Of(string.Concat(joined.Select(x => x.Text))) > width)
            {
                lines.Add(new PaintedRow(runs));
                joined = [.. part];
            }
            runs = joined;
        }
        if (runs.Count > 0) lines.Add(new PaintedRow(runs));
        return lines;
    }

    //what one token reads: the active count of a MoE, the whole count of a dense model, the expert count when the header gave no size, null when nothing says
    private static string? ReadsPerToken(ModelRow r, ModelFacts f)
    {
        var dense = string.Equals(f.Structure, "dense", StringComparison.Ordinal);
        if (dense && r.Params is > 0 and { } whole) return Count(whole);
        if (ActiveSize(f.Structure) is { } a) return a;
        if (r.Active is > 0 and { } active) return Count(active);
        if (f.Experts is { } e) return $"{e.Active} of {e.Total} experts";
        return f.Structure is null && r.Params is > 0 and { } p ? Count(p) : null;
    }

    //a count as the facts line writes it, 9B and 3.5B, with no trailing zero
    private static string Count(long n)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var billions = n / 1e9;
        if (billions >= 1000) return (billions / 1000).ToString("0.#", inv) + "T";
        return billions >= 1 ? billions.ToString("0.#", inv) + "B" : (n / 1e6).ToString("0", inv) + "M";
    }

    //every publisher on one line, closed, and the open one's files in a three-row window under its line
    private static IReadOnlyList<PaintedRow> PublisherRows(
        IReadOnlyList<PanePublisher> pubs, int rowPublisher, MachineShape shape, int width,
        int cursor, int open, bool focused, ModelFacts f, string? haveQuant, GlyphSet g, out int focusRow,
        out int openRow, bool folders = false)
    {
        var lines = Lines(pubs, open);
        var at = LineAt(lines, cursor, rowPublisher);
        //the org takes what the longest tail leaves, and the fit words go before the org is cut under four cells
        var words = width - 6 - pubs.Max(p => UnicodeWidth.Of(Tail(p, shape, g, words: true))) >= 4;
        var room = width - 6 - pubs.Max(p => UnicodeWidth.Of(Tail(p, shape, g, words)));
        var orgWidth = Math.Max(1, Math.Min(pubs.Max(p => UnicodeWidth.Of(p.Org)), room));
        var rows = new List<PaintedRow>();
        focusRow = 0;
        openRow = -1;

        for (var p = 0; p < pubs.Count; p++)
        {
            if (lines[at] == new PaneLine(p, -1)) focusRow = rows.Count;
            var here = focused && lines[at] == new PaneLine(p, -1);
            var mark = here ? $"{g.Prompt} " : p == rowPublisher ? $"{g.Angle} " : "  ";
            var head = new List<Run>
            {
                new(mark, here ? RunInk.Accent : RunInk.Dim),
                new((p == open ? g.Caret : g.Triangle) + " ", RunInk.Dim),
                //a folder keeps its end, which names it, a publisher its start
                new(Cells(folders ? PathTail(pubs[p].Org, orgWidth, g) : Clip(pubs[p].Org, orgWidth, g), orgWidth) + "  ",
                    here ? RunInk.Accent : RunInk.Plain),
            };
            var ordered = Ordered(pubs[p].Files);
            var pick = PickIndex(ordered, pubs[p].Pick);
            if (pick >= 0)
            {
                var fit = FitMarks.Of(ordered[pick].Fit, shape, g);
                head.Add(new Run(SearchRow.Gb(ordered[pick].Bytes) + " " + ordered[pick].Label + "  "));
                head.Add(new Run(fit.Glyph, fit.Ink));
                head.Add(new Run(words && fit.Word.Length > 0 ? " " + fit.Word : "", RunInk.Dim));
            }
            else
            {
                head.Add(new Run("no file fits", RunInk.Dim));
            }
            //a press on the line acts as Enter on it, so it carries its publisher
            var lineTag = new HitTag(HitKind.PaneLine, Index: p, Area: Region.Files);
            rows.Add(new PaintedRow([.. head.Select(x => x with { Tag = lineTag, Band = here })]));

            if (p != open) continue;
            openRow = rows.Count - 1;
            var window = OpenRows(p, pubs[p].Marked, ordered, pick, lines[at].Publisher == p ? lines[at].File : -1,
                focused, shape, p == rowPublisher ? f.Have : HaveMark.None, haveQuant, width, g, out var cursorRow);
            if (cursorRow >= 0) focusRow = rows.Count + cursorRow;
            rows.AddRange(window);
        }
        return rows;
    }

    //the open publisher's window: three files from its pick, moved to keep the cursor in view, the rest counted at the edges
    private static IReadOnlyList<PaintedRow> OpenRows(
        int publisher, bool marked, IReadOnlyList<PaneFile> ordered, int pick, int cursorFile, bool focused, MachineShape shape,
        HaveMark have, string? haveQuant, int width, GlyphSet g, out int cursorRow)
    {
        var start = Math.Clamp(Math.Max(0, pick), 0, Math.Max(0, ordered.Count - Window));
        if (cursorFile >= 0 && cursorFile < start) start = cursorFile;
        if (cursorFile > start + Window - 1) start = cursorFile - (Window - 1);

        var rows = new List<PaintedRow>();
        cursorRow = -1;
        if (start > 0) rows.Add(PaintedRow.Of($"    {g.Ellipsis} {start} lighter {g.Up}", RunInk.Dim));
        foreach (var i in Enumerable.Range(start, Math.Min(Window, ordered.Count - start)))
        {
            if (i == cursorFile) cursorRow = rows.Count;
            var here = focused && i == cursorFile;
            var mark = here ? $"{g.Prompt} " : i == pick && marked ? $"{g.Angle} " : "  ";
            //the have-mark outranks the fit mark here, since a file you already hold does not need to be told it would fit
            var onDisk = have != HaveMark.None
                && string.Equals(ordered[i].Quant, haveQuant, StringComparison.OrdinalIgnoreCase);
            var fit = FitMarks.Of(ordered[i].Fit, shape, g);
            var word = onDisk ? HaveMarks.Word(have) : fit.Word;
            //the line is four cells, the file cells and the glyph before the word, which goes when the pane is narrower
            if (4 + QuantColumn + SizeColumn + 2 + 1 + 1 + UnicodeWidth.Of(word) > width) word = "";
            //the file's index is its place in the ordered list, the index a pane line names
            var fileTag = new HitTag(HitKind.PaneFile, Index: publisher, File: i, Area: Region.Files);
            var line = new PaintedRow([
                new Run(mark, here ? RunInk.Accent : RunInk.Dim, fileTag),
                new Run(Cells(ordered[i].Label, QuantColumn) + Right(SearchRow.Gb(ordered[i].Bytes), SizeColumn),
                    here ? RunInk.Accent : RunInk.Plain, fileTag),
                new Run("  ", Tag: fileTag),
                new Run(onDisk ? HaveMarks.Glyph(have, g) : fit.Glyph, onDisk ? HaveMarks.Ink(have) : fit.Ink, fileTag),
                new Run(word.Length > 0 ? " " + word : "", RunInk.Dim, fileTag)]);
            //the indent stays off the band, which starts at the mark as the publisher line's does
            rows.Add(new PaintedRow([new Run("  "), .. (here ? line.Banded() : line).Runs]));
        }
        var rest = ordered.Count - start - Math.Min(Window, ordered.Count - start);
        if (rest > 0) rows.Add(PaintedRow.Of($"    {g.Ellipsis} {rest} heavier {g.Down}", RunInk.Dim));
        return rows;
    }

    //what a publisher's line shows after the org: its pick's size, quant and fit, or the words for no pick
    private static string Tail(PanePublisher p, MachineShape shape, GlyphSet g, bool words)
    {
        var ordered = Ordered(p.Files);
        var pick = PickIndex(ordered, p.Pick);
        if (pick < 0) return "no file fits";
        var fit = FitMarks.Of(ordered[pick].Fit, shape, g);
        return SearchRow.Gb(ordered[pick].Bytes) + " " + ordered[pick].Label + "  " + fit.Glyph
            + (words && fit.Word.Length > 0 ? " " + fit.Word : "");
    }

    //the lines the publisher list holds, each publisher's own line and the open one's files under it
    internal static IReadOnlyList<PaneLine> Lines(IReadOnlyList<PanePublisher> pubs, int open)
    {
        var lines = new List<PaneLine>();
        for (var p = 0; p < pubs.Count; p++)
        {
            lines.Add(new PaneLine(p, -1));
            if (p == open) lines.AddRange(Enumerable.Range(0, pubs[p].Files.Count).Select(i => new PaneLine(p, i)));
        }
        return lines;
    }

    //the one place -1 becomes a line. the face sends it for a row the keys have not entered, and it rests on the row's own publisher
    internal static int LineAt(IReadOnlyList<PaneLine> lines, int cursor, int rowPublisher) =>
        cursor >= 0 && cursor < lines.Count ? cursor
            : Math.Max(0, lines.ToList().FindIndex(l => l.Publisher == rowPublisher && l.File < 0));

    //where opening a publisher puts the cursor: on its pick, its lightest file when it has none, its own line when it holds no file
    internal static int OpenedAt(IReadOnlyList<PanePublisher> pubs, int open)
    {
        var ordered = Ordered(pubs[open].Files);
        var file = ordered.Count == 0 ? -1 : Math.Max(0, PickIndex(ordered, pubs[open].Pick));
        return Lines(pubs, open).ToList().IndexOf(new PaneLine(open, file));
    }

    //the file on a line, or null on a publisher's own line
    internal static FileRef? FileOn(IReadOnlyList<PanePublisher> pubs, PaneLine line) =>
        line.File >= 0 && line.Publisher < pubs.Count && Ordered(pubs[line.Publisher].Files) is var ordered
            && line.File < ordered.Count ? ordered[line.File].File : null;

    private static int PickIndex(IReadOnlyList<PaneFile> ordered, FileRef? pick) =>
        pick is null ? -1 : ordered.ToList().FindIndex(x => x.File == pick);

    private static string Clip(string text, int n, GlyphSet g) =>
        UnicodeWidth.Of(text) <= n || n < 2 ? text : text[..(n - 1)] + g.Ellipsis;

    //the model's own name, which no publisher prefixes
    private static string Name(ModelRow r) => r.Model;

    //a MoE with no active size in the label says so rather than picking a number. the accent sits on the number that varies, so a dense count stays plain
    private static IReadOnlyList<Run> Reads(ModelRow r, ModelFacts f, GlyphSet g)
    {
        var p = ShelfTable.ParamsCell(r);

        if (string.Equals(f.Structure, "dense", StringComparison.Ordinal) && p.Length > 0)
            return [new Run("reads all ", RunInk.Dim), new Run(p), new Run(" params/token", RunInk.Dim)];

        if (ActiveSize(f.Structure) is { Length: > 0 } a)
            return [new Run("reads ", RunInk.Dim), new Run(a, RunInk.Accent),
                    new Run(" params/token", RunInk.Dim)];

        if (f.Experts is { } e)
            return [new Run("reads ", RunInk.Dim), new Run($"{e.Active} of {e.Total}", RunInk.Accent),
                    new Run(" experts/token", RunInk.Dim)];

        if (f.Structure is { Length: > 0 } s && s.StartsWith("MoE", StringComparison.Ordinal))
            return [new Run("MoE, active size not in the label", RunInk.Dim)];

        return p.Length > 0
            ? [new Run("reads all ", RunInk.Dim), new Run(p), new Run(" params/token", RunInk.Dim)]
            : [new Run("")];
    }

    //the A-number out of MoE A4B, or null. the pattern is anchored and shape-checked, so an odd structure string cannot put an unvalidated span on the screen

    //an unconventional filename shows its size alone rather than a made-up token, so this follows QuantToken.Of's rule instead of copying it

    //the sentence for the loaded model, whose own width is the threshold

    //a method rather than a const, since the glyph comes from a set chosen at launch
    private static string LoadedTailOf(GlyphSet g) => $" {g.Dot} the model you're on";

    //the have-fact with the glyph inked and the rest dim
    private static PaintedRow HaveRow(ModelRow r, ModelFacts f, int width, GlyphSet g)
    {
        var glyph = HaveMarks.Glyph(f.Have, g);
        var text = HaveText(r, f, width, g);
        return new PaintedRow([
            new Run(glyph, HaveMarks.Ink(f.Have)),
            new Run(text[glyph.Length..], RunInk.Dim)]);
    }

    //the loaded sentence shows only when its measured width fits. no threshold constant here, since a 30 would go stale the day the wording changes
    private static string HaveText(ModelRow r, ModelFacts f, int width, GlyphSet g)
    {
        var bare = HaveMarks.Text(f.Have, g);
        if (f.Have == HaveMark.Loaded)
        {
            var full = bare + LoadedTailOf(g);
            return Gatto.Terminal.UnicodeWidth.Of(full) <= width ? full : bare;
        }

        //the added form uses a width threshold rather than fitting, so the id does not come and go. it reuses the pane's own compaction width rather than a second 36
        var tail = Gatto.Core.Acquire.QuantToken.Of(SearchRow.RowFileName(r)) is { Length: > 0 } q
            ? $" {g.Dot} " + q
            : "";
        //an id longer than the pane allows is cut from its end, so the quant after it stays whole and the line never meets the edge
        var room = width - Gatto.Terminal.UnicodeWidth.Of($"{bare} as {tail}");
        var text = width >= CompactWidth && f.HaveId is { Length: > 0 } id && room >= MinIdCells
            ? $"{bare} as {Gatto.Terminal.TermText.TruncateCells(id, room, g)}{tail}"
            : bare + tail;

        //a sibling of the loaded file gets a note, since the column cannot say why its neighbour has the dot. the note is fitted the way the loaded sentence is
        if (f.Have != HaveMark.OtherFile) return text;
        var noted = $"{text} {g.Dot} not the file in use";
        return Gatto.Terminal.UnicodeWidth.Of(noted) <= width ? noted : text;
    }

    private static string LocalQuantAndSize(ModelRow r) =>
        Gatto.Core.Acquire.QuantToken.Of(SearchRow.RowFileName(r)) is { Length: > 0 } q
            ? $"{q}  {SearchRow.Gb(SearchRow.RowBytes(r))}"
            : SearchRow.Gb(SearchRow.RowBytes(r));

    private static string? ActiveSize(string? structure)
    {
        if (structure is null) return null;
        var m = System.Text.RegularExpressions.Regex.Match(structure, @"^MoE A(\d+(?:\.\d+)?[BM])$");
        return m.Success ? m.Groups[1].Value : null;
    }

    //the same facts as one line per list under the table, for a screen narrower than MinWidth. the wording is shorter on purpose, since keeping every word would wrap
    public static IReadOnlyList<PaintedRow> Fold(
        ModelRow r, ModelFacts? facts, MachineShape shape, int width, bool focused, int cursor = -1,
        GlyphSet? glyphs = null)
    {
        var g = glyphs ?? GlyphSet.Unicode;
        var f = facts ?? new ModelFacts();
        var rows = new List<PaintedRow>
        {
            PaintedRow.Of(""),
            PaintedRow.Of(string.Concat(Enumerable.Repeat(g.Rule, Math.Max(1, width))), RunInk.Dim),
            f.LocalPath is not null
                ? new PaintedRow([new Run("  "), new Run(Name(r), RunInk.Bright)])
                : new PaintedRow([
                    new Run("  "),
                    new Run(Name(r), RunInk.Bright),
                    new Run($"  {g.Dot}  by {r.RowOffer?.Org}", RunInk.Dim)]),
        };

        //the path gets its own row, since it tells two identically-named files apart and may not be squeezed onto another line
        if (f.LocalPath is { Length: > 0 } dir)
            rows.Add(PaintedRow.Of("  " + PathTail(dir, Math.Max(1, width - 2), g), RunInk.Dim));

        //the context opens the line when there is one and the vision clause opens it otherwise, so no separator starts the line
        var facts2 = new List<Run> { new("  ") };
        var lead = true;
        if (r.NativeCtx is > 0 and { } ctx)
        {
            facts2.Add(new Run("context up to "));
            facts2.Add(new Run(SearchRow.Ctx(ctx), RunInk.Accent));
            lead = false;
        }
        //a local row says nothing about vision, its files carry no reading of it
        if (f.LocalPath is null)
        {
            facts2.Add(new Run((lead ? "" : $" {g.Dot} ") + (r.Vision ? $"{g.Vision} vision" : "text only"), RunInk.Dim));
            lead = false;
        }
        if (!lead) facts2.Add(new Run($" {g.Dot} ", RunInk.Dim));
        facts2.AddRange(Reads(r, f, g));
        rows.Add(new PaintedRow(facts2));

        //the arch gets its own row, since joining it to the facts line overflows at the widths the fold is drawn
        if (r.Arch is { Length: > 0 } foldedArch)
            rows.Add(new PaintedRow([new Run("  "), new Run($"arch {g.Dot} ", RunInk.Dim),
                                     new Run(foldedArch)]));

        var shown = FoldFiles(r, f, shape, focused, cursor, g);
        //the shown file answers a press as Enter on it, the label in front of it does not
        var foldTag = new HitTag(HitKind.FoldedFile, File: f.Files is { Count: > 0 } ff ? FileAt(ff, cursor) : -1, Area: Region.Files);
        rows.Add(new PaintedRow([.. shown.Take(2), .. shown.Skip(2).Select(x => x with { Tag = foldTag })]));

        //a hub row carries no badge, so only the local fold says what gatto measured, the wide pane's rule in shorter words
        if (f.LocalPath is not null)
            rows.Add(r.Badge is { } b
                ? new PaintedRow([new Run("  "), new Run(g.Ok, RunInk.Ok), new Run(" " + SearchRow.BadgeWordsBare(b), RunInk.Dim)])
                : new PaintedRow([new Run("  "), new Run(g.Bad + " not measured yet", RunInk.Dim)]));
        return rows;
    }

    //the files list as one line: the pick, its verdict and how many are heavier. the arrow points right, and lighter files are counted nowhere once the pick moves
    private static IReadOnlyList<Run> FoldFiles(
        ModelRow r, ModelFacts f, MachineShape shape, bool focused, int cursor, GlyphSet g)
    {
        var runs = new List<Run> { new("  "), new($"files {g.Dot} ", RunInk.Dim) };
        runs.Add(new Run(focused ? $"{g.Prompt} " : $"{g.Angle} ", focused ? RunInk.Accent : RunInk.Dim));

        //locally the tail says how much is here, since the model is the file that is here. one space between quant and size here, where the pane's two-line form uses two
        if (f.LocalPath is not null)
        {
            var localMark = FitMarks.Of(r.Fit, shape, g);
            runs.Add(new Run($"{LocalQuantAndSize(r).Replace("  ", " ", StringComparison.Ordinal)}"));
            runs.Add(new Run($" {g.Dot} " + (f.FilesHere ?? "") + "  ", RunInk.Dim));
            runs.Add(new Run(localMark.Glyph, localMark.Ink));
            runs.Add(new Run(localMark.Word.Length > 0 ? " " + localMark.Word : "", RunInk.Dim));
            return runs;
        }

        if (f.Files is { Count: > 0 } files)
        {
            var ordered = Ordered(files);
            //the same resolution the vertical window uses, so -1 means the knee in both layouts
            var at = FileAt(files, cursor);
            var mark = FitMarks.Of(ordered[at].Fit, shape, g);
            runs.Add(new Run($"{ordered[at].Label} {SearchRow.Gb(ordered[at].Bytes)}  "));
            runs.Add(new Run(mark.Glyph, mark.Ink));
            runs.Add(new Run(mark.Word.Length > 0 ? " " + mark.Word : "", RunInk.Dim));
            var heavier = ordered.Count - 1 - at;
            if (heavier > 0) runs.Add(new Run($" {g.Dot} {g.Ellipsis} {heavier} heavier {g.Right}", RunInk.Dim));
        }
        else
        {
            //with no file list the fold shows the row's picked quant rather than a blank line
            var mark = FitMarks.Of(r.Fit, shape, g);
            var quant = QuantToken.Of(SearchRow.RowFileName(r));
            runs.Add(new Run(
                (quant is { Length: > 0 } q ? q + " " : "") + SearchRow.Gb(SearchRow.RowBytes(r)) + "  "));
            runs.Add(new Run(mark.Glyph, mark.Ink));
            runs.Add(new Run(mark.Word.Length > 0 ? " " + mark.Word : "", RunInk.Dim));
        }

        return runs;
    }

    //the token the window scrolls to. the UD- prefix is stripped upstream by QuantToken, so it never reaches this comparison
    private const string Knee = "Q4_K_M";

    //files smallest first, the order the window lists and the order its edge counts describe
    internal static IReadOnlyList<PaneFile> Ordered(IReadOnlyList<PaneFile> files) =>
        [.. files.OrderBy(x => x.Bytes)];

    //the file under the pane's cursor. the reference leaves this frame rather than the index, since the pane's order differs from the row's own file list
    internal static FileRef? QuantAt(IReadOnlyList<PaneFile>? files, int cursor)
    {
        if (files is not { Count: > 0 }) return null;
        var ordered = Ordered(files);
        var at = FileAt(files, cursor);
        return at >= 0 && at < ordered.Count ? ordered[at].File : null;
    }

    //where the knee sits in that order, or 0 when the repo has no Q4_K_M. it never returns -1, since the window has to open somewhere
    internal static int KneeIndex(IReadOnlyList<PaneFile> ordered)
    {
        for (var i = 0; i < ordered.Count; i++)
            if (string.Equals(ordered[i].Quant, Knee, StringComparison.Ordinal)) return i;
        return 0;
    }

    //the one place -1 becomes an index. the face sends "wherever the knee is" as a sentinel, so a cursor survives a row change
    internal static int FileAt(IReadOnlyList<PaneFile> files, int cursor)
    {
        if (files.Count == 0) return 0;
        var ordered = Ordered(files);
        return cursor >= 0 && cursor < ordered.Count ? cursor : KneeIndex(ordered);
    }

    private const int QuantColumn = 9;
    private const int SizeColumn = 8;

    internal static string Cells(string text, int n) =>
        text + new string(' ', Math.Max(0, n - UnicodeWidth.Of(text)));

    internal static string Right(string text, int n) =>
        new string(' ', Math.Max(0, n - UnicodeWidth.Of(text))) + text;

    //every row padded to the pane's own column, so the divider beside it is straight
    private static PaintedRow Pad(PaintedRow row, int width)
    {
        var gap = width - UnicodeWidth.Of(row.Text);
        return gap <= 0 ? row : new PaintedRow([.. row.Runs, new Run(new string(' ', gap))]);
    }

    //the front of the path goes, since the end names the folder. the cut falls on a separator, since half a folder name reads as a different folder
    internal static string PathTail(string path, int width, GlyphSet g)
    {
        if (width <= 1 || UnicodeWidth.Of(path) <= width) return path;

        var segs = path.Split('\\');
        for (var i = 1; i < segs.Length; i++)
        {
            var tail = $"{g.Ellipsis}\\" + string.Join('\\', segs[i..]);
            if (UnicodeWidth.Of(tail) <= width) return tail;
        }
        return $"{g.Ellipsis}" + path[^Math.Min(path.Length, Math.Max(0, width - 1))..];
    }

    private static IReadOnlyList<string> Wrap(string text, int width)
    {
        if (width <= 0) return [text];
        var segs = SoftWrap.Wrap(text, width, width);
        return segs.Count == 0 ? [text] : [.. segs.Select(s => s.Text)];
    }
}
