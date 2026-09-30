using System.Text;
using System.Text.RegularExpressions;
using Gatto.Core;
using Gatto.Core.Tools;
using Gatto.Terminal;

namespace Gatto.Repl.Render;

//the words of a grep result row and the marks of its open rows, the pattern's matches in the accent
public static class GrepRowsRender
{
    //the linear engine, since the pattern is the model's and a backtracking one run at paint time can stall the screen
    public static Regex? Marks(string? pattern)
    {
        if (string.IsNullOrEmpty(pattern)) return null;
        try { return new Regex(pattern, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant); }
        catch (ArgumentException) { return null; }
        catch (NotSupportedException) { return null; }
    }

    //the result row's words, the counts taken from the rows and a cap said in the warn ink
    public static string Words(GrepParse p, Theme theme, GlyphSet g)
    {
        var ok = theme.Paint(g.Ok, Theme.Ok) + " ";
        if (p.Matches.Count == 0) return ok + theme.Paint(Globbing.NoMatches, Theme.Dim);
        var words = ok + theme.Paint($"{Plural.Of(p.Matches.Count, "match", "matches")} in {Plural.Of(p.Files, "file")}", Theme.Dim);
        return p.Notes.Count == 0 ? words : words + theme.Paint($" {g.Dot} ", Theme.Dim) + theme.Paint("capped", Theme.Warn);
    }

    //the painter the window calls with a row's sanitized text and one piece of it, the spans found in the whole text. the row's index is not needed
    public static Func<int, string, int, int, string> Painter(Regex? marks, Theme theme) =>
        (_, text, start, length) => Ink(text.Substring(start, length), start, Spans(marks, text), theme);

    private static List<(int Start, int End)> Spans(Regex? marks, string text)
    {
        var spans = new List<(int, int)>();
        if (marks is null) return spans;
        foreach (Match m in marks.Matches(text))
            if (m.Length > 0) spans.Add((m.Index, m.Index + m.Length));
        return spans;
    }

    //one piece of a line, the marked spans in the accent and the rest in the code ink
    private static string Ink(string seg, int start, List<(int Start, int End)> spans, Theme theme)
    {
        var sb = new StringBuilder();
        var at = 0;
        foreach (var (s, e) in spans)
        {
            var a = Math.Clamp(s - start, 0, seg.Length);
            var b = Math.Clamp(e - start, 0, seg.Length);
            if (b <= a || a < at) continue;
            if (a > at) sb.Append(theme.Paint(seg[at..a], Theme.CodeBlockFg));
            sb.Append(theme.Paint(seg[a..b], Theme.Accent));
            at = b;
        }
        if (at < seg.Length) sb.Append(theme.Paint(seg[at..], Theme.CodeBlockFg));
        return sb.ToString();
    }
}
