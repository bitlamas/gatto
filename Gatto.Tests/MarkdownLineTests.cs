using System.Text.RegularExpressions;
using Gatto.Repl.Render;
using Gatto.Terminal;

namespace Gatto.Tests;

public class MarkdownLineTests
{
    private static readonly Theme T = new(new TermCaps(true, true));
    private static string Accent(string s, bool bold = false) => T.Paint(s, Theme.Accent, bold);

    //the wrapper reads RenderLine's Styled, which equals the raw text for a plain line and is null for a fence close
    private static (string? Styled, BlockState Next) R(string raw, BlockState s = BlockState.Normal)
    {
        var r = MarkdownLine.RenderLine(raw, s, T, width: 80);
        return (r.Styled, r.Next);
    }

    //inline styling goes through the API in two steps, parse to spans and then paint
    private static string Inline(string text, string baseStyle = "") =>
        T.Paint(InlineStyler.Parse(text), baseStyle);

    private static Theme TestTheme() => T;

    private const string Esc = "\x1b";
    private static readonly Regex AnsiStrip =
        new(Esc + @"\[[0-9;]*m" + "|" + Esc + @"\][^" + Esc + @"]*" + Esc + @"\\");

    //strip sgr and osc 8 hyperlink escapes, so assertions can compare content only.
    private static string Strip(string s) => AnsiStrip.Replace(s, "");

    [Fact]
    public void PlainProse_IsUnchanged()
    {
        var (styled, next) = R("just some words.");
        Assert.Equal("just some words.", styled);      //a plain line comes back unchanged, which is how the caller knows there is nothing to repaint
        Assert.Equal(BlockState.Normal, next);
    }

    [Fact]
    public void Heading_StripsMarksAccentBold()
    {
        var (styled, _) = R("## Changes");
        Assert.Equal(T.Paint("Changes", Theme.Accent, bold: true), styled);
    }

    [Fact]
    public void Bullet_AccentDotKeepsIndent()
    {
        var (styled, _) = R("  - item one");
        Assert.Equal("  " + Accent("•") + " item one", styled);
    }

    [Fact]
    public void NumberedList_AccentNumber()
    {
        var (styled, _) = R("2. second");
        Assert.Equal(Accent("2.") + " second", styled);
    }

    [Fact]
    public void Blockquote_DimBarItalic()
    {
        var (styled, _) = R("> wisdom");
        var baseStyle = Ansi.Fg(Theme.Dim, true) + Ansi.Italic;
        Assert.Equal(T.Paint("▏ ", Theme.Dim) + baseStyle + "wisdom" + Ansi.Reset, styled);
    }

    [Fact]
    public void HorizontalRule_DimLine()
    {
        var (styled, _) = R("---");
        Assert.Equal(T.Paint(new string('─', 40), Theme.Dim), styled);
    }

    [Fact]
    public void HorizontalRule_CapsAtWidth()
    {
        var styled = MarkdownLine.RenderLine("***", BlockState.Normal, T, width: 30).Styled;
        Assert.Equal(T.Paint(new string('─', 30), Theme.Dim), styled);
    }

    [Fact]
    public void FenceOpen_RendersCapAndEntersFence()
    {
        var (styled, next) = R("```csharp");
        Assert.Equal(T.PaintBgLine("csharp", Theme.Dim, Theme.CodeBlockBg), styled);
        Assert.Equal(BlockState.Fence, next);
    }

    [Fact]
    public void FenceInterior_NoInlineParsing()
    {
        var (styled, next) = R("var x = \"**not bold**\";", BlockState.Fence);
        Assert.Equal(T.PaintBgLine("var x = \"**not bold**\";", Theme.CodeBlockFg, Theme.CodeBlockBg), styled);
        Assert.Equal(BlockState.Fence, next);
    }

    [Fact]
    public void FenceClose_EmitsNothing()
    {
        var (styled, next) = R("```", BlockState.Fence);
        Assert.Null(styled);
        Assert.Equal(BlockState.Normal, next);
    }

    [Fact]
    public void InlineCode_Chip() =>
        Assert.Equal("run " + T.Chip("dotnet test") + " now", Inline("run `dotnet test` now"));

    [Fact]
    public void Bold_KeepsTextColor() =>
        Assert.Equal("a " + Ansi.Bold + "big" + Ansi.Reset + " deal", Inline("a **big** deal"));

    [Fact]
    public void Italic_Star() =>
        Assert.Equal("so " + Ansi.Italic + "smooth" + Ansi.Reset, Inline("so *smooth*"));

    [Fact]
    public void SnakeCase_NotItalic() =>
        Assert.Equal("use snake_case_names ok", Inline("use snake_case_names ok"));

    [Fact]
    public void UnmatchedMarkers_StayLiteral()
    {
        Assert.Equal("2 * 3 and a ` alone", Inline("2 * 3 and a ` alone"));
        Assert.Equal("**unclosed bold", Inline("**unclosed bold"));
    }

    [Fact]
    public void MarkupInsideBackticks_IsLiteral() =>
        Assert.Equal(T.Chip("**raw**"), Inline("`**raw**`"));

    [Fact]
    public void BaseStyle_ReappliedAfterSpan()
    {
        var baseStyle = Ansi.Fg(Theme.Accent, true) + Ansi.Bold;
        var got = Inline("a `c` b", baseStyle);
        Assert.Equal("a " + T.Chip("c") + baseStyle + " b", got);
    }

    [Fact]
    public void EmptyBackticks_StayLiteral_NoHang()
    {
        Assert.Equal("a `` b", Inline("a `` b"));
        Assert.Equal("``x", Inline("``x"));
    }

