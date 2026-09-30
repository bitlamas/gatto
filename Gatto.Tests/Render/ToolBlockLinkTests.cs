using System.Text.Json;
using Gatto.Core.Client;
using Gatto.Core.Tools;
using Gatto.Repl;
using Gatto.Repl.Input;
using Gatto.Repl.Render;
using Gatto.Repl.Term;
using Gatto.Terminal;
using Gatto.Tests.Fakes;
using Xunit;

namespace Gatto.Tests.Render;

public sealed class ToolBlockLinkTests
{
    private static readonly Theme T = new(new TermCaps(true, true));
    private static readonly GlyphSet U = GlyphSet.Unicode;
    private const string Matches = "a.cs:1: one\r\nb.cs:2: two\r\nc.cs:3: three";
    private const string Expand = ShellBlockRender.ExpandWords;
    private const string Collapse = ShellBlockRender.CollapseWords;

    private static string Visible(string row) => TermText.StripAnsiForWidth(row);

    private static ToolBlockItem Block(string name, ToolResult r, bool collapsed = true, bool leadingBlank = false, GlyphSet? g = null) =>
        new(name, "args", ItemRender.ToolGloss(r, T, g ?? U), true, "coder")
            { Collapsed = collapsed, FullResult = r.Text, Parts = ItemRender.ToolGlossParts(r, T, g ?? U), LeadingBlank = leadingBlank };

    private static ToolBlockItem Grep(bool collapsed = true, GlyphSet? g = null) => Block("list_dir", new ToolResult(Matches, Gloss: "3 matches"), collapsed, g: g);

    private static string Row(ToolBlockItem b, int width, GlyphSet? g = null) => Visible(b.Render(width, T, g ?? U)[b.LeadingBlank ? 2 : 1]);

    private static bool HasLink(ToolBlockItem b, int width) => Row(b, width).Contains("click to", StringComparison.Ordinal);

    //the rows
    [Fact]
    public void The_link_sits_between_the_words_and_the_token_count()
    {
        var b = Grep();
        var row = Row(b, 120);
        Assert.StartsWith($"  {U.Elbow} {U.Ok} 3 matches {U.Dot} {Expand} {U.Dot} ~", row, StringComparison.Ordinal);
        Assert.EndsWith(" tok", row, StringComparison.Ordinal);
        var l = b.LinkLayout(120, T, U);
        Assert.Equal(1, l.ResultRow);
        Assert.Equal(Expand, row[l.LinkStart..l.LinkEnd]);
        Assert.Contains(T.Paint(Expand, Theme.Thought), b.Render(120, T, U)[1], StringComparison.Ordinal);
    }

    [Fact]
    public void An_open_block_says_click_to_collapse()
    {
        var b = Grep(collapsed: false);
        var row = Row(b, 120);
        var l = b.LinkLayout(120, T, U);
        Assert.Equal(Collapse, row[l.LinkStart..l.LinkEnd]);
        Assert.Contains("c.cs:3: three", string.Join("\n", b.Render(120, T, U).Select(Visible)), StringComparison.Ordinal);
    }

    [Fact]
    public void A_row_without_the_link_is_the_painted_gloss_as_before()
    {
        var r = new ToolResult("no matches", Gloss: "0 matches");
        var b = Block("glob", r);
        Assert.Equal(ItemRender.ToolRows("glob", "args", ItemRender.ToolGloss(r, T, U), true, T, "coder", glyphs: U), b.Render(120, T, U));
    }

    //when the link shows
    [Fact]
    public void An_error_of_several_lines_carries_the_link()
    {
        var b = Block("read_file", new ToolResult("file not found: x\nat line two", IsError: true));
        var row = Row(b, 120);
        Assert.Contains($"{U.Bad} file not found: x {U.Dot} {Expand}", row, StringComparison.Ordinal);
    }

    [Fact]
    public void An_error_of_one_line_that_fits_has_no_link_and_is_cut_to_the_room_when_it_does_not()
    {
        var r = new ToolResult("file not found: C:\\work\\src\\missing\\file.cs", IsError: true);
        var b = Block("read_file", r);
        Assert.Equal($"  {U.Elbow} " + Visible(ItemRender.ToolGloss(r, T, U)), Row(b, 120));
        var narrow = Row(b, 40);
        Assert.EndsWith($"{U.Ellipsis} {U.Dot} {Expand}", narrow, StringComparison.Ordinal);
        Assert.Equal(40, UnicodeWidth.Of(narrow));
    }

    [Theory]
    [InlineData("write_file", "wrote 12 bytes to a.txt")]
    [InlineData("edit_file", "edited a.txt")]
    [InlineData("grep", "no matches")]
    [InlineData("glob", "no matches")]
    public void Opening_that_shows_nothing_new_has_no_link(string name, string text)
    {
        Assert.False(HasLink(Block(name, new ToolResult(text, Gloss: "g")), 120));
    }

    [Fact]
    public void An_empty_result_has_no_link()
    {
        Assert.False(HasLink(Block("memory_write", new ToolResult("", Gloss: "saved")), 120));
    }

