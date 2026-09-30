using Gatto.Terminal;

namespace Gatto.Tests;

public class ConsoleInputModeTests
{
    private sealed class FakeCtl : IConsoleModeControl
    {
        public uint Mode;
        public int Sets;
        public uint Get() => Mode;
        public void Set(uint m) { Mode = m; Sets++; }
    }

    [Fact]
    public void Enter_enables_mouse_and_extended_clears_quickedit_and_processed()
    {
        var ctl = new FakeCtl { Mode = ConsoleInputMode.EnableProcessedInput | ConsoleInputMode.EnableQuickEdit };
        new ConsoleInputMode(ctl).Enter();
        Assert.True((ctl.Mode & ConsoleInputMode.EnableMouseInput) != 0);
        Assert.True((ctl.Mode & ConsoleInputMode.EnableExtendedFlags) != 0);
        Assert.Equal(0u, ctl.Mode & ConsoleInputMode.EnableProcessedInput);   //processed input is off, so Ctrl+C arrives as an input record.
        Assert.Equal(0u, ctl.Mode & ConsoleInputMode.EnableQuickEdit);
    }

    [Fact]
    public void Restore_returns_processed_input_so_the_shell_ctrlC_works_again()
    {
        var ctl = new FakeCtl { Mode = ConsoleInputMode.EnableProcessedInput | ConsoleInputMode.EnableQuickEdit };
        var m = new ConsoleInputMode(ctl);
        m.Enter();
        Assert.Equal(0u, ctl.Mode & ConsoleInputMode.EnableProcessedInput);   //processed input stays off while gatto owns console input.
        m.Restore();
        Assert.True((ctl.Mode & ConsoleInputMode.EnableProcessedInput) != 0);  //restore must write the saved mode back verbatim.
    }

    [Fact]
    public void Enter_is_idempotent()
    {
        var ctl = new FakeCtl { Mode = 0x00F7 };
        var m = new ConsoleInputMode(ctl);
        m.Enter();
        var after = ctl.Sets;
        m.Enter();
        Assert.Equal(after, ctl.Sets);   //idempotent here means the second Enter calls Set no more
    }

    [Fact]
    public void Restore_writes_the_saved_mode_verbatim_and_is_idempotent()
    {
        var ctl = new FakeCtl { Mode = 0x00F7 };
        var m = new ConsoleInputMode(ctl);
        m.Enter();
        m.Restore();
        Assert.Equal(0x00F7u, ctl.Mode);
        var sets = ctl.Sets;
        m.Restore();
        Assert.Equal(0x00F7u, ctl.Mode);
        Assert.Equal(sets, ctl.Sets);
    }

    [Fact]
    public void Restore_without_Enter_is_a_noop()
    {
        var ctl = new FakeCtl { Mode = 0x0042 };
        new ConsoleInputMode(ctl).Restore();
        Assert.Equal(0x0042u, ctl.Mode);
        Assert.Equal(0, ctl.Sets);
    }
}
