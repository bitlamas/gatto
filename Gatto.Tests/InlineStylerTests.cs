using Gatto.Repl.Render;
using Gatto.Terminal;

namespace Gatto.Tests;

public class InlineStylerTests
{
    [Fact]
    public void PlainText_SingleUnstyledSpan()
    {
        var s = InlineStyler.Parse("just words");
        Assert.Equal(new[] { new StyledSpan("just words", SpanFlags.None) }, s);
    }

    [Fact]
    public void BoldAroundChip_TheLiveGateBug()
    {
        //bold must survive across a chip span, and no literal ** may remain in the text
        var s = InlineStyler.Parse("**`write_file`** - Cria");
        Assert.Equal(new StyledSpan("write_file", SpanFlags.Bold | SpanFlags.Chip), s[0]);
        Assert.Equal(new StyledSpan(" - Cria", SpanFlags.None), s[1]);
    }

    [Fact]
    public void Strikethrough()
    {
        var s = InlineStyler.Parse("a ~~gone~~ b");
        Assert.Equal(new[]
        {
            new StyledSpan("a ", SpanFlags.None),
            new StyledSpan("gone", SpanFlags.Strike),
            new StyledSpan(" b", SpanFlags.None),
        }, s);
    }

    [Fact]
    public void Link_TextCarriesUrl()
    {
        var s = InlineStyler.Parse("see [docs](https://example.com) ok");
        Assert.Equal(new StyledSpan("docs", SpanFlags.Link, "https://example.com"), s[1]);
        Assert.Equal(new StyledSpan(" ok", SpanFlags.None), s[2]);
    }

    [Fact]
    public void UnmatchedMarkers_StayLiteral()
    {
        Assert.Equal("a ** b", string.Concat(InlineStyler.Parse("a ** b").Select(x => x.Text)));
        Assert.Equal("~~open", string.Concat(InlineStyler.Parse("~~open").Select(x => x.Text)));
        Assert.Equal("[text](no space", string.Concat(InlineStyler.Parse("[text](no space").Select(x => x.Text)));
        Assert.Equal("[t](u rl)", string.Concat(InlineStyler.Parse("[t](u rl)").Select(x => x.Text)));  //whitespace inside the url means no link, the text stays literal
    }

    [Fact]
    public void SnakeCase_NotItalicized()
    {
        var s = InlineStyler.Parse("snake_case_name stays");
        Assert.All(s, sp => Assert.Equal(SpanFlags.None, sp.Flags));
    }

    [Fact]
    public void BoldItalicCompose()
    {
        var s = InlineStyler.Parse("**bold *both* bold**");
        Assert.Contains(new StyledSpan("both", SpanFlags.Bold | SpanFlags.Italic), s);
    }

    [Fact]
    public void EmptyBacktickPair_LiteralAndAlwaysAdvances()
    {
        var s = InlineStyler.Parse("a `` b");
        Assert.Equal("a `` b", string.Concat(s.Select(x => x.Text)));
    }

    [Fact]
    public void MarkersInsideChip_AreChipText()
    {
        var s = InlineStyler.Parse("`a ** b`");
        Assert.Equal(new[] { new StyledSpan("a ** b", SpanFlags.Chip) }, s);
    }

    [Theory]
    //no input may hang the scanner, and every non-marker character must survive (markers may be consumed)
    [InlineData("`")]
    [InlineData("``")]
    [InlineData("```")]
    [InlineData("***")]
    [InlineData("~~~")]
    [InlineData("[](")]
    [InlineData("[a](")]
    [InlineData("_")]
    [InlineData("__")]
    [InlineData("**_~~`[")]
    [InlineData("**a *b ~~c __d [e](f `g")]
    [InlineData("a`b`c`d`e")]
    [InlineData("[x](y)[z](w)")]
    public void AdversarialInput_TerminatesAndKeepsNonMarkerChars(string input)
    {
        var spans = InlineStyler.Parse(input);   //parse must return for any input here, the rows are all malformed
        //a non-marker character survives in Text or in the LinkUrl of a link
        var outText = string.Concat(spans.Select(s => s.Text + s.LinkUrl));
        foreach (var ch in input.Where(c => "*_~`[]()".IndexOf(c) < 0))
            Assert.Contains(ch, outText);
    }

