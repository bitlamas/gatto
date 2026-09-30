//the ladder is a pure static function, so these cases need no terminal, pump or painter
using Gatto.Repl;

namespace Gatto.Tests.Repl;

public class CancelLadderTests
{
    //every case here holds the composer empty, so the composer rung never fires
    [Fact]
    public void DecideCancel_Esc_ladder_selection_then_turn_then_passthrough()
    {
        Assert.Equal(Gatto.Repl.Repl.CancelAction.ClearSelection,
            Gatto.Repl.Repl.DecideCancel(ConsoleKey.Escape, hasSelection: true, turnInFlight: true, composerEmpty: true));   //clearing the selection outranks aborting the turn.
        Assert.Equal(Gatto.Repl.Repl.CancelAction.AbortTurn,
            Gatto.Repl.Repl.DecideCancel(ConsoleKey.Escape, hasSelection: false, turnInFlight: true, composerEmpty: true));
        Assert.Equal(Gatto.Repl.Repl.CancelAction.PassThrough,
            Gatto.Repl.Repl.DecideCancel(ConsoleKey.Escape, hasSelection: false, turnInFlight: false, composerEmpty: true));
    }

    [Fact]
    public void DecideCancel_CtrlC_copies_a_selection_else_passes_through()
    {
        Assert.Equal(Gatto.Repl.Repl.CancelAction.Copy,
            Gatto.Repl.Repl.DecideCancel(ConsoleKey.C, hasSelection: true, turnInFlight: false, composerEmpty: true));
        Assert.Equal(Gatto.Repl.Repl.CancelAction.Copy,
            Gatto.Repl.Repl.DecideCancel(ConsoleKey.C, hasSelection: true, turnInFlight: true, composerEmpty: true));    //a selection copies even while a turn runs.
        Assert.Equal(Gatto.Repl.Repl.CancelAction.PassThrough,
            Gatto.Repl.Repl.DecideCancel(ConsoleKey.C, hasSelection: false, turnInFlight: false, composerEmpty: true));
    }

    //the mid-turn abort belongs in the Ctrl+C branch too, or ctrl+c on the rich path falls to the composer's quit chord
    [Fact]
    public void DecideCancel_CtrlC_aborts_a_turn_when_no_selection()
    {
        Assert.Equal(Gatto.Repl.Repl.CancelAction.AbortTurn,
            Gatto.Repl.Repl.DecideCancel(ConsoleKey.C, hasSelection: false, turnInFlight: true, composerEmpty: true));
        Assert.Equal(Gatto.Repl.Repl.CancelAction.Copy,
            Gatto.Repl.Repl.DecideCancel(ConsoleKey.C, hasSelection: true, turnInFlight: true, composerEmpty: true));
        Assert.Equal(Gatto.Repl.Repl.CancelAction.PassThrough,
            Gatto.Repl.Repl.DecideCancel(ConsoleKey.C, hasSelection: false, turnInFlight: false, composerEmpty: true));
    }

    //composer text outranks a running turn in the ladder
    [Fact]
    public void Esc_with_text_in_the_composer_passes_through_to_clear_it_even_mid_turn()
    {
        //a running turn must not eat the esc that was meant to clear composer text.
        Assert.Equal(Gatto.Repl.Repl.CancelAction.PassThrough,
            Gatto.Repl.Repl.DecideCancel(ConsoleKey.Escape, hasSelection: false, turnInFlight: true,
                composerEmpty: false));
    }

    [Fact]
    public void Esc_on_an_empty_composer_mid_turn_aborts_the_turn()
    {
        Assert.Equal(Gatto.Repl.Repl.CancelAction.AbortTurn,
            Gatto.Repl.Repl.DecideCancel(ConsoleKey.Escape, hasSelection: false, turnInFlight: true,
                composerEmpty: true));
    }

    [Fact]
    public void Esc_on_an_empty_composer_at_rest_passes_through_to_drop_the_newest()
    {
        Assert.Equal(Gatto.Repl.Repl.CancelAction.PassThrough,
            Gatto.Repl.Repl.DecideCancel(ConsoleKey.Escape, hasSelection: false, turnInFlight: false,
                composerEmpty: true));
    }

    [Fact]
    public void A_live_selection_still_outranks_a_non_empty_composer()
    {
        Assert.Equal(Gatto.Repl.Repl.CancelAction.ClearSelection,
            Gatto.Repl.Repl.DecideCancel(ConsoleKey.Escape, hasSelection: true, turnInFlight: true,
                composerEmpty: false));
    }

    //first match wins: a selection, then a turn, then pass-through, with an empty composer (the composer rung has its own facts above)
    [Theory]
    [InlineData(true,  true,  (int)Gatto.Repl.Repl.CancelAction.ClearSelection)]   //a selection outranks every other rung.
    [InlineData(false, true,  (int)Gatto.Repl.Repl.CancelAction.AbortTurn)]
    [InlineData(false, false, (int)Gatto.Repl.Repl.CancelAction.PassThrough)]
    //the expected column is an int because CancelAction is internal and a public theory can't expose it (CS0051)
    public void The_escape_ladder_matches_the_spec_table(bool sel, bool turn, int expected) =>
        Assert.Equal((Gatto.Repl.Repl.CancelAction)expected,
            Gatto.Repl.Repl.DecideCancel(ConsoleKey.Escape, sel, turn, composerEmpty: true));

}
