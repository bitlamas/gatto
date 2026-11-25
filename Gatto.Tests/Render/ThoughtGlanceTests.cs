using System.Text.RegularExpressions;
using Gatto.Core.Client;
using Gatto.Core.Home;
using Gatto.Core.Tools;
using Gatto.Repl.Input;
using Gatto.Repl.Render;
using Gatto.Repl.Render.Chrome;
using Gatto.Terminal;
using Gatto.Terminal.Tests.Fakes;
using Xunit;

namespace Gatto.Tests.Render;

//a closed thought between tool calls shows its first line on one row
public sealed class ThoughtGlanceTests
{
    private static readonly Theme T = new(new TermCaps(true, true));

    private static readonly GlyphSet U = GlyphSet.Unicode;

    private static readonly Regex Sgr = new(((char)27).ToString() + @"\[[0-9;]*m", RegexOptions.Compiled);

    private static string Plain(string row) => Sgr.Replace(row, "");

    private static ReasoningItem Thought(params string[] lines) =>
        new(lines) { Collapsed = true, Streaming = false, Elapsed = TimeSpan.FromSeconds(5), BetweenTools = true };

    private static ToolBlockItem Tool() => new("shell", "git status", "ran", HasResult: true, "coder");

    [Fact]
    public void A_long_first_line_is_cut_to_the_row_and_the_label_stays_whole()
    {
        var it = Thought("That RecursiveKey line hack-ish approach is wrong, the parameter parsing is the issue here", "second line");
        var row = Plain(Assert.Single(it.Render(60, T, U)));

        Assert.StartsWith($"{U.Triangle} That RecursiveKey", row, StringComparison.Ordinal);
        Assert.EndsWith($"{U.Ellipsis} (thought for 5s)", row, StringComparison.Ordinal);
        Assert.Equal(60, UnicodeWidth.Of(row));
    }

    [Fact]
    public void Short_lines_join_on_one_row_between_the_chevron_and_the_bracketed_time()
    {
        var row = Plain(Assert.Single(Thought("Now the stamp script.", "and then the page").Render(80, T, U)));
        Assert.Equal($"{U.Triangle} Now the stamp script. and then the page (thought for 5s)", row);
    }

    [Fact]
    public void Lines_that_outgrow_the_row_join_then_cut_with_an_ellipsis_before_the_time()
    {
        var row = Plain(Assert.Single(Thought("I have a lot of info on Opencode.", "the docs list the commands", "and the flags too").Render(60, T, U)));
        Assert.Equal(60, UnicodeWidth.Of(row));
        Assert.StartsWith($"{U.Triangle} I have a lot of info on Opencode. the", row, StringComparison.Ordinal);   //the second line follows the first with one space
        Assert.EndsWith($"{U.Ellipsis} (thought for 5s)", row, StringComparison.Ordinal);
    }

    [Fact]
    public void Inner_space_runs_and_leading_blanks_join_with_single_spaces()
    {
        var row = Plain(Assert.Single(Thought("  a   b  ", "   c     d").Render(80, T, U)));
        Assert.Equal($"{U.Triangle} a b c d (thought for 5s)", row);
    }

    [Fact]
    public void A_row_that_holds_the_whole_thought_shows_no_ellipsis()
    {
        var row = Plain(Assert.Single(Thought("", "Now the stamp script.", "  ").Render(80, T, U)));
        Assert.Equal($"{U.Triangle} Now the stamp script. (thought for 5s)", row);
    }

    [Fact]
    public void A_thought_that_fills_the_room_exactly_is_not_cut()
    {
        //60 cells less the chevron and its space and the 17 of the bracketed time leave 41
        var row = Plain(Assert.Single(Thought(new string('z', 41)).Render(60, T, U)));
        Assert.Equal($"{U.Triangle} {new string('z', 41)} (thought for 5s)", row);
    }

    [Fact]
    public void A_one_line_thought_cut_to_fit_ends_in_the_ellipsis_and_the_time()
    {
        var row = Plain(Assert.Single(Thought(new string('x', 200)).Render(50, T, U)));
        Assert.EndsWith($"{U.Ellipsis} (thought for 5s)", row, StringComparison.Ordinal);
        Assert.Equal(50, UnicodeWidth.Of(row));
    }

