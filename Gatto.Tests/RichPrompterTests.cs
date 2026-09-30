using System.Collections.Concurrent;
using Gatto.Core.Tools;
using Gatto.Repl;
using Gatto.Tests.Fakes;
using Gatto.Terminal;

namespace Gatto.Tests;

//the line reader stays live code, the deny-reason capture reads through it
public class RichPrompterTests
{
    private sealed class FeedKeys(BlockingCollection<ConsoleKeyInfo> q) : IKeySource
    {
        public ConsoleKeyInfo ReadKey() => q.Take();
        public bool KeyAvailable => q.Count > 0;
    }

    private static ConsoleKeyInfo K(char c) => new(c, ConsoleKey.A, false, false, false);
    private static ConsoleKeyInfo Enter() => new('\r', ConsoleKey.Enter, false, false, false);
    private static ConsoleKeyInfo Backspace() => new('\b', ConsoleKey.Backspace, false, false, false);

    [Fact]
    public void ReadLine_PreservesTab()
    {
        //the rich reader must keep tabs intact, pasted columnar input survives unchanged
        var feed = new BlockingCollection<ConsoleKeyInfo>();
        feed.Add(K('a'));
        feed.Add(new ConsoleKeyInfo('\t', ConsoleKey.Tab, false, false, false));
        feed.Add(K('b'));
        feed.Add(Enter());
        var line = RichPrompter.ReadLine(new FeedKeys(feed), new RecordingSurface(), CancellationToken.None);
        Assert.Equal("a\tb", line);
    }

    [Fact]
    public void ReadLine_BackspaceEdits()
    {
        //this drives ReadLine directly with a preloaded key queue, no pump or focus race is involved
        var feed = new BlockingCollection<ConsoleKeyInfo>();
        foreach (var c in "ab") feed.Add(K(c));
        feed.Add(Backspace());
        feed.Add(K('c'));
        feed.Add(Enter());
        var s = new RecordingSurface();
        var line = RichPrompter.ReadLine(new FeedKeys(feed), s, CancellationToken.None);
        Assert.Equal("ac", line);
        Assert.Contains("\b \b", s.Text);   //the erase must echo as backspace, space, backspace.
    }
}

//the free-text row is unconditional for single-select and absent for multi-select
[Collection("e2e")]   //this class redirects stdio, so it runs serialized
public class AskOptionPlainMenuTests : IDisposable
{
    private readonly TextWriter _priorOut = Console.Out;
    private readonly TextReader _priorIn = Console.In;
    public void Dispose() { Console.SetOut(_priorOut); Console.SetIn(_priorIn); }

    //runs the console prompter over redirected stdio, returns the answers and the whole of stdout
    private static (IReadOnlyList<AskAnswer> Answers, string Text) DrivePlain(AskQuestion q, string stdin)
    {
        var stdout = new StringWriter();
        var priorOut = Console.Out;
        var priorIn = Console.In;
        try
        {
            Console.SetOut(stdout);
            Console.SetIn(new StringReader(stdin));
            var answers = new ConsolePrompter().AskAsync(new[] { q }, CancellationToken.None)
                .GetAwaiter().GetResult();
            return (answers, stdout.ToString());
        }
        finally
        {
            Console.SetOut(priorOut);
            Console.SetIn(priorIn);
        }
    }

    [Fact]
    public void AskOption_ImplicitFromString_SetsLabelOnly()
    {
        AskOption opt = "hello";
        Assert.Equal(new AskOption("hello", null, false), opt);
    }

    [Fact]
    public void AskQuestion_LegacyStringListCtor_BuildsNeutralAskOptions()
    {
        //the string-list ctor must behave exactly like hand-built AskOption values, so old call sites and shipped extensions keep working unchanged
        var q = new AskQuestion("q", "h", new List<string> { "a", "b" }, false);
        Assert.Equal(new AskOption("a"), q.Options[0]);
        Assert.Equal(new AskOption("b"), q.Options[1]);
        Assert.Null(q.Options[0].Description);
        Assert.False(q.Options[0].Recommended);
    }

