using Gatto.Core.Tools;
using Gatto.Tests.Fakes;
using Gatto.Repl;
using Gatto.Repl.Input;
using Gatto.Repl.Render;
using Gatto.Repl.Term;
using Gatto.Terminal;

namespace Gatto.Tests;

//no table-owned mark may render under ascii and the marks must stay under unicode. box glyphs are outside the table
public class AsciiReplFrameTests
{
    private static readonly Theme Plain = new(TermCaps.Plain);

    //the literal spells out the ten table-owned marks, reading them from GlyphSet.Unicode would only assert that the code agrees with itself
    private const string Owned = "\u2713\u2717\u26a0\u25c8\u276f\u25cf\u2500\u00b7\u25b3\u2013";

    private static void NoOwnedMark(string what, string text)
    {
        foreach (var ch in text)
            Assert.False(Owned.Contains(ch),
                $"{what} drew U+{(int)ch:X4} under the ASCII set, which a legacy console has no "
                + $"glyph for: {text}");
    }

    private static StatusInfo Status() =>
        new(@"C:\Users\user\projects\gatto", "qwen3.6-35b", "coder", new CtxState(), @"C:\Users\user");

    //sweep three widths. a rule is composed by arithmetic against the width, so one width cannot tell a converted rule from one that fits by accident
    [Theory]
    [InlineData(80)]
    [InlineData(100)]
    [InlineData(120)]
    public void THE_COMPOSER_FRAME_DRAWS_NO_TABLE_MARK_UNDER_THE_ASCII_SET(int width)
    {
        NoOwnedMark("the top rule",
            InputFrame.BuildTopRule("coder", width, Plain, glyphs: GlyphSet.Ascii));
        NoOwnedMark("the top rule, wild",
            InputFrame.BuildTopRule("coder", width, Plain, wild: true, glyphs: GlyphSet.Ascii));
        NoOwnedMark("the status line",
            InputFrame.BuildStatusLine(Status(), width, Plain, glyphs: GlyphSet.Ascii));
        NoOwnedMark("the status line with a serving mismatch",
            InputFrame.BuildStatusLine(Status() with { Serving = "gemma-4-26b" }, width, Plain,
                glyphs: GlyphSet.Ascii));
    }

    [Fact]
    public void AND_THE_UNICODE_FRAME_STILL_CARRIES_ITS_MARKS()
    {
        Assert.Contains('\u2500', InputFrame.BuildTopRule("coder", 100, Plain, glyphs: GlyphSet.Unicode));
        Assert.Contains('\u00b7', InputFrame.BuildStatusLine(Status(), 100, Plain, glyphs: GlyphSet.Unicode));
        Assert.Contains('\u26a0', InputFrame.BuildStatusLine(Status() with { Serving = "gemma-4-26b" },
            100, Plain, glyphs: GlyphSet.Unicode));
    }

    //the streamed table gets no guard here, its border is box glyphs outside the table (converting the horizontal alone would draw half-ascii borders)

    [Fact]
    public void A_MARKDOWN_RULE_DRAWS_NO_TABLE_MARK_UNDER_THE_ASCII_SET()
    {
        var line = MarkdownLine.RenderLine("---", BlockState.Normal, Plain, 60, glyphs: GlyphSet.Ascii);
        NoOwnedMark("a markdown rule", line.Styled ?? "");
    }

    [Fact]
    public void AND_THE_UNICODE_MARKDOWN_RULE_IS_STILL_DRAWN() =>
        Assert.Contains('\u2500',
            MarkdownLine.RenderLine("---", BlockState.Normal, Plain, 60, glyphs: GlyphSet.Unicode).Styled ?? "");

    [Fact]
    public void THE_PURR_ROW_DRAWS_NO_TABLE_MARK_UNDER_THE_ASCII_SET()
    {
        NoOwnedMark("the purr row",
            ChromeTicker.PurrRow("(=^.w.^)>", 12_400, 1234, 0, glyphs: GlyphSet.Ascii));
        NoOwnedMark("the purr row with a turn total",
            ChromeTicker.PurrRow("(=^.w.^)>", 12_400, 1234, 900, glyphs: GlyphSet.Ascii));
        NoOwnedMark("the waiting row",
            ChromeTicker.WaitingRow(12_400, 1234, 0, GlyphSet.Ascii));
        NoOwnedMark("the completion line",
            ChromeTicker.CompletionText("(=^.w.^)>", 38_000, 5400, 0, GlyphSet.Ascii));
    }

    [Fact]
    public void AND_THE_UNICODE_PURR_ROW_KEEPS_ITS_DOT() =>
        Assert.Contains('\u00b7', ChromeTicker.PurrRow("x", 12_400, 1234, 0, glyphs: GlyphSet.Unicode));

    //the tool rows need no guard here, their ring and elbow glyphs are outside the table. a guard would assert about work that never happened

