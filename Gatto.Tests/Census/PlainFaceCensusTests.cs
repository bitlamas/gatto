namespace Gatto.Tests.Census;

//no production code may construct the plain wizard face, the TUI face replaced it. the file stays for BackLabel, the one home for the back row's text
public class PlainFaceCensusTests
{
    [Fact]
    public void NOTHING_IN_PRODUCTION_CONSTRUCTS_THE_PLAIN_WIZARD_FACE()
    {
        var sites = SourceTree.ProductionFiles()
            .Select(f => (File: Path.GetFileName(f), Code: SourceTree.WithoutComments(SourceTree.Read(f))))
            .Where(x => x.Code.Contains("new SetupFace(", StringComparison.Ordinal)
                     || x.Code.Contains("new Setup.SetupFace(", StringComparison.Ordinal))
            .Select(x => x.File)
            .ToList();

        //this proves the pattern can find a construction site in the tree, so a zero below means something
        var tuiSites = SourceTree.ProductionFiles()
            .Count(f => SourceTree.WithoutComments(SourceTree.Read(f))
                .Contains("new Setup.Tui.TuiWizardSurface(", StringComparison.Ordinal));
        Assert.True(tuiSites > 0,
            "the construction-site pattern matched NOTHING, so the zero below means nothing either");

        Assert.True(sites.Count == 0,
            "the plain wizard face is constructed again, in: " + string.Join(", ", sites)
            + "\nThat re-opens every finding dropped as unreachable while it had no callers, so re-read "
            + "those findings before wiring it, and give each one an owner.");
    }
}
