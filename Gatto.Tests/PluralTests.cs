using Gatto.Core;

namespace Gatto.Tests;

//these prove the rule only, so each call site still needs its own rendered check
public class PluralTests
{
    //one is the discriminating count, since 0 and 2 read the same with or without the rule.
    [Theory]
    [InlineData(0, "0 lines")]
    [InlineData(1, "1 line")]
    [InlineData(2, "2 lines")]
    [InlineData(11, "11 lines")]
    public void Of_pluralises_on_the_count(long n, string expected)
        => Assert.Equal(expected, Plural.Of(n, "line"));

    [Theory]
    [InlineData(0, "lines")]
    [InlineData(1, "line")]
    [InlineData(2, "lines")]
    public void Noun_returns_the_noun_alone(long n, string expected)
        => Assert.Equal(expected, Plural.Noun(n, "line"));

    //the default appends s, so a noun with another plural passes its own
    [Theory]
    [InlineData(1, "1 entry")]
    [InlineData(2, "2 entries")]
    public void Irregular_plurals_are_supplied_not_derived(long n, string expected)
        => Assert.Equal(expected, Plural.Of(n, "entry", "entries"));

    //a negative count is not singular, so -1 reads as -1 lines
    [Fact]
    public void Minus_one_is_not_singular()
        => Assert.Equal("-1 lines", Plural.Of(-1, "line"));
}
