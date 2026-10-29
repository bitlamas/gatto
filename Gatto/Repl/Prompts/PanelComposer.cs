using System.Text;
using Gatto.Terminal;

namespace Gatto.Repl.Prompts;

//the live state a frame is composed from, copied at publish so the paint thread never reads the loop's own fields
internal sealed record PanelView(
    int Tab,
    IReadOnlyList<int> Shown,   //the original indices the filter keeps, in list order
    int Cursor,   //a position in Shown, -1 when nothing is shown
    string Draft,
    bool InBox,
    int Offset,   //the window's requested first row, clamped at compose
    string? SettingValue,
    string? Sentence,
    string? HoverKey,
    IReadOnlyList<FooterKey>? Keys = null,   //the key row the row or the detail names, null keeps the panel's own
    string? Detail = null,   //the open detail's row key, null for the list
    string? DetailLabel = null,
    IReadOnlyList<DetailRow>? DetailRows = null,
    int DetailOffset = int.MaxValue);   //the detail window's first row, the maximum meaning the end first

//the rows a frame shows and the layout it was composed at
internal sealed record PanelFrame(List<string> Rows, PanelLayout Layout);

//composes a panel's rows, pure in its arguments so a test can drive it at any width and room
internal static class PanelComposer
{
    private const string Indent = "  ";
    private const int ColumnGap = 2;
    private const int TabGap = 4;
    private const int MinBody = 3;

    //one painted piece of a row, its press target carried beside it
    private readonly record struct Piece(string Text, RgbColor? Ink = null, bool Bold = false, RgbColor? Band = null, PanelHit? Hit = null);

    internal static PanelFrame Compose(PanelFields panel, IReadOnlyList<SelectOption> options, bool numbered,
        PanelView view, int w, int room, Theme theme, GlyphSet glyphs)
    {
        var inside = Margins.Inside(w <= 0 ? 80 : w);
        var tint = panel.Role is { } role ? theme.RoleTint(role) : Theme.Accent;
        var unbounded = room <= 0 || room == int.MaxValue;
        if (view.Detail is not null) return ComposeDetail(panel, options.Count, view, w, room, unbounded, theme, glyphs, tint);

        var hasSubtitle = panel.Subtitle is { Length: > 0 };
        var hasHeader = panel.Header is { Count: > 0 } && view.Shown.Count > 0 && options.Any(o => o.Columns is { Count: > 0 });   //a header over nothing names no cells
        var under = UnderPieces(panel, view, theme, glyphs);
        var hasUnder = under is not null;

        //the rows that are not the body, shed in order when the body would get fewer than three
        var blankTop = true;
        var blankUnder = hasUnder;
        var blankKeys = true;
        int Fixed(bool box) => 1 + (hasSubtitle ? 1 : 0) + (box ? 1 : 0) + (blankTop ? 1 : 0) + (hasHeader ? 1 : 0)
            + (blankUnder ? 1 : 0) + (hasUnder ? 1 : 0) + (blankKeys ? 1 : 0) + 1;
        int Body(bool box) => unbounded ? int.MaxValue : room - Fixed(box);

        var box = panel.SearchWhenLong && (view.InBox || view.Draft.Length > 0 || options.Count > Body(false));
        if (!unbounded)
        {
            //the blanks first, then the row under the list, then the subtitle. the title, the box and the key row never shed
            while (Body(box) < MinBody)
            {
                if (blankTop) blankTop = false;
                else if (blankUnder) blankUnder = false;
                else if (blankKeys) blankKeys = false;
                else if (hasUnder) hasUnder = false;
                else if (hasSubtitle) hasSubtitle = false;
                else break;
            }
        }
        var bodyRows = unbounded ? Math.Max(1, view.Shown.Count) : Math.Max(1, Body(box));

        var rows = new List<List<Piece>>();
        rows.Add(TitlePieces(panel, options.Count, view, theme, glyphs, tint));
        if (hasSubtitle) rows.Add([new(Indent), new(TermText.Sanitize(panel.Subtitle!), Theme.Dim)]);
        if (box) rows.Add(BoxPieces(view, options.Count, inside, theme, glyphs, tint));
        if (blankTop) rows.Add([]);

        var table = Table.Of(options, numbered, inside, panel.LabelWhole);
        if (hasHeader) rows.Add(HeaderPieces(panel.Header!, table));

        var (offset, up, down, count) = Window(view.Shown.Count, view.Cursor, view.Offset, bodyRows);
        var digitless = !numbered;
        if (view.Shown.Count == 0)
            rows.Add([new(Indent), new("nothing matches", Theme.Dim)]);
        else
        {
            if (up) rows.Add([new(Indent), new($"{glyphs.Up} {offset} more", Theme.Dim)]);
            for (var at = offset; at < offset + count; at++)
            {
                if (at >= SelectPrompt.NumberedRows) digitless = true;
                rows.Add(OptionPieces(options[view.Shown[at]], view.Shown[at], at, at == view.Cursor, numbered, table, theme, glyphs, tint));
            }
            if (down) rows.Add([new(Indent), new($"{glyphs.Down} {view.Shown.Count - offset - count} more", Theme.Dim)]);
        }

        if (blankUnder) rows.Add([]);
        if (hasUnder) rows.Add(under!);
        if (blankKeys) rows.Add([]);
        rows.Add(KeyPieces(panel, view, digitless, w <= 0 ? 80 : w, theme, glyphs, tint));

        var (painted, hits) = Render(rows, w, theme, glyphs);
        return new PanelFrame(painted, new PanelLayout(w, room, bodyRows, offset, hits));
    }

