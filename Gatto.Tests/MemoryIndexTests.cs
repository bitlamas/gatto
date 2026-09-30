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

        var r = MemoryIndex.Compose(_root, 11);   //budget 11 is a cap of 44 characters, so the curated line fits and the harvested one doesn't

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
        //two lines cost 5 each with the newline, so budget 3 (a cap of 12) fits both and nothing is dropped
        MemoryDir.Write(_root, "a", "- xx");
        MemoryDir.Write(_root, "b", "- xx");

        var r = MemoryIndex.Compose(_root, 3);

        Assert.Equal("- xx\n- xx", r.Text);
        Assert.Equal(0, r.TruncatedLines);
    }

    [Fact]
    public void Boundary_OneCharOver_WholeLineDropped_AndCounted()
    {
        //each line is 42 chars, so budget 11 (a cap of 44) fits exactly one and drops the rest
        for (var i = 0; i < 10; i++) MemoryDir.Write(_root, $"fact-{i}", "- " + new string('x', 40));

        var r = MemoryIndex.Compose(_root, 11);

        Assert.Equal(9, r.TruncatedLines);
        Assert.DoesNotContain("\n", r.Text!);   //truncation drops whole lines, so the index never surfaces half a fact
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

        Assert.Equal("- a fact → crlf.md", MemoryIndex.Compose(_root, 1000).Text);
    }
}
