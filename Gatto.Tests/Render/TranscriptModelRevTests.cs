using Gatto.Repl.Render;
using Xunit;

namespace Gatto.Tests.Render;

public sealed class TranscriptModelRevTests
{
    [Fact]
    public void Collapsed_setter_bumps_Rev()
    {
        var item = new AssistantBlockItem(new[] { "hi" }, "generalist");
        var r0 = item.Rev;
        item.Collapsed = !item.Collapsed;
        Assert.NotEqual(r0, item.Rev);
    }

    [Fact]
    public void Collapsed_setter_no_change_does_not_bump_Rev()   //the setter must compare the value before bumping Rev, close paths assign Collapsed without changing it
    {
        var item = new AssistantBlockItem(new[] { "hi" }, "generalist") { Collapsed = true };
        var r0 = item.Rev;
        item.Collapsed = true;
        Assert.Equal(r0, item.Rev);
    }

    [Fact]
    public void AppendOpenLine_extend_bumps_the_open_items_Rev()
    {
        var m = new TranscriptModel("generalist");
        m.AppendOpenLine("line 1", reasoning: false);
        var open = m.Items[^1];
        var r0 = open.Rev;
        m.AppendOpenLine("line 2", reasoning: false);
        Assert.Same(open, m.Items[^1]);
        Assert.NotEqual(r0, open.Rev);
    }

    [Fact]
    public void CloseOpen_whitespace_removal_fires_OnItemRemoved()
    {
        var m = new TranscriptModel("generalist");
        m.AppendOpenLine("real", reasoning: false);
        m.CloseOpen();                                  //closing a non-whitespace item seals it without removal.
        TranscriptItem? removed = null;
        m.OnItemRemoved += it => removed = it;
        m.AppendOpenLine("   ", reasoning: false);
        m.CloseOpen();
        Assert.NotNull(removed);
    }

    [Fact]
    public void CloseOpen_of_a_real_item_does_not_fire_OnItemRemoved()
    {
        var m = new TranscriptModel("generalist");
        var fired = false;
        m.OnItemRemoved += _ => fired = true;
        m.AppendOpenLine("content", reasoning: false);
        m.CloseOpen();
        Assert.False(fired);
    }
}
