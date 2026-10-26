namespace Gatto.Tests;

//an unset budget is 2 percent of the model's context, clamped between the floor and the ceiling. the ceiling stops a huge context handing its prefix an unbounded slice
public class DerivedMemoryBudgetTests
{
    [Theory]
    //rows up to the crossover at 85k, where 2 percent meets the floor of 1700
    [InlineData(8_192, 1700)]
    [InlineData(16_384, 1700)]
    [InlineData(32_768, 1700)]
    [InlineData(65_536, 1700)]
    [InlineData(85_000, 1700)]
    //a row inside the proportional band, at a real, in-use context size
    [InlineData(131_072, 2621)]
    //rows above the ceiling crossover clamp to 4000.
    [InlineData(200_000, 4000)]
    [InlineData(262_144, 4000)]
    public void DerivesTwoPercent_ClampedBetweenFloorAndCeiling(int context, int expected)
        => Assert.Equal(expected, Gatto.Cli.GattoApp.DerivedMemoryBudget(context));

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    public void UnknownOrNonsenseContext_FallsBackToTheFloor_NeverGuesses(int? context)
        => Assert.Equal(1700, Gatto.Cli.GattoApp.DerivedMemoryBudget(context));
}
