//pin the slug rule as a pure function with no filesystem or clock
using Gatto.Core.Memory;

namespace Gatto.Tests;

public class FactSlugTests
{
    [Fact]
    public void WorkedExample_FromTheSpec()
        => Assert.Equal("c-powershell-5-1-no",
            FactSlug.FromFactLine("- PowerShell 5.1: no `&&` — use `;` or `if ($?)`"));

    //the harvest's bullets open with articles, so the slug skips them and single letters to name the subject
    [Theory]
    [InlineData("- SSH host alias on this machine is relay7", "c-ssh-host-alias-machine")]
    [InlineData("- A page cite must be exactly (line N) or (lines N, M)", "c-page-cite-must-exactly")]
    [InlineData("- the grep tool's root parameter", "c-grep-tool-root-parameter")]
    [InlineData("- no legacy shims in gatto", "c-no-legacy-shims-gatto")]
    [InlineData("- a 5 b 6", "c-5-6")]
    [InlineData("- it is to be", "c-it-is-to-be")]
    public void THE_FIRST_FOUR_SUBJECT_TOKENS_AFTER_STOPWORDS_AND_SINGLE_LETTERS(string line, string slug)
        => Assert.Equal(slug, FactSlug.FromFactLine(line));

    [Fact]
    public void THE_STOPLIST_IS_THE_23_WORDS()
    {
        var words = "a an the this that these those it its is are was be to of for in on at by with and or".Split(' ');
        Console.WriteLine(string.Join(' ', FactSlug.Stopwords));
        Assert.Equal(23, FactSlug.Stopwords.Length);
        Assert.Equal(words, FactSlug.Stopwords);
    }

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
