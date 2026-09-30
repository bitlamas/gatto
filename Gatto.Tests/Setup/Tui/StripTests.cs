using Gatto.Terminal;

namespace Gatto.Tests.Setup.Tui;

//the expected rows come out of the golden corpus, a hand-typed expectation is the author's own model of the screen and proves nothing
public class StripTests
{
    private static readonly StripSection Machine = new("machine", StripState.Done);
    private static readonly StripSection Engine = new("engine", StripState.Done);

    private static StripSection At(string name, StripState s) => new(name, s);

    [Fact]
    public void The_walk_opens_with_the_cursor_on_the_first_section()
    {
        //the welcome map's first row
        Assert.Equal("  ❯ machine · engine · model · check · done", Strip.Compose([
            At("machine", StripState.Current), At("engine", StripState.Pending),
            At("model", StripState.Pending), At("check", StripState.Pending),
            At("done", StripState.Pending)]));
    }

    [Fact]
    public void An_answered_section_carries_its_tick_and_the_current_one_the_cursor()
    {
        //the shelf screen, row 2
        Assert.Equal("  machine ✓ · engine ✓ · ❯ model · check · done", Strip.Compose([
            Machine, Engine, At("model", StripState.Current),
            At("check", StripState.Pending), At("done", StripState.Pending)]));
    }

    [Fact]
    public void The_connect_road_swaps_engine_and_model_for_one_server_section()
    {
        //the road re-forms the strip instead of greying the old rows out
        Assert.Equal("  machine ✓ · ❯ server · check · done", Strip.Compose([
            Machine, At("server", StripState.Current),
            At("check", StripState.Pending), At("done", StripState.Pending)]));
    }

    //when the strip has the keys the focused section takes the cursor and the current one gives it up. model is current and unmarked here, one cursor per row
    [Fact]
    public void FOCUS_takes_the_cursor_from_the_current_section()
    {
        //the strip-states demo's third row, the state after tabbing back to engine
        Assert.Equal("  machine ✓ · ❯ engine ✓ · model · check · done", Strip.Compose([
            Machine, Engine, At("model", StripState.Current),
            At("check", StripState.Pending), At("done", StripState.Pending)], focused: 1));
    }

    //without this, a Compose painting both the focused and the current section would satisfy every other test here
    [Fact]
    public void A_row_never_carries_two_cursors()
    {
        var row = Strip.Compose([
            Machine, Engine, At("model", StripState.Current),
            At("check", StripState.Pending), At("done", StripState.Pending)], focused: 0);

        Assert.Equal(1, row.Split("❯").Length - 1);
        Assert.Equal("  ❯ machine ✓ · engine ✓ · model · check · done", row);
    }

    [Fact]
    public void A_focused_section_that_is_DONE_keeps_its_tick()
    {
        //the cursor says where you are, the tick says what happened
        Assert.Contains("❯ engine ✓", Strip.Compose([Machine, Engine], focused: 1), StringComparison.Ordinal);
    }

    //chrome that can crash the screen it decorates is worse than chrome that is briefly wrong, so an out-of-range focus degrades to no focus
    [Theory]
    [InlineData(-1)]
    [InlineData(99)]
    public void An_out_of_range_focus_falls_back_to_the_current_section(int focused)
    {
        Assert.Equal("  machine ✓ · ❯ engine · model · check · done", Strip.Compose([
            Machine, At("engine", StripState.Current), At("model", StripState.Pending),
            At("check", StripState.Pending), At("done", StripState.Pending)], focused));
    }

    //every distinct strip row in the corpus must come back out of Compose. a test on one row cannot see a state the screens use and the composer forgot
    [Fact]
    public void EVERY_strip_row_in_the_golden_corpus_is_reproducible()
    {
        var goldens = Path.Combine(AppContext.BaseDirectory, "Setup", "Tui", "Goldens");
        var rows = Directory.GetFiles(goldens, "*.txt")
            .SelectMany(f => File.ReadAllText(f).Split('\n'))
            .Select(l => l.TrimEnd())
            .Where(l => l.StartsWith("  machine", StringComparison.Ordinal)
                     || l.StartsWith("  ❯ machine", StringComparison.Ordinal))
            .Where(l => l.Contains(" · ", StringComparison.Ordinal) && l.Contains("done", StringComparison.Ordinal))
            .Select(l => l.Split("   ")[0].TrimEnd())      //the demo screen's caption column
            .Distinct()
            .ToArray();

        Assert.NotEmpty(rows);
        foreach (var row in rows)
        {
            var names = row.Trim().Replace("❯ ", "").Split(" · ")
                .Select(p => p.Replace(" ✓", "")).ToArray();
            var cursorAt = row.Trim().Split(" · ").ToList().FindIndex(p => p.StartsWith("❯", StringComparison.Ordinal));
            var sections = row.Trim().Split(" · ")
                .Select((p, i) => new StripSection(names[i],
                    p.Contains('✓') ? StripState.Done
                    : i == cursorAt ? StripState.Current : StripState.Pending))
                .ToList();

            //focus is only distinguishable from current when the cursor sits on a done section
            var focused = cursorAt >= 0 && sections[cursorAt].State == StripState.Done ? cursorAt : -1;
            Assert.Equal(row, Strip.Compose(sections, focused));
        }
    }
}