    [Fact]
    public void The_ascii_set_cuts_with_its_own_marks_and_never_passes_the_width()
    {
        var row = Plain(Assert.Single(Thought(new string('y', 200), "more").Render(50, T, GlyphSet.Ascii)));
        Assert.StartsWith(GlyphSet.Ascii.Triangle + " y", row, StringComparison.Ordinal);
        Assert.EndsWith(GlyphSet.Ascii.Ellipsis + " (thought for 5s)", row, StringComparison.Ordinal);
        Assert.True(UnicodeWidth.Of(row) <= 50);
    }

    [Fact]
    public void Too_narrow_a_row_keeps_the_plain_summary()
    {
        var row = Plain(Assert.Single(Thought("a thought", "more").Render(24, T, U)));
        Assert.Equal($"{U.Triangle} thought for 5s", row);
    }

    [Fact]
    public void A_thought_of_blank_lines_keeps_the_plain_summary()
    {
        var row = Plain(Assert.Single(Thought("", "   ").Render(80, T, U)));
        Assert.Equal($"{U.Triangle} thought for 5s", row);
    }

    [Fact]
    public void The_model_text_is_sanitized_before_it_reaches_the_row()
    {
        var row = Assert.Single(Thought("bell" + (char)7 + " and " + (char)27 + "[31mred", "more").Render(80, T, U));
        Assert.DoesNotContain((char)7, row);
        Assert.Contains("bell and [31mred", Plain(row), StringComparison.Ordinal);   //the escape byte is gone and the rest stays as text
    }

    [Fact]
    public void An_open_thought_between_tool_calls_renders_as_before()
    {
        var it = Thought("line one", "line two");
        it.Collapsed = false;
        var rows = it.Render(80, T, U).Select(Plain).ToList();
        Assert.StartsWith($"{U.Caret} thought for 5s", rows[0], StringComparison.Ordinal);
    }

    [Fact]
    public void A_thought_after_a_tool_call_is_marked_when_the_next_tool_call_arrives()
    {
        var model = new TranscriptModel("coder");
        model.Append(Tool());
        model.AppendOpenLine("checking the next file", reasoning: true);
        model.CloseOpenReasoning(TimeSpan.FromSeconds(2), collapse: true);
        var thought = Assert.IsType<ReasoningItem>(model.Items[^1]);
        Assert.False(thought.BetweenTools);
        var rev = thought.Rev;

        model.Append(Tool());

        Assert.True(thought.BetweenTools);
        Assert.True(thought.Rev > rev);   //the row count cache and a live selection see the change
        Assert.Contains("checking the next file", Plain(thought.Render(80, T, U)[^1]), StringComparison.Ordinal);
    }

    [Fact]
    public void A_thought_after_the_pictures_a_tool_call_read_counts_as_after_that_call()
    {
        var model = new TranscriptModel("coder");
        model.Append(Tool());
        model.Append(ImageBlockItem.From(new Gatto.Core.Client.ChatMessage("user", "images read this round: a.png (9 bytes)",
            Images: [new Gatto.Core.Client.ImageRef("a.png", 9, "ab", "image/png")], By: "read_file")));
        model.AppendOpenLine("looked at it", reasoning: true);
        model.CloseOpenReasoning(TimeSpan.FromSeconds(2), collapse: true);
        model.Append(Tool());
        Assert.True(Assert.IsType<ReasoningItem>(model.Items[^2]).BetweenTools);
    }

    [Fact]
    public void A_thought_after_the_user_message_or_before_prose_keeps_the_plain_row()
    {
        var model = new TranscriptModel("coder");
        model.AppendOpenLine("the first thought of the turn", reasoning: true);
        model.CloseOpenReasoning(TimeSpan.FromSeconds(2), collapse: true);
        model.Append(Tool());
        model.AppendOpenLine("a thought before the answer", reasoning: true);
        model.CloseOpenReasoning(TimeSpan.FromSeconds(2), collapse: true);
        model.AppendOpenLine("the answer", reasoning: false);
        model.CloseOpen();

        var thoughts = model.Items.OfType<ReasoningItem>().ToList();
        Assert.Equal(2, thoughts.Count);
        Assert.All(thoughts, t => Assert.False(t.BetweenTools));
    }