    [Fact]
    public void Any_other_result_with_text_carries_the_link()
    {
        Assert.True(HasLink(Block("read_file", new ToolResult("line one", Gloss: "1 line")), 120));
        Assert.True(HasLink(Block("glob", new ToolResult("a.cs", Gloss: "1 file")), 120));
    }

    //narrow rows
    [Fact]
    public void The_token_count_leaves_before_the_link_and_the_link_is_whole_or_absent()
    {
        var tokGoneLinkStays = false;
        foreach (var g in new[] { GlyphSet.Unicode, GlyphSet.Ascii })
        foreach (var b in new[]
            {
                Grep(g: g), Grep(collapsed: false, g: g),
                Block("read_file", new ToolResult("file not found: C:\\work\\src\\missing\\file.cs\nmore", IsError: true), g: g),
                Block("read_file", new ToolResult("file not found: C:\\work\\src\\missing\\file.cs", IsError: true), g: g),
            })
            for (var width = 30; width <= 120; width++)
            {
                var rows = b.Render(width, T, g).Select(Visible).ToList();
                Assert.All(rows, r => Assert.True(UnicodeWidth.Of(r) <= width, $"a row is wider than {width}: [{r}]"));
                if (ReferenceEquals(g, GlyphSet.Ascii)) Assert.All(rows, r => Assert.True(r.All(c => c < 128), $"a row holds a glyph outside ASCII: [{r}]"));
                var row = rows[1];
                var l = b.LinkLayout(width, T, g);
                var word = b.Collapsed ? Expand : Collapse;
                if (row.Contains("click", StringComparison.Ordinal)) Assert.Equal(word, row[l.LinkStart..l.LinkEnd]);
                else Assert.Equal(l.LinkStart, l.LinkEnd);
                if (row.Contains(" tok", StringComparison.Ordinal) && b.FullResult == Matches) Assert.Contains(word, row, StringComparison.Ordinal);
                if (!row.Contains(" tok", StringComparison.Ordinal) && row.Contains(word, StringComparison.Ordinal)) tokGoneLinkStays = true;
            }
        Assert.True(tokGoneLinkStays, "no width showed the link without the token count");
    }

    [Fact]
    public void The_error_text_is_cut_before_the_link_leaves()
    {
        var b = Block("read_file", new ToolResult("file not found: C:\\work\\src\\missing\\file.cs\nmore", IsError: true));
        var row = Row(b, 47);
        Assert.Contains($"{U.Ellipsis} {U.Dot} {Expand}", row, StringComparison.Ordinal);
        Assert.StartsWith($"  {U.Elbow} {U.Bad} file not", row, StringComparison.Ordinal);
    }

    //the press, through the real controller
    private static (ChromePainter P, VtScreenSurface S) Painted(int width, params TranscriptItem[] items)
    {
        var s = new VtScreenSurface(width, 30);
        var model = new TranscriptModel("coder");
        foreach (var i in items) model.Append(i);
        var p = new ChromePainter(s, T, new object(), model)
        {
            Frame = new InputFrame(s, T, "coder", new StatusInfo(@"C:\proj", "qwen", "coder", new CtxState(), @"C:\Users\x"), glyphs: GlyphSet.Unicode),
            RoleForTint = "coder",
        };
        p.State.Composer = new EditorView(new List<string> { "" }, 0, 0);
        p.AltScreen.Enter();
        p.Repaint();
        return (p, s);
    }

    private static void Press(ChromePainter p, int x, int y)
    {
        p.Mouse.Handle(new MouseEvent(x, y, MouseKind.Press, MouseButton.Left, 0, 0), p.Width, p.ViewportRows());
        p.Mouse.Handle(new MouseEvent(x, y, MouseKind.Release, MouseButton.Left, 0, 0), p.Width, p.ViewportRows());
        p.Repaint();
    }

    [Theory]
    [InlineData(47)]
    [InlineData(80)]
    [InlineData(120)]
    public void A_press_on_the_links_first_or_last_cell_toggles_and_focuses_and_one_cell_outside_does_not(int width)
    {
        foreach (var second in new[] { false, true })
        foreach (var dx in new[] { -1, 0, Expand.Length - 1, Expand.Length })
        {
            var first = Block("list_dir", new ToolResult(Matches, Gloss: "3 matches"));
            var other = Block("list_dir", new ToolResult(Matches.Replace("one", "uno", StringComparison.Ordinal), Gloss: "3 matches"), leadingBlank: true);
            var (p, s) = Painted(width, first, other);
            var target = second ? other : first;
            var ys = Enumerable.Range(0, s.Viewport.Count).Where(y => s.Viewport[y].Contains(Expand, StringComparison.Ordinal)).ToList();
            Assert.Equal(2, ys.Count);
            var y = ys[second ? 1 : 0];
            var x = s.Viewport[y].IndexOf(Expand, StringComparison.Ordinal) + dx;
            Press(p, x, y);
            var inside = dx >= 0 && dx < Expand.Length;
            Assert.Equal(!inside, target.Collapsed);
            Assert.Equal(inside ? p.Model.Items.ToList().IndexOf(target) : null, p.Focus.Focused);
        }
    }