    [Fact]
    public void MenuLines_RendersDescriptionRecommendedSuffixAndFreeTextEntry()
    {
        var q = new AskQuestion("Which?", "Lang",
            new[]
            {
                new AskOption("Go", "Fast compiles", Recommended: true),
                new AskOption("Rust", "Memory safety"),
            },
            MultiSelect: false);

        var rows = PrompterCore.MenuLines(q, glyphs: GlyphSet.Unicode);

        Assert.Equal(new[]
        {
            "[Lang] Which?",
            "  1. Go (Recommended)",
            "     Fast compiles",
            "  2. Rust",
            "     Memory safety",
            "  3. Type my own answer…",
            "pick 1-2 or type your answer",
        }, rows);
    }

    [Fact]
    public void MenuLines_OnlyFirstRecommendedGetsSuffix_NoErrorNoReorder()
    {
        //when several options ask for recommendation only the first gets the suffix. no error, no reorder.
        var q = new AskQuestion("Which?", "Lang",
            new[] { new AskOption("Go", Recommended: true), new AskOption("Rust", Recommended: true) },
            MultiSelect: false);

        var rows = PrompterCore.MenuLines(q, glyphs: GlyphSet.Unicode);

        Assert.Equal("  1. Go (Recommended)", rows[1]);
        Assert.Equal("  2. Rust", rows[2]);
    }

    [Fact]
    public void MenuLines_NoDescription_NoIndentedLine()
    {
        var q = new AskQuestion("Which?", "Lang", new[] { new AskOption("Go"), new AskOption("Rust") }, false);
        var rows = PrompterCore.MenuLines(q, glyphs: GlyphSet.Unicode);
        //the count covers title, two options, the free-text row, and the hint.
        Assert.Equal(5, rows.Count);
        Assert.DoesNotContain(rows, r => r.StartsWith("     ", StringComparison.Ordinal));
    }

    [Fact]
    public void MenuLines_SingleSelect_AlwaysCarriesTheFreeTextEntry()
    {
        //a single-select menu always includes the free-text row
        var q = new AskQuestion("Which?", "Lang", new[] { "Go", "Rust" }, false);
        var rows = PrompterCore.MenuLines(q, glyphs: GlyphSet.Unicode);
        Assert.Equal("  3. Type my own answer…", rows[3]);
    }

    [Fact]
    public void MenuLines_MultiSelect_NeverGetsFreeTextEntry()
    {
        //a multi-select question must never get a free-text row
        var q = new AskQuestion("Which?", "Lang", new[] { "Go", "Rust" }, true);
        var rows = PrompterCore.MenuLines(q, glyphs: GlyphSet.Unicode);
        Assert.DoesNotContain(rows, r => r.Contains("Type my own answer"));
    }

    [Fact]
    public void TryParsePicks_ReturnsOptionLabels_MultiSelect()
    {
        var q = new AskQuestion("Which?", "Lang",
            new[] { new AskOption("Go", "desc"), new AskOption("Rust") }, true);
        var ok = PrompterCore.TryParsePicks(q, "1,2", out var selected);
        Assert.True(ok);
        Assert.Equal(new[] { "Go", "Rust" }, selected);
    }

