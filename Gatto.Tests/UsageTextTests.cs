using Gatto.Core.Client;
using Gatto.Repl.Commands;

namespace Gatto.Tests;

//the rows /usage draws, every label whole and every number the provider's
public class UsageTextTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 4, 12, 0, 0, TimeSpan.Zero);

    private static AllowanceReport Report(IReadOnlyList<Allowance>? rows = null, DateTimeOffset? readAt = null, string? failure = null,
        decimal? cost = null) =>
        new(cost, rows?.FirstOrDefault(), rows ?? [], readAt, failure, true);

    [Fact]
    public void Rows_draw_the_grammar_with_the_reset_and_the_age()
    {
        var reset = new DateTimeOffset(2026, 3, 9, 0, 0, 0, TimeSpan.Zero);
        var lines = UsageText.Lines(Report([
            new Allowance("week", AllowanceShape.Percent, UsedPercent: 42, ResetAt: reset),
            new Allowance("a tier with a long name", AllowanceShape.Label),
            new Allowance("month", AllowanceShape.UsedOfTotal, Used: 420, Total: 1000),
        ], readAt: Now.AddMinutes(-2)), "hosted", Now);

        Assert.Equal([
            "week 42% used resets " + reset.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture),
            "a tier with a long name",
            "month 420 of 1000",
            "read 2 min ago",
        ], lines);
    }

    [Theory]
    [InlineData(30, "just now")]
    [InlineData(59 * 60, "59 min ago")]
    [InlineData(3 * 3600 + 5, "3 h ago")]
    [InlineData(2 * 86400 + 5, "2 d ago")]
    public void The_age_is_the_largest_whole_unit(int seconds, string age) =>
        Assert.Equal(age, UsageText.Age(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void A_failed_read_keeps_the_rows_and_adds_the_line()
    {
        var lines = UsageText.Lines(Report([new Allowance("week", AllowanceShape.Percent)], readAt: Now.AddHours(-1), failure: "billing said no"),
            "hosted", Now);

        Assert.Equal(["week -% used", "read 1 h ago", "last read failed: billing said no"], lines);
    }

    [Fact]
    public void The_cost_line_names_the_provider()
    {
        var lines = UsageText.Lines(Report(cost: 0.4231m), "hosted", Now);

        Assert.Equal(["cost $0.42 accounted by the provider"], lines);
    }

    [Fact]
    public void No_rows_and_no_cost_is_one_line_saying_nothing_was_read()
    {
        Assert.Equal(["nothing read yet from endpoint hosted"], UsageText.Lines(Report(), "hosted", Now));
        Assert.Equal(["last read failed: no route"], UsageText.Lines(Report(failure: "no route"), "hosted", Now));
    }
}
