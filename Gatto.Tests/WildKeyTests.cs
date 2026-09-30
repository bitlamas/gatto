using System.Collections.Concurrent;
using Gatto.Terminal;

namespace Gatto.Tests;

//shift+Tab toggles wild mode, the same toggle /wild performs, through the pump's global sinks so it works at any moment
public class WildKeyTests
{
    private sealed class FeedSource : IKeySource
    {
        public BlockingCollection<ConsoleKeyInfo> Feed { get; } = new();
        public ConsoleKeyInfo ReadKey() => Feed.Take();
        public bool KeyAvailable => Feed.Count > 0;
    }

    //windows sends shift+Tab as VK_TAB with SHIFT_PRESSED and a tab char, and NativeInput passes both through
    private static ConsoleKeyInfo Tab(bool shift = false, bool ctrl = false, bool alt = false) =>
        new('\t', ConsoleKey.Tab, shift, alt, ctrl);

    //the WildKeys.Of cases

    [Fact]
    public void Shift_tab_is_the_wild_key()
    {
        Assert.True(WildKeys.Of(Tab(shift: true)));
    }

    [Theory]
    [InlineData(false, false, false)]   //bare Tab, which must still reach the composer
    [InlineData(true, true, false)]     //ctrl+Shift+Tab is the terminal's own tab-switcher
    [InlineData(true, false, true)]     //alt+Shift+Tab is window switching
    [InlineData(true, true, true)]      //ctrl+Alt+Shift+Tab is AltGr composition territory
    public void Other_tab_combinations_are_not_the_wild_key(bool shift, bool ctrl, bool alt)
    {
        Assert.False(WildKeys.Of(Tab(shift, ctrl, alt)));
    }

    [Fact]
    public void Shift_on_another_key_is_not_the_wild_key()
    {
        Assert.False(WildKeys.Of(new ConsoleKeyInfo('R', ConsoleKey.R, shift: true, alt: false, control: false)));
    }

    //pump routing

    [Fact]
    public void Shift_tab_routes_to_the_wild_sink()
    {
        var src = new FeedSource();
        var pump = new InputPump(new KeyInputSource(src));
        var fired = new BlockingCollection<bool>();
        pump.SetWildSink(() => fired.Add(true));
        pump.Start();

        src.Feed.Add(Tab(shift: true));

        Assert.True(fired.TryTake(out _, 2000), "the wild sink was not invoked in time");
    }

    [Fact]
    public void Shift_tab_reaches_the_sink_even_while_a_modal_holds_focus()
    {
        //the toggle fires while a prompt is up, but must not answer it, so the wild key routes before the focus stack
        var src = new FeedSource();
        var pump = new InputPump(new KeyInputSource(src));
        var fired = new BlockingCollection<bool>();
        pump.SetWildSink(() => fired.Add(true));
        pump.Start();

        using var modal = pump.PushFocus();          //a permission prompt is up
        src.Feed.Add(Tab(shift: true));

        Assert.True(fired.TryTake(out _, 2000), "the wild sink was not invoked under a modal");
        Assert.False(modal.Keys.KeyAvailable, "the keystroke leaked into the prompt");
    }

    [Fact]
    public async Task Bare_tab_is_not_swallowed_and_reaches_the_composer()
    {
        var src = new FeedSource();
        var pump = new InputPump(new KeyInputSource(src));
        pump.SetWildSink(() => Assert.Fail("bare Tab must not toggle wild mode"));
        pump.Start();

        src.Feed.Add(Tab());
        var t = Task.Run(pump.Composer.Read);

        Assert.True(await Task.WhenAny(t, Task.Delay(2000)) == t, "bare Tab should have reached the composer");
        Assert.Equal(ConsoleKey.Tab, Assert.IsType<ComposerInput.Key>(await t).K.Key);
    }

    [Fact]
    public void Scroll_and_focus_keys_still_win_over_the_wild_sink()
    {
        //the wild branch goes after scroll and focus in RouteKey, so it can't shadow them
        var src = new FeedSource();
        var pump = new InputPump(new KeyInputSource(src));
        var focus = new BlockingCollection<FocusKey>();
        pump.SetFocusSink(focus.Add);
        pump.SetWildSink(() => Assert.Fail("a focus key must not reach the wild sink"));
        pump.Start();

        src.Feed.Add(new ConsoleKeyInfo('\0', ConsoleKey.R, shift: false, alt: false, control: true));

        Assert.True(focus.TryTake(out var fk, 2000));
        Assert.Equal(FocusKey.Toggle, fk);
    }
}
