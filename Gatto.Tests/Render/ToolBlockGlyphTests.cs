using System.Text.Json;
using Gatto.Core.Client;
using Gatto.Core.Session;
using Gatto.Core.Tools;
using Gatto.Repl.Input;
using Gatto.Repl.Render;
using Gatto.Repl.Render.Chrome;
using Gatto.Terminal;
using Gatto.Terminal.Tests.Fakes;
using Gatto.Tests.Fakes;
using Xunit;

namespace Gatto.Tests.Render;

//the link's words on the first 2 blocks that can open, then a triangle alone at the row's end, and one order for what a row gives up
public sealed class ToolBlockGlyphTests
{
    private static readonly Theme T = new(new TermCaps(true, true));
    private static readonly Theme Plain = new(new TermCaps(true, false));
    private static readonly GlyphSet U = GlyphSet.Unicode;
    private static readonly GlyphSet A = GlyphSet.Ascii;
    private static readonly GlyphSet[] Sets = { U, A };
    private const string Expand = ShellBlockRender.ExpandWords;
    private const string Collapse = ShellBlockRender.CollapseWords;
    private const string Matches = "a.cs:1: one\nb.cs:2: two\nc.cs:3: three";

    //an error line whole on a row at 160 columns and cut at 100 and 60
    private const string MissingError = "file not found: C:\\work\\projects\\gatto\\core\\Gatto.Tests\\Render\\Pointer\\Selection\\FocusControllerWindowTests.cs";

    private static string Visible(string row) => TermText.StripAnsiForWidth(row);

    private static ToolBlockItem Block(string name, ToolResult r, bool collapsed = true, bool words = true, GlyphSet? g = null, Theme? theme = null) =>
        new(name, "args", ItemRender.ToolGloss(r, theme ?? T, g ?? U), true, "coder")
            { Collapsed = collapsed, FullResult = r.Text, RawGloss = r.Gloss, Parts = ItemRender.ToolGlossParts(r, theme ?? T, g ?? U), IsError = r.IsError, LinkWords = words };

    private static ToolBlockItem Grep(bool collapsed = true, bool words = true, GlyphSet? g = null) =>
        Block("list_dir", new ToolResult(Matches, Gloss: "3 matches"), collapsed, words, g);

    private static ToolBlockItem ShellBlock(string command, string result, int exit, bool words = true, bool collapsed = true)
    {
        var r = new ToolResult(result, IsError: exit != 0, Gloss: $"exit {exit}");
        return new ToolBlockItem("shell", ItemRender.CompactArgs(JsonSerializer.Serialize(new { command })), ItemRender.ToolGloss(r, T, U), true, "coder")
            { Collapsed = collapsed, FullResult = result, RawGloss = r.Gloss, Parts = ItemRender.ToolGlossParts(r, T, U), FullArgs = command, IsError = r.IsError, LinkWords = words };
    }

    private static string WithStderr(string stdout, params string[] stderr) =>
        stdout + "\n" + ShellTool.StderrMarker + "\n" + string.Join("\n", stderr);

    private static string Row(ToolBlockItem b, int width, GlyphSet? g = null, Theme? theme = null)
    {
        var layout = b.Layout(width, theme ?? T, g ?? U);
        return Visible(layout.Rows[layout.ResultRow].Text).TrimEnd();
    }

    //the two shapes and the order
    [Fact]
    public void A_CLOSED_BLOCK_WITHOUT_WORDS_ENDS_ON_THE_TRIANGLE()
    {
        foreach (var g in Sets)
            for (var width = 30; width <= 160; width++)
            {
                var row = Row(Grep(words: false, g: g), width, g);
                Assert.EndsWith(" " + g.Triangle, row, StringComparison.Ordinal);
                Assert.DoesNotContain(Expand, row, StringComparison.Ordinal);
                Assert.True(UnicodeWidth.Of(row) <= width, $"{width}: {row}");
                if (width >= 60) Assert.EndsWith($"tok {g.Triangle}", row, StringComparison.Ordinal);
            }
    }

    [Fact]
    public void AN_OPEN_BLOCK_ALWAYS_SAYS_CLICK_TO_COLLAPSE()
    {
        foreach (var g in Sets)
            for (var width = 30; width <= 160; width++)
            {
                var row = Row(Grep(collapsed: false, words: false, g: g), width, g);
                Assert.Contains(Collapse, row, StringComparison.Ordinal);
                Assert.False(row.EndsWith(" " + g.Triangle, StringComparison.Ordinal), $"{width}: {row}");
            }
    }

