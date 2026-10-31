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
        var row = Line(Info(), 200, new BackgroundFacts(shells, watch, paused));
        if (words is null) Assert.DoesNotContain("shell", row, StringComparison.Ordinal);
        else Assert.Contains("ctx 50% (50k) · " + words + " · max 18 left · $0.42", row, StringComparison.Ordinal);
    }

    //never shed: every rung keeps it, and the floor drops the head before it touches the tail
    [Fact]
    public void The_cell_survives_every_rung_and_the_floor()
    {
        var facts = new BackgroundFacts(2, true, false);
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
        Assert.EndsWith("↓ selects footer", Line(s, 200, new BackgroundFacts(1, false, false)), StringComparison.Ordinal);
        Assert.EndsWith("Enter opens", Line(s, 200, new BackgroundFacts(1, false, false, StatusCell.Shells)), StringComparison.Ordinal);
        Assert.EndsWith("v selects footer", Line(s, 200, new BackgroundFacts(1, false, false), GlyphSet.Ascii), StringComparison.Ordinal);

        //beside the paste hint it is the second, reached by the triangle, and an index past the list clamps to it
        var both = s with { Hints = [new StatusHint(HintKind.Paste, ["Alt+V to paste the image on your clipboard"])], HintIndex = 1 };
        Assert.EndsWith($"↓ selects footer {U.Triangle}", Line(both, 200, new BackgroundFacts(1, false, false)), StringComparison.Ordinal);
        Assert.Contains("Alt+V", Line(both with { HintIndex = 0 }, 200, new BackgroundFacts(1, false, false)), StringComparison.Ordinal);
    }

    //a cell a click acts on wears a colour at rest, the shells in PowerShell's yellow and the watch in the role tint, dim only between them
    [Fact]
    public void The_cell_wears_its_inks_at_rest_and_never_the_dim()
    {
        var row = InputFrame.BuildStatusLine(Info(), 200, T, U, new BackgroundFacts(2, true, false));
        Assert.Contains(T.Paint("2 shells", Theme.CampbellBrightYellow) + T.Paint(" · ", Theme.Dim) + T.Paint("watch", T.RoleTint("coder")), row, StringComparison.Ordinal);
        Assert.DoesNotContain(T.Paint("2 shells", Theme.Dim), row, StringComparison.Ordinal);
        Assert.DoesNotContain(Ansi.Bg(T.Map(Theme.UserInputBg), T.TrueColor), row, StringComparison.Ordinal);
        Assert.DoesNotContain(Ansi.Bg(T.BandOf(Theme.CampbellBrightYellow), T.TrueColor), row, StringComparison.Ordinal);
    }

    //the selected or hovered cell alone sits on the band its own ink makes, never the user's band, and the words stay
    [Theory]
    [InlineData(StatusCell.Shells, null)]
    [InlineData(null, StatusCell.Shells)]
    [InlineData(StatusCell.Watch, null)]
    [InlineData(null, StatusCell.Watch)]
    public void The_selected_or_hovered_cell_alone_wears_its_band(StatusCell? selected, StatusCell? hovered)
    {
        var row = InputFrame.BuildStatusLine(Info(), 200, T, U, new BackgroundFacts(2, true, false, selected, hovered));
        string On(string word, RgbColor ink)
        {
            var band = Ansi.Bg(T.BandOf(ink), T.TrueColor);
            return band + T.Paint(word, ink).Replace(Ansi.Reset, Ansi.Reset + band) + Ansi.Reset;
        }
        var shells = (selected ?? hovered) == StatusCell.Shells;
        var expected = (shells ? On("2 shells", Theme.CampbellBrightYellow) : T.Paint("2 shells", Theme.CampbellBrightYellow))
            + T.Paint(" · ", Theme.Dim) + (shells ? T.Paint("watch", T.RoleTint("coder")) : On("watch", T.RoleTint("coder")));
        Assert.Contains(expected, row, StringComparison.Ordinal);
        Assert.DoesNotContain(Ansi.Bg(T.Map(Theme.UserInputBg), T.TrueColor), row, StringComparison.Ordinal);
        Assert.Equal(StripSgr(InputFrame.BuildStatusLine(Info(), 200, T, U, new BackgroundFacts(2, true, false, selected))), StripSgr(row));
    }

    //the band is the ink a quarter and the ground three quarters, one constant for both cells, and the light theme mixes its own ink toward white
    [Fact]
    public void The_band_is_the_ink_mixed_toward_the_ground_by_one_share()
    {
        Assert.Equal(0.25, Theme.BandInkShare);
        var y = Theme.CampbellBrightYellow;
        var dark = T.BandOf(y);
        Assert.Equal(((byte)71, (byte)69, (byte)50), (dark.R, dark.G, dark.B));   //0xF9F1A5 a quarter, 0x0C0C0C the rest
        var light = new Theme(new TermCaps(true, true), ThemeMode.Light);
        var ly = light.Map(y);
        Assert.Equal(((byte)Math.Round(ly.R * 0.25 + 255 * 0.75, MidpointRounding.AwayFromZero), (byte)Math.Round(ly.G * 0.25 + 255 * 0.75, MidpointRounding.AwayFromZero),
            (byte)Math.Round(ly.B * 0.25 + 255 * 0.75, MidpointRounding.AwayFromZero)), (light.BandOf(y).R, light.BandOf(y).G, light.BandOf(y).B));
        var row = InputFrame.BuildStatusLine(Info(), 200, light, U, new BackgroundFacts(1, false, false, StatusCell.Shells));
        Assert.Contains(Ansi.Bg(light.BandOf(y), true) + light.Paint("1 shell", y), row, StringComparison.Ordinal);
    }

    //the hint keeps four cells from the core at every width, taking its shorter form and then none before the gap closes under four
    [Fact]
    public void The_hint_keeps_four_cells_from_the_core()
    {
        var s = Info() with { Hints = [new StatusHint(HintKind.Paste, ["Alt+V to paste the image on your clipboard", "Alt+V pastes the image"])] };
        var facts = new BackgroundFacts(0, true, false);
        var forms = new HashSet<string>();
        foreach (var w in Enumerable.Range(60, 141))
        {
            var row = Line(s, w, facts);
            var cells = InputFrame.StatusCellsOf(s, w, T, U, facts);
            if (cells.FirstOrDefault(c => c.Kind == StatusCell.Hint) is not { End: > 0 } hint) continue;
            var before = cells.Where(c => c.Kind is not (StatusCell.Hint or StatusCell.HintNext)).Max(c => c.End);
            Assert.True(hint.Start - before >= InputFrame.HintGap, $"gap {hint.Start - before} at {w}: {row}");
            forms.Add(row[(row.Length - UnicodeWidth.Of(row) + hint.Start)..].Replace(U.Triangle, "").Trim());
        }
        Assert.Contains("Alt+V pastes the image", forms);
        Assert.Contains("Alt+V to paste the image on your clipboard", forms);
    }

    //the map names each cell's columns, the separator between them a part of neither, so a press resolves to one without counting
    [Fact]
    public void The_cell_map_names_each_cell()
    {
        var facts = new BackgroundFacts(2, true, false);
        var row = Line(Info(), 200, facts);
        var cells = InputFrame.StatusCellsOf(Info(), 200, T, U, facts);
        var shells = cells.Single(c => c.Kind == StatusCell.Shells);
        var watch = cells.Single(c => c.Kind == StatusCell.Watch);
        Assert.Equal(UnicodeWidth.Of("2 shells"), shells.End - shells.Start);
        Assert.Equal(UnicodeWidth.Of("watch"), watch.End - watch.Start);
        Assert.Equal(UnicodeWidth.Of(" · "), watch.Start - shells.End);
        Assert.True(shells.Start > row.IndexOf("ctx", StringComparison.Ordinal));
        Assert.Equal([StatusCell.Shells, StatusCell.Watch], facts.Cells);
        Assert.Equal([StatusCell.Watch], new BackgroundFacts(0, true, false).Cells);
        Assert.Empty(new BackgroundFacts(0, false, false).Cells);
    }

    //two composes of one record and one frozen set of facts give one row
    [Fact]
    public void Two_composes_of_one_tuple_give_one_row()
    {
        var facts = new BackgroundFacts(1, true, true, StatusCell.Watch);
        foreach (var w in new[] { 70, 90, 120, 188 })
            Assert.Equal(InputFrame.BuildStatusLine(Info(), w, T, U, facts), InputFrame.BuildStatusLine(Info(), w, T, U, facts));
    }
}
