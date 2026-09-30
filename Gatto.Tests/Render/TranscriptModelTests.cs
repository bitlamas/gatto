using Gatto.Repl.Render;
using Gatto.Terminal;
using Xunit;

namespace Gatto.Tests.Render;

public sealed class TranscriptModelTests
{
    private static readonly Theme T = new(new TermCaps(true, true));

    [Fact]
    public void Role_change_retints_future_blocks_only()
    {
        //each new assistant block is stamped with the current role, committed blocks keep theirs
        var m = new TranscriptModel("generalist");
        m.AppendOpenLine("first block", reasoning: false);
        m.CloseOpen();
        var first = Assert.IsType<AssistantBlockItem>(m.Items[0]);
        Assert.Equal("generalist", first.Role);

        m.Role = "oracle";
        m.AppendOpenLine("second block", reasoning: false);
        m.CloseOpen();
        Assert.Equal("oracle", Assert.IsType<AssistantBlockItem>(m.Items[1]).Role);
        Assert.Equal("generalist", first.Role);
    }

    [Fact]
    public void Append_sets_leading_blank_on_every_item_but_the_first()
    {
        var m = new TranscriptModel("generalist");
        m.Append(new UserEchoItem(new[] { "hi" }));
        m.Append(new SystemLineItem("warn", null));
        m.AppendOpenLine("prose", reasoning: false);

        Assert.False(m.Items[0].LeadingBlank);
        Assert.True(m.Items[1].LeadingBlank);
        Assert.True(m.Items[2].LeadingBlank);
    }

    [Fact]
    public void Rendered_items_carry_one_blank_separator_between_blocks_not_before_the_first()
    {
        var m = new TranscriptModel("generalist");
        m.Append(new UserEchoItem(new[] { "do it" }));
        m.AppendOpenLine("working on it", reasoning: false);
        m.CloseOpen();
        m.Append(new ToolBlockItem("shell", "", "✓ ok", HasResult: true, "generalist"));

        Assert.NotEqual("", m.Items[0].Render(40, T, glyphs: GlyphSet.Unicode)[0]);
        Assert.Equal("", m.Items[1].Render(40, T, glyphs: GlyphSet.Unicode)[0]);
        Assert.Equal("", m.Items[2].Render(40, T, glyphs: GlyphSet.Unicode)[0]);
    }

    [Fact]
    public void Open_item_grows_in_place_instead_of_being_recreated()
    {
        var m = new TranscriptModel("generalist");
        m.AppendOpenLine("first", reasoning: false);
        m.AppendOpenLine("second", reasoning: false);

        Assert.Single(m.Items);
        var block = Assert.IsType<AssistantBlockItem>(m.Items[0]);
        Assert.Equal(new[] { "first", "second" }, block.RawLines);
    }

    [Fact]
    public void CloseOpen_then_append_starts_a_second_item()
    {
        var m = new TranscriptModel("generalist");
        m.AppendOpenLine("run one", reasoning: false);
        m.CloseOpen();
        m.AppendOpenLine("run two", reasoning: false);

        Assert.Equal(2, m.Items.Count);
    }

    [Fact]
    public void Append_a_finished_item_auto_closes_the_open_item()
    {
        var m = new TranscriptModel("generalist");
        m.AppendOpenLine("prose", reasoning: false);
        m.Append(new SystemLineItem("a warning", After: null));
        m.AppendOpenLine("more prose", reasoning: false);

        Assert.Equal(3, m.Items.Count);
        Assert.IsType<AssistantBlockItem>(m.Items[0]);
        Assert.IsType<SystemLineItem>(m.Items[1]);
        Assert.IsType<AssistantBlockItem>(m.Items[2]);
    }

    [Fact]
    public void Switching_reasoning_mode_closes_the_prose_run_and_opens_a_reasoning_item()
    {
        var m = new TranscriptModel("generalist");
        m.AppendOpenLine("thinking", reasoning: true);
        m.AppendOpenLine("still thinking", reasoning: true);
        m.AppendOpenLine("answer", reasoning: false);

        Assert.Equal(2, m.Items.Count);
        Assert.IsType<ReasoningItem>(m.Items[0]);
        Assert.IsType<AssistantBlockItem>(m.Items[1]);
    }

    [Fact]
    public void OnAppendedOrExtended_fires_on_each_append_and_each_extend()
    {
        var m = new TranscriptModel("generalist");
        var fires = 0;
        m.OnAppendedOrExtended += _ => fires++;

        m.AppendOpenLine("a", reasoning: false);   //opening an item counts as one event.
        m.AppendOpenLine("b", reasoning: false);   //extending an open item counts as the second event.
        m.Append(new SystemLineItem("x", null));   //appending a finished item counts as the third event.

        Assert.Equal(3, fires);
    }

    [Fact]
    public void The_first_two_reasoning_blocks_carry_the_click_teaser_then_it_stops()
    {
        var m = new TranscriptModel("generalist");
        ReasoningItem Reason(string s) { m.AppendOpenLine(s, reasoning: true); m.CloseOpen(); return (ReasoningItem)m.Items[^1]; }

        Assert.True(Reason("first").ClickHint);
        Assert.True(Reason("second").ClickHint);
        Assert.False(Reason("third").ClickHint);     //from the third block on the hint stays off, the user has learned the gesture

        m.Reset();                                   //a reset starts a fresh session, so the hint returns.
        Assert.True(Reason("after reset").ClickHint);
    }
}
