using Gatto.Cli;

namespace Gatto.Tests;

//the one home for bytes-as-gigabytes, with the round-to-zero guard, and a census so a second home cannot arrive
public class SizeWordsTests
{
    private const long Gigabyte = 1024L * 1024 * 1024;

    [Fact]
    public void A_SIZE_BELOW_THE_RESOLUTION_SAYS_SO_rather_than_rounding_to_none()
    {
        //a quantity below the display's resolution must say so, since 0 GB reads as nothing loaded
        Assert.Equal("<0.1 GB", SizeWords.Gb(0));
        Assert.Equal("<0.1 GB", SizeWords.Gb((long)(0.04 * Gigabyte)));

        //the tilde is dropped below the threshold, since ~<0.1 GB hedges a bound that is already a hedge
        Assert.Equal("<0.1 GB", SizeWords.Gb((long)(0.04 * Gigabyte), approx: true));
    }

    [Fact]
    public void THE_BOUNDARY_IS_WHERE_THE_CONSTANT_SAYS_IT_IS()
    {
        //the boundary is asserted against SizeWords.RoundsToZero, so the guard and the rule cannot disagree
        Assert.Equal("<0.1 GB", SizeWords.Gb((SizeWords.RoundsToZero - 0.001) * Gigabyte));
        Assert.Equal("0.1 GB", SizeWords.Gb(SizeWords.RoundsToZero * Gigabyte));
    }

    [Fact]
    public void AN_ORDINARY_SIZE_READS_THE_SAME_WITH_AND_WITHOUT_THE_TILDE()
    {
        Assert.Equal("0.6 GB", SizeWords.Gb((long)(0.6 * Gigabyte)));
        Assert.Equal("~0.6 GB", SizeWords.Gb((long)(0.6 * Gigabyte), approx: true));
        //a whole number of gigabytes keeps its tenth, since the figures are read down a column and the digits must line up
        Assert.Equal("~40.0 GB", SizeWords.Gb(40L * Gigabyte, approx: true));
        Assert.Equal("40.0 GB", SizeWords.Gb(40L * Gigabyte));
    }

    [Fact]
    public void A_NEGATIVE_SIZE_IS_CLAMPED_not_rendered()
    {
        //a negative is not a size, so it clamps instead of rendering
        Assert.Equal("<0.1 GB", SizeWords.Gb(-1));
    }

    //a download's size in the unit its reader expects, megabytes below a gigabyte and gigabytes above it
    [Theory]
    [InlineData(214L * 1024 * 1024, "214 MB")]          //the engine consent's own row
    [InlineData(851L * 1024 * 1024, "851 MB")]          //the projector's size, from the model step
    [InlineData(1024L * 1024 * 1024 - 1, "1024 MB")]    //the last byte below the boundary
    [InlineData(1024L * 1024 * 1024, "1.0 GB")]         //the boundary itself flips the unit
    [InlineData(16960L * 1024 * 1024, "16.6 GB")]       //the size of a model, in tenths
    [InlineData(0L, "0 MB")]
    [InlineData(-5L, "0 MB")]                           //a negative is not a size and must not crash
    public void A_DOWNLOADS_SIZE_TAKES_THE_UNIT_ITS_READER_THINKS_IN(long bytes, string said) =>
        Assert.Equal(said, SizeWords.Auto(bytes));

    [Fact]
    public void THE_GIGABYTE_DIVISION_HAS_EXACTLY_ONE_HOME_and_one_named_exemption()
    {
        //a sixth home cannot arrive quietly, and SetupFlow.WholeGb is the one exemption, rounding to whole gigabytes for another purpose
        var root = Gatto.Tests.Census.SourceTree.RepoRoot();
        var offenders = new List<string>();

        foreach (var file in Gatto.Tests.Census.SourceTree.ProductionFiles())
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (rel.EndsWith("Gatto/Cli/SizeWords.cs", StringComparison.OrdinalIgnoreCase)) continue;

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (!lines[i].Contains("1024.0 * 1024 * 1024", StringComparison.Ordinal)) continue;
                //the single exemption, matched on what it does rather than on a line number
                if (lines[i].Contains("Math.Round(", StringComparison.Ordinal)) continue;
                offenders.Add($"  {rel}:{i + 1}: {lines[i].Trim()}");
            }
        }

        Assert.True(offenders.Count == 0,
            "bytes-to-GB has one home (Gatto/Cli/SizeWords.cs) plus SetupFlow.WholeGb. Found:\n"
            + string.Join("\n", offenders));
    }
}