    [Fact]
    public void A_press_on_the_link_of_an_open_block_closes_it_and_keeps_its_header_in_view()
    {
        var prose = new AssistantBlockItem(Enumerable.Range(0, 40).Select(i => $"prose {i}").ToArray(), "coder");
        var b = Grep();
        b.LeadingBlank = true;
        var (p, s) = Painted(120, prose, b);
        var y = s.Viewport.ToList().FindIndex(v => v.Contains(Expand, StringComparison.Ordinal));
        Press(p, s.Viewport[y].IndexOf(Expand, StringComparison.Ordinal), y);
        Assert.False(b.Collapsed);
        y = s.Viewport.ToList().FindIndex(v => v.Contains(Collapse, StringComparison.Ordinal));
        Press(p, s.Viewport[y].IndexOf(Collapse, StringComparison.Ordinal), y);
        Assert.True(b.Collapsed);
        Assert.Contains(s.Viewport, v => v.Contains("list_dir args", StringComparison.Ordinal));
    }

    [Fact]
    public void A_copy_across_the_result_row_holds_the_link_words()
    {
        var (p, s) = Painted(120, Grep());
        var y = s.Viewport.ToList().FindIndex(v => v.Contains(Expand, StringComparison.Ordinal));
        p.Mouse.Handle(new MouseEvent(4, y, MouseKind.Press, MouseButton.Left, 0, 0), p.Width, p.ViewportRows());
        p.Mouse.Handle(new MouseEvent(119, y, MouseKind.Move, MouseButton.Left, 0, 0), p.Width, p.ViewportRows());
        p.Mouse.Handle(new MouseEvent(119, y, MouseKind.Release, MouseButton.Left, 0, 0), p.Width, p.ViewportRows());
        Assert.Contains($"3 matches {U.Dot} {Expand} {U.Dot} ~", p.Selection.CopyText(p.Width, T, null, U), StringComparison.Ordinal);
    }

    //the live path and replay
    private static (StreamRenderer R, ChromePainter P, TranscriptModel M) Live(int width)
    {
        var s = new VtScreenSurface(width, 40);
        var gate = new object();
        var model = new TranscriptModel("coder");
        var p = new ChromePainter(s, T, gate, model)
        {
            Frame = new InputFrame(s, T, "coder", new StatusInfo(@"C:\proj", "qwen", "coder", new CtxState(), @"C:\Users\x"), glyphs: GlyphSet.Unicode),
            RoleForTint = "coder",
        };
        p.State.Composer = new EditorView(new List<string> { "" }, 0, 0);
        var r = new StreamRenderer(p, s, T, "coder", new ChromeTicker(p, gate), gate, model: model, convoTail: () => null);
        p.AltScreen.Enter();
        return (r, p, model);
    }

    [Fact]
    public void A_live_block_and_its_replay_draw_the_same_rows_with_the_link()
    {
        var call = new ToolCall("c1", "grep", JsonSerializer.Serialize(new { pattern = "one" }));
        var result = new ToolResult(Matches, Gloss: "3 matches");
        var (r, _, model) = Live(120);
        r.BeginTurn();
        r.OnToolCallStart(call);
        r.OnToolResult(call, result);
        r.EndTurn();
        var live = model.Items.OfType<ToolBlockItem>().Single();

        var convo = new Gatto.Core.Loop.Conversation("sys");
        convo.AddUser("go");
        convo.SetGattoRole("coder");
        convo.AddAssistant("", new[] { call });
        convo.AddToolResult("c1", result);
        var (_, rebuilt) = TranscriptStore.Rebuild(TranscriptStore.BuildLines(new TranscriptModel("coder"), convo), T, "coder", glyphs: U);
        var replay = rebuilt.Items.OfType<ToolBlockItem>().Single();

        foreach (var width in new[] { 80, 120 })
        {
            var a = live.Render(width, T, U).Skip(live.LeadingBlank ? 1 : 0).ToList();
            Assert.Contains(a, row => Visible(row).Contains(Expand, StringComparison.Ordinal));
            Assert.Equal(a, replay.Render(width, T, U).Skip(replay.LeadingBlank ? 1 : 0));
        }
    }

    [Fact]
    public void A_denied_call_carries_no_link()
    {
        var call = new ToolCall("c1", "read_file", JsonSerializer.Serialize(new { path = "a.txt" }));
        var (r, _, model) = Live(120);
        r.BeginTurn();
        r.OnToolCallStart(call);
        r.NotePermissionOutcome(PermissionOutcomeKind.Denied);
        r.OnToolResult(call, new ToolResult("the user denied this call", IsError: true));
        r.EndTurn();
        var b = model.Items.OfType<ToolBlockItem>().Single();
        var row = Row(b, 120);
        Assert.Equal($"  {U.Elbow} {U.Bad} denied", row.TrimEnd());
    }
}
