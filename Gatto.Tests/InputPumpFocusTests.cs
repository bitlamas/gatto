using System.Collections.Concurrent;
using Gatto.Terminal;

namespace Gatto.Tests;

public class InputPumpFocusTests
{
    private sealed class FeedSource : IKeySource
    {
        public BlockingCollection<ConsoleKeyInfo> Feed { get; } = new();
        public ConsoleKeyInfo ReadKey() => Feed.Take();
        public bool KeyAvailable => Feed.Count > 0;
    }

    private static ConsoleKeyInfo Key(ConsoleKey k, bool ctrl = false) => new('\0', k, false, false, ctrl);

    private static FocusKey TakeFocus(BlockingCollection<FocusKey> c, int ms = 2000)
    {
        Assert.True(c.TryTake(out var v, ms), "focus sink was not invoked in time");
        return v;
    }

    [Fact]
    public void Ctrl_arrow_and_ctrl_r_route_to_the_focus_sink()
    {
        var src = new FeedSource();
        var pump = new InputPump(new KeyInputSource(src));
        var focus = new BlockingCollection<FocusKey>();
        pump.SetFocusSink(focus.Add);
        pump.Start();

        src.Feed.Add(Key(ConsoleKey.UpArrow, ctrl: true));
        Assert.Equal(FocusKey.Prev, TakeFocus(focus));
        src.Feed.Add(Key(ConsoleKey.DownArrow, ctrl: true));
        Assert.Equal(FocusKey.Next, TakeFocus(focus));
        src.Feed.Add(Key(ConsoleKey.R, ctrl: true));
        Assert.Equal(FocusKey.Toggle, TakeFocus(focus));
    }

    [Fact]
    public void Focus_keys_reach_the_sink_even_while_a_modal_holds_focus()
    {
        var src = new FeedSource();
        var pump = new InputPump(new KeyInputSource(src));
        var focus = new BlockingCollection<FocusKey>();
        pump.SetFocusSink(focus.Add);
        pump.Start();

        using var modal = pump.PushFocus();
        src.Feed.Add(Key(ConsoleKey.DownArrow, ctrl: true));
        Assert.Equal(FocusKey.Next, TakeFocus(focus));
        Assert.False(modal.Keys.KeyAvailable);        //the modal must not also see the key.
    }

    [Fact]
    public async Task Plain_arrows_are_NOT_focus_keys_and_reach_the_composer()
    {
        var src = new FeedSource();
        var pump = new InputPump(new KeyInputSource(src));
        pump.SetFocusSink(_ => Assert.Fail("plain ↑ is not a focus key"));
        pump.Start();

        src.Feed.Add(Key(ConsoleKey.UpArrow));
        var t = Task.Run(pump.Composer.Read);
        Assert.True(await Task.WhenAny(t, Task.Delay(30000)) == t, "plain ↑ should have reached the composer");
        Assert.Equal(ConsoleKey.UpArrow, Assert.IsType<ComposerInput.Key>(await t).K.Key);
    }

    [Fact]
    public async Task CtrlAlt_R_is_AltGr_and_must_not_trigger_focus()   //the focus chord requires control without alt, so altgr must not trigger focus
    {
        var src = new FeedSource();
        var pump = new InputPump(new KeyInputSource(src));
        pump.SetFocusSink(_ => Assert.Fail("Ctrl+Alt (AltGr) must not trigger focus"));
        pump.Start();

        src.Feed.Add(new ConsoleKeyInfo('®', ConsoleKey.R, shift: false, alt: true, control: true));
        var t = Task.Run(pump.Composer.Read);
        Assert.True(await Task.WhenAny(t, Task.Delay(30000)) == t, "AltGr+R should have reached the composer");
        Assert.Equal(ConsoleKey.R, Assert.IsType<ComposerInput.Key>(await t).K.Key);
    }
}
