using System.Linq;
using System.Text.RegularExpressions;
using Gatto.Terminal;

namespace Gatto.Repl.Render;

public enum BlockState { Normal, Fence }

//a rendered markdown line. row 0 takes the marker and later rows the hang, and Styled equal to the raw text means a plain line needs no repaint
public sealed record RenderedLine(
    BlockState Next,
    bool FenceClose,
    bool IsPlain,
    string? Styled,
    IReadOnlyList<StyledSpan>? Spans,
    string FirstPrefix,
    string ContPrefix,
    int FirstPrefixCells,
    int ContPrefixCells,
    string LineStyle,
    IReadOnlyList<string>? Rows = null);

//turns one markdown line into styled spans, with no console and no state beyond the block state
public static partial class MarkdownLine
{
    public static RenderedLine RenderLine(string raw, BlockState state, Theme theme, int width,
        RgbColor? accent = null, GlyphSet? glyphs = null, IReadOnlyList<SpanRole>? codeRoles = null, CodeLanguage codeLanguage = CodeLanguage.None)
    {
        var acc = accent ?? Theme.Accent;   //the output accent, a role tint for assistant prose and the default accent otherwise
        var trimmed = raw.TrimEnd();
        if (state == BlockState.Fence)
        {
            if (trimmed.Trim() == "```")
                return new RenderedLine(BlockState.Normal, true, false, null, null, "", "", 0, 0, "");
            return codeRoles is null ? FenceResult(raw, Theme.CodeBlockFg, theme, width) : FenceRuns(raw, codeRoles, theme, width, codeLanguage);
        }
        if (trimmed.TrimStart().StartsWith("```", StringComparison.Ordinal))
        {
            var lang = trimmed.Trim()[3..].Trim();
            return FenceResult(lang, Theme.Dim, theme, width);
        }

        //three or more of the same dash, star or underscore alone on a line is a horizontal rule
        if (HrRegex().IsMatch(trimmed))
            return NoSpanResult(BlockState.Normal,
                theme.Paint(string.Concat(Enumerable.Repeat((glyphs ?? GlyphSet.Unicode).Rule,
                    Math.Min(40, width <= 0 ? 40 : width))), Theme.Dim));

        var heading = HeadingRegex().Match(trimmed);
        if (heading.Success)
        {
            var lineStyle = Ansi.Fg(theme.Map(acc), theme.TrueColor) + Ansi.Bold;
            return SpanResult(BlockState.Normal, InlineStyler.Parse(heading.Groups[1].Value),
                "", "", 0, 0, lineStyle, theme, accent: accent);
        }

        var bullet = BulletRegex().Match(raw);
        if (bullet.Success)
        {
            var indent = bullet.Groups[1].Value;
            var firstPrefix = indent + theme.Paint($"{(glyphs ?? GlyphSet.Unicode).Bullet}", acc) + " ";
            var cells = UnicodeWidth.Of(indent) + UnicodeWidth.Of($"{(glyphs ?? GlyphSet.Unicode).Bullet} ");
            var contPrefix = new string(' ', cells);
            return SpanResult(BlockState.Normal, InlineStyler.Parse(bullet.Groups[3].Value),
                firstPrefix, contPrefix, cells, cells, "", theme, accent: accent);
        }

        var numbered = NumberedRegex().Match(raw);
        if (numbered.Success)
        {
            var indent = numbered.Groups[1].Value;
            var token = numbered.Groups[2].Value;
            var firstPrefix = indent + theme.Paint(token, acc) + " ";
            var cells = UnicodeWidth.Of(indent) + UnicodeWidth.Of(token) + 1;
            var contPrefix = new string(' ', cells);
            return SpanResult(BlockState.Normal, InlineStyler.Parse(numbered.Groups[3].Value),
                firstPrefix, contPrefix, cells, cells, "", theme, accent: accent);
        }

        var quote = QuoteRegex().Match(raw);
        if (quote.Success)
        {
            var lineStyle = Ansi.Fg(theme.Map(Theme.Dim), theme.TrueColor) + Ansi.Italic;
            var bar = theme.Paint($"{(glyphs ?? GlyphSet.Unicode).Bar} ", Theme.Dim);
            var cells = UnicodeWidth.Of($"{(glyphs ?? GlyphSet.Unicode).Bar} ");
            return SpanResult(BlockState.Normal, InlineStyler.Parse(quote.Groups[1].Value),
                bar, bar, cells, cells, lineStyle, theme, accent: accent);
        }

        var plainSpans = InlineStyler.Parse(raw);
        //a line is plain when no span has styling, a blank line counts, repainting one would clear the rows below the streaming frontier
        var isPlain = plainSpans.All(s => s.Flags == SpanFlags.None);
        return SpanResult(BlockState.Normal, plainSpans, "", "", 0, 0, "", theme, isPlain, accent);
    }

