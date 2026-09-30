using Gatto.Repl.Render;
using Xunit;

namespace Gatto.Tests;

//the ledger holds no clock of its own, so every rule here is checked against readings the test hands in
public class UserWaitTests
{
    [Fact]
    public void A_turn_with_no_prompt_loses_nothing()
    {
        var w = default(UserWait);
        Assert.Equal(0, w.TotalAt(90_000));
        Assert.Equal(0, w.OpenAt(90_000));
    }

    [Fact]
    public void A_closed_wait_subtracts_exactly_its_own_span()
    {
        var w = default(UserWait);
        w.Begin(5_000);
        w.End(29_000);

        Assert.Equal(24_000, w.TotalAt(30_000));
        Assert.Equal(24_000, w.TotalAt(9_000_000));   //the total stops growing when the wait closes
        Assert.Equal(0, w.OpenAt(9_000_000));
    }

    [Fact]
    public void An_open_wait_grows_with_the_clock_and_is_readable_on_its_own()
    {
        var w = default(UserWait);
        w.Begin(1_000);

        Assert.Equal(4_000, w.TotalAt(5_000));
        Assert.Equal(4_000, w.OpenAt(5_000));
        Assert.Equal(28_800_000, w.OpenAt(28_801_000));   //8 hours in, an open wait is still readable
    }

    [Fact]
    public void Two_waits_accumulate()
    {
        var w = default(UserWait);
        w.Begin(0); w.End(1_000);
        w.Begin(10_000); w.End(12_500);

        Assert.Equal(3_500, w.TotalAt(20_000));
    }

    //the inner close comes 5 seconds before the outer one, so ending on it gives a different total
    [Fact]
    public void Nested_scopes_are_one_wait_and_end_with_the_OUTER_close()
    {
        var w = default(UserWait);
        w.Begin(1_000);        //outer
        w.Begin(2_000);        //inner
        w.End(6_000);          //inner closes

        Assert.Equal(9_000, w.OpenAt(10_000));   //still open after the inner close, counted from the outer begin

        w.End(11_000);         //outer closes
        Assert.Equal(10_000, w.TotalAt(50_000));
        Assert.Equal(0, w.OpenAt(50_000));
    }

    [Fact]
    public void An_unmatched_close_adds_nothing()
    {
        var w = default(UserWait);
        w.Begin(1_000);
        w.End(3_000);
        w.End(60_000);   //the double dispose

        Assert.Equal(2_000, w.TotalAt(60_000));
    }

    //a stray close must not send the depth negative, the next Begin then reads as nested and its wait is never counted
    [Fact]
    public void A_close_with_nothing_open_cannot_poison_the_NEXT_wait()
    {
        var w = default(UserWait);
        w.End(60_000);   //a pop with no push

        w.Begin(70_000);
        w.End(75_000);

        Assert.Equal(5_000, w.TotalAt(80_000));
    }

    //a restart clears the depth too, a leaked scope would leave the ledger open for every later turn
    [Fact]
    public void Restart_clears_a_leaked_open_scope()
    {
        var w = default(UserWait);
        w.Begin(1_000);
        w.End(2_000);
        w.Begin(3_000);   //never closed, the leaked scope

        w.Restart();

        Assert.Equal(0, w.TotalAt(500_000));
        Assert.Equal(0, w.OpenAt(500_000));
    }
}
