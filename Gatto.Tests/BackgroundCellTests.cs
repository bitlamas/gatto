using Gatto.Core.Client;
using Gatto.Repl.Input;
using Gatto.Terminal;

namespace Gatto.Tests;

//the status line's background cell: its words, its place in the never-shed tail, its hint and its selected ink, all read from facts at compose
public class BackgroundCellTests
{
    private static readonly Theme T = new(new TermCaps(true, true));
    private static readonly GlyphSet U = GlyphSet.Unicode;

    private static string StripSgr(string s) => System.Text.RegularExpressions.Regex.Replace(s, "\u001b\\[[0-9;]*m", "");

    private static CtxState Ctx(int used, int window)
    {
        var ctx = new CtxState { ContextWindow = window };
        ctx.RecordUsedTokens(used);
        return ctx;
    }

    private static StatusInfo Info() => new(@"C:\Users\user\projects\gatto", "qwen3.6-35b", "coder", Ctx(50_000, 100_000),
        @"C:\Users\user", Branch: "main", Thinking: "high", Cloud: true, Quota: new QuotaReading(18, "max"), Cost: 0.42m);

    private static string Line(StatusInfo s, int width, BackgroundFacts facts, GlyphSet? g = null) =>
        StripSgr(InputFrame.BuildStatusLine(s, width, T, g ?? U, facts));

    [Theory]
    [InlineData(0, false, false, null)]
    [InlineData(1, false, false, "1 shell")]
    [InlineData(2, false, false, "2 shells")]
    [InlineData(0, true, false, "watch")]
    [InlineData(2, true, false, "2 shells · watch")]
    [InlineData(1, true, true, "1 shell · watch paused")]
    public void The_cell_names_what_runs(int shells, bool watch, bool paused, string? words)
    {
        Assert.Equal(words, InputFrame.BackgroundCell(new BackgroundFacts(shells, watch, paused, false), U));
        var row = Line(Info(), 200, new BackgroundFacts(shells, watch, paused, false));
        if (words is null) Assert.DoesNotContain("shell", row, StringComparison.Ordinal);
        else Assert.Contains("ctx 50% (50.0k) · " + words + " · max 18 left · $0.42", row, StringComparison.Ordinal);
    }

    //never shed: every rung keeps it, and the floor drops the head before it touches the tail
    [Fact]
    public void The_cell_survives_every_rung_and_the_floor()
    {
        var facts = new BackgroundFacts(2, true, false, false);
        foreach (var w in Enumerable.Range(60, 141))
        {
            var row = Line(Info(), w, facts);
            Assert.True(UnicodeWidth.Of(row) <= w, $"{UnicodeWidth.Of(row)} cells at {w}: {row}");
            Assert.Contains("2 shells · watch", row, StringComparison.Ordinal);
            Assert.Contains("(high)", row, StringComparison.Ordinal);
        }
    }

    //the background hint is listed exactly while the cell is on, last, in the state the composer or the selection gives it
    [Fact]
    public void The_background_hint_follows_the_cell_and_the_selection()
    {
        var s = Info() with { Cost = null, Quota = null };
        Assert.DoesNotContain("background", Line(s, 200, default), StringComparison.Ordinal);
        Assert.EndsWith("Down selects what runs in the background", Line(s, 200, new BackgroundFacts(1, false, false, false)), StringComparison.Ordinal);
        Assert.EndsWith("Enter opens what runs in the background", Line(s, 200, new BackgroundFacts(1, false, false, true)), StringComparison.Ordinal);

        //beside the paste hint it is the second, reached by the triangle, and an index past the list clamps to it
        var both = s with { Hints = [new StatusHint(HintKind.Paste, ["Alt+V to paste the image on your clipboard"])], HintIndex = 1 };
        Assert.EndsWith($"Down selects what runs in the background {U.Triangle}", Line(both, 200, new BackgroundFacts(1, false, false, false)), StringComparison.Ordinal);
        Assert.Contains("Alt+V", Line(both with { HintIndex = 0 }, 200, new BackgroundFacts(1, false, false, false)), StringComparison.Ordinal);
    }

    //a selected cell sits on the band a hovered footer word takes, in the accent, and an unselected one stays dim
    [Fact]
    public void The_selected_cell_paints_on_the_band_in_the_accent()
    {
        var on = InputFrame.BuildStatusLine(Info(), 200, T, U, new BackgroundFacts(1, false, false, true));
        var off = InputFrame.BuildStatusLine(Info(), 200, T, U, new BackgroundFacts(1, false, false, false));
        Assert.Contains(InputFrame.SelectedCell("1 shell", T), on, StringComparison.Ordinal);
        Assert.StartsWith(Ansi.Bg(T.Map(Theme.UserInputBg), T.TrueColor), InputFrame.SelectedCell("1 shell", T), StringComparison.Ordinal);
        Assert.Contains(T.Paint("1 shell", Theme.Dim), off, StringComparison.Ordinal);
    }

    //the map names the cell's columns, so a press on it resolves without counting
    [Fact]
    public void The_cell_map_names_the_cell()
    {
        var facts = new BackgroundFacts(2, false, false, false);
        var row = Line(Info(), 200, facts);
        var cell = InputFrame.StatusCellsOf(Info(), 200, T, U, facts).Single(c => c.Kind == StatusCell.Background);
        Assert.Equal(cell.End - cell.Start, UnicodeWidth.Of("2 shells"));
        Assert.True(cell.Start > row.IndexOf("ctx", StringComparison.Ordinal));
    }

    //two composes of one record and one frozen set of facts give one row
    [Fact]
    public void Two_composes_of_one_tuple_give_one_row()
    {
        var facts = new BackgroundFacts(1, true, true, true);
        foreach (var w in new[] { 70, 90, 120, 188 })
            Assert.Equal(InputFrame.BuildStatusLine(Info(), w, T, U, facts), InputFrame.BuildStatusLine(Info(), w, T, U, facts));
    }
}
