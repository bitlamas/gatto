using Gatto.Terminal;

namespace Gatto.Repl.Render;

//the result row in pieces, words and token count painted, an error line kept plain so the paint can cut it
public sealed record ToolGlossParts(string Words, string Tok, string? Error, bool ErrorWhole);

//the result row as painted, and the link's columns on it. both are 0 when the row carries no link, and Opens says the body holds more than the row
public sealed record ToolRowLayout(string Gloss, int ResultRow, int LinkStart, int LinkEnd, bool Opens = false);

public static class ToolResultRow
{
    public const int ErrorCells = 120;

    //the row with no link, the same bytes the gloss held before the link existed
    public static string Plain(ToolGlossParts p, Theme theme, GlyphSet g) =>
        p.Error is { } e ? theme.Paint(ErrorHead(e, g), Theme.Err) : p.Words + p.Tok;

    //the token count goes first, then an error line is cut, then the link goes whole, since a cut link reads as another word
    public static ToolRowLayout Layout(ToolGlossParts p, bool linkable, bool open, int width, Theme theme, GlyphSet g)
    {
        var none = new ToolRowLayout(Plain(p, theme, g), 1, 0, 0);
        if (!linkable) return none;
        var prefix = GutterWrap.Hang.Length + UnicodeWidth.Of(g.Elbow) + 1;
        var room = width > 0 ? width - prefix : int.MaxValue;
        var head = p.Error is { } e ? ErrorHead(e, g) : null;
        //one error line shown whole leaves nothing for the link to open
        if (head is not null && p.ErrorWhole && UnicodeWidth.Of(p.Error!) <= ErrorCells && Cells(head) <= room) return none;
        var sep = $" {g.Dot} ";
        var link = open ? ShellBlockRender.CollapseWords : ShellBlockRender.ExpandWords;
        var linkCells = Cells(sep) + Cells(link);
        var headCells = head is null ? Cells(p.Words) : Cells(head);
        var tok = headCells + linkCells + Cells(p.Tok) <= room ? p.Tok : "";
        if (headCells + linkCells > room)
        {
            var avail = room - linkCells;
            if (head is null || avail < 1 + Cells(g.Ellipsis))
                return none with { Gloss = head is null ? p.Words : theme.Paint(head, Theme.Err), Opens = true };
            head = TermText.TruncateCells(head, avail, g);
            headCells = Cells(head);
        }
        var start = prefix + headCells + Cells(sep);
        var painted = (head is null ? p.Words : theme.Paint(head, Theme.Err)) + theme.Paint(sep, Theme.Dim) + theme.Paint(link, Theme.Thought) + tok;
        return new ToolRowLayout(painted, 1, start, start + Cells(link), Opens: true);
    }

    private static string ErrorHead(string e, GlyphSet g) => g.Bad + " " + TermText.TruncateCells(e, ErrorCells, glyphs: g);

    private static int Cells(string s) => UnicodeWidth.Of(TermText.StripAnsiForWidth(s));
}
