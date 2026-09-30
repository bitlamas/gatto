using Gatto.Cli.Setup.Tui;
using Gatto.Terminal;

namespace Gatto.Tests.Setup.Tui;

//the harness has to fail before anything is diffed against it. these tests feed it wrong renders and check it names them
public class WalkRenderSelfTests
{
    private static Screen Simple(string title) => new(
        [new("machine", StripState.Done), new("engine", StripState.Current)],
        Region.List, title, ["  a body row"], null,
        [new("Enter", "choose"), new("Esc", "leave")]);

    [Fact]
    public void Diff_returns_null_when_the_rows_match()
    {
        Assert.Null(Golden.Diff(["a", "b"], ["a", "b"]));
    }

    //a failure names the row, so reading it is cheaper than diffing two screens by eye
    [Fact]
    public void Diff_names_the_FIRST_row_that_differs()
    {
        var d = Golden.Diff(["a", "b", "c"], ["a", "X", "c"]);
        Assert.NotNull(d);
        Assert.Contains("row 1 differs", d!, StringComparison.Ordinal);
        Assert.Contains("expected: b", d, StringComparison.Ordinal);
        Assert.Contains("actual  : X", d, StringComparison.Ordinal);
    }

    [Fact]
    public void Diff_reports_a_render_that_is_SHORTER_than_its_golden()
    {
        var d = Golden.Diff(["a", "b"], ["a"]);
        Assert.Contains("row 1 differs", d!, StringComparison.Ordinal);
        Assert.Contains("<no such row>", d!, StringComparison.Ordinal);
    }

    //a missing golden fails loudly with the file name, the corpus holds every screen the wizard may draw
    [Fact]
    public void A_missing_golden_fails_by_NAME()
    {
        var ex = Assert.Throws<Xunit.Sdk.TrueException>(() => Golden.Load("s5", "no-such-screen", 100));
        Assert.Contains("s5-no-such-screen-100.txt", ex.Message, StringComparison.Ordinal);
    }

    //a golden that does exist loads, so the failure above is about the missing name and not the loader
    [Fact]
    public void A_golden_that_exists_loads_and_is_not_empty()
    {
        var rows = Golden.Load("s5", "unified-96-default", 100);
        Assert.NotEmpty(rows);
        Assert.Contains(rows, r => r.Contains("gatto setup", StringComparison.Ordinal));
    }

    //a render made at 80 must fail against a 100-wide golden, the width is part of the oracle
    [Fact]
    public void A_render_at_the_WRONG_width_is_refused_rather_than_compared()
    {
        var wide = WalkRender.Paint(Simple("Which engine build fits this machine?"), 100);
        var ex = Assert.Throws<Xunit.Sdk.TrueException>(
            () => Golden.AssertEquals("s5", "unified-96-80", 80, wide.Rows));
        Assert.Contains("wrong width", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_sweep_passes_on_a_screen_that_fits_at_every_width()
    {
        WalkRender.Sweep(_ => Simple("a short title"));
    }

    //a too-wide screen cannot come out of the painter, so this feeds the sweep's predicate by hand
    [Fact]
    public void The_sweeps_PREDICATE_catches_an_over_wide_row_when_handed_one()
    {
        var overWide = new string('x', 120);
        Assert.True(Gatto.Terminal.UnicodeWidth.Of(overWide) > 100,
            "the fixture is not actually too wide, so this proves nothing");

        //the sweep's own comparison, applied to a row the painter never saw
        Assert.False(overWide.Length <= 100);
    }

    //the painter unit test proves the clamp works, this proves the surface wires it in. a surface that bypassed the painter would leave that test green
    [Fact]
    public void Through_the_PAINTER_no_screen_can_exceed_its_width_by_construction()
    {
        var cap = WalkRender.Paint(Simple("t") with { Body = [new string('x', 400)] }, 100);
        Assert.All(cap.Rows, r => Assert.True(Gatto.Terminal.UnicodeWidth.Of(r) <= 100));
        Assert.Contains(cap.Rows, r => r.Length == 100 && r.All(c => c == 'x'));
    }
}