    //the detail replaces the body: the title, the open row's label as the subtitle, the rows windowed end first, the detail's keys
    private static PanelFrame ComposeDetail(PanelFields panel, int total, PanelView view, int w, int room, bool unbounded,
        Theme theme, GlyphSet glyphs, RgbColor tint)
    {
        var detail = Physical(view.DetailRows ?? [], w);
        var hasSubtitle = view.DetailLabel is { Length: > 0 };
        var blankTop = true;
        var blankKeys = true;
        int Body() => unbounded ? int.MaxValue : room - 1 - (hasSubtitle ? 1 : 0) - (blankTop ? 1 : 0) - (blankKeys ? 1 : 0) - 1;
        if (!unbounded)
        {
            //the blanks first, then the subtitle, the same order as the list's
            while (Body() < MinBody)
            {
                if (blankTop) blankTop = false;
                else if (blankKeys) blankKeys = false;
                else if (hasSubtitle) hasSubtitle = false;
                else break;
            }
        }
        var bodyRows = unbounded ? Math.Max(1, detail.Count) : Math.Max(1, Body());

        var rows = new List<List<Piece>> { TitlePieces(panel, total, view, theme, glyphs, tint) };
        if (hasSubtitle) rows.Add([new(Indent), new(TermText.Sanitize(view.DetailLabel!), Theme.Dim)]);
        if (blankTop) rows.Add([]);
        var (offset, up, down, count) = Window(detail.Count, -1, view.DetailOffset, bodyRows);
        if (up) rows.Add([new(Indent), new($"{glyphs.Up} {offset} more", Theme.Dim)]);
        var bodyStart = rows.Count;
        for (var at = offset; at < offset + count; at++) rows.Add(detail[at]);
        if (down) rows.Add([new(Indent), new($"{glyphs.Down} {detail.Count - offset - count} more", Theme.Dim)]);
        if (blankKeys) rows.Add([]);
        rows.Add(KeyPieces(panel, view, false, w <= 0 ? 80 : w, theme, glyphs, tint));

        var (painted, hits) = Render(rows, w, theme, glyphs);
        return new PanelFrame(painted, new PanelLayout(w, room, bodyRows, offset, hits, view.Detail, bodyStart, count, detail.Count));
    }

    //the rows the window counts: a wrapping row gives one per wrapped line, any other row is one the width cuts
    private static List<List<Piece>> Physical(IReadOnlyList<DetailRow> detail, int w)
    {
        var rows = new List<List<Piece>>();
        foreach (var d in detail)
        {
            var gutter = d.Gutter is { Length: > 0 } g ? TermText.Sanitize(g) : "";
            var ink = d.Warn ? Theme.Warn : d.Dim ? Theme.Dim : Theme.Bright;
            var text = TermText.Sanitize(d.Text);
            var room = Margins.Inside(w <= 0 ? 80 : w) - Indent.Length;
            if (!d.Wrap || w <= 0 || room <= UnicodeWidth.Of(gutter))
            {
                rows.Add(Line(gutter, text, ink));
                continue;
            }
            var segs = SoftWrap.Wrap(text, room - UnicodeWidth.Of(gutter), room);
            for (var i = 0; i < segs.Count; i++) rows.Add(Line(i == 0 ? gutter : "", segs[i].Text, ink));
        }
        return rows;

        static List<Piece> Line(string gutter, string text, RgbColor ink)
        {
            var row = new List<Piece> { new(Indent) };
            if (gutter.Length > 0) row.Add(new(gutter, Theme.Dim));
            row.Add(new(text, ink));
            return row;
        }
    }

