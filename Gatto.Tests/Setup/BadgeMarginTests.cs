using Gatto.Core.Acquire;

namespace Gatto.Tests.Setup;

//a badge shows the pass margin when it has one. the pass line is fixed, so a borderline model shows how it sat rather than the line moving.
public class BadgeMarginTests
{
    private static string Home()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gatto-badge-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(dir, BadgeRegister.DirectoryName));
        return dir;
    }

    private static void WriteRecord(string home, string key, string tasksJson) =>
        File.WriteAllText(
            Path.Combine(home, BadgeRegister.DirectoryName, BadgeRegister.FileNameFor(key)),
            "{ \"schema\": 1, \"model_key\": \"" + key + "\", \"verdict\": \"pass\", \"battery_version\": 4, "
            + "\"measured\": \"2026-08-12\", \"tasks\": " + tasksJson + " }");

    [Fact]
    public void THE_MARGIN_IS_COUNTED_OFF_THE_RECORDS_OWN_TASK_ROWS()
    {
        //the margin is counted from the task rows on each read, a stored count could drift from the rows it summarises.
        var home = Home();
        WriteRecord(home, "org/m",
            "[{\"id\":\"B1\",\"pass\":true},{\"id\":\"B2\",\"pass\":true},{\"id\":\"B3\",\"pass\":true},"
            + "{\"id\":\"B4\",\"pass\":false},{\"id\":\"B5\",\"pass\":true}]");

        var badge = BadgeRegister.Lookup(home, "org/m");

        Assert.NotNull(badge);
        Assert.Equal(4, badge.Passed);
        Assert.Equal(5, badge.Ran);
    }

    [Fact]
    public void A_SKIPPED_TASK_IS_IN_NEITHER_COUNT()
    {
        //a task the run never reached is in neither count, counting it in the denominator invents a result the model was never asked for.
        var home = Home();
        WriteRecord(home, "org/m",
            "[{\"id\":\"B1\",\"pass\":true},{\"id\":\"B2\",\"pass\":true},{\"id\":\"B3\",\"skipped\":true}]");

        var badge = BadgeRegister.Lookup(home, "org/m");

        Assert.Equal(2, badge!.Passed);
        Assert.Equal(2, badge.Ran);
    }

    [Fact]
    public void A_RECORD_WITH_NO_TASK_ROWS_SCORES_NOTHING_rather_than_zero_of_zero()
    {
        //a record with no task rows reads as unknown, and a zero count shows no margin on any screen.
        var home = Home();
        File.WriteAllText(
            Path.Combine(home, BadgeRegister.DirectoryName, BadgeRegister.FileNameFor("org/m")),
            "{ \"schema\": 1, \"model_key\": \"org/m\", \"verdict\": \"pass\", \"battery_version\": 4, \"measured\": \"2026-08-12\" }");

        var badge = BadgeRegister.Lookup(home, "org/m");

        Assert.NotNull(badge);
        Assert.Equal(0, badge.Ran);
    }
}
