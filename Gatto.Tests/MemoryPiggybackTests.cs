using Gatto.Core.Memory;

namespace Gatto.Tests;

//the pure half of the summary piggyback: the extraction touches no filesystem and no clock, so these tests assert against strings
public sealed class MemoryPiggybackTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("gatto-piggyback").FullName;
    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string IndexPath => MemoryDir.PathFor(_root, "index");
    private string ReadIndex() => File.ReadAllText(IndexPath);

    [Fact]
    public void NoHeading_NoCandidates_SummaryUntouched()
    {
        const string summary = "Task / goal: ship it.\n\nCurrent state: shipped.";

        var ex = MemoryPiggyback.Extract(summary);

        Assert.Empty(ex.Candidates);
        Assert.Equal(0, ex.Dropped);
        Assert.Same(summary, ex.StrippedSummary);   //with no heading the call returns the very same string instance.
    }

    [Fact]
    public void HeadingPresent_BulletsExtracted_SummaryStripped()
    {
        const string summary =
            "Task / goal: ship it.\n\nImmediate next step: run the suite.\n\n"
            + "Memory candidates:\n- the build output must be tmp/testbin\n- master is the branch\n";

        var ex = MemoryPiggyback.Extract(summary);

        Assert.Equal(
            new[] { "- the build output must be tmp/testbin", "- master is the branch" },
            ex.Candidates);
        Assert.Equal(0, ex.Dropped);
        //the stripped summary ends before the heading line: heading and region are both gone.
        Assert.Equal("Task / goal: ship it.\n\nImmediate next step: run the suite.", ex.StrippedSummary);
        Assert.DoesNotContain(MemoryPiggyback.Heading, ex.StrippedSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("none"), InlineData("None."), InlineData("None!"), InlineData("- none"),
     InlineData("* none"), InlineData("- nothing"), InlineData("  Nothing.  ")]
    public void NoneVariants_YieldNothing(string line)
    {
        var ex = MemoryPiggyback.Extract($"Memory candidates:\n{line}\n");

        Assert.Empty(ex.Candidates);
        Assert.Equal(0, ex.Dropped);
    }

    [Fact]
    public void StarBullets_NormalizedToDash()
    {
        var ex = MemoryPiggyback.Extract("Memory candidates:\n* nginx runs on 127.0.0.1:80\n");

        Assert.Equal(new[] { "- nginx runs on 127.0.0.1:80" }, ex.Candidates);
    }

    [Fact]
    public void ProseLine_KeptVerbatimAsBullet()
    {
        //prose instead of bullets still banks, the line gets a dash prefix and stays verbatim. no sentence-splitting heuristics
        var ex = MemoryPiggyback.Extract("Memory candidates:\nThe test project is xunit only.\n");

        Assert.Equal(new[] { "- The test project is xunit only." }, ex.Candidates);
    }

    [Fact]
    public void EleventhLine_Dropped_CountReported()
    {
        var lines = string.Join('\n', Enumerable.Range(1, 12).Select(i => $"- fact {i}"));

        var ex = MemoryPiggyback.Extract("Memory candidates:\n" + lines);

        Assert.Equal(MemoryPiggyback.MaxLines, ex.Candidates.Count);
        Assert.Equal("- fact 1", ex.Candidates[0]);
        Assert.Equal("- fact 10", ex.Candidates[^1]);
        Assert.Equal(2, ex.Dropped);   //facts 11 and 12 are the dropped ones.
    }

    [Fact]
    public void LastHeadingWins()
    {
        //the decoy prose line really matches the scan predicate, so a first-wins scan would open the region there. that is what makes this a test of last-wins.
        const string summary =
            "Findings & facts learned:\n"
            + "Memory candidates are extracted mechanically, never by a second model call.\n\n"
            + "Memory candidates:\n- the real one\n";

        var ex = MemoryPiggyback.Extract(summary);

        //first-wins would yield two candidates here, because the later heading line itself becomes one.
        Assert.Equal(new[] { "- the real one" }, ex.Candidates);
        //first-wins would also cut the summary above the decoy, losing the finding.
        Assert.Contains("extracted mechanically", ex.StrippedSummary);
        Assert.DoesNotContain("the real one", ex.StrippedSummary);
    }

    [Theory]
    [InlineData("## Memory candidates"), InlineData("### Memory candidates:"),
     InlineData("**Memory candidates:**"), InlineData("__Memory candidates__"),
     InlineData("###   Memory candidates"), InlineData("## **Memory candidates:**"),
     InlineData("###### memory candidates:")]
    public void MarkdownStyledHeading_OpensTheRegion_AndIsStripped(string heading)
    {
        //small local models style this heading as markdown even under plain-prose instructions. a miss banks nothing and lets the raw section reach the prefix
        var ex = MemoryPiggyback.Extract($"Current state: done.\n\n{heading}\n- nginx binds 127.0.0.1\n");

        Assert.Equal(new[] { "- nginx binds 127.0.0.1" }, ex.Candidates);
        Assert.Equal("Current state: done.", ex.StrippedSummary);
        Assert.DoesNotContain("nginx binds", ex.StrippedSummary, StringComparison.Ordinal);
        Assert.DoesNotContain(MemoryPiggyback.Heading, ex.StrippedSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("- Memory candidates are extracted mechanically, never by a second model call."),
     InlineData("* Memory candidates are extracted mechanically, never by a second model call."),
     InlineData("####### Memory candidates")]
    public void BulletedProseQuotingThePhrase_DoesNotOpenARegion(string decoy)
    {
        //a markdown heading opens the region, and a bullet that quotes the phrase stays non-matching. seven # is not a heading, since the run caps at six
        const string tail = "\n\nCurrent state: done.";
        var summary = "Findings & facts learned:\n" + decoy + tail;

        var ex = MemoryPiggyback.Extract(summary);

        Assert.Empty(ex.Candidates);
        Assert.Same(summary, ex.StrippedSummary);   //no region opened, so the call returns the very same string instance.
    }

    [Fact]
    public void LastHeadingWins_HoldsAcrossHeadingStyles()
    {
        //a markdown-styled decoy earlier loses to the plain heading later, since the region opens at the last match
        var ex = MemoryPiggyback.Extract(
            "## Memory candidates in general are a design idea.\n\nMemory candidates:\n- the real one\n");

        Assert.Equal(new[] { "- the real one" }, ex.Candidates);
        Assert.Contains("a design idea", ex.StrippedSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void BlankAndMarkerOnlyLines_Skipped()
    {
        var ex = MemoryPiggyback.Extract("Memory candidates:\n\n- real fact\n\n-\n   \n* \n");

        Assert.Equal(new[] { "- real fact" }, ex.Candidates);
        Assert.Equal(0, ex.Dropped);
    }

    [Fact]
    public void HeadingFound_DistinguishesNoSectionFromAnEmptyOne()
    {
        //both cases give zero candidates and an identical store, so HeadingFound is the only thing that tells them apart
        Assert.False(MemoryPiggyback.Extract("Current state: done.").HeadingFound);
        Assert.True(MemoryPiggyback.Extract("Memory candidates:\nnone").HeadingFound);
        Assert.True(MemoryPiggyback.Extract("**Memory candidates:**\n- a fact").HeadingFound);
    }

    //one file per candidate, prefixed c- so a machine-fed file is prunable as a class. no provenance line, the mtime and compaction record hold the date and counts

    private string FactText(string slug) => File.ReadAllText(MemoryDir.PathFor(_root, slug));

    [Fact]
    public void BankCandidates_WritesOneFactFilePerCandidate_LineOneOnly()
    {
        var (banked, skipped) = MemoryPiggyback.BankCandidates(_root, new[] { "- PowerShell 5.1: no `&&`" });

        Assert.Equal((1, 0), (banked, skipped));
        Assert.Equal("- PowerShell 5.1: no `&&`\n", FactText("c-powershell-5-1-no"));
    }

    [Fact]
    public void BankCandidates_NoProvenanceComment_SoNoFactAdvertisesEmptyDetail()
    {
        MemoryPiggyback.BankCandidates(_root, new[] { "- alpha beta gamma delta" });

        var text = FactText("c-alpha-beta-gamma-delta");
        Assert.DoesNotContain("from compact", text);
        Assert.Single(text.TrimEnd('\n').Split('\n'));   //the file holds line 1 and nothing else.
    }

    [Fact]
    public void BankCandidates_DedupsAgainstLineOneOfAnyExistingFact()
    {
        //the dedup set is line 1 of every fact, so a hand-written candidate under another slug is not banked again
        MemoryDir.Write(_root, "hand-written", "- alpha beta gamma delta");

        var (banked, skipped) = MemoryPiggyback.BankCandidates(
            _root, new[] { "- alpha beta gamma delta", "- gamma delta epsilon zeta" });

        Assert.Equal((1, 1), (banked, skipped));
        Assert.False(File.Exists(MemoryDir.PathFor(_root, "c-alpha-beta-gamma-delta")));
        Assert.True(File.Exists(MemoryDir.PathFor(_root, "c-gamma-delta-epsilon-zeta")));
    }

    [Fact]
    public void BankCandidates_CollidingSlugs_GetNumberedSuffixes()
    {
        //two facts whose first four words match get numbered slugs. the bank only appends, so a wrong fact sits beside a right one and both stay visible
        MemoryPiggyback.BankCandidates(_root, new[] { "- the same four words here" });
        MemoryPiggyback.BankCandidates(_root, new[] { "- the same four words DIFFERENT" });

        Assert.True(File.Exists(MemoryDir.PathFor(_root, "c-the-same-four-words")));
        Assert.True(File.Exists(MemoryDir.PathFor(_root, "c-the-same-four-words-2")));
    }

    [Fact]
    public void BankCandidates_AllDuplicates_WritesNothing()
    {
        MemoryDir.Write(_root, "a", "- alpha beta gamma delta");
        MemoryDir.Write(_root, "b", "- epsilon zeta eta theta");
        var before = MemoryDir.FactFiles(_root).Count;

        var (banked, skipped) = MemoryPiggyback.BankCandidates(
            _root, new[] { "- alpha beta gamma delta", "- epsilon zeta eta theta" });

        Assert.Equal((0, 2), (banked, skipped));
        Assert.Equal(before, MemoryDir.FactFiles(_root).Count);
    }

    [Fact]
    public void BankCandidates_Empty_WritesNothing()
    {
        var (banked, skipped) = MemoryPiggyback.BankCandidates(_root, Array.Empty<string>());

        Assert.Equal((0, 0), (banked, skipped));
        Assert.Empty(MemoryDir.FactFiles(_root));
    }

    [Fact]
    public void BankCandidates_DuplicatesWithinOneBatch_BankedOnce()
    {
        var (banked, skipped) = MemoryPiggyback.BankCandidates(
            _root, new[] { "- alpha beta gamma delta", "- alpha beta gamma delta" });

        Assert.Equal((1, 1), (banked, skipped));
        Assert.Single(MemoryDir.FactFiles(_root));
    }
}
