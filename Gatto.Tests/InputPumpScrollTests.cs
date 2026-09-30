using System.Collections.Concurrent;
using Gatto.Terminal;

namespace Gatto.Tests;

public class InputPumpScrollTests
{
    private sealed class FeedSource : IKeySource
    {
        public BlockingCollection<ConsoleKeyInfo> Feed { get; } = new();
        public ConsoleKeyInfo ReadKey() => Feed.Take();
        public bool KeyAvailable => Feed.Count > 0;
    }

    private static ConsoleKeyInfo Key(ConsoleKey k, bool ctrl = false) => new('\0', k, false, false, ctrl);
    private static ConsoleKeyInfo Ch(char c, ConsoleKey k) => new(c, k, false, false, false);

    private static ScrollKey TakeScroll(BlockingCollection<ScrollKey> c, int ms = 2000)
    {
        Assert.True(c.TryTake(out var v, ms), "scroll sink was not invoked in time");
        return v;
    }

    private static ConsoleKeyInfo? Read(IKeySource s, int ms = 2000)
    {
        var t = Task.Run(s.ReadKey);
        return t.Wait(ms) ? t.Result : null;
    }

    [Fact]
    public void Scroll_keys_reach_the_sink_even_while_a_modal_holds_focus()
    {
        var src = new FeedSource();
        var pump = new InputPump(new KeyInputSource(src));
        var scrolls = new BlockingCollection<ScrollKey>();
        pump.SetScrollSink(scrolls.Add);
        pump.Start();

        using var focus = pump.PushFocus();
        src.Feed.Add(Key(ConsoleKey.PageUp));

        Assert.Equal(ScrollKey.PageUp, TakeScroll(scrolls));   //the scroll sink wins the key before the focus stack.
        Assert.False(focus.Keys.KeyAvailable);
    }

    [Fact]
    public void Non_scroll_keys_still_reach_the_focused_modal()
    {
        var src = new FeedSource();
        var pump = new InputPump(new KeyInputSource(src));
        pump.SetScrollSink(_ => Assert.Fail("y is not a scroll key"));
        pump.Start();

        using var focus = pump.PushFocus();
        src.Feed.Add(Ch('y', ConsoleKey.Y));
        Assert.Equal('y', Read(focus.Keys)!.Value.KeyChar);
    }

    [Fact]
    public void With_no_sink_scroll_keys_fall_through_to_the_focus_stack()
    {
        var src = new FeedSource();
        var pump = new InputPump(new KeyInputSource(src));
        pump.Start();

        using var focus = pump.PushFocus();
        src.Feed.Add(Key(ConsoleKey.PageUp));
        Assert.Equal(ConsoleKey.PageUp, Read(focus.Keys)!.Value.Key);
    }

    [Fact]
    public void Home_end_jump_the_conversation_top_bottom_with_or_without_ctrl()
    {
        var src = new FeedSource();
        var pump = new InputPump(new KeyInputSource(src));
        var scrolls = new BlockingCollection<ScrollKey>();
        pump.SetScrollSink(scrolls.Add);
        pump.Start();

        //the home and end keys scroll the transcript, and ctrl is accepted but not required.
        src.Feed.Add(Key(ConsoleKey.Home));
        Assert.Equal(ScrollKey.Top, TakeScroll(scrolls));
        src.Feed.Add(Key(ConsoleKey.End));
        Assert.Equal(ScrollKey.Bottom, TakeScroll(scrolls));
        src.Feed.Add(Key(ConsoleKey.Home, ctrl: true));
        Assert.Equal(ScrollKey.Top, TakeScroll(scrolls));
        src.Feed.Add(Key(ConsoleKey.End, ctrl: true));
        Assert.Equal(ScrollKey.Bottom, TakeScroll(scrolls));
    }
}
