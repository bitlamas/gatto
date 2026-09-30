using Gatto.Repl;
using Gatto.Terminal;

namespace Gatto.Tests;

public class ReplFocusHintTests
{
    //the derivation behind RefreshStatus shows the hint when focused and teases once on the first collapse
    [Fact]
    public void ComputeFocusHint_shows_when_focused_and_teases_once_on_first_collapse()
    {
        var taught = false;

        //focus shows the hint but must not consume the one-shot teaser.
        Assert.Equal(FocusKeys.HintOf(glyphs: GlyphSet.Unicode), Gatto.Repl.Repl.ComputeFocusHint(focused: true, anyReasoningCollapsed: true, ref taught, glyphs: GlyphSet.Unicode));
        Assert.False(taught);

        Assert.Null(Gatto.Repl.Repl.ComputeFocusHint(focused: false, anyReasoningCollapsed: false, ref taught, glyphs: GlyphSet.Unicode));

        //the first unfocused collapse fires the teaser exactly once and latches the flag.
        Assert.Equal(FocusKeys.HintOf(glyphs: GlyphSet.Unicode), Gatto.Repl.Repl.ComputeFocusHint(focused: false, anyReasoningCollapsed: true, ref taught, glyphs: GlyphSet.Unicode));
        Assert.True(taught);
        Assert.Null(Gatto.Repl.Repl.ComputeFocusHint(focused: false, anyReasoningCollapsed: true, ref taught, glyphs: GlyphSet.Unicode));   //the latched teaser must not fire again.

        //the teaser latch must not suppress the hint on later focus.
        Assert.Equal(FocusKeys.HintOf(glyphs: GlyphSet.Unicode), Gatto.Repl.Repl.ComputeFocusHint(focused: true, anyReasoningCollapsed: true, ref taught, glyphs: GlyphSet.Unicode));
    }
}
