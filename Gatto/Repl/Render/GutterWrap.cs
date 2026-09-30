using Gatto.Terminal;

namespace Gatto.Repl.Render;

//a rendered row with its wrap metadata. a continuation drops PrefixCells on copy, any other row drops LeadCells
public readonly record struct RenderedRow(string Text, bool Continuation, int PrefixCells = 0, int LeadCells = 0);

//the wrap metadata alone, which copy uses to rejoin and the mouse to pick a logical line
public readonly record struct RowWrap(bool Continuation, int PrefixCells, int LeadCells = 0);

//pre-wrap a committed block into physical rows, marker first and a two-space hang on each wrap row. scrollback then never depends on the terminal's wrapping
public static class GutterWrap
{
    public const string Hang = "  ";

    //markerRendered may hold ANSI and markerVisible is its plain form for width math. the caller sanitizes text, and paint styles each segment
    public static IReadOnlyList<string> Rows(
        string markerRendered, string markerVisible, string text, int width,
        Func<string, string>? paint = null)
        => RowsTagged(markerRendered, markerVisible, text, width, paint).Select(r => r.Text).ToList();

    //as Rows, but each row also holds its Continuation flag. the first row is false and the wrap rows after it are true
    public static IReadOnlyList<RenderedRow> RowsTagged(
        string markerRendered, string markerVisible, string text, int width,
        Func<string, string>? paint = null, string? hang = null)
    {
        paint ??= s => s;
        if (width <= 4)
            return new[] { new RenderedRow(markerRendered + paint(text), false) };
        var budget = width - 2;
        var segs = SoftWrap.Wrap(text, budget, budget);
        var rows = new List<RenderedRow>(segs.Count);
        for (var i = 0; i < segs.Count; i++)
            //a wrap row gets the hang, or the rail if the block has one (2 cells either way). a rail stays out of a copy on every row
            rows.Add(new RenderedRow((i == 0 ? markerRendered : hang ?? Hang) + paint(segs[i].Text), i > 0, i > 0 ? Hang.Length : 0,
                i == 0 && hang is not null ? Hang.Length : 0));
        return rows;
    }
}