    [Theory]
    //an identifier with a leading underscore has no valid closer, so it stays literal
    [InlineData("set _max_retries to 3")]
    [InlineData("call __init__foo now")]
    [InlineData("file _test_utils.py here")]
    //a lone closer that belongs to a pair marker never opens emphasis, and the text stays literal.
    [InlineData("*a**")]
    [InlineData("_a__")]
    public void InvalidClosers_StayFullyLiteral(string input)
    {
        var spans = InlineStyler.Parse(input);
        var span = Assert.Single(spans);
        Assert.Equal(input, span.Text);
        Assert.Equal(SpanFlags.None, span.Flags);
    }

    [Fact]
    public void PairAfterFailedSingleOpen_StillOpensBold()
    {
        //a lone * with no valid single closer stays literal, and the following ** pair still opens bold
        var spans = InlineStyler.Parse("*a**b**");
        Assert.Equal(2, spans.Count);
        Assert.Equal("*a", spans[0].Text);
        Assert.Equal(SpanFlags.None, spans[0].Flags);
        Assert.Equal("b", spans[1].Text);
        Assert.Equal(SpanFlags.Bold, spans[1].Flags);
    }

    [Fact]
    public void IntrawordUnderscore_StaysLiteralInsideValidEmphasis()
    {
        //the opening _ is valid, the intraword _ is boundary-invalid and stays literal, and the final _ closes
        var spans = InlineStyler.Parse("_max_retries end_");
        var span = Assert.Single(spans);
        Assert.Equal("max_retries end", span.Text);
        Assert.Equal(SpanFlags.Italic, span.Flags);
    }

    [Theory]  //these rows prove valid emphasis still parses
    [InlineData("_hello_", "hello", SpanFlags.Italic)]
    [InlineData("_hello_.", "hello", SpanFlags.Italic)]   //punctuation after a closing marker still counts as a boundary
    [InlineData("__bold__.", "bold", SpanFlags.Bold)]
    [InlineData("*star*", "star", SpanFlags.Italic)]
    public void ValidEmphasis_StillParses(string input, string styledText, SpanFlags flags)
    {
        var spans = InlineStyler.Parse(input);
        var styled = spans.First(s => s.Flags == flags);
        Assert.Equal(styledText, styled.Text);
    }

    [Fact]
    public void ThemePaint_StrikeAndLinkSequences()
    {
        var theme = new Theme(new TermCaps(Rich: true, TrueColor: true));
        var strike = theme.Paint(new[] { new StyledSpan("x", SpanFlags.Strike) });
        Assert.Contains("\e[9m", strike, StringComparison.Ordinal);
        var link = theme.Paint(new[] { new StyledSpan("docs", SpanFlags.Link, "https://e.com") });
        Assert.Contains("\e]8;;https://e.com\e\\", link);
        Assert.Contains("\e]8;;\e\\", link);
        Assert.Contains("\e[4m", link, StringComparison.Ordinal);
    }

    [Fact]
    public void ThemePaint_Bold_KeepsTextForeground()
    {
        //bold sets no foreground, with or without an accent, and must not override the chip or link colors
        var theme = new Theme(new TermCaps(Rich: true, TrueColor: true));

        var bold = theme.Paint(new[] { new StyledSpan("big", SpanFlags.Bold) });
        Assert.Equal(Ansi.Bold + "big" + Ansi.Reset, bold);
        Assert.Equal(bold, theme.Paint(new[] { new StyledSpan("big", SpanFlags.Bold) }, "", Theme.Accent));

        var boldChip = theme.Paint(new[] { new StyledSpan("write_file", SpanFlags.Bold | SpanFlags.Chip) });
        Assert.Equal(
            Ansi.Fg(Theme.CodeInlineFg, true) + Ansi.Bold
                + "write_file" + Ansi.Reset,   //a bold chip gets its foreground color only, with no background band and no padding.
            boldChip);
        Assert.DoesNotContain(Ansi.Fg(Theme.Bright, true), boldChip, StringComparison.Ordinal);

        var boldLink = theme.Paint(new[] { new StyledSpan("docs", SpanFlags.Bold | SpanFlags.Link, "https://e.com") });
        Assert.Equal(
            Ansi.LinkOpen("https://e.com") + Ansi.Fg(Theme.Accent, true) + Ansi.Underline + Ansi.Bold
                + "docs" + Ansi.Reset + Ansi.LinkClose,
            boldLink);
        Assert.DoesNotContain(Ansi.Fg(Theme.Bright, true), boldLink, StringComparison.Ordinal);
    }
}
