using Gatto.Terminal;
using Gatto.Tests.Fakes;
using Xunit;

namespace Gatto.Tests.Term;

public sealed class AltScreenTests
{
    private static int Count(string haystack, string needle)
    {
        int n = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, System.StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }

    [Fact]
    public void Enter_writes_the_alt_sequence_once_and_is_idempotent()
    {
        var rec = new RecordingSurface();
        var alt = new AltScreen(rec);

        alt.Enter();
        alt.Enter();   //a second Enter writes nothing

        Assert.True(alt.Active);
        Assert.Equal(1, Count(rec.Text, Ansi.AltScreenEnter));
    }

    [Fact]
    public void Restore_writes_exit_plus_show_cursor_once_and_is_idempotent()
    {
        var rec = new RecordingSurface();
        var alt = new AltScreen(rec);
        alt.Enter();
        rec.Clear();

        alt.Restore();
        alt.Restore();   //a second Restore writes nothing

        Assert.False(alt.Active);
        Assert.Equal(1, Count(rec.Text, Ansi.AltScreenExit));
        Assert.Equal(1, Count(rec.Text, Ansi.ShowCursor));
    }

    [Fact]
    public void Restore_without_enter_is_a_no_op()
    {
        var rec = new RecordingSurface();
        var alt = new AltScreen(rec);

        alt.Restore();

        Assert.False(alt.Active);
        Assert.Equal("", rec.Text);
    }

    [Fact]
    public void Enter_then_restore_leaves_the_emulated_terminal_on_the_main_screen()
    {
        var s = new VtScreenSurface(20, 4);
        s.Write("shell prompt");
        var alt = new AltScreen(s);

        alt.Enter();
        Assert.True(s.AltScreen);
        alt.Restore();

        Assert.False(s.AltScreen);
        Assert.Equal("shell prompt", s.Viewport[0]);
    }

    [Fact]
    public void RegisterExitHooks_does_not_throw_and_may_precede_enter()
    {
        var rec = new RecordingSurface();
        var alt = new AltScreen(rec);
        alt.RegisterExitHooks();   //the hooks may be registered before Enter
        alt.Enter();
        Assert.True(alt.Active);
    }
}
