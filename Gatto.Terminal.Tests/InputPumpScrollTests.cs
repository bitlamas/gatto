using System.Collections.Concurrent;
using Gatto.Terminal;

namespace Gatto.Terminal.Tests;

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

    //a scope that pages its own body reads the paging keys, and the transcript does not move under it
    [Theory]
    [InlineData(ConsoleKey.PageDown)]
    [InlineData(ConsoleKey.PageUp)]
    [InlineData(ConsoleKey.Home)]
    [InlineData(ConsoleKey.End)]
    public void A_scope_that_takes_scroll_keys_reads_them_instead_of_the_sink(ConsoleKey key)
    {
        var src = new FeedSource();
        var pump = new InputPump(new KeyInputSource(src));
        var scrolls = new BlockingCollection<ScrollKey>();
        pump.SetScrollSink(scrolls.Add);
        pump.Start();

        using var focus = pump.PushFocus(takesScrollKeys: true);
        src.Feed.Add(Key(key));

        Assert.Equal(key, Read(focus.Keys)!.Value.Key);
        Assert.Equal(0, scrolls.Count);
    }

    //the flag is the scope's own, a default scope pushed over it gives the transcript its keys back
    [Fact]
    public void A_default_scope_over_one_that_takes_them_sends_them_to_the_sink()
    {
        var src = new FeedSource();
        var pump = new InputPump(new KeyInputSource(src));
        var scrolls = new BlockingCollection<ScrollKey>();
        pump.SetScrollSink(scrolls.Add);
        pump.Start();

        using var panel = pump.PushFocus(takesScrollKeys: true);
        using (var prompt = pump.PushFocus())
        {
            src.Feed.Add(Key(ConsoleKey.PageDown));
            Assert.Equal(ScrollKey.PageDown, TakeScroll(scrolls));
        }
        src.Feed.Add(Key(ConsoleKey.PageDown));
        Assert.Equal(ConsoleKey.PageDown, Read(panel.Keys)!.Value.Key);
    }

    //an injected key is read like a typed one, and one handed to a disposed scope is dropped without a throw
    [Fact]
    public void Inject_reaches_a_live_scope_and_drops_on_a_disposed_one()
    {
        var src = new FeedSource();
        var pump = new InputPump(new KeyInputSource(src));
        pump.Start();

        var focus = pump.PushFocus();
        var wake = new ConsoleKeyInfo('\0', ConsoleKey.NoName, false, false, false);
        focus.Inject(wake);
        Assert.True(focus.Keys.KeyAvailable);
        Assert.Equal(ConsoleKey.NoName, Read(focus.Keys)!.Value.Key);

        focus.Dispose();
        focus.Inject(wake);
    }
}
