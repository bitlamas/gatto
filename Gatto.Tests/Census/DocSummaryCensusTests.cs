using System.Text;

namespace Gatto.Tests.Census;

//a doc block holds one summary, and every tool silently ignores a second. pin the count per file, so a fix in one file cannot hide a new block in another.
public class DocSummaryCensusTests
{
    //every entry here is a defect, so the map only holds the count down. removing a line is the right direction
    private static readonly Dictionary<string, int> Known = new(StringComparer.Ordinal)
    {
        ["Gatto.Tests/Fakes/RichReplHarness.cs"] = 0,
        ["Gatto.Tests/Fakes/WizardProbes.cs"] = 0,
        ["Gatto.Tests/GgufTestBytes.cs"] = 0,
        ["Gatto.Tests/HubSearchTests.cs"] = 0,
        ["Gatto.Tests/MemoryPiggybackWiringTests.cs"] = 0,
        ["Gatto.Tests/Setup/ModelAddDoorWalkTests.cs"] = 2,
        ["Gatto.Tests/Setup/RenderedWalkTests.cs"] = 0,
        ["Gatto.Tests/Setup/SetupFlowTests.cs"] = 0,
        ["Gatto.Tests/Setup/ShelfControlsTests.cs"] = 0,
        ["Gatto.Tests/Setup/Tui/EngineFetchTests.cs"] = 0,
        ["Gatto.Tests/Setup/Tui/WalkRender.cs"] = 0,
        ["Gatto/Cli/GattoApp.cs"] = 0,
        ["Gatto/Cli/Setup/ArchNote.cs"] = 0,
        ["Gatto/Cli/Setup/LiveSetupProbes.cs"] = 0,
        ["Gatto/Cli/Setup/MachineFacts.cs"] = 0,
        ["Gatto/Cli/Setup/PrompterWizardSurface.cs"] = 0,
        ["Gatto/Cli/Setup/SearchRow.cs"] = 0,
        ["Gatto/Cli/Setup/SetupFlow.cs"] = 0,
        ["Gatto/Cli/Setup/SetupProbes.cs"] = 0,
        ["Gatto/Cli/Setup/SetupRunner.cs"] = 0,
        ["Gatto/Cli/Setup/ShelfBinding.cs"] = 0,
        ["Gatto/Cli/Setup/ShelfTable.cs"] = 0,
        //a pin may only move down
        ["Gatto.Terminal/Footer.cs"] = 0,
        ["Gatto/Cli/Setup/Tui/Pane.cs"] = 0,
        ["Gatto/Cli/Setup/Tui/ScreenPainter.cs"] = 0,
        ["Gatto/Cli/Setup/Tui/Shelf.cs"] = 0,
        ["Gatto.Terminal/Strip.cs"] = 0,
        ["Gatto/Cli/Setup/Tui/TuiWizardSurface.cs"] = 0,
        ["Gatto/Cli/Setup/WizardRows.cs"] = 0,
        ["Gatto/Cli/SizeWords.cs"] = 0,
        ["Gatto/Cli/SwapConfirm.cs"] = 0,
        ["Gatto/Cli/UninstallConsent.cs"] = 0,
        ["Gatto/Cli/UpdateCheck.cs"] = 0,
        ["Gatto/Core/Acquire/BadgeRegister.cs"] = 0,
        ["Gatto/Core/Acquire/HubClient.cs"] = 0,
        ["Gatto/Core/Acquire/HubSearch.cs"] = 0,
        ["Gatto/Core/Acquire/ModelAdoption.cs"] = 0,
        ["Gatto/Core/Acquire/ServerConnect.cs"] = 0,
        ["Gatto/Core/Client/ImageCache.cs"] = 0,
        ["Gatto/Core/Home/GattoConfigWriter.cs"] = 0,
        ["Gatto/Core/Loop/Compactor.cs"] = 0,
        ["Gatto/Core/Models/ModelId.cs"] = 0,
        ["Gatto/Core/Web/GuardedFetch.cs"] = 0,
        ["Gatto/Repl/ConsolePrompter.cs"] = 0,
        ["Gatto/Repl/IWizardPrompter.cs"] = 0,
        ["Gatto/Repl/Input/ImagePaste.cs"] = 0,
        ["Gatto/Repl/Input/LineEditor.cs"] = 0,
        ["Gatto/Repl/Repl.cs"] = 0,
        ["Gatto/Repl/SelectPrompt.cs"] = 0,
        ["Gatto/Repl/Term/CtxState.cs"] = 0,
        ["Gatto/Roles/Audition/AuditionRunner.cs"] = 0,
        ["Gatto/Roles/Audition/Grader.cs"] = 0,
        ["Gatto/Roles/Model.cs"] = 0,
        ["Gatto/Roles/ModelScaffold.cs"] = 0,
        ["Gatto/Roles/ServeManager.cs"] = 0,    };