    //the pieces painted and cut at the width, each press target recorded at the cells it was painted on
    private static (List<string> Rows, List<PanelHitSpan> Hits) Render(List<List<Piece>> rows, int w, Theme theme, GlyphSet glyphs)
    {
        var painted = new List<string>(rows.Count);
        var hits = new List<PanelHitSpan>();
        for (var r = 0; r < rows.Count; r++)
        {
            var sb = new StringBuilder();
            var col = 0;
            foreach (var p in rows[r])
            {
                var cells = UnicodeWidth.Of(p.Text);
                if (p.Hit is { } hit && cells > 0) hits.Add(new PanelHitSpan(r, col, col + cells, hit));
                col += cells;
                sb.Append(Paint(p, theme));
            }
            painted.Add(w <= 0 ? sb.ToString() : TermText.TruncateCells(sb.ToString(), w, glyphs));
        }
        return (painted, hits);
    }

    //the window over the shown rows: the first row, whether each mark shows, and how many option rows fit between them. the cursor always stays inside
    internal static (int Offset, bool Up, bool Down, int Count) Window(int total, int cursor, int requested, int rows)
    {
        if (total <= rows) return (0, false, false, total);
        var offset = Math.Clamp(requested, 0, total - 1);
        for (var pass = 0; pass < 8; pass++)
        {
            var (up, down, count, avail) = Marks(total, offset, rows);
            //a window past the end gives its spare rows back to the rows above
            if (!down && count < avail && offset > 0) { offset = Math.Max(0, offset - (avail - count)); continue; }
            if (cursor >= 0 && cursor < offset) { offset = cursor; continue; }
            if (cursor >= 0 && cursor >= offset + count) { offset = cursor - count + 1; continue; }
            return (offset, up, down, count);
        }
        var settled = Marks(total, offset, rows);
        return (offset, settled.Up, settled.Down, settled.Count);
    }

    //below three body rows both marks would leave no room between them, so only the side with more outside keeps its mark
    private static (bool Up, bool Down, int Count, int Avail) Marks(int total, int offset, int rows)
    {
        var up = offset > 0;
        var down = offset + rows - (up ? 1 : 0) < total;
        var avail = rows - (up ? 1 : 0) - (down ? 1 : 0);
        if (avail < 1)
        {
            if (rows <= 1) (up, down) = (false, false);
            else if (offset >= total - offset - 1) down = false;
            else up = false;
            avail = Math.Max(1, rows - (up ? 1 : 0) - (down ? 1 : 0));
        }
        return (up, down, Math.Min(avail, total - offset), avail);
    }

    private static List<Piece> TitlePieces(PanelFields panel, int total, PanelView view, Theme theme, GlyphSet glyphs, RgbColor tint)
    {
        var row = new List<Piece> { new(Indent), new(TermText.Sanitize(panel.Title), Theme.Bright, Bold: true) };
        if (panel.Tabs is { Count: > 0 } tabs)
        {
            //a tabbed title carries no count, so the tabs never move under a keystroke
            for (var i = 0; i < tabs.Count; i++)
            {
                row.Add(new(new string(' ', TabGap)));
                var name = TermText.Sanitize(tabs[i].Name);
                row.Add(i == view.Tab
                    ? new(name, tint, Band: theme.BandOf(tint), Hit: new PanelHit.Tab(i))
                    : new(name, Theme.Dim, Hit: new PanelHit.Tab(i)));
            }
            return row;
        }
        if (panel.Count is { } said) row.Add(new($" {glyphs.Dot} {TermText.Sanitize(said)}", Theme.Dim));
        else if (panel.CountUnit is { } unit)
        {
            var filtered = view.Draft.Length > 0;
            var count = filtered ? $"{view.Shown.Count} of {total} {unit}" : $"{total} {unit}";
            row.Add(new($" {glyphs.Dot} {TermText.Sanitize(count)}", Theme.Dim));
        }
        return row;
    }