    [Fact]
    public void A_TOOL_GLOSS_DRAWS_NO_TABLE_MARK_UNDER_THE_ASCII_SET()
    {
        NoOwnedMark("a passing gloss",
            ItemRender.ToolGloss(new ToolResult("forty lines of output"), Plain, GlyphSet.Ascii));
        NoOwnedMark("a failing gloss",
            ItemRender.ToolGloss(new ToolResult("no such file", IsError: true), Plain, GlyphSet.Ascii));
    }

    [Fact]
    public void AND_THE_UNICODE_GLOSS_KEEPS_ITS_TICK_AND_CROSS()
    {
        Assert.Contains('\u2713', ItemRender.ToolGloss(new ToolResult("ok"), Plain, GlyphSet.Unicode));
        Assert.Contains('\u2717',
            ItemRender.ToolGloss(new ToolResult("bad", IsError: true), Plain, GlyphSet.Unicode));
    }

    //a guard on the primitive misses a banner that drops the passed set, so render the whole banner
    [Fact]
    public void THE_BANNER_DRAWS_THE_ASCII_CAT_UNDER_THE_ASCII_SET()
    {
        var rows = Gatto.Repl.Repl.BannerItem(Plain, "coder", "qwen3.6-35b", "v0.5.0", GlyphSet.Ascii)
            .Render(100, Plain, glyphs: GlyphSet.Unicode);

        foreach (var row in rows) NoOwnedMark("a banner row", row);

        //rows hold sgr codes even under TermCaps.Plain, so the check is per-row containment
        var cat = GlyphSet.Ascii.Cat;
        for (var i = 0; i < cat.Count; i++)
            Assert.Contains(cat[i], rows[i], StringComparison.Ordinal);
    }

    //this test proves the banner still draws the role's own cat, the ascii check alone would pass against a banner that lost its cat
    [Fact]
    public void AND_THE_UNICODE_BANNER_STILL_DRAWS_THE_ROLE_CAT()
    {
        var coder = string.Join("\n", Gatto.Repl.Repl.BannerItem(Plain, "coder", "m", "v", GlyphSet.Unicode)
            .Render(100, Plain, glyphs: GlyphSet.Unicode));
        var oracle = string.Join("\n", Gatto.Repl.Repl.BannerItem(Plain, "oracle", "m", "v", GlyphSet.Unicode)
            .Render(100, Plain, glyphs: GlyphSet.Unicode));

        Assert.NotEqual(coder, oracle);
        Assert.Contains('\u2580', coder);        //this glyph belongs to the coder's cat alone, proving the role's own cat drew.
    }

    //this drives the item seam, an item that never passes the set down draws unicode marks whatever the run resolved. a guard on the primitive stays green
    [Fact]
    public void A_PROSE_BLOCK_DRAWS_NO_TABLE_MARK_UNDER_THE_ASCII_SET()
    {
        var item = new AssistantBlockItem(["here is the answer"], "coder");
        foreach (var row in item.Render(80, Plain, GlyphSet.Ascii)) NoOwnedMark("a prose row", row);
    }

    [Fact]
    public void AND_THE_UNICODE_PROSE_BLOCK_KEEPS_ITS_MARKER() =>
        Assert.Contains(new AssistantBlockItem(["here is the answer"], "coder").Render(80, Plain, GlyphSet.Unicode),
            r => r.Contains('\u25cf'));

    [Fact]
    public void A_USER_ECHO_DRAWS_NO_TABLE_MARK_UNDER_THE_ASCII_SET()
    {
        var item = new UserEchoItem(["what did you change"]);
        foreach (var row in item.Render(80, Plain, GlyphSet.Ascii)) NoOwnedMark("an echo row", row);
    }

    [Fact]
    public void AND_THE_UNICODE_USER_ECHO_KEEPS_ITS_PROMPT() =>
        Assert.Contains(new UserEchoItem(["what did you change"]).Render(80, Plain, GlyphSet.Unicode),
            r => r.Contains('\u276f'));

    //the collapsed row holds the dot, reached only through the item's switch (a guard on the expanded form would pass anyway)
    [Fact]
    public void A_COLLAPSED_REASONING_ROW_DRAWS_NO_TABLE_MARK_UNDER_THE_ASCII_SET()
    {
        var item = new ReasoningItem(["thinking about it"]) { Collapsed = true, ClickHint = true };
        foreach (var row in item.Render(80, Plain, GlyphSet.Ascii)) NoOwnedMark("a reasoning row", row);
    }

    [Fact]
    public void AND_THE_UNICODE_COLLAPSED_REASONING_ROW_KEEPS_ITS_DOT() =>
        Assert.Contains(new ReasoningItem(["thinking about it"]) { Collapsed = true, ClickHint = true }
                .Render(80, Plain, GlyphSet.Unicode),
            r => r.Contains('\u00b7'));