    //count a block as one unbroken run of /// lines. the compiler attaches that run to one declaration, so a second summary in it documents nothing
    internal static int DoubledBlocks(string source)
    {
        var found = 0;
        var summaries = 0;
        foreach (var line in source.Split('\n').Append(""))
        {
            if (line.TrimStart().StartsWith("///", StringComparison.Ordinal))
            {
                summaries += line.Split("<summary>").Length - 1;
                continue;
            }

            if (summaries > 1) found++;
            summaries = 0;
        }
        return found;
    }

    //count a /// run whose next real line is a closing brace. blank lines and attributes are skipped, and a run before a statement stays out of scope.
    internal static int OrphanedBlocks(string source)
    {
        var found = 0;
        var inRun = false;
        foreach (var raw in source.Split('\n').Append(""))
        {
            var line = raw.Trim();
            if (line.StartsWith("///", StringComparison.Ordinal)) { inRun = true; continue; }
            if (!inRun) continue;
            //a blank line or an attribute can sit between a block and its member, so skip both.
            if (line.Length == 0 || line.StartsWith("[", StringComparison.Ordinal)) continue;
            if (line.StartsWith("}", StringComparison.Ordinal)) found++;
            inRun = false;
        }
        return found;
    }

    private static IEnumerable<string> Sources() =>
        Directory.EnumerateFiles(SourceTree.RepoRoot(), "*.cs", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(SourceTree.RepoRoot(), p).Replace('\\', '/'))
            .Where(p => SourceTree.ProductProjects.Append("Gatto.Tests").Any(d => p.StartsWith(d + "/", StringComparison.Ordinal))
                && !p.Contains("/bin/", StringComparison.Ordinal)
                && !p.Contains("/obj/", StringComparison.Ordinal));

    //no file may grow a doc block with two summaries, and no file may differ from its pin in either direction.
    [Fact]
    public void NO_FILE_GROWS_A_SECOND_SUMMARY_IN_ONE_DOC_BLOCK()
    {
        var wrong = new List<string>();
        var seen = 0;
        foreach (var rel in Sources())
        {
            var n = DoubledBlocks(File.ReadAllText(Path.Combine(SourceTree.RepoRoot(), rel)));
            seen++;
            var pinned = Known.TryGetValue(rel, out var k) ? k : 0;
            if (n != pinned) wrong.Add($"{rel}: {n} doubled block(s), pinned at {pinned}");
        }

        //the floor counts the files read, a reader that found none would leave the failure list empty and the census would pass on nothing
        Assert.True(seen > 200, $"the census read only {seen} source files - the reader is broken");
        Assert.True(wrong.Count == 0,
            "a doc block holds ONE summary; a second is invisible to every tool:"
            + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", wrong)
            + Environment.NewLine
            + "(if you FIXED one, lower its pin - that is the direction this number moves.)");
    }