    //the caret cell, the draft or the placeholder, and the hint against the right edge. at rest the row is dim
    private static List<Piece> BoxPieces(PanelView view, int total, int inside, Theme theme, GlyphSet glyphs, RgbColor tint)
    {
        var draft = TermText.Sanitize(view.Draft);
        var box = new PanelHit.Box();
        var left = new List<Piece> { new(Indent, Hit: box) };
        if (view.InBox)
        {
            if (draft.Length > 0) left.Add(new(draft, Theme.Bright, Hit: box));
            left.Add(new(glyphs.Bar, tint, Hit: box));
            if (draft.Length == 0) left.Add(new(" search", Theme.Dim, Hit: box));
        }
        else if (draft.Length > 0)
        {
            left.Add(new(draft, Theme.Dim, Hit: box));
            left.Add(new(glyphs.Bar, Theme.Dim, Hit: box));
        }
        else
        {
            left.Add(new(glyphs.Bar, Theme.Dim, Hit: box));
            left.Add(new(" search", Theme.Dim, Hit: box));
        }

        var hint = new List<string>();
        if (draft.Length > 0) hint.Add($"{view.Shown.Count} of {total}");
        hint.Add(view.InBox ? "Esc back" : "? to search");
        var right = string.Join($" {glyphs.Dot} ", hint);
        var used = left.Sum(p => UnicodeWidth.Of(p.Text));
        var gap = Math.Max(1, inside - used - UnicodeWidth.Of(right));
        left.Add(new(new string(' ', gap), Hit: box));
        left.Add(new(right, Theme.Dim, Hit: box));
        return left;
    }

    private static List<Piece> HeaderPieces(IReadOnlyList<string> header, Table table)
    {
        var row = new List<Piece> { new(new string(' ', table.LabelStart + table.LabelWidth + ColumnGap)) };
        var sb = new StringBuilder();
        for (var c = 0; c < header.Count && c < table.Columns.Length; c++)
        {
            var cell = TermText.Sanitize(header[c]);
            sb.Append(c == table.Columns.Length - 1 ? cell : Pad(cell, table.Columns[c] + ColumnGap));
        }
        row.Add(new(sb.ToString().TrimEnd(), Theme.Dim));
        return row;
    }

    private static List<Piece> OptionPieces(SelectOption o, int index, int shownAt, bool isCursor, bool numbered,
        Table table, Theme theme, GlyphSet glyphs, RgbColor tint)
    {
        var hit = new PanelHit.Option(index);
        var row = new List<Piece>
        {
            isCursor ? new(glyphs.Prompt + " ", tint, Hit: hit) : new(Indent, Hit: hit),
        };
        if (numbered) row.Add(new(shownAt < SelectPrompt.NumberedRows ? $"{shownAt + 1}. " : "   ", Hit: hit));

        //the label and the marks that ride on it share one cell, cut with the ellipsis past the cap
        var tail = o.Tail ?? [];
        var tailCells = tail.Sum(t => UnicodeWidth.Of(t.Text));
        var label = TermText.Sanitize(o.Label);
        var labelRoom = Math.Max(1, table.LabelWidth - tailCells);
        if (UnicodeWidth.Of(label) > labelRoom) label = TermText.TruncateCells(label, labelRoom, glyphs);
        row.Add(new(label, isCursor ? tint : Theme.Bright, Hit: hit));
        foreach (var t in tail) row.Add(new(t.Text, t.Ink == Theme.Accent ? tint : t.Ink, Hit: hit));
        var labelCells = UnicodeWidth.Of(label) + tailCells;

        var cols = o.Columns ?? [];
        if (cols.Count > 0 || o.Description is not null || o.Trail is { Count: > 0 })
            row.Add(new(new string(' ', Math.Max(0, table.LabelWidth - labelCells) + ColumnGap), Hit: hit));
        for (var c = 0; c < cols.Count && c < table.Columns.Length; c++)
        {
            var cell = TermText.Sanitize(cols[c]);
            if (UnicodeWidth.Of(cell) > table.Columns[c]) cell = TermText.TruncateCells(cell, table.Columns[c], glyphs);
            var last = c == cols.Count - 1 || c == table.Columns.Length - 1;
            row.Add(new(last ? cell : Pad(cell, table.Columns[c] + ColumnGap), Hit: hit));
        }
        if (cols.Count == 0 && o.Description is { } desc)
            row.Add(new(TermText.Sanitize(desc), Theme.Dim, Hit: hit));
        if (o.Trail is { Count: > 0 } trail)
        {
            if (cols.Count > 0) row.Add(new(new string(' ', ColumnGap), Hit: hit));
            foreach (var t in trail) row.Add(new(t.Text, t.Ink == Theme.Accent ? tint : t.Ink, Hit: hit));
        }
        return row;
    }

    private static List<Piece>? UnderPieces(PanelFields panel, PanelView view, Theme theme, GlyphSet glyphs) => panel.Under switch
    {
        PanelUnder.Sentence when view.Sentence is { Length: > 0 } s => [new(Indent), new(TermText.Sanitize(s), Theme.Dim)],
        PanelUnder.Setting set when view.SettingValue is { } v =>
            [new(Indent), new(TermText.Sanitize(set.Name) + " ", Theme.Dim), new(TermText.Sanitize(v), Theme.Bright),
             new($"   {glyphs.LeftRightKey} adjust", Theme.Dim)],
        _ => null,
    };

