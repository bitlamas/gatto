using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests;

public class ConsoleInputModeTests
{
    [Fact]
    public void Enter_enables_mouse_and_extended_clears_quickedit_and_processed()
    {
        var ctl = new FakeConsoleModeControl { Mode = ConsoleInputMode.EnableProcessedInput | ConsoleInputMode.EnableQuickEdit };
        new ConsoleInputMode(ctl).Enter();
        Assert.True((ctl.Mode & ConsoleInputMode.EnableMouseInput) != 0);
        Assert.True((ctl.Mode & ConsoleInputMode.EnableExtendedFlags) != 0);
        Assert.Equal(0u, ctl.Mode & ConsoleInputMode.EnableProcessedInput);   //processed input is off, so Ctrl+C arrives as an input record.
        Assert.Equal(0u, ctl.Mode & ConsoleInputMode.EnableQuickEdit);
    }

    [Fact]
    public void Restore_returns_processed_input_so_the_shell_ctrlC_works_again()
    {
        var ctl = new FakeConsoleModeControl { Mode = ConsoleInputMode.EnableProcessedInput | ConsoleInputMode.EnableQuickEdit };
        var m = new ConsoleInputMode(ctl);
        m.Enter();
        Assert.Equal(0u, ctl.Mode & ConsoleInputMode.EnableProcessedInput);   //processed input stays off while gatto owns console input.
        m.Restore();
        Assert.True((ctl.Mode & ConsoleInputMode.EnableProcessedInput) != 0);  //restore must write the saved mode back verbatim.
    }

    [Fact]
    public void Enter_is_idempotent()
    {
        var ctl = new FakeConsoleModeControl { Mode = 0x00F7 };
        var m = new ConsoleInputMode(ctl);
        m.Enter();
        var after = ctl.Sets.Count;
        m.Enter();
        Assert.Equal(after, ctl.Sets.Count);   //idempotent here means the second Enter calls Set no more
    }

    [Fact]
    public void Restore_writes_the_saved_mode_verbatim_and_is_idempotent()
    {
        var ctl = new FakeConsoleModeControl { Mode = 0x00F7 };
        var m = new ConsoleInputMode(ctl);
        m.Enter();
        m.Restore();
        Assert.Equal(0x00F7u, ctl.Mode);
        var sets = ctl.Sets.Count;
        m.Restore();
        Assert.Equal(0x00F7u, ctl.Mode);
        Assert.Equal(sets, ctl.Sets.Count);
    }

    [Fact]
    public void Restore_without_Enter_is_a_noop()
    {
        var ctl = new FakeConsoleModeControl { Mode = 0x0042 };
        new ConsoleInputMode(ctl).Restore();
        Assert.Equal(0x0042u, ctl.Mode);
        Assert.Empty(ctl.Sets);
    }
}
