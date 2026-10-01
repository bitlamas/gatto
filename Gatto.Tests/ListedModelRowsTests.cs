using Gatto.Cli;

namespace Gatto.Tests;

public class ListedModelRowsTests
{
    private static readonly string[] Offered = ["a", "b", "c"];

    [Fact]
    public void The_saved_model_is_the_default_row_and_the_current_one_is_marked()
    {
        var rows = ListedModelRows.Build(Offered, current: "a", saved: "b");
        Assert.Equal(new[] { false, true, false }, rows.Select(r => r.Default));
        Assert.Equal(new[] { true, false, false }, rows.Select(r => r.Marked));
        Assert.Equal(new[] { true, false, false }, rows.Select(r => r.Current));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("gone")]
    public void With_nothing_saved_or_a_stale_save_the_first_row_is_the_default(string? saved) =>
        Assert.Equal(new[] { true, false, false }, ListedModelRows.Build(Offered, "c", saved).Select(r => r.Default));
}
