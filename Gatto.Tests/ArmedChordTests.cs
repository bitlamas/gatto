using Gatto.Repl.Input;
using Xunit;

namespace Gatto.Tests;

public sealed class ArmedChordTests
{
    //this alias moves with the constant, so none of the assertions below can fail about its value
    private const long Window = Gatto.Repl.Repl.CtrlCDoubleTapMs;

    //the value is pinned as a literal in exactly one test. a test that reads the constant on both sides only proves the code agrees with itself
    [Fact]
    public void THE_WINDOW_IS_TWO_SECONDS() => Assert.Equal(2000, Gatto.Repl.Repl.CtrlCDoubleTapMs);

    [Fact]
    public void A_first_press_arms_but_does_not_fire()
    {
        var a = new ArmedChord();
        Assert.False(a.Fire(Chord.ClearComposer, 1000));
        Assert.Equal(Chord.ClearComposer, a.ArmedAt(1000));
        Assert.Equal("Esc again to clear", a.HintAt(1000));
    }

    [Fact]
    public void A_second_press_inside_the_window_fires()
    {
        var a = new ArmedChord();
        a.Fire(Chord.ClearComposer, 1000);
        Assert.True(a.Fire(Chord.ClearComposer, 1000 + Window - 1));
    }

    [Fact]
    public void A_second_press_exactly_at_the_window_edge_still_fires()
    {
        var a = new ArmedChord();
        a.Fire(Chord.ClearComposer, 1000);
        Assert.True(a.Fire(Chord.ClearComposer, 1000 + Window));   //the boundary instant counts as inside the window, matching DecideCtrlC
    }

    [Fact]
    public void A_second_press_past_the_window_re_arms_instead_of_firing()
    {
        var a = new ArmedChord();
        a.Fire(Chord.ClearComposer, 1000);
        Assert.False(a.Fire(Chord.ClearComposer, 1000 + Window + 1));
        Assert.Equal(Chord.ClearComposer, a.ArmedAt(1000 + Window + 1));   //a press past the window re-arms instead of firing or clearing.
    }

    [Fact]
    public void An_expired_arm_reports_nothing_armed_and_no_hint()
    {
        var a = new ArmedChord();
        a.Fire(Chord.Quit, 1000);
        Assert.Equal(Chord.Quit, a.ArmedAt(1000 + Window));
        Assert.Equal(Chord.None, a.ArmedAt(1000 + Window + 1));
        Assert.Null(a.HintAt(1000 + Window + 1));
    }

    [Fact]
    public void Arming_one_chord_disarms_the_other()
    {
        //one Ctrl+C after an armed Esc arms quit, it never quits
        var a = new ArmedChord();
        a.Fire(Chord.ClearComposer, 1000);
        Assert.False(a.Fire(Chord.Quit, 1100));       //the first Ctrl+C arms quit and fires nothing
        Assert.Equal(Chord.Quit, a.ArmedAt(1100));
        Assert.Equal("Ctrl+C again to quit", a.HintAt(1100));
    }

    [Fact]
    public void Disarm_clears_the_arm_and_the_hint()
    {
        var a = new ArmedChord();
        a.Fire(Chord.Quit, 1000);
        a.Disarm();
        Assert.Equal(Chord.None, a.Armed);
        Assert.Null(a.Hint);
    }

    [Fact]
    public void Firing_consumes_the_arm_so_a_third_press_arms_afresh()
    {
        var a = new ArmedChord();
        a.Fire(Chord.Quit, 1000);
        Assert.True(a.Fire(Chord.Quit, 1500));
        Assert.Equal(Chord.None, a.Armed);            //firing clears the arm
        Assert.False(a.Fire(Chord.Quit, 1600));       //the next press arms again rather than firing
    }
}
