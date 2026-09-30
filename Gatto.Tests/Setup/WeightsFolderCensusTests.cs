namespace Gatto.Tests.Setup;

//the weights folder name has one home, so no other file may spell it as a literal
public class WeightsFolderCensusTests
{
    private static IEnumerable<string> ProductSources() =>
        Gatto.Tests.Census.SourceTree.ProductionFiles();

    //exactly one file may spell the folder name as a literal, and it is the one that owns the answer
    [Fact]
    public void Only_ModelLocation_spells_the_weights_folder_name()
    {
        var offenders = ProductSources()
            .Where(p => File.ReadAllText(p).Contains("\"weights\"", StringComparison.Ordinal))
            .Select(p => Path.GetFileName(p))
            .Where(f => f != "ModelAdoption.cs")   //this is the file where ModelLocation lives
            .Order()
            .ToArray();

        Assert.True(offenders.Length == 0,
            "the weights folder name has a second home: " + string.Join(", ", offenders)
            + ". Call ModelLocation.SuggestedDir or ForModel instead of spelling it.");
    }

    //the absence check above proves nothing until the pattern matches a known file, so assert it here
    [Fact]
    public void The_census_pattern_can_actually_find_the_literal()
    {
        var owner = ProductSources().Single(p => Path.GetFileName(p) == "ModelAdoption.cs");
        Assert.Contains("\"weights\"", File.ReadAllText(owner), StringComparison.Ordinal);
    }
}
