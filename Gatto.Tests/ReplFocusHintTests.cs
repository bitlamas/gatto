using Gatto.Repl;
using Gatto.Repl.Input;
using Gatto.Terminal;

namespace Gatto.Tests;

public class ReplFocusHintTests
{
    private static readonly GlyphSet U = GlyphSet.Unicode;

    private static (IReadOnlyList<StatusHint> Hints, int Index, bool Taught) Pick(bool paste = false, bool focused = false,
        bool collapsed = false, bool taught = false, HintKind[]? last = null, int wanted = 0) =>
        Gatto.Repl.Repl.PickHints(paste, focused, collapsed, taught, last ?? [], wanted, [], U);

    //focus gives the focus hint, a collapsed block with the latch unset gives the teaser, both from the one keymap
    [Fact]
    public void ComputeFocusHint_gives_the_focus_hint_or_the_teaser_in_two_forms()
    {
        var focus = Gatto.Repl.Repl.ComputeFocusHint(focused: true, anyReasoningCollapsed: true, taught: true, U);
        Assert.Equal(HintKind.Focus, focus!.Kind);
        Assert.Equal([FocusKeys.HintOf(U), FocusKeys.ShortHintOf(U)], focus.Forms);

        Assert.Equal(HintKind.Teaser, Gatto.Repl.Repl.ComputeFocusHint(false, true, taught: false, U)!.Kind);
        Assert.Null(Gatto.Repl.Repl.ComputeFocusHint(false, true, taught: true, U));
        Assert.Null(Gatto.Repl.Repl.ComputeFocusHint(false, false, taught: false, U));
    }

    //the short forms keep the three keys, and the ASCII set spells the arrows as words
    [Fact]
    public void The_short_forms_come_from_the_one_keymap()
    {
        Assert.Equal("Ctrl+↑/↓ moves · Ctrl+R toggles", FocusKeys.ShortHintOf(U));
        Assert.Equal("Ctrl+Up/Down moves . Ctrl+R toggles", FocusKeys.ShortHintOf(GlyphSet.Ascii));
        Assert.Equal("Alt+V pastes the image", Gatto.Repl.Repl.PasteHintShort);
    }

    //the paste hint first, then the focus hint, with the teaser only while nothing is focused
    [Fact]
    public void The_list_is_in_the_ruled_order()
    {
        Assert.Equal([HintKind.Paste, HintKind.Focus], Pick(paste: true, focused: true, collapsed: true).Hints.Select(h => h.Kind));
        Assert.Equal([HintKind.Paste, HintKind.Teaser], Pick(paste: true, collapsed: true).Hints.Select(h => h.Kind));
        Assert.Empty(Pick().Hints);
    }

    //the chosen hint is matched by its kind, so a hint that stays keeps its place even when the list around it moves
    [Fact]
    public void The_index_is_kept_by_kind_and_reset_when_the_hint_goes()
    {
        //the focus hint was chosen at index 1 beside paste, and the paste hint has gone, so it is now at 0
        var kept = Pick(focused: true, last: [HintKind.Paste, HintKind.Focus], wanted: 1);
        Assert.Equal(0, kept.Index);
        Assert.Equal(HintKind.Focus, kept.Hints[kept.Index].Kind);

        //paste was chosen and stays listed, so it stays chosen
        var stays = Pick(paste: true, focused: true, last: [HintKind.Paste, HintKind.Focus], wanted: 0);
        Assert.Equal(0, stays.Index);

        //the chosen focus hint has gone, the index resets to the first hint
        var gone = Pick(paste: true, last: [HintKind.Paste, HintKind.Focus], wanted: 1);
        Assert.Equal(0, gone.Index);
    }

    //the teaser stays listed through every triangle press and latches on the first focus of a block
    [Fact]
    public void The_teaser_latches_on_the_first_focus_and_never_on_a_triangle_press()
    {
        var first = Pick(collapsed: true);
        Assert.Equal(HintKind.Teaser, first.Hints[first.Index].Kind);
        Assert.False(first.Taught);

        //the triangle steps from the teaser to the paste hint and back, and both stay listed each way
        var away = Pick(paste: true, collapsed: true, last: [HintKind.Paste, HintKind.Teaser], wanted: 0);
        Assert.False(away.Taught);
        Assert.Equal([HintKind.Paste, HintKind.Teaser], away.Hints.Select(h => h.Kind));
        var back = Pick(paste: true, collapsed: true, last: [HintKind.Paste, HintKind.Teaser], wanted: 1);
        Assert.False(back.Taught);
        Assert.Equal(HintKind.Teaser, back.Hints[back.Index].Kind);

        //a focus latches it whatever was chosen, and a later unfocused refresh brings no teaser back
        var focused = Pick(paste: true, focused: true, collapsed: true, last: [HintKind.Paste, HintKind.Teaser], wanted: 0);
        Assert.True(focused.Taught);
        Assert.Equal([HintKind.Paste], Pick(paste: true, collapsed: true, taught: true).Hints.Select(h => h.Kind));
    }
}