    [Fact]
    public void THE_TOKEN_COUNT_LEAVES_BEFORE_ANY_WORD_IS_CUT()
    {
        foreach (var words in new[] { true, false })
            for (var width = 20; width <= 160; width++)
            {
                var row = Row(Grep(words: words), width);
                if (row.Contains("tok", StringComparison.Ordinal))
                    Assert.Contains("3 matches", row, StringComparison.Ordinal);
            }
    }

    [Fact]
    public void A_LONG_SUCCESS_ROW_KEEPS_ITS_LINK_AND_CUTS_ITS_WORDS()
    {
        var gloss = "Theme: dark · Name: gatto-lite, since the binary already ca… · Targets: Windows x64, Window… · Notes: skipped";
        var b = Block("ask_user", new ToolResult("[{\"header\":\"Theme\",\"selected\":[\"dark\"]}]", Gloss: gloss));
        var row = Row(b, 100);
        Assert.EndsWith(Expand, row, StringComparison.Ordinal);
        Assert.Contains(U.Ellipsis + $" {U.Dot} {Expand}", row, StringComparison.Ordinal);
        Assert.DoesNotContain("tok", row, StringComparison.Ordinal);
        Assert.EndsWith(" " + U.Triangle, Row(b with { LinkWords = false }, 100), StringComparison.Ordinal);
    }

    [Fact]
    public void THE_LINK_IS_WHOLE_OR_ABSENT_AT_EVERY_WIDTH()
    {
        foreach (var words in new[] { true, false })
            for (var width = 12; width <= 160; width++)
            {
                var b = Grep(words: words);
                var row = Row(b, width);
                var l = b.LinkLayout(width, T, U);
                if (row.Contains("click", StringComparison.Ordinal)) Assert.EndsWith(Expand, row[..l.LinkEnd], StringComparison.Ordinal);
                if (l.LinkEnd > l.LinkStart) Assert.Equal(words ? Expand : U.Triangle, row[l.LinkStart..l.LinkEnd]);
            }
    }

