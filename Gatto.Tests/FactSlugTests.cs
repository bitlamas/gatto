//pin the slug rule as a pure function with no filesystem or clock
using Gatto.Core.Memory;

namespace Gatto.Tests;

public class FactSlugTests
{
    [Fact]
    public void WorkedExample_FromTheSpec()
        => Assert.Equal("c-powershell-5-1-no",
            FactSlug.FromFactLine("- PowerShell 5.1: no `&&` — use `;` or `if ($?)`"));

    [Fact]
    public void TakesFirstFourTokens_NoStopwordFiltering()
        => Assert.Equal("c-ssh-host-alias-on",
            FactSlug.FromFactLine("- SSH host alias on this machine is rejeobs"));

    [Fact]
    public void AllSymbolLine_FallsBackToCFact()
        => Assert.Equal("c-fact", FactSlug.FromFactLine("- *** !!! ***"));

    [Fact]
    public void EmptyLine_FallsBackToCFact()
        => Assert.Equal("c-fact", FactSlug.FromFactLine(""));

    [Fact]
    public void PrefixIsReservedInsideTheFortyCharCap()
    {
        //cap the base at 38, or the c- prefix pushes the slug past the 40 char limit
        var slug = FactSlug.FromFactLine(
            "- aaaaaaaaaaaaaaa bbbbbbbbbbbbbbb ccccccccccccccc ddddddddddddddd");

        Assert.StartsWith("c-", slug);
        Assert.True(slug.Length <= 40, $"slug was {slug.Length} chars: {slug}");
        Assert.Equal(slug, MemoryDir.ValidateTopic(slug));   //check against the real validator rather than a copy of its pattern
    }

    [Fact]
    public void TruncationNeverLeavesATrailingHyphen()
    {
        //a base cut at a token boundary ends with a hyphen, which reads broken next to the -2 collision suffix
        var slug = FactSlug.FromFactLine("- aaaaaaaaaa bbbbbbbbbb cccccccccc dddddddddddddddddddd");
        Assert.False(slug.EndsWith('-'), $"slug ended with a hyphen: {slug}");
    }
}