    //the live bullet is chrome, so the mark's driver needs a painter. mirrors the Make harness of StreamRendererTests
    private static (StreamRenderer R, TranscriptModel M) Live()
    {
        var s = new RecordingSurface { Width = 80 };
        var gate = new object();
        var painter = new ChromePainter(s, T, gate, new TranscriptModel("coder"))
        {
            Frame = new InputFrame(s, T, "coder",
                new StatusInfo(@"C:\proj", "qwen", "coder", new CtxState(), @"C:\Users\x"), glyphs: GlyphSet.Unicode),
            RoleForTint = "coder",
        };
        var r = new StreamRenderer(painter, s, T, "coder", new ChromeTicker(painter, gate), gate,
            model: painter.Model, convoTail: () => null);
        return (r, painter.Model);
    }

    //the first streamed fragment of a call is already a call, so the thought above takes its row before any second block exists
    [Fact]
    public void A_thought_takes_its_row_when_the_next_call_starts_streaming()
    {
        var (r, m) = Live();
        var first = new ToolCall("i", "read_file", """{"path":"a.cs"}""");
        r.OnToolCallStart(first);
        r.OnToolResult(first, new ToolResult("ok", Gloss: "1 line"));
        r.OnReasoningDelta("the next thing is a call");

        r.OnToolCallDelta("read_file", "{\"pat", 0);

        var thought = Assert.IsType<ReasoningItem>(m.Items[^1]);   //still the last item, no second block was appended
        Assert.True(thought.BetweenTools);
    }

    //a call dropped before it was saved follows the thought nowhere, so the mark leaves with the live bullet
    [Fact]
    public void A_thought_gives_the_row_back_when_the_streamed_call_is_dropped()
    {
        var (r, m) = Live();
        var first = new ToolCall("i", "read_file", """{"path":"a.cs"}""");
        r.OnToolCallStart(first);
        r.OnToolResult(first, new ToolResult("ok", Gloss: "1 line"));
        r.OnReasoningDelta("the next thing is a call");
        r.OnToolCallDelta("read_file", "{\"pat", 0);
        var thought = Assert.IsType<ReasoningItem>(m.Items[^1]);
        Assert.True(thought.BetweenTools);

        r.EndTurn();   //the turn dies with the call half-assembled, and no block follows the thought

        Assert.False(thought.BetweenTools);
    }

    //the completing call commits its block and the mark it already stamped stands, as today
    [Fact]
    public void The_row_stands_when_the_previewed_call_completes()
    {
        var (r, m) = Live();
        var first = new ToolCall("i", "read_file", """{"path":"a.cs"}""");
        r.OnToolCallStart(first);
        r.OnToolResult(first, new ToolResult("ok", Gloss: "1 line"));
        r.OnReasoningDelta("the next thing is a call");
        r.OnToolCallDelta("read_file", "{\"pat", 0);
        var thought = Assert.IsType<ReasoningItem>(m.Items[^1]);

        var second = new ToolCall("j", "read_file", """{"path":"b.cs"}""");
        r.OnToolCallStart(second);
        r.OnToolResult(second, new ToolResult("ok", Gloss: "1 line"));

        Assert.True(thought.BetweenTools);
        Assert.IsType<ToolBlockItem>(m.Items[^1]);
    }

    //the first thought of a turn has no call before it, so a streaming call does not make it between tools
    [Fact]
    public void A_first_thought_stays_plain_when_a_call_starts()
    {
        var (r, m) = Live();
        r.OnReasoningDelta("the first thought of the turn");

        r.OnToolCallDelta("read_file", "{\"pat", 0);

        Assert.False(Assert.IsType<ReasoningItem>(m.Items[^1]).BetweenTools);
    }
}