    [Fact]
    public void THE_ERROR_ROW_OF_THE_FRAMES()
    {
        var b = Block("read_file", new ToolResult(MissingError, IsError: true), words: false);
        Assert.False(b.Opens(160, T, U));
        Assert.DoesNotContain(U.Triangle, Row(b, 160), StringComparison.Ordinal);
        foreach (var width in new[] { 100, 60 })
        {
            Assert.True(b.Opens(width, T, U));
            var row = Row(b, width);
            Assert.EndsWith(U.Ellipsis + " " + U.Triangle, row, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_SHELL_BLOCK_TAKES_THE_SAME_SHAPES_AND_ORDER()
    {
        var blocks = new[]
        {
            (Name: "stderr", Make: (Func<bool, bool, ToolBlockItem>)((w, c) => ShellBlock("dotnet build", WithStderr("one\ntwo", "warn a"), 0, w, c))),
            (Name: "exit 1", Make: (w, c) => ShellBlock("dotnet test", WithStderr("out a\nout b", "Build FAILED with a long reason that runs on"), 1, w, c)),
            (Name: "multi-line", Make: (w, c) => ShellBlock("cd src\ngit status", "M a.cs\nM b.cs", 0, w, c)),
        };
        foreach (var (name, make) in blocks)
        {
            Assert.EndsWith(" " + U.Triangle, Row(make(false, true), 120), StringComparison.Ordinal);
            Assert.Contains(Expand, Row(make(true, true), 120), StringComparison.Ordinal);
            Assert.Contains(Collapse, Row(make(false, false), 120), StringComparison.Ordinal);
            for (var width = 24; width <= 120; width++)
            {
                var row = Row(make(false, true), width);
                Assert.True(UnicodeWidth.Of(row) <= width, $"{name} at {width}: {row}");
                var whole = Row(make(false, true), 400);
                if (row.Contains("tok", StringComparison.Ordinal)) Assert.Equal(whole, row);
            }
        }
    }

    [Fact]
    public void A_CLOSED_EDIT_AND_WRITE_BLOCK_END_ON_THE_TRIANGLE()
    {
        var edit = new ToolBlockItem("edit_file", "a.cs", "", true, "coder")
        {
            Collapsed = true, FullResult = "edited a.cs", RawGloss = "ok", EditOld = "old a", EditNew = "new a",
            Parts = ItemRender.ToolGlossParts(new ToolResult("edited a.cs", Gloss: "ok"), T, U), LinkWords = false,
        };
        var write = new ToolBlockItem("write_file", "b.cs", "", true, "coder")
        {
            Collapsed = true, FullResult = "wrote 12 bytes to b.cs", RawGloss = "wrote 12 bytes", WriteContent = "one\ntwo\nthree",
            Parts = ItemRender.ToolGlossParts(new ToolResult("wrote 12 bytes to b.cs", Gloss: "wrote 12 bytes"), T, U), LinkWords = false,
        };
        Assert.EndsWith(" " + U.Triangle, Row(edit, 120), StringComparison.Ordinal);
        Assert.Contains("edited", Row(edit, 120), StringComparison.Ordinal);
        Assert.EndsWith(" " + U.Triangle, Row(write, 120), StringComparison.Ordinal);
        Assert.Contains("wrote", Row(write, 120), StringComparison.Ordinal);
    }

    [Fact]
    public void CAN_OPEN_IS_OPENS_AT_AN_UNBOUNDED_WIDTH()
    {
        var cases = new (ToolBlockItem Block, bool Expected)[]
        {
            (ShellBlock("dotnet test", WithStderr("", "Build FAILED"), 1), false),
            (ShellBlock("echo written", "written", 0), false),
            (ShellBlock("cd src\ngit status", "M a.cs", 0), true),
            (Block("grep", new ToolResult(Globbing.NoMatches, Gloss: "0 matches")), false),
            (Grep(), true),
            (Block("read_file", new ToolResult(MissingError, IsError: true)), false),
        };
        foreach (var (b, expected) in cases)
        {
            Assert.Equal(expected, b.CanOpen);
            Assert.Equal(b.Opens(0, T, U), b.CanOpen);
            Assert.Equal(b.Opens(0, Plain, A), b.CanOpen);
        }
    }

    [Fact]
    public void THE_PLAIN_REPL_LINE_DOES_NOT_CHANGE()
    {
        var call = new ToolCall("c1", "shell", JsonSerializer.Serialize(new { command = "dotnet build" }));
        var line = PlainRenderer.ResultLine(call, new ToolResult(WithStderr("one\ntwo", "warn a"), Gloss: "exit 0"), U);
        Assert.DoesNotContain(U.Triangle, line, StringComparison.Ordinal);
        Assert.DoesNotContain("click", line, StringComparison.Ordinal);
    }

    //the count of 2
    private static List<ToolBlockItem> AppendAll(TranscriptModel model, IEnumerable<ToolBlockItem> blocks)
    {
        var list = blocks.ToList();
        foreach (var b in list) model.Append(b);
        return list;
    }

    [Fact]
    public void THE_FIRST_2_BLOCKS_THAT_CAN_OPEN_KEEP_THE_WORDS()
    {
        var blocks = AppendAll(new TranscriptModel("coder"), Enumerable.Range(0, 5).Select(_ => Grep()));
        Assert.Equal(new[] { true, true, false, false, false }, blocks.Select(b => b.LinkWords));
        Assert.Contains(Expand, Row(blocks[1], 120), StringComparison.Ordinal);
        Assert.EndsWith(" " + U.Triangle, Row(blocks[2], 120), StringComparison.Ordinal);
    }

    [Fact]
    public void A_BLOCK_THAT_CANNOT_OPEN_TAKES_NO_HINT()
    {
        var denied = Block("grep", new ToolResult("blocked: the user denied this", IsError: true)) with { BulletTint = Theme.Err };
        var none = Block("grep", new ToolResult(Globbing.NoMatches, Gloss: "0 matches"));
        var blocks = AppendAll(new TranscriptModel("coder"), new[] { Grep(), none, denied, Grep(), Grep() });
        Assert.True(blocks[3].LinkWords);
        Assert.False(blocks[4].LinkWords);
    }

    [Fact]
    public void A_BLOCK_THAT_OPENS_ONLY_ON_A_NARROW_ROW_TAKES_NO_HINT_AND_SHOWS_THE_TRIANGLE_THERE()
    {
        var error = Block("read_file", new ToolResult(MissingError, IsError: true));
        var blocks = AppendAll(new TranscriptModel("coder"), new[] { error, Grep(), Grep(), Grep() });
        Assert.False(error.LinkWords);
        Assert.True(blocks[1].LinkWords);
        Assert.True(blocks[2].LinkWords);
        Assert.False(blocks[3].LinkWords);
        Assert.EndsWith(" " + U.Triangle, Row(error, 60), StringComparison.Ordinal);
        Assert.DoesNotContain(Expand, Row(error, 60), StringComparison.Ordinal);
    }

    [Fact]
    public void A_SHELL_BLOCK_AND_A_BACKGROUND_END_SHARE_THE_COUNT()
    {
        var model = new TranscriptModel("coder");
        var shell = ShellBlock("dotnet build", WithStderr("one\ntwo", "warn a"), 0);
        var end = BackgroundEndItem.From(new ChatMessage("user", "background task 1 ended · exit 0 · 3s\n" + string.Join("\n", Enumerable.Range(1, 9).Select(i => $"out {i}")),
            Gloss: "exit 0", Background: 1), T, U, "coder");
        model.Append(shell);
        model.Append(end);
        var third = Grep();
        model.Append(third);
        Assert.True(shell.LinkWords);
        Assert.True(end.LinkWords);
        Assert.False(third.LinkWords);
        Assert.EndsWith(" " + U.Triangle, Row(end with { LinkWords = false }, 120), StringComparison.Ordinal);
    }

    [Fact]
    public void NEW_STARTS_THE_COUNT_AGAIN()
    {
        var model = new TranscriptModel("coder");
        AppendAll(model, Enumerable.Range(0, 3).Select(_ => Grep()));
        model.Reset();
        var after = AppendAll(model, Enumerable.Range(0, 3).Select(_ => Grep()));
        Assert.Equal(new[] { true, true, false }, after.Select(b => b.LinkWords));
    }

    [Fact]
    public void A_COMPACTION_STARTS_THE_COUNT_AGAIN()
    {
        var (r, model) = Rich();
        AppendAll(model, Enumerable.Range(0, 3).Select(_ => Grep()));
        Gatto.Repl.Repl.RotateDisplay(model, r, null, "compacted", null, Array.Empty<string>());
        var after = AppendAll(model, Enumerable.Range(0, 3).Select(_ => Grep()));
        Assert.Equal(new[] { true, true, false }, after.Select(b => b.LinkWords));
    }

    private static (StreamRenderer R, TranscriptModel M) Rich()
    {
        var s = new RecordingSurface { Width = 120, Height = 0 };
        var gate = new object();
        var status = new StatusInfo(@"C:\proj", "qwen", "coder", new CtxState(), @"C:\Users\x");
        var painter = new ChromePainter(s, T, gate) { Frame = new InputFrame(s, T, "coder", status, glyphs: U), RoleForTint = "coder" };
        var r = new StreamRenderer(painter, s, T, "coder", new ChromeTicker(painter, gate), gate, model: painter.Model, convoTail: () => null);
        return (r, painter.Model);
    }

    [Fact]
    public void A_RESUMED_SESSION_DRAWS_THE_ROWS_IT_DREW_LIVE()
    {
        var (live, liveModel) = Rich();
        var convo = new Conversation("sys");
        var calls = Enumerable.Range(1, 5).Select(i => new ToolCall($"c{i}", "grep", JsonSerializer.Serialize(new { pattern = $"p{i}" }))).ToList();
        convo.AddAssistant("", calls);
        live.BeginTurn();
        foreach (var c in calls)
        {
            var result = new ToolResult(Matches, Gloss: "3 matches");
            live.OnToolCallStart(c);
            live.OnToolResult(c, result);
            convo.AddToolResult(c.Id, result);
        }
        live.EndTurn();

        var (_, stored) = TranscriptStore.Rebuild(TranscriptStore.BuildLines(liveModel, convo), T, "coder", U);
        var resumed = new TranscriptModel("coder");
        foreach (var item in stored.Items) resumed.Append(item);

        var liveBlocks = liveModel.Items.OfType<ToolBlockItem>().ToList();
        var resumedBlocks = resumed.Items.OfType<ToolBlockItem>().ToList();
        Assert.Equal(5, resumedBlocks.Count);
        foreach (var width in new[] { 80, 120 })
            for (var i = 0; i < 5; i++)
                Assert.Equal(liveBlocks[i].Render(width, T, U).Select(Visible), resumedBlocks[i].Render(width, T, U).Select(Visible));
        var sixth = Grep();
        resumed.Append(sixth);
        Assert.False(sixth.LinkWords);
    }
}