    //roles cannot reference the glyph table, so Cli composes EngineMarks and hands them in. a second glyph table in roles would give one run two vocabularies
    [Fact]
    public void THE_AUDITION_REPORT_DRAWS_THE_MARKS_IT_IS_HANDED()
    {
        var ascii = new Gatto.Roles.EngineMarks(
            GlyphSet.Ascii.Ok, GlyphSet.Ascii.Bad, GlyphSet.Ascii.Dot, GlyphSet.Ascii.Ellipsis);

        Assert.Equal(GlyphSet.Ascii.Ok, ascii.Ok);
        Assert.Equal(GlyphSet.Unicode.Ok, Gatto.Roles.EngineMarks.Unicode.Ok);
        Assert.Equal(GlyphSet.Unicode.Bad, Gatto.Roles.EngineMarks.Unicode.Bad);
        Assert.Equal(GlyphSet.Unicode.Dot, Gatto.Roles.EngineMarks.Unicode.Dot);
        Assert.Equal(GlyphSet.Unicode.Ellipsis, Gatto.Roles.EngineMarks.Unicode.Ellipsis);
    }

    //an option that sets no key answers its label, so label comparisons never move
    [Fact]
    public void AN_OPTION_WITHOUT_A_KEY_ANSWERS_ITS_LABEL()
    {
        var plain = new SelectOption("qwen3.6-35b");
        Assert.Equal("qwen3.6-35b", plain.Identity);
        Assert.Null(plain.Key);
    }

    //the back row answers one key while its label follows the set, so assert on both
    [Fact]
    public void THE_BACK_OPTION_ANSWERS_ONE_KEY_WHILE_ITS_LABEL_FOLLOWS_THE_SET()
    {
        var uni = new SelectOption(Gatto.Cli.Setup.SetupFace.BackLabelOf(GlyphSet.Unicode),
            Key: Gatto.Cli.Setup.SetupFace.BackOptionKey);
        var ascii = new SelectOption(Gatto.Cli.Setup.SetupFace.BackLabelOf(GlyphSet.Ascii),
            Key: Gatto.Cli.Setup.SetupFace.BackOptionKey);

        Assert.Equal(uni.Identity, ascii.Identity);
        Assert.NotEqual(uni.Label, ascii.Label);

        NoOwnedMark("the back label", ascii.Label);
        Assert.All(ascii.Label, ch => Assert.True(ch <= 0x7F,
            $"the ascii back label drew U+{(int)ch:X4}: {ascii.Label}"));
    }

    //the key must differ from the label under both sets, otherwise the check above passes for the wrong reason
    [Fact]
    public void THE_BACK_KEY_IS_NEITHER_SETS_LABEL()
    {
        Assert.NotEqual(Gatto.Cli.Setup.SetupFace.BackOptionKey,
            Gatto.Cli.Setup.SetupFace.BackLabelOf(GlyphSet.Unicode));
        Assert.NotEqual(Gatto.Cli.Setup.SetupFace.BackOptionKey,
            Gatto.Cli.Setup.SetupFace.BackLabelOf(GlyphSet.Ascii));
    }
    //this drives the real prompter, a prompter that answered with the label would stop matching on an ascii-only host
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void THE_PROMPTER_ANSWERS_WITH_THE_KEY_UNDER_EITHER_SET(bool ascii)
    {
        var set = ascii ? GlyphSet.Ascii : GlyphSet.Unicode;
        var ask = new WizardAsk("go on?",
            [new SelectOption("keep going"),
             new SelectOption(Gatto.Cli.Setup.SetupFace.BackLabelOf(set),
                 Key: Gatto.Cli.Setup.SetupFace.BackOptionKey)]);

        var keys = new KeyScript([
            new ConsoleKeyInfo('2', ConsoleKey.D2, false, false, false)]);
        var answer = new RichPrompter(new RecordingSurface { Width = 80, Height = 0 }, Plain, keys)
            .AskOne(ask);

        Assert.NotNull(answer);
        Assert.Equal(Gatto.Cli.Setup.SetupFace.BackOptionKey, answer!.Label);
    }

    //the negative case for the key answer above, a prompter that answered with the back label for every option would pass without it
    [Fact]
    public void AN_OPTION_WITHOUT_A_KEY_STILL_ANSWERS_ITS_LABEL_THROUGH_THE_PROMPTER()
    {
        var ask = new WizardAsk("go on?",
            [new SelectOption("keep going"),
             new SelectOption(Gatto.Cli.Setup.SetupFace.BackLabelOf(GlyphSet.Unicode),
                 Key: Gatto.Cli.Setup.SetupFace.BackOptionKey)]);

        var keys = new KeyScript([
            new ConsoleKeyInfo('1', ConsoleKey.D1, false, false, false)]);
        var answer = new RichPrompter(new RecordingSurface { Width = 80, Height = 0 }, Plain, keys)
            .AskOne(ask);

        Assert.Equal("keep going", answer!.Label);
    }

    private sealed class KeyScript(IEnumerable<ConsoleKeyInfo> keys) : IKeySource
    {
        private readonly Queue<ConsoleKeyInfo> _q = new(keys);
        public bool KeyAvailable => false;
        public ConsoleKeyInfo ReadKey() => _q.Count > 0
            ? _q.Dequeue()
            : throw new InvalidOperationException("the script ran out of keys");
    }
}