    //an empty result proves nothing about the tree until the pattern matches a known double that looks like ordinary prose
    [Fact]
    public void THE_CENSUS_CAN_SEE_A_PLANTED_DOUBLE()
    {
        var planted = string.Join('\n',
        [
            "    /// <summary>The first one, which documents the member below.</summary>",
            "    /// <summary>The second, which documents nothing at all.</summary>",
            "    private const string X = \"x\";",
        ]);

        Assert.Equal(1, DoubledBlocks(planted));
    }

    //the census must not fire on one summary with a paragraph and a param. two summaries in separate blocks document two members and must not fire either
    [Fact]
    public void THE_CENSUS_LEAVES_ORDINARY_DOC_BLOCKS_ALONE()
    {
        var rich = string.Join('\n',
        [
            "    /// <summary>One summary.",
            "    ///",
            "    /// <para>With a paragraph.</para></summary>",
            "    /// <param name=\"a\">And a param.</param>",
            "    private static void M(int a) { }",
        ]);

        var two = string.Join('\n',
        [
            "    /// <summary>The first member.</summary>",
            "    private const string A = \"a\";",
            "",
            "    /// <summary>The second member.</summary>",
            "    private const string B = \"b\";",
        ]);

        Assert.Equal(0, DoubledBlocks(rich));
        Assert.Equal(0, DoubledBlocks(two));
    }
    //pin this count at zero, since this shape has no old instances. the floor on files read stops a reader that found nothing from passing.
    [Fact]
    public void NO_DOC_BLOCK_DOCUMENTS_A_CLOSING_BRACE()
    {
        var wrong = new List<string>();
        var seen = 0;
        foreach (var rel in Sources())
        {
            var n = OrphanedBlocks(File.ReadAllText(Path.Combine(SourceTree.RepoRoot(), rel)));
            seen++;
            if (n != 0) wrong.Add($"{rel}: {n} block(s) documenting a closing brace");
        }

        Assert.True(seen > 200, $"the census read only {seen} source files - the reader is broken");
        Assert.True(wrong.Count == 0,
            "a /// run inside a method body is a plain comment attached to nothing - move it above "
            + "the member it was written for:"
            + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", wrong));
    }

    //use the exact shape that escaped the census. the doc sits at the end of a method body, and only the next brace shows that it documents nothing
    [Fact]
    public void THE_CENSUS_CAN_SEE_A_BLOCK_THAT_DOCUMENTS_A_BRACE()
    {
        var planted = string.Join('\n',
        [
            "    public void First()",
            "    {",
            "        DoSomething();",
            "",
            "    /// <summary>Written for the method below, stranded in the one above.</summary>",
            "    }",
            "",
            "    [Fact]",
            "    public void Second() { }",
        ]);

        Assert.Equal(1, OrphanedBlocks(planted));
        //the doubled-block reader cannot see this run, it holds one summary. that is why a separate reader exists rather than a wider pattern on the old one
        Assert.Equal(0, DoubledBlocks(planted));
    }

    //the brace census must not fire on a member behind an attribute or a blank line. a doc on the last member of a type has a declaration before its brace.
    [Fact]
    public void THE_BRACE_CENSUS_LEAVES_ORDINARY_DOC_BLOCKS_ALONE()
    {
        var attributed = string.Join('\n',
        [
            "    /// <summary>Documented, behind an attribute.</summary>",
            "    [Fact]",
            "    public void M() { }",
        ]);

        var spaced = string.Join('\n',
        [
            "    /// <summary>Documented, behind a blank line.</summary>",
            "",
            "    public void M() { }",
        ]);

        var last = string.Join('\n',
        [
            "    /// <summary>The last member of its type.</summary>",
            "    private const string X = \"x\";",
            "}",
        ]);

        Assert.Equal(0, OrphanedBlocks(attributed));
        Assert.Equal(0, OrphanedBlocks(spaced));
        Assert.Equal(0, OrphanedBlocks(last));
    }
}
