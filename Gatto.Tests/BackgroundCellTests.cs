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
        Assert.EndsWith("↓ selects footer", Line(s, 200, new BackgroundFacts(1, false, false, false)), StringComparison.Ordinal);
        Assert.EndsWith("Enter opens", Line(s, 200, new BackgroundFacts(1, false, false, true)), StringComparison.Ordinal);
        Assert.EndsWith("v selects footer", Line(s, 200, new BackgroundFacts(1, false, false, false), GlyphSet.Ascii), StringComparison.Ordinal);

        //beside the paste hint it is the second, reached by the triangle, and an index past the list clamps to it
        var both = s with { Hints = [new StatusHint(HintKind.Paste, ["Alt+V to paste the image on your clipboard"])], HintIndex = 1 };
        Assert.EndsWith($"↓ selects footer {U.Triangle}", Line(both, 200, new BackgroundFacts(1, false, false, false)), StringComparison.Ordinal);
        Assert.Contains("Alt+V", Line(both with { HintIndex = 0 }, 200, new BackgroundFacts(1, false, false, false)), StringComparison.Ordinal);
    }

    //a cell a click acts on wears a colour at rest, the shells in PowerShell's yellow and the watch in the role tint, dim only between them
    [Fact]
    public void The_cell_wears_its_inks_at_rest_and_never_the_dim()
    {
        var row = InputFrame.BuildStatusLine(Info(), 200, T, U, new BackgroundFacts(2, true, false, false));
        Assert.Contains(T.Paint("2 shells", Theme.CampbellBrightYellow) + T.Paint(" · ", Theme.Dim) + T.Paint("watch", T.RoleTint("coder")), row, StringComparison.Ordinal);
        Assert.DoesNotContain(T.Paint("2 shells", Theme.Dim), row, StringComparison.Ordinal);
        Assert.DoesNotContain(Ansi.Bg(T.Map(Theme.UserInputBg), T.TrueColor), row, StringComparison.Ordinal);
        Assert.DoesNotContain(Ansi.Bg(T.BandOf(Theme.CampbellBrightYellow), T.TrueColor), row, StringComparison.Ordinal);
    }

    //when Down selects the cell or the pointer rests on it each word sits on the band its own ink makes, never the user's band, and the words stay
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void The_selected_or_hovered_cell_puts_each_word_on_its_own_band(bool selected, bool hovered)
    {
        var row = InputFrame.BuildStatusLine(Info(), 200, T, U, new BackgroundFacts(2, true, false, selected, hovered));
        string On(string word, RgbColor ink)
        {
            var band = Ansi.Bg(T.BandOf(ink), T.TrueColor);
            return band + T.Paint(word, ink).Replace(Ansi.Reset, Ansi.Reset + band) + Ansi.Reset;
        }
        Assert.Contains(On("2 shells", Theme.CampbellBrightYellow) + T.Paint(" · ", Theme.Dim) + On("watch", T.RoleTint("coder")), row, StringComparison.Ordinal);
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
        var row = InputFrame.BuildStatusLine(Info(), 200, light, U, new BackgroundFacts(1, false, false, true));
        Assert.Contains(Ansi.Bg(light.BandOf(y), true) + light.Paint("1 shell", y), row, StringComparison.Ordinal);
    }

    //the hint keeps four cells from the core at every width, taking its shorter form and then none before the gap closes under four
    [Fact]
    public void The_hint_keeps_four_cells_from_the_core()
    {
        var s = Info() with { Hints = [new StatusHint(HintKind.Paste, ["Alt+V to paste the image on your clipboard", "Alt+V pastes the image"])] };
        var facts = new BackgroundFacts(0, true, false, false);
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
