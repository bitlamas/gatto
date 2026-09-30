using Gatto.Core.Acquire;
using Gatto.Core.Hardware;
using Gatto.Core.Models;
using Gatto.Terminal;

namespace Gatto.Cli.Setup.Tui;

//one file as the pane lists it: the quant, its size and the fit verdict FitArithmetic reached. the pane renders the verdict rather than working it out
internal readonly record struct PaneFile(string Quant, long Bytes, FitRegime Fit);

//one build of a model: repos sharing a base model fold into one row. the picked build is what gatto would take, marked ›, since a tick reports a fit verdict
internal readonly record struct PaneBuild(string Label, string Words, bool Picked);

//what the frame knows about one model beyond the listing, as data. no field is derived here, and an absent one renders nothing

//the file count and the file list stay, since a lazy producer fills them in v0.5.1
internal sealed record ModelFacts(
    string? Structure = null,
    (long Total, long Active)? Experts = null,
    int? FileCount = null,
    IReadOnlyList<PaneFile>? Files = null,
    bool Older = false,
    IReadOnlyList<PaneBuild>? Builds = null,
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

    //below this the pane compacts: the have-row drops the model id and a build's words lose their tail
    private const int CompactWidth = 36;

    //how many files the window shows at once: three, with the knee at the top so the file gatto would pick opens the list
    private const int Window = 3;

    //the words say gatto has not measured it, since unverified reads as a verdict on the model
    internal const string NotMeasured = "not measured by gatto yet";

    //the pane for one row, padded to width. files and builds are two Tab stops, so nobody has to step through thirty quants
    public static IReadOnlyList<PaintedRow> Rows(
        ShelfRow r, ModelFacts? facts, MachineShape shape, int width,
        int cursor = -1, bool focused = false, int build = -1, Region focus = Region.List,
        GlyphSet? glyphs = null, bool holdFilesBlock = false)
    {
        var g = glyphs ?? GlyphSet.Unicode;
        var f = facts ?? new ModelFacts();
        var rows = new List<PaintedRow>();

        //the name is wrapped rather than clamped. a repo name is something the user may have to type, and its tail is the part that tells two apart
        foreach (var line in Wrap(Name(r), width)) rows.Add(PaintedRow.Of(line, RunInk.Bright));

        //a local row shows the folder path, a hub row shows the publisher. the path is tail-cut at a separator, so half a folder name cannot read as a different folder
        rows.Add(f.LocalPath is { Length: > 0 } dir
            ? PaintedRow.Of(PathTail(dir, width, g), RunInk.Dim)
            : PaintedRow.Of($"by {r.Publisher}", RunInk.Dim));

        //what the user already has: the pane's first fact, since it is about the user rather than about the model
        if (f.Have != HaveMark.None) rows.Add(HaveRow(r, f, width, g));

        //the pane names which generation a dim row belongs to, so the dimness on the table has somewhere to be explained
        if (f.Older) rows.Add(PaintedRow.Of("older generation", RunInk.Dim));

        rows.Add(PaintedRow.Of(""));

        if (r.NativeCtx is > 0 and { } ctx)
            rows.Add(new PaintedRow([
                new Run("context up to ", RunInk.Plain),
                new Run(ctx.ToString("N0", System.Globalization.CultureInfo.InvariantCulture), RunInk.Accent)]));

        rows.Add(r.Vision
            ? new PaintedRow([new Run($"{g.Vision} vision {g.Dot} ", RunInk.Dim), new Run("can see images")])
            : PaintedRow.Of("text only", RunInk.Dim));

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

        //on the local shelf the file block is two lines and nothing scrolls. the model is the file that is here, so the quant is a fact rather than a choice
        if (f.LocalPath is not null)
        {
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

        //the files heading follows its rows. no line stands in its place, since nothing populates the list and a sentence about files not read would invent an attempt
        if (f.Files is { Count: > 0 })
        {
            rows.Add(PaintedRow.Of(FilesLine(f), RunInk.Dim));
            rows.AddRange(FileRows(f, shape, width, cursor, focused,
                Gatto.Core.Acquire.QuantToken.Of(r.PickedQuant.FileName), g));
        }
        //a row with no files holds the block's height, or the frame moves as the cursor passes. the held lines are blank and say nothing, since nothing was read
        else if (holdFilesBlock)
        {
            for (var i = 0; i < Window + 3; i++) rows.Add(PaintedRow.Of(""));
        }
        rows.AddRange(BuildRows(f, width, build, focus == Region.Builds, g));

        return [.. rows.Select(row => Pad(row, width))];
    }

    //the name with the publisher prefix dropped, since the header already says whose shelf this is
    private static string Name(ShelfRow r) =>
        r.RepoId.StartsWith(r.Publisher + "/", StringComparison.OrdinalIgnoreCase)
            ? r.RepoId[(r.Publisher.Length + 1)..]
            : r.RepoId;

    //a MoE with no active size in the label says so rather than picking a number. the accent sits on the number that varies, so a dense count stays plain
    private static IReadOnlyList<Run> Reads(ShelfRow r, ModelFacts f, GlyphSet g)
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
    private static PaintedRow HaveRow(ShelfRow r, ModelFacts f, int width, GlyphSet g)
    {
        var glyph = HaveMarks.Glyph(f.Have, g);
        var text = HaveText(r, f, width, g);
        return new PaintedRow([
            new Run(glyph, HaveMarks.Ink(f.Have)),
            new Run(text[glyph.Length..], RunInk.Dim)]);
    }

    //the loaded sentence shows only when its measured width fits. no threshold constant here, since a 30 would go stale the day the wording changes
    private static string HaveText(ShelfRow r, ModelFacts f, int width, GlyphSet g)
    {
        var bare = HaveMarks.Text(f.Have, g);
        if (f.Have == HaveMark.Loaded)
        {
            var full = bare + LoadedTailOf(g);
            return Gatto.Terminal.UnicodeWidth.Of(full) <= width ? full : bare;
        }

        //the added form uses a width threshold rather than fitting, so the id does not come and go. it reuses the pane's own compaction width rather than a second 36
        var tail = Gatto.Core.Acquire.QuantToken.Of(r.PickedQuant.FileName) is { Length: > 0 } q
            ? $" {g.Dot} " + q
            : "";
        var text = width >= CompactWidth && f.HaveId is { Length: > 0 } id
            ? $"{bare} as {id}{tail}"
            : bare + tail;

        //a sibling of the loaded file gets a note, since the column cannot say why its neighbour has the dot. the note is fitted the way the loaded sentence is
        if (f.Have != HaveMark.OtherFile) return text;
        var noted = $"{text} {g.Dot} not the file in use";
        return Gatto.Terminal.UnicodeWidth.Of(noted) <= width ? noted : text;
    }

    private static string LocalQuantAndSize(ShelfRow r) =>
        Gatto.Core.Acquire.QuantToken.Of(r.PickedQuant.FileName) is { Length: > 0 } q
            ? $"{q}  {SearchRow.Gb(r.PickedQuant.Bytes)}"
            : SearchRow.Gb(r.PickedQuant.Bytes);

    private static string? ActiveSize(string? structure)
    {
        if (structure is null) return null;
        var m = System.Text.RegularExpressions.Regex.Match(structure, @"^MoE A(\d+(?:\.\d+)?[BM])$");
        return m.Success ? m.Groups[1].Value : null;
    }

    //the files header, with the count when there is one. the count does not shrink, so the line has one width
    private static string FilesLine(ModelFacts f) =>
        f.FileCount is > 0 and { } n ? $"files ({n})" : "files";

    //the three-row file window, the knee at its top and the rest counted at the edges. the pick is › and the keyboard row ❯, since ✓ only reports a fit verdict
    private static IReadOnlyList<PaintedRow> FileRows(
        ModelFacts f, MachineShape shape, int width, int cursor, bool focused,
        string? haveQuant, GlyphSet g)
    {
        if (f.Files is not { Count: > 0 } files) return [];

        var ordered = Ordered(files);
        var knee = KneeIndex(ordered);
        var at = FileAt(files, cursor);
        var start = knee;
        if (at < start) start = at;
        if (at > start + Window - 1) start = at - (Window - 1);

        //the quants window is a fixed number of rows, ellipsis slots included, so the shelf does not shift as the cursor moves
        var rows = new List<PaintedRow>();
        if (start > 0) rows.Add(PaintedRow.Of($"  {g.Ellipsis} {start} lighter {g.Up}", RunInk.Dim));

        foreach (var (file, i) in ordered.Skip(start).Take(Window).Select((x, i) => (x, start + i)))
        {
            var mark = i == at ? (focused ? $"{g.Prompt} " : $"{g.Angle} ") : "  ";
            var body = Cells(file.Quant, QuantColumn) + Right(SearchRow.Gb(file.Bytes), SizeColumn);
            //the have-mark outranks the fit mark here, since a file you already hold does not need to be told it would fit
            var onDisk = f.Have != HaveMark.None
                && string.Equals(file.Quant, haveQuant, StringComparison.OrdinalIgnoreCase);
            var fit = FitMarks.Of(file.Fit, shape, g);
            var glyph = onDisk ? HaveMarks.Glyph(f.Have, g) : fit.Glyph;
            var ink = onDisk ? HaveMarks.Ink(f.Have) : fit.Ink;
            var word = onDisk ? HaveMarks.Word(f.Have) : fit.Word;
            rows.Add(new PaintedRow([
                new Run(mark, i == at && focused ? RunInk.Accent : RunInk.Dim),
                new Run(body, i == at && focused ? RunInk.Accent : RunInk.Plain),
                new Run("  "),
                new Run(glyph, ink),
                new Run(word.Length > 0 ? " " + word : "", RunInk.Dim)]));
        }

        var rest = ordered.Count - (start + Math.Min(Window, ordered.Count - start));
        if (rest > 0) rows.Add(PaintedRow.Of($"  {g.Ellipsis} {rest} heavier {g.Down}", RunInk.Dim));

        //the window holds its height, with the padding at the end. a blank at the top would separate the files heading from its first row
        while (rows.Count < Window + 2) rows.Add(PaintedRow.Of(""));
        return rows;
    }

    //the same facts as one line per list under the table, for a screen narrower than MinWidth. the wording is shorter on purpose, since keeping every word would wrap
    public static IReadOnlyList<PaintedRow> Fold(
        ShelfRow r, ModelFacts? facts, MachineShape shape, int width, bool focused, int cursor = -1,
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
                    new Run($"  {g.Dot}  by {r.Publisher}", RunInk.Dim)]),
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
        facts2.Add(new Run((lead ? "" : $" {g.Dot} ") + (r.Vision ? $"{g.Vision} vision" : "text only"), RunInk.Dim));
        facts2.Add(new Run($" {g.Dot} ", RunInk.Dim));
        facts2.AddRange(Reads(r, f, g));
        rows.Add(new PaintedRow(facts2));

        //the arch gets its own row, since joining it to the facts line overflows at the widths the fold is drawn
        if (r.Arch is { Length: > 0 } foldedArch)
            rows.Add(new PaintedRow([new Run("  "), new Run($"arch {g.Dot} ", RunInk.Dim),
                                     new Run(foldedArch)]));

        rows.Add(new PaintedRow(FoldFiles(r, f, shape, focused, cursor, g)));

        if (f.Builds is { Count: > 1 } builds)
        {
            var runs = new List<Run> { new("  "), new($"builds {g.Dot} ", RunInk.Dim) };
            for (var i = 0; i < builds.Count; i++)
            {
                if (i > 0) runs.Add(new Run($" {g.Dot} ", RunInk.Dim));
                if (builds[i].Picked) runs.Add(new Run($"{g.Angle} ", RunInk.Dim));
                runs.Add(new Run(builds[i].Label));
                runs.Add(new Run(" " + builds[i].Words.Replace(" inside", "", StringComparison.Ordinal),
                    RunInk.Dim));
            }
            rows.Add(new PaintedRow(runs));
        }

        rows.Add(new PaintedRow([new Run("  "), new Run(g.Bad + " not measured yet", RunInk.Dim)]));
        return rows;
    }

    //the files list as one line: the pick, its verdict and how many are heavier. the arrow points right, and lighter files are counted nowhere once the pick moves
    private static IReadOnlyList<Run> FoldFiles(
        ShelfRow r, ModelFacts f, MachineShape shape, bool focused, int cursor, GlyphSet g)
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
            runs.Add(new Run($"{ordered[at].Quant} {SearchRow.Gb(ordered[at].Bytes)}  "));
            runs.Add(new Run(mark.Glyph, mark.Ink));
            runs.Add(new Run(mark.Word.Length > 0 ? " " + mark.Word : "", RunInk.Dim));
            var heavier = ordered.Count - 1 - at;
            if (heavier > 0) runs.Add(new Run($" {g.Dot} {g.Ellipsis} {heavier} heavier {g.Right}", RunInk.Dim));
        }
        else
        {
            //with no file list the fold shows the row's picked quant rather than a blank line
            var mark = FitMarks.Of(r.Fit, shape, g);
            var quant = QuantToken.Of(r.PickedQuant.FileName);
            runs.Add(new Run(
                (quant is { Length: > 0 } q ? q + " " : "") + SearchRow.Gb(r.PickedQuant.Bytes) + "  "));
            runs.Add(new Run(mark.Glyph, mark.Ink));
            runs.Add(new Run(mark.Word.Length > 0 ? " " + mark.Word : "", RunInk.Dim));
        }

        return runs;
    }

    //absent when there is only one build, since one row says nothing. a narrow pane shortens the words, since nothing else explains the build
    private static IReadOnlyList<PaintedRow> BuildRows(
        ModelFacts f, int width, int cursor, bool focused, GlyphSet g)
    {
        if (f.Builds is not { Count: > 1 } builds) return [];

        var rows = new List<PaintedRow> { PaintedRow.Of(""), PaintedRow.Of("builds of this model", RunInk.Dim) };
        var at = cursor >= 0 && cursor < builds.Count ? cursor : -1;

        for (var i = 0; i < builds.Count; i++)
        {
            var b = builds[i];
            var here = focused && i == at;
            var words = width < CompactWidth ? b.Words.Replace(" inside", "", StringComparison.Ordinal) : b.Words;
            var mark = here ? $"{g.Prompt} " : b.Picked ? $"{g.Angle} " : "  ";
            rows.Add(new PaintedRow([
                new Run(mark, here ? RunInk.Accent : RunInk.Dim),
                new Run(Cells(b.Label, QuantColumn), here ? RunInk.Accent : RunInk.Plain),
                new Run(words, RunInk.Dim)]));
        }

        return rows;
    }

    //the token the window scrolls to. the UD- prefix is stripped upstream by QuantToken, so it never reaches this comparison
    private const string Knee = "Q4_K_M";

    //files smallest first, the order the window lists and the order its edge counts describe
    internal static IReadOnlyList<PaneFile> Ordered(IReadOnlyList<PaneFile> files) =>
        [.. files.OrderBy(x => x.Bytes)];

    //the quant label under the pane's cursor. the label leaves this frame rather than the index, since the pane's order differs from the row's own file list
    internal static string? QuantAt(IReadOnlyList<PaneFile>? files, int cursor)
    {
        if (files is not { Count: > 0 }) return null;
        var ordered = Ordered(files);
        var at = FileAt(files, cursor);
        return at >= 0 && at < ordered.Count ? ordered[at].Quant : null;
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