    [Fact]
    public void ConsolePrompter_FreeTextPick_ReadsNextLineAsVerbatimAnswer()
    {
        var q = new AskQuestion("Which?", "Lang", new[] { "Go", "Rust" }, false);
        var (answers, text) = DrivePlain(q, "3\nzig, my favorite\n");

        Assert.Equal(new[] { "zig, my favorite" }, answers[0].Selected);
        Assert.Contains("  3. Type my own answer…", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ConsolePrompter_EmptyInput_RepromptsSilently_ThenAccepts()
    {
        //a stray enter must re-prompt rather than answer with empty (a piped run would otherwise accept an empty string)
        var q = new AskQuestion("Which?", "Lang", new[] { "Go", "Rust" }, false);
        var (answers, text) = DrivePlain(q, "\n1\n");

        Assert.Equal(new[] { "Go" }, answers[0].Selected);
        //two prompt lines and no retry message prove the re-prompt is silent
        Assert.Equal(2, text.Split("> ").Length - 1);
        Assert.DoesNotContain("pick 1-2 or type your answer", text[(text.IndexOf("> ", StringComparison.Ordinal) + 2)..],
            StringComparison.Ordinal);
    }

    [Fact]
    public void ConsolePrompter_NormalPick_StillSelectsOption_NotFreeText()
    {
        var q = new AskQuestion("Which?", "Lang", new[] { "Go", "Rust" }, false);
        var (answers, _) = DrivePlain(q, "1\n");
        Assert.Equal(new[] { "Go" }, answers[0].Selected);
    }

    [Fact]
    public void ConsolePrompter_NumberBeyondFreeTextIndex_IsVerbatimAnswer_NotFreeTextRead()
    {
        var q = new AskQuestion("Which?", "Lang", new[] { "Go", "Rust" }, false);
        var (answers, _) = DrivePlain(q, "9\n");
        Assert.Equal(new[] { "9" }, answers[0].Selected);
    }

    [Fact]
    public void ConsolePrompter_NonPickText_RemainsVerbatimAnswer_TodaysContractStillHolds()
    {
        var q = new AskQuestion("Which?", "Lang", new[] { "Go", "Rust" }, false);
        var (answers, _) = DrivePlain(q, "zig\n");
        Assert.Equal(new[] { "zig" }, answers[0].Selected);
    }

    [Fact]
    public void ConsolePrompter_MultiSelect_CommaPick_ParseUnchanged()
    {
        var q = new AskQuestion("Which?", "Lang", new[] { "Go", "Rust" }, true);
        var (answers, _) = DrivePlain(q, "1,2\n");
        Assert.Equal(new[] { "Go", "Rust" }, answers[0].Selected);
    }

    [Fact]
    public void ConsolePrompter_MultiSelect_NeverTriggersFreeTextRead_EvenAtOptionCountPlusOne()
    {
        //multi-select has no free-text row, so entering 3 must fall through to ordinary handling and not start the two-line read
        var q = new AskQuestion("Which?", "Lang", new[] { "Go", "Rust" }, true);
        var (answers, _) = DrivePlain(q, "3\n");
        Assert.Equal(new[] { "3" }, answers[0].Selected);
    }

    [Fact]
    public void ConsolePrompter_NormalMenuWithDescriptionAndRecommended_EmitsNoEscBytes()
    {
        var q = new AskQuestion("Which?", "Lang",
            new[]
            {
                new AskOption("Go", "Fast compiles", Recommended: true),
                new AskOption("Rust", "Memory safety"),
            },
            MultiSelect: false);
        var (_, text) = DrivePlain(q, "1\n");
        Assert.DoesNotContain('\x1b', text);   //checked as a char, so the escape can't hide behind a culture-aware comparison
    }

    [Fact]
    public void ConsolePrompter_PlainOutput_DescriptionAndRecommended_ModelEscapesRenderInert()
    {
        //an escape embedded in a description must be stripped and shown as inert text
        var evilDesc = "safe\x1b[31mtail";
        var q = new AskQuestion("Which?", "Lang",
            new[] { new AskOption("Go", evilDesc, Recommended: true), new AskOption("Rust") },
            MultiSelect: false);
        var (_, text) = DrivePlain(q, "1\n");

        Assert.DoesNotContain('\x1b', text);
        Assert.Contains("[31mtail", text, StringComparison.Ordinal);   //the text after a stripped escape must remain as literal characters.
    }
}
