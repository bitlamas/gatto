using System.Text.RegularExpressions;
using Gatto.Repl.Render;
using Gatto.Terminal;
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

        Assert.StartsWith("  That RecursiveKey", row, StringComparison.Ordinal);
        Assert.EndsWith($"{U.Ellipsis} {U.Triangle} thought for 5s", row, StringComparison.Ordinal);
        Assert.Equal(60, UnicodeWidth.Of(row));
    }

    [Fact]
    public void The_label_follows_a_short_line_directly()
    {
        var row = Plain(Assert.Single(Thought("Now the stamp script.", "and then the page").Render(80, T, U)));
        Assert.Equal($"  Now the stamp script. {U.Triangle} thought for 5s", row);
    }

    [Fact]
    public void A_row_that_holds_the_whole_thought_marks_its_label_with_a_dot()
    {
        //nothing is hidden, so the label shows no triangle to open
        var row = Plain(Assert.Single(Thought("", "Now the stamp script.", "  ").Render(80, T, U)));
        Assert.Equal($"  Now the stamp script. {U.Dot} thought for 5s", row);
    }

    [Fact]
    public void A_one_line_thought_cut_to_fit_keeps_the_triangle()
    {
        var row = Plain(Assert.Single(Thought(new string('x', 200)).Render(50, T, U)));
        Assert.EndsWith($"{U.Ellipsis} {U.Triangle} thought for 5s", row, StringComparison.Ordinal);
        Assert.Equal(50, UnicodeWidth.Of(row));
    }

    [Fact]
    public void The_ascii_set_cuts_with_its_own_marks_and_never_passes_the_width()
    {
        var row = Plain(Assert.Single(Thought(new string('y', 200), "more").Render(50, T, GlyphSet.Ascii)));
        Assert.EndsWith(GlyphSet.Ascii.Triangle + " thought for 5s", row, StringComparison.Ordinal);
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
}
