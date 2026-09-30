using Gatto.Cli.Setup;
using Gatto.Terminal;

namespace Gatto.Tests.Setup;

//an aside row's highlight must paint accent, the same ink every other page uses
public class WizardPaintInkTests
{
    private static readonly Theme T = new(new TermCaps(true, true));

    private static string Sgr(RgbColor c)
    {
        var painted = T.Paint("x", c);
        return painted[..painted.IndexOf('x')];
    }

    private static string Aside(string text, params string[] highlight) =>
        WizardPaint.Line(new WizardRow(text, Tone: RowTone.Aside, Highlight: highlight), T);

    [Fact]
    public void An_ASIDE_rows_highlight_paints_ACCENT_not_the_terminal_default()
    {
        var row = Aside("gatto tested llama-server.exe and it answered another way", "llama-server.exe");
        Assert.Contains(T.Paint("llama-server.exe", Theme.Accent), row, StringComparison.Ordinal);
    }

    //the highlight is the subject of the act, so the rest of the sentence stays dim
    [Fact]
    public void The_rest_of_an_aside_sentence_stays_dim()
    {
        var row = Aside("gatto tested llama-server.exe and it answered", "llama-server.exe");
        Assert.Contains(Sgr(Theme.Dim), row, StringComparison.Ordinal);
    }

    //an aside with no highlight carries no accent, so the check above is about the highlight rather than the theme
    [Fact]
    public void An_aside_with_no_highlight_carries_no_accent()
    {
        Assert.DoesNotContain(Sgr(Theme.Accent), Aside("nothing to act on here"), StringComparison.Ordinal);
    }
}
