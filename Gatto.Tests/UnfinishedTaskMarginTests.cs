using Gatto.Core.Acquire;
using Gatto.Roles.Audition;

namespace Gatto.Tests;

//the margin says when a missed task was cut short, read from the shapes already in the record
public class UnfinishedTaskMarginTests : IDisposable
{
    private readonly string _home = Path.Combine(
        Path.GetTempPath(), "gatto-t16-" + Guid.NewGuid().ToString("N"));

    public UnfinishedTaskMarginTests() => Directory.CreateDirectory(Path.Combine(_home, "audition"));

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch (Exception) { } //best effort
        GC.SuppressFinalize(this);
    }

    //writes a real badge file rather than a mock, so the verdict clause and the register's own filename both matter
    private Badge? Write(string key, string tasksJson)
    {
        var json = "{\"model_key\":\"" + key + "\",\"verdict\":\"pass\",\"battery_version\":4,\"measured\":\"2026-08-13\","
            + "\"gatto_build\":\"abc1234\",\"sampling_note\":\"vendor\",\"tasks\":" + tasksJson + "}";
        File.WriteAllText(Path.Combine(_home, "audition", BadgeRegister.FileNameFor(key)), json);
        return BadgeRegister.Lookup(_home, key);
    }

    [Fact]
    public void A_TASK_CUT_SHORT_BY_THE_WATCHDOG_IS_COUNTED_AS_UNFINISHED()
    {
        var badge = Write("o/cut",
            "[{\"pass\":true},{\"pass\":true},{\"pass\":true},{\"pass\":true},"
            + "{\"pass\":false,\"shapes\":[\"StoppedAtCap\"]}]");

        Assert.Equal(4, badge!.Passed);
        Assert.Equal(5, badge.Ran);
        Assert.Equal(1, badge.Unfinished);
    }

    [Fact]
    public void A_TASK_ANSWERED_WRONG_IS_NOT_UNFINISHED()
    {
        //both records read 4 of 5, and only one means the model was given all five tasks
        var badge = Write("o/wrong",
            "[{\"pass\":true},{\"pass\":true},{\"pass\":true},{\"pass\":true},"
            + "{\"pass\":false,\"shapes\":[\"NoToolCall\"]}]");

        Assert.Equal(4, badge!.Passed);
        Assert.Equal(0, badge.Unfinished);
    }

    [Fact]
    public void A_STALLED_TASK_COUNTS_TOO_because_nothing_was_measured_there_either()
    {
        var badge = Write("o/stalled", "[{\"pass\":true},{\"pass\":false,\"shapes\":[\"Stalled\"]}]");
        Assert.Equal(1, badge!.Unfinished);
    }

    [Fact]
    public void RUNAWAY_IS_NOT_COUNTED_because_the_SERVER_ended_it()
    {
        //the server ended this one after output, so it is not counted as unfinished
        var badge = Write("o/runaway", "[{\"pass\":true},{\"pass\":false,\"shapes\":[\"Runaway\"]}]");
        Assert.Equal(0, badge!.Unfinished);
    }

    [Fact]
    public void A_PASSING_TASK_CARRYING_A_SHAPE_IS_NEVER_UNFINISHED()
    {
        //otherwise the margin could claim an unfinished task on a run with nothing missing
        var badge = Write("o/passed",
            "[{\"pass\":true,\"shapes\":[\"StoppedAtCap\"]},{\"pass\":true}]");

        Assert.Equal(2, badge!.Passed);
        Assert.Equal(0, badge.Unfinished);
    }

    [Fact]
    public void A_SCHEMA_1_BADGE_WITH_NO_SHAPES_READS_AS_NONE_rather_than_failing()
    {
        //an old record with no shapes array reads as nobody was told, so no qualifier shows
        var badge = Write("o/old", "[{\"pass\":true},{\"pass\":false}]");
        Assert.Equal(0, badge!.Unfinished);
    }

    //the boundary the architecture forces

    [Fact]
    public void THE_SHAPE_NAMES_CROSSING_INTO_CORE_ARE_REAL_ENUM_MEMBERS()
    {
        //the core project cannot reference Gatto.Roles, so the shapes are matched by name and this test catches a rename
        Assert.True(Enum.IsDefined(typeof(FailureShape), nameof(FailureShape.StoppedAtCap)));
        Assert.True(Enum.IsDefined(typeof(FailureShape), nameof(FailureShape.Stalled)));
        Assert.Equal("StoppedAtCap", FailureShape.StoppedAtCap.ToString());
        Assert.Equal("Stalled", FailureShape.Stalled.ToString());

        //the writer serializes with ToString, so the check reads the real wire format
        Assert.Equal("Runaway", FailureShape.Runaway.ToString());
    }

    //the sentence

    [Fact]
    public void THE_MARGIN_SAYS_UNFINISHED_and_never_grades_the_model()
    {
        var row = new ShelfRow("o/m", "o", new HubQuant("m-Q4_K_M.gguf", 4_000_000_000, null),
            Gatto.Core.Models.FitRegime.FitsGpu, 32768, false,
            new Badge("o/m", new DateOnly(2026, 8, 13), "abc", "vendor", Passed: 4, Ran: 5, Unfinished: 1),
            10, false);

        //assert on the pane the shelf really draws, a surface nobody calls proves nothing
        var detail = string.Join(" · ", Gatto.Cli.Setup.Tui.Pane
            .Rows(row, null, Gatto.Core.Hardware.MachineShape.Discrete, 100,
                glyphs: Gatto.Terminal.GlyphSet.Unicode).Select(x => x.Text));

        Assert.Contains("4 of 5 tasks, one unfinished", detail, StringComparison.Ordinal);
        //no bare fraction and no verdict word in the pane text
        Assert.DoesNotContain("4/5", detail, StringComparison.Ordinal);
        foreach (var banned in new[] { "failed", "bad", "worse", "recommended" })
            Assert.DoesNotContain(banned, detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_CLEAN_SWEEP_STILL_SAYS_NOTHING_EXTRA()
    {
        var row = new ShelfRow("o/m", "o", new HubQuant("m-Q4_K_M.gguf", 4_000_000_000, null),
            Gatto.Core.Models.FitRegime.FitsGpu, 32768, false,
            new Badge("o/m", new DateOnly(2026, 8, 13), "abc", "vendor", Passed: 5, Ran: 5), 10, false);

        //assert on the pane the shelf really draws, a surface nobody calls proves nothing
        var detail = string.Join(" · ", Gatto.Cli.Setup.Tui.Pane
            .Rows(row, null, Gatto.Core.Hardware.MachineShape.Discrete, 100,
                glyphs: Gatto.Terminal.GlyphSet.Unicode).Select(x => x.Text));

        Assert.Contains("tool-calling verified", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("tasks", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("unfinished", detail, StringComparison.Ordinal);
    }
}