    //the shelf's key row: the keys of the region that holds them, the moves named when a row on screen has no digit, Esc last
    private static List<Piece> KeyPieces(PanelFields panel, PanelView view, bool digitless, int w, Theme theme, GlyphSet glyphs, RgbColor tint)
    {
        var keys = new List<FooterKey>(view.Detail is not null ? view.Keys ?? [new("Esc", "back")]
            : view.InBox ? panel.BoxKeys ?? [new("Enter", "list"), new("Esc", "back")]
            : view.Keys ?? panel.Keys);
        if (!view.InBox && view.Detail is null)
        {
            if (panel.Tabs is { Count: > 1 }) keys.Insert(Math.Max(0, keys.Count - 1), new FooterKey(glyphs.LeftRightKey, "tabs", Shed: 1));
            if (digitless) keys.Insert(0, new FooterKey(glyphs.ArrowsKey, "move", Shed: 1));
        }
        var legend = !view.InBox && view.Detail is null && panel.Legend is { } words ? new Legend(glyphs.Loaded + " " + words) : (Legend?)null;

        var row = new List<Piece>();
        foreach (var r in Footer.Runs(w, keys, legend))
        {
            var word = r.Tag as string;
            RgbColor? ink = r.Ink switch { RunInk.Bright => Theme.Bright, RunInk.Dim => Theme.Dim, RunInk.Accent => tint, _ => null };
            RgbColor? band = word is not null && word == view.HoverKey ? theme.BandOf(Theme.Bright) : null;
            var hit = word is null ? null : new PanelHit.Key(word);
            //the legend's dot is a mark, so it takes the tint and its words stay dim
            if (legend is { } leg && r.Ink == RunInk.Dim && r.Text.EndsWith(leg.Text, StringComparison.Ordinal))
            {
                var at = r.Text.Length - leg.Text.Length;
                row.Add(new(r.Text[..at], Theme.Dim));
                row.Add(new(glyphs.Loaded, tint));
                row.Add(new(r.Text[(at + glyphs.Loaded.Length)..], Theme.Dim));
                continue;
            }
            row.Add(new(r.Text, ink, Band: band, Hit: hit));
        }
        return row;
    }

    private static string Paint(Piece p, Theme theme)
    {
        if (p.Text.Length == 0) return "";
        var band = p.Band is { } b ? Ansi.Bg(theme.Map(b), theme.TrueColor) : "";
        if (p.Ink is { } ink) return band + theme.Paint(p.Text, ink, bold: p.Bold);
        return band.Length > 0 ? band + p.Text + Ansi.Reset : p.Text;
    }

    private static string Pad(string s, int cells) => s + new string(' ', Math.Max(0, cells - UnicodeWidth.Of(s)));

    //the label column and the column widths, measured over every option so a filter never moves a column
    private sealed record Table(int LabelStart, int LabelWidth, int[] Columns)
    {
        internal static Table Of(IReadOnlyList<SelectOption> options, bool numbered, int inside, bool whole)
        {
            var labelStart = Indent.Length + (numbered ? 3 : 0);
            var cap = Math.Max(4, inside / 2 - labelStart);
            var widest = options.Count == 0 ? 0 : options.Max(o =>
                UnicodeWidth.Of(TermText.Sanitize(o.Label)) + (o.Tail ?? []).Sum(t => UnicodeWidth.Of(t.Text)));
            var labelWidth = whole ? widest : Math.Min(widest, cap);

            var n = options.Count == 0 ? 0 : options.Max(o => o.Columns?.Count ?? 0);
            var cols = new int[n];
            foreach (var o in options)
                for (var c = 0; c < (o.Columns?.Count ?? 0); c++)
                    cols[c] = Math.Max(cols[c], UnicodeWidth.Of(TermText.Sanitize(o.Columns![c])));

            //the last column is cut first, then the one before it, each to the room the others leave
            var room = inside - labelStart - labelWidth - ColumnGap;
            for (var c = n - 1; c >= 0; c--)
            {
                var used = cols.Sum() + ColumnGap * Math.Max(0, n - 1);
                if (used <= room) break;
                cols[c] = Math.Max(1, cols[c] - (used - room));
            }
            return new Table(labelStart, labelWidth, cols);
        }
    }
}
