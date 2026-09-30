using Gatto.Repl.Render;

namespace Gatto.Tests.Render;

public class GutterWrapTaggedTests
{
    [Fact]
    public void RowsTagged_tags_row0_false_continuations_true_and_Text_matches_Rows()
    {
        //at width twelve the budget is ten cells, so the fourteen-cell text wraps to two rows.
        var tagged = GutterWrap.RowsTagged("● ", "● ", "aaaa bbbb cccc", 12);
        var plain = GutterWrap.Rows("● ", "● ", "aaaa bbbb cccc", 12);
        Assert.Equal(plain.Count, tagged.Count);
        Assert.True(tagged.Count >= 2);
        Assert.False(tagged[0].Continuation);
        Assert.All(tagged.Skip(1), r => Assert.True(r.Continuation));
        Assert.Equal(plain, tagged.Select(r => r.Text).ToList());   //the projected text must equal the plain rows exactly.
    }

    [Fact]
    public void RowsTagged_single_row_is_one_false()
    {
        var tagged = GutterWrap.RowsTagged("♯ ", "♯ ", "short", 80);
        Assert.Single(tagged);
        Assert.False(tagged[0].Continuation);
    }

    [Fact]
    public void RowsTagged_width_le_4_degrades_to_one_false_row()
    {
        var tagged = GutterWrap.RowsTagged("● ", "● ", "anything at all here", 4);
        Assert.Single(tagged);
        Assert.False(tagged[0].Continuation);
    }
}