    private static RenderedLine NoSpanResult(BlockState next, string styled) =>
        new(next, false, false, styled, null, "", "", 0, 0, "", new[] { styled });

    //fence text is literal so it wraps with SoftWrap, and every row re-opens the band, any row may be clipped at the window top
    private static RenderedLine FenceResult(string text, RgbColor fg, Theme theme, int width)
    {
        var rows = SoftWrap.Wrap(text, width, width)
            .Select(seg => theme.PaintBgLine(seg.Text, fg, Theme.CodeBlockBg)).ToList();
        return new RenderedLine(BlockState.Fence, false, false,
            theme.PaintBgLine(text, fg, Theme.CodeBlockBg),   //the unwrapped row, as the record requires
            null, "", "", 0, 0, "", rows);
    }

    //a highlighted fence row wraps exactly like a literal one, a wrapped row starts where the rows before it stopped consuming source
    private static RenderedLine FenceRuns(string text, IReadOnlyList<SpanRole> roles, Theme theme, int width, CodeLanguage language)
    {
        var rows = new List<string>();
        var start = 0;
        foreach (var seg in SoftWrap.Wrap(text, width, width))
        {
            var aligned = string.CompareOrdinal(text, start, seg.Text, 0, seg.Text.Length) == 0;
            rows.Add(aligned
                ? theme.PaintBgRuns(SyntaxHighlight.Runs(text, roles, start, seg.Text.Length), Theme.CodeBlockFg, Theme.CodeBlockBg, language)
                : theme.PaintBgLine(seg.Text, Theme.CodeBlockFg, Theme.CodeBlockBg));   //a row that stops matching its source shows its text plain
            start += seg.SourceChars;
        }
        return new RenderedLine(BlockState.Fence, false, false,
            theme.PaintBgRuns(SyntaxHighlight.Runs(text, roles, 0, text.Length), Theme.CodeBlockFg, Theme.CodeBlockBg, language),
            null, "", "", 0, 0, "", rows);
    }

    private static RenderedLine SpanResult(BlockState next, IReadOnlyList<StyledSpan> spans,
        string firstPrefix, string contPrefix, int firstCells, int contCells, string lineStyle, Theme theme,
        bool isPlain = false, RgbColor? accent = null)
    {
        var styled = firstPrefix + lineStyle + theme.Paint(spans, lineStyle, accent) + (lineStyle != "" ? Ansi.Reset : "");
        return new RenderedLine(next, false, isPlain, styled, spans, firstPrefix, contPrefix, firstCells, contCells, lineStyle);
    }

    [GeneratedRegex(@"^\s*([-*_])\1{2,}\s*$")] private static partial Regex HrRegex();
    [GeneratedRegex(@"^#{1,6}\s+(.*)$")] private static partial Regex HeadingRegex();
    [GeneratedRegex(@"^(\s*)([-*+])\s+(.*)$")] private static partial Regex BulletRegex();
    [GeneratedRegex(@"^(\s*)(\d{1,3}[.)])\s+(.*)$")] private static partial Regex NumberedRegex();
    [GeneratedRegex(@"^\s*>\s?(.*)$")] private static partial Regex QuoteRegex();
}
