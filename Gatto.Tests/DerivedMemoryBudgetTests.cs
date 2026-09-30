namespace Gatto.Tests;

//an unset budget is 2 percent of the model's context, clamped between 1000 and 4000. the ceiling stops a huge context handing its prefix an unbounded slice
public class DerivedMemoryBudgetTests
{
    [Theory]
    //rows below the crossover, where the 1000 floor wins
    [InlineData(8_000, 1000)]
    [InlineData(32_000, 1000)]
    [InlineData(50_000, 1000)]     //the row sits at the crossover where 2 percent equals 1000.
    //rows inside the proportional band.
    [InlineData(65_536, 1310)]
    [InlineData(131_072, 2621)]    //one row uses a real, in-use context size.
    //rows above the ceiling crossover clamp to 4000.
    [InlineData(200_000, 4000)]
    [InlineData(1_000_000, 4000)]
    public void DerivesTwoPercent_ClampedBetweenFloorAndCeiling(int context, int expected)
        => Assert.Equal(expected, Gatto.Cli.GattoApp.DerivedMemoryBudget(context));

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    public void UnknownOrNonsenseContext_FallsBackToTheFloor_NeverGuesses(int? context)
        => Assert.Equal(1000, Gatto.Cli.GattoApp.DerivedMemoryBudget(context));
}
