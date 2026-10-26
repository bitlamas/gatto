//each fact file's first line composes the index, so there is no aggregate file to update. the budget counts characters and drops whole lines past the cap
using Gatto.Core.Memory;

namespace Gatto.Tests;

public sealed class MemoryIndexTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("gatto-mem-idx").FullName;
    public void Dispose() => Directory.Delete(_root, recursive: true);

    //writes a file under a name the validator would refuse, so a test can place a leftover file there
    private void WriteRaw(string fileName, string content)
    {
        var dir = Path.Combine(_root, ".gatto", "memory");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, fileName), content);
    }

    [Fact]
    public void NoDirectory_ReturnsNull()
        => Assert.Null(MemoryIndex.Compose(_root, 1000).Text);

    [Fact]
    public void OneLinePerFact_FilenameOrdered()
    {
        MemoryDir.Write(_root, "bravo", "- second fact");
        MemoryDir.Write(_root, "alpha", "- first fact");

        Assert.Equal("- first fact\n- second fact", MemoryIndex.Compose(_root, 1000).Text);
    }

    [Fact]
    public void CuratedFactsComposeBeforeMachineFedOnes_SoTruncationEvictsTheHarvestedFirst()
    {
        //truncation drops whole lines from the tail, so the compose order is the eviction priority and machine-fed facts must not outrank curated ones
        MemoryDir.Write(_root, "c-aaa-harvested", "- harvested first alphabetically");
        MemoryDir.Write(_root, "zzz-curated", "- curated last alphabetically");

        Assert.Equal("- curated last alphabetically\n- harvested first alphabetically",
            MemoryIndex.Compose(_root, 1000).Text);
    }

    [Fact]
    public void OverBudget_EvictsTheMachineFedFactAndKeepsTheCuratedOne()
    {
        //the eviction rule in the case that bites, a budget with room for exactly one line
        MemoryDir.Write(_root, "c-aaa-harvested", "- harvested aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        MemoryDir.Write(_root, "zzz-curated", "- curated cccccccccccccccccccccccccccccc");

        var r = MemoryIndex.Compose(_root, 13);   //budget 13 is a cap of 42.9 characters, so the curated line fits and the harvested one doesn't

        Assert.Equal("- curated cccccccccccccccccccccccccccccc", r.Text);
        Assert.Equal(1, r.TruncatedLines);
    }

    [Fact]
    public void PointerSuffix_OnlyWhenTheFileHasDetailBelowLineOne()
    {
        MemoryDir.Write(_root, "plain", "- just a fact");
        MemoryDir.Write(_root, "deep", "- has detail\nthe detail line");

        var text = MemoryIndex.Compose(_root, 1000).Text!;

        Assert.Contains("- has detail → deep.md", text);
        Assert.Contains("- just a fact", text);
        Assert.DoesNotContain("plain.md", text);   //a file with nothing behind line 1 gets no pointer.
    }

    [Fact]
    public void PointerSuffix_NotTriggeredByTrailingBlankLines()
    {
        //a hand-edited file can hold extra blank lines, and a pointer for them would send the model to read_file for nothing
        WriteRaw("hand-edited.md", "- a fact\n\n   \n");

        Assert.Equal("- a fact", MemoryIndex.Compose(_root, 1000).Text);
    }

    [Fact]
    public void BlankFirstLine_ContributesNothing()
    {
        MemoryDir.Write(_root, "real", "- a real fact");
        WriteRaw("headless.md", "   \nbody but no headline\n");

        Assert.Equal("- a real fact", MemoryIndex.Compose(_root, 1000).Text);
    }

    [Fact]
    public void StrayEmptyV1Index_IsHarmless_ByTheGeneralRuleNotByItsName()
    {
        //a blank first line contributes nothing, so a leftover empty INDEX.md needs no special handling
        WriteRaw("INDEX.md", "");
        MemoryDir.Write(_root, "real", "- a real fact");

        Assert.Equal("- a real fact", MemoryIndex.Compose(_root, 1000).Text);
    }

    [Fact]
    public void StrayNonEmptyV1Index_IsJustAnotherFact_NoSpecialCasing()
    {
        //a leftover INDEX.md composes as one ordinary fact line, no code knows that name
        WriteRaw("INDEX.md", "- leftover v1 line\n- and another\n");

        var text = MemoryIndex.Compose(_root, 1000).Text!;

        Assert.StartsWith("- leftover v1 line", text);
        Assert.Contains("→ INDEX.md", text);            //the file has a body below line 1, so it gets the ordinary pointer.
        Assert.DoesNotContain("- and another", text);   //only line 1 composes, like every other fact.
    }

    [Fact]
    public void NoContributingFiles_IsNull_SoTheBlockIsOmittedEntirely()
    {
        WriteRaw("blank.md", "\n");
        Assert.Null(MemoryIndex.Compose(_root, 1000).Text);
    }

    [Fact]
    public void UnreadableFactFile_IsSkipped_NotFatal()
    {
        //one unreadable fact must never cost a launch, nor hide the facts beside it.
        Directory.CreateDirectory(Path.Combine(_root, ".gatto", "memory", "locked.md"));
        MemoryDir.Write(_root, "real", "- a real fact");

        Assert.Equal("- a real fact", MemoryIndex.Compose(_root, 1000).Text);
    }

    [Fact]
    public void Boundary_LastWholeLineAtExactlyCapIncluded()
    {
        //three lines cost 11 each with the newline, so budget 10 (a cap of 33) fits all three and nothing is dropped
        MemoryDir.Write(_root, "a", "- xxxxxxxx");
        MemoryDir.Write(_root, "b", "- xxxxxxxx");
        MemoryDir.Write(_root, "c", "- xxxxxxxx");

        var r = MemoryIndex.Compose(_root, 10);

        Assert.Equal("- xxxxxxxx\n- xxxxxxxx\n- xxxxxxxx", r.Text);
        Assert.Equal(0, r.TruncatedLines);
    }

    [Fact]
    public void Boundary_OneCharOver_WholeLineDropped_AndCounted()
    {
        //budget 7 is a cap of 23.1, so a 22-char line costs 23 and carries, a 23-char line costs 24 and drops
        for (var i = 0; i < 10; i++) MemoryDir.Write(_root, $"fact-{i}", "- " + new string('x', 20));

        var r = MemoryIndex.Compose(_root, 7);

        Assert.Equal(9, r.TruncatedLines);
        Assert.DoesNotContain("\n", r.Text!);   //truncation drops whole lines, so the index never surfaces half a fact

        foreach (var f in MemoryDir.FactFiles(_root)) File.Delete(f);
        MemoryDir.Write(_root, "long", "- " + new string('x', 21));
        Assert.Equal((string?)"", MemoryIndex.Compose(_root, 7).Text);
    }

    [Fact]
    public void FirstLineAloneOverCap_ZeroLinesIncluded_ButNotNull()
    {
        //an empty string rather than null, so the block still renders with only the truncation note and an over-budget index is never silently empty
        MemoryDir.Write(_root, "huge", "- " + new string('x', 500));

        var r = MemoryIndex.Compose(_root, 1);

        Assert.Equal("", r.Text);
        Assert.Equal(1, r.TruncatedLines);
    }

    [Fact]
    public void CrLfFactFile_YieldsACleanIndexLine()
    {
        WriteRaw("crlf.md", "- a fact\r\nsome detail\r\n");

        Assert.Equal("- a fact → crlf.md (+1 line)", MemoryIndex.Compose(_root, 1000).Text);
    }

    //five planted facts of 31 characters cost 32 each, so the cap of budget times the ratio carries the whole lines it covers
    [Theory]
    [InlineData(10, 1)]   //a cap of 33
    [InlineData(20, 2)]   //a cap of 66
    [InlineData(49, 5)]   //a cap of 161.7, and five lines cost 160
    public void A_PLANTED_STORE_CARRIES_THE_LINES_THE_CAP_COVERS(int budget, int carried)
    {
        for (var i = 0; i < 5; i++) MemoryDir.Write(_root, $"fact-{i}", $"- fact {i} " + new string('x', 22));

        var r = MemoryIndex.Compose(_root, budget);

        Assert.Equal(carried, r.Text!.Split('\n').Length);
        Assert.Equal(5 - carried, r.TruncatedLines);
    }

    //the pointer says how long the body is, so the model can price a read before it spends a turn
    [Fact]
    public void A_FILE_WITH_THREE_BODY_LINES_POINTS_WITH_PLUS_THREE()
    {
        MemoryDir.Write(_root, "deep", "- has detail\none\ntwo\nthree");

        Assert.Equal("- has detail → deep.md (+3 lines)", MemoryIndex.Compose(_root, 1000).Text);
    }

    [Fact]
    public void ONE_BODY_LINE_POINTS_IN_THE_SINGULAR()
    {
        MemoryDir.Write(_root, "deep", "- has detail\nthe detail line");

        Assert.Equal("- has detail → deep.md (+1 line)", MemoryIndex.Compose(_root, 1000).Text);
    }

    [Fact]
    public void BLANK_BODY_LINES_ARE_NOT_COUNTED()
    {
        WriteRaw("spaced.md", "- has detail\n\nfirst\n   \n\nsecond\n\n");

        Assert.Equal("- has detail → spaced.md (+2 lines)", MemoryIndex.Compose(_root, 1000).Text);
    }

    [Fact]
    public void THE_UNCAPPED_INDEX_CARRIES_THE_SAME_SUFFIX()
    {
        MemoryDir.Write(_root, "plain", "- just a fact");
        MemoryDir.Write(_root, "deep", "- has detail\none\ntwo");

        Assert.Equal(MemoryIndex.Compose(_root, 1000).Text, MemoryIndex.ComposeUncapped(_root));
        Assert.Contains("- has detail → deep.md (+2 lines)", MemoryIndex.ComposeUncapped(_root));
    }
}
