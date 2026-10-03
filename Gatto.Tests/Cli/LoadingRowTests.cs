using Gatto.Cli;
using Gatto.Terminal;

namespace Gatto.Tests.Cli;

//assert the whole string, a word match would pass across a rung boundary and prove nothing about which rung answered
public class LoadingRowTests
{
    private static readonly GlyphSet G = GlyphSet.Unicode;

    //59 against 60 and 119 against 120 tell a later rung from an earlier one. without a pair that disagrees, a chain in the wrong order still passes
    [Theory]
    [InlineData(0, "loading qwen3.6-35b-a3b…")]
    [InlineData(29, "loading qwen3.6-35b-a3b…")]
    [InlineData(30, "still loading qwen3.6-35b-a3b…")]
    [InlineData(59, "still loading qwen3.6-35b-a3b…")]
    [InlineData(60, "pumping qwen3.6-35b-a3b…")]
    [InlineData(119, "pumping qwen3.6-35b-a3b…")]
    [InlineData(120, "still pumping qwen3.6-35b-a3b, ~23.8 GB…")]
    [InlineData(200, "still pumping qwen3.6-35b-a3b, ~23.8 GB…")]
    public void THE_RUNGS_CHANGE_AT_30_60_AND_120_SECONDS(int seconds, string expected) =>
        Assert.Equal(expected,
            LoadingRow.Words(TimeSpan.FromSeconds(seconds), "qwen3.6-35b-a3b", "~23.8 GB", G));

    //the size is the only new fact at two minutes, and it explains a long wait rather than signalling trouble
    [Fact]
    public void THE_120_RUNG_NAMES_THE_SIZE() =>
        Assert.Equal("still pumping big-model, ~95.4 GB…",
            LoadingRow.Words(TimeSpan.FromSeconds(120), "big-model", "~95.4 GB", G));

    //a size that could not be measured is dropped rather than guessed, a wrong number is the one detail a reader would act on
    [Fact]
    public void THE_120_RUNG_FALLS_BACK_WITH_NO_SIZE() =>
        Assert.Equal("still pumping big-model…",
            LoadingRow.Words(TimeSpan.FromSeconds(120), "big-model", null, G));

    //a field that changed width would move every cell after it on each frame, which reads as the row jittering rather than as motion.
    [Theory]
    [InlineData(0, "~  ")]
    [InlineData(1, "~~ ")]
    [InlineData(2, "~~~")]
    [InlineData(3, "~  ")]
    public void THE_PULSE_IS_ALWAYS_THREE_CELLS(int frame, string expected)
    {
        Assert.Equal(expected, LoadingRow.Pulse(frame));
        Assert.Equal(3, LoadingRow.Pulse(frame).Length);
    }

    //the ellipsis comes from the table, so a console that cannot draw the glyph gets the row and not a tofu box
    [Fact]
    public void THE_ROW_TAKES_ITS_ELLIPSIS_FROM_THE_TABLE() =>
        Assert.Equal("loading m...", LoadingRow.Words(TimeSpan.Zero, "m", null, GlyphSet.Ascii));

    //the unpainted row holds no escape at all, and it is the painted row with the escapes stripped
    [Fact]
    public void THE_UNPAINTED_ROW_HOLDS_NO_ESCAPES()
    {
        var themed = LoadingRow.Render(
            TimeSpan.FromSeconds(31), "qwen3.6-35b-a3b", "~23.8 GB", G,
            new Theme(new TermCaps(true, true)), frame: 1);
        var plain = LoadingRow.Render(
            TimeSpan.FromSeconds(31), "qwen3.6-35b-a3b", "~23.8 GB", G, null, frame: 1);

        Assert.DoesNotContain('\u001b', plain);
        Assert.Contains('\u001b', themed);
        Assert.Equal(plain, TermText.StripAnsiForWidth(themed));
    }

    //assert the id's own ink, a row painted wholly in the body colour would also strip back to the same plain text
    [Fact]
    public void THE_MODEL_ID_TAKES_CODEINLINEFG_INSIDE_THE_ROW()
    {
        var theme = new Theme(new TermCaps(true, true));
        var painted = LoadingRow.Render(
            TimeSpan.Zero, "qwen3.6-35b-a3b", null, G, theme, frame: 0);

        Assert.Contains(theme.Paint("qwen3.6-35b-a3b", Theme.CodeInlineFg), painted, StringComparison.Ordinal);
    }

    //a null theme is the ordinary case for a pipe, and it must produce the same bytes through the same call
    [Fact]
    public void A_NULL_THEME_PAINTS_NOTHING()
    {
        var row = LoadingRow.Render(TimeSpan.Zero, "m", null, G, null, frame: 0);

        Assert.DoesNotContain('\u001b', row);
    }
}