    [Fact]
    public void RenderLine_Bullet_CarriesHangMetadata()
    {
        var theme = TestTheme();
        var r = MarkdownLine.RenderLine("- item text", BlockState.Normal, theme, 80);
        Assert.NotNull(r.Spans);
        Assert.Equal(2, r.FirstPrefixCells);
        Assert.Equal(2, r.ContPrefixCells);
        Assert.Equal("  ", Strip(r.ContPrefix));
        Assert.Contains("•", Strip(r.FirstPrefix));
    }

    [Fact]
    public void RenderLine_PlainUnstyled_IsPlain()
    {
        var r = MarkdownLine.RenderLine("nothing fancy here", BlockState.Normal, TestTheme(), 80);
        Assert.True(r.IsPlain);
    }

    [Fact]
    public void BlankLine_IsPlain()
    {
        var r = MarkdownLine.RenderLine("", BlockState.Normal, TestTheme(), 80);
        Assert.True(r.IsPlain);
    }

    [Fact]
    public void BoldAroundChip_NoLiteralAsterisks_AnyLineType()
    {
        var styled = MarkdownLine.RenderLine("- **`write_file`** does things", BlockState.Normal, TestTheme(), 80).Styled;
        Assert.DoesNotContain("**", Strip(styled!));
        Assert.Contains("write_file", Strip(styled!));
    }

    [Fact]
    public void Strikethrough_RendersSgr9()
    {
        var styled = MarkdownLine.RenderLine("~~old~~ new", BlockState.Normal, TestTheme(), 80).Styled;
        Assert.Contains("\e[9m", styled, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderLine_with_no_roles_paints_a_cs_fence_literal()
    {
        var r1 = MarkdownLine.RenderLine("```cs", BlockState.Normal, TestTheme(), 80);
        var r2 = MarkdownLine.RenderLine("var x = 1; // **not markdown**", r1.Next, TestTheme(), 80);
        Assert.Null(r2.Spans);
        Assert.Contains("**not markdown**", Strip(r2.Styled!));
    }

    [Fact]
    public void RenderLine_Numbered_CarriesHangMetadata()
    {
        var r = MarkdownLine.RenderLine("12. twelfth item", BlockState.Normal, TestTheme(), 80);
        Assert.NotNull(r.Spans);
        Assert.Equal(4, r.FirstPrefixCells);    //the prefix is 12. plus one space, so four cells
        Assert.Equal(4, r.ContPrefixCells);
        Assert.Equal("    ", Strip(r.ContPrefix));
        Assert.Contains("12.", Strip(r.FirstPrefix));
    }

    [Fact]
    public void RenderLine_Quote_ReemitsBarBothPrefixes()
    {
        var r = MarkdownLine.RenderLine("> wisdom", BlockState.Normal, TestTheme(), 80);
        Assert.NotNull(r.Spans);
        Assert.Equal(2, r.FirstPrefixCells);
        Assert.Equal(2, r.ContPrefixCells);
        Assert.Equal(Strip(r.FirstPrefix), Strip(r.ContPrefix));
        Assert.Equal("▏ ", Strip(r.FirstPrefix));
        Assert.NotEqual("", r.LineStyle);
    }

    [Fact]
    public void RenderLine_Heading_NoPrefixHasLineStyle()
    {
        var r = MarkdownLine.RenderLine("## Changes", BlockState.Normal, TestTheme(), 80);
        Assert.NotNull(r.Spans);
        Assert.Equal("", r.FirstPrefix);
        Assert.Equal("", r.ContPrefix);
        Assert.Equal(0, r.FirstPrefixCells);
        Assert.Equal(0, r.ContPrefixCells);
        Assert.False(r.IsPlain);
        Assert.NotEqual("", r.LineStyle);
    }

    [Fact]
    public void RenderLine_Hr_SpansNullNeverPlain()
    {
        var r = MarkdownLine.RenderLine("---", BlockState.Normal, TestTheme(), 80);
        Assert.Null(r.Spans);
        Assert.False(r.IsPlain);
        Assert.False(r.FenceClose);
    }

    [Fact]
    public void RenderLine_FenceClose_SetsFlagAndNullStyled()
    {
        var open = MarkdownLine.RenderLine("```", BlockState.Normal, TestTheme(), 80);
        var close = MarkdownLine.RenderLine("```", open.Next, TestTheme(), 80);
        Assert.True(close.FenceClose);
        Assert.Null(close.Styled);
        Assert.Null(close.Spans);
        Assert.Equal(BlockState.Normal, close.Next);
    }

    [Fact]
    public void RenderLine_PlainWithInlineStyling_IsNotPlainButNoPrefix()
    {
        var r = MarkdownLine.RenderLine("a **big** deal", BlockState.Normal, TestTheme(), 80);
        Assert.NotNull(r.Spans);
        Assert.False(r.IsPlain);
        Assert.Equal("", r.FirstPrefix);
        Assert.Equal("", r.ContPrefix);
        Assert.Equal("", r.LineStyle);
    }

    [Fact]
    public void RenderLine_PlainLine_StyledEqualsRaw()
    {
        //a plain line's Styled is byte-identical to the raw input, so a caller can spot nothing-to-repaint by comparing them
        var r = MarkdownLine.RenderLine("nothing to style at all", BlockState.Normal, TestTheme(), 80);
        Assert.True(r.IsPlain);
        Assert.Equal("nothing to style at all", r.Styled);
        Assert.Equal(BlockState.Normal, r.Next);
    }
}
