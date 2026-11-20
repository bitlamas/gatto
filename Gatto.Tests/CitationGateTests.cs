using Gatto.Core.Client;
using Gatto.Core.Loop;
using Gatto.Core.Tools;
using Gatto.Core.Web;
using Gatto.Extensions;
using Gatto.Roles;

namespace Gatto.Tests;

//a CitationLedger that counts reads of Entries, so a test can show a disarmed gate reads the ledger zero times. the other behaviour is the real ledger's
file sealed class CountingLedger() : CitationLedger(() => null)
{
    public int EntriesReads { get; private set; }
    public override IReadOnlyList<LedgerEntry> Entries { get { EntriesReads++; return base.Entries; } }
}

public sealed class CitationGateTests
{

    //a write_file result with the given path. the content argument doesn't matter, the gate reads the file back through the injected readFile.
    private static HookPayload Write(string path, string text = "wrote 3 bytes", bool isError = false) =>
        new(Call: new ToolCall("c1", "write_file", $"{{\"path\":\"{path}\",\"content\":\"x\"}}"),
            Result: new ToolResult(text, IsError: isError));

    private static CitationGate ArmedGate(CitationLedger ledger, string fileText) =>
        new(ledger, _ => fileText) { Armed = true };

    [Fact]
    public async Task A_never_fetched_url_appends_the_reminder_with_the_three_exits()
    {
        var ledger = new CitationLedger(() => null);
        var gate = ArmedGate(ledger, "The answer is at https://example.com/foo per my research.");

        var res = await gate.OnToolResultAsync(Write("out.md"));

        Assert.NotNull(res);
        Assert.False(res!.IsError);
        Assert.Contains("wrote 3 bytes", res.Text);
        Assert.Contains("[citation gate] not fetched and in no search result this session:", res.Text);
        Assert.Contains("https://example.com/foo", res.Text);
        Assert.Contains("Fetch and read each with web_fetch", res.Text);
        Assert.Contains("replace it with a source you did read", res.Text);
        Assert.Contains("delete the claim", res.Text);
    }

    [Fact]
    public async Task Edit_file_also_triggers_the_gate()
    {
        var ledger = new CitationLedger(() => null);
        var gate = new CitationGate(ledger, _ => "cite https://nowhere.test/x here") { Armed = true };

        var payload = new HookPayload(
            Call: new ToolCall("c1", "edit_file", "{\"path\":\"out.md\",\"old_string\":\"a\",\"new_string\":\"b\"}"),
            Result: new ToolResult("edited out.md"));
        var res = await gate.OnToolResultAsync(payload);

        Assert.NotNull(res);
        Assert.Contains("not fetched and in no search result", res!.Text);
    }

    [Fact]
    public async Task Verified_url_recorded_this_session_returns_null()
    {
        var ledger = new CitationLedger(() => null);
        ledger.Record("https://example.com/foo", "the page content", searchOnly: false);
        var gate = ArmedGate(ledger, "See https://example.com/foo for the details.");

        Assert.Null(await gate.OnToolResultAsync(Write("out.md")));
    }

    [Fact]
    public async Task Verified_compares_after_trimming_one_trailing_slash()
    {
        var ledger = new CitationLedger(() => null);
        ledger.Record("https://example.com/foo", "content", searchOnly: false);
        var gate = ArmedGate(ledger, "Cited as https://example.com/foo/ in the report.");

        Assert.Null(await gate.OnToolResultAsync(Write("out.md")));
    }

    [Fact]
    public async Task A_url_seen_only_in_a_search_is_listed_beside_a_never_fetched_one_never_blocks()
    {
        var ledger = new CitationLedger(() => null);
        ledger.Record("query", "results include https://seen.com/bar among others", searchOnly: true);
        var gate = ArmedGate(ledger, "See https://fab.com/x and https://seen.com/bar for context.");

        var res = await gate.OnToolResultAsync(Write("out.md"));

        Assert.NotNull(res);
        Assert.False(res!.IsError);                                        //the gate warns but never blocks.
        Assert.Contains("not fetched and in no search result", res.Text);
        Assert.Contains("https://fab.com/x", res.Text);
        Assert.Contains("\nin a search result, not fetched: https://seen.com/bar", res.Text);
        Assert.Contains("https://seen.com/bar", res.Text);
    }

    [Fact]
    public async Task A_search_seen_url_alone_writes_no_note()
    {
        var ledger = new CitationLedger(() => null);
        ledger.Record("query", "results include https://seen.com/bar", searchOnly: true);
        var gate = ArmedGate(ledger, "Only https://seen.com/bar is cited here.");

        Assert.Null(await gate.OnToolResultAsync(Write("out.md")));
    }

    [Fact]
    public async Task Quote_present_in_recorded_content_is_no_mismatch()
    {
        var ledger = new CitationLedger(() => null);
        ledger.Record("https://ok.com/a", "The sky is blue and the grass is green today.", searchOnly: false);
        var gate = ArmedGate(ledger, "Per https://ok.com/a, \"the grass is green today\" is a fact.");

        Assert.Null(await gate.OnToolResultAsync(Write("out.md")));        //all cites verify and the quote matches, so nothing is reported.
    }

    [Fact]
    public async Task Quote_absent_from_recorded_content_warns_quote_mismatch()
    {
        var ledger = new CitationLedger(() => null);
        ledger.Record("https://ok.com/a", "The sky is blue.", searchOnly: false);
        //the fabricated url triggers the reminder round. the verified url has a quote absent from the recorded content.
        var fileText =
            "See https://fab.com/z for more.\n\n" +
            "Per https://ok.com/a, \"this exact quote is absent from the source\" indeed.";
        var gate = ArmedGate(ledger, fileText);

        var res = await gate.OnToolResultAsync(Write("out.md"));

        Assert.NotNull(res);
        Assert.Contains("QUOTE_MISMATCH: https://ok.com/a", res!.Text);
    }

    [Fact]
    public async Task Quote_mismatch_only_counts_within_the_same_paragraph()
    {
        var ledger = new CitationLedger(() => null);
        ledger.Record("https://ok.com/a", "The sky is blue.", searchOnly: false);
        //the bad quote sits in a different paragraph from the verified url, and must not be attached to it.
        var fileText =
            "Here is https://fab.com/z which is bogus.\n\n" +
            "Per https://ok.com/a nothing is quoted.\n\n" +
            "\"this exact quote is absent from the source\" stands alone.";
        var gate = ArmedGate(ledger, fileText);

        var res = await gate.OnToolResultAsync(Write("out.md"));

        Assert.NotNull(res);
        Assert.Contains("not fetched and in no search result", res!.Text);
        Assert.DoesNotContain("QUOTE_MISMATCH", res.Text);
    }

    [Fact]
    public async Task Fourth_round_for_the_same_file_goes_silent()
    {
        var ledger = new CitationLedger(() => null);
        var gate = ArmedGate(ledger, "cite https://fab.com/x here");

        Assert.NotNull(await gate.OnToolResultAsync(Write("out.md")));
        Assert.NotNull(await gate.OnToolResultAsync(Write("out.md")));
        Assert.NotNull(await gate.OnToolResultAsync(Write("out.md")));
        Assert.Null(await gate.OnToolResultAsync(Write("out.md")));
    }

    //the process keeps one gate across /new, so a reset gives a spent file its notes again
    [Fact]
    public async Task A_RESET_GIVES_A_SPENT_FILE_ITS_NOTES_AGAIN()
    {
        var gate = ArmedGate(new CitationLedger(() => null), "cite https://fab.com/x here");
        for (var i = 0; i < 3; i++) await gate.OnToolResultAsync(Write("out.md"));
        Assert.Null(await gate.OnToolResultAsync(Write("out.md")));

        gate.Reset();

        Assert.NotNull(await gate.OnToolResultAsync(Write("out.md")));
    }

    private const string LastNoteForOut ="\nLast note for out.md: a later write to it gets no citation note.";

    //only the third note says it is the last, and the line is the note's final one
    [Fact]
    public async Task THE_THIRD_NOTE_FOR_A_PATH_SAYS_IT_IS_THE_LAST()
    {
        var ledger = new CitationLedger(() => null);
        var gate = ArmedGate(ledger, "cite https://fab.com/x here");

        var first = (await gate.OnToolResultAsync(Write("out.md")))!.Text;
        var second = (await gate.OnToolResultAsync(Write("out.md")))!.Text;
        var third = (await gate.OnToolResultAsync(Write("out.md")))!.Text;

        Assert.DoesNotContain("Last note", first);
        Assert.DoesNotContain("Last note", second);
        Assert.EndsWith(LastNoteForOut, third);
        Assert.Null(await gate.OnToolResultAsync(Write("out.md")));
    }

    //the line follows the warnings, so a note with an unread URL still ends with it
    [Fact]
    public async Task THE_LAST_NOTE_LINE_COMES_AFTER_THE_SEARCH_LINE()
    {
        var ledger = new CitationLedger(() => null);
        ledger.Record("query", "results include https://seen.com/y", searchOnly: true);
        var gate = ArmedGate(ledger, "cite https://fab.com/x and https://seen.com/y here");

        await gate.OnToolResultAsync(Write("out.md"));
        await gate.OnToolResultAsync(Write("out.md"));
        var third = (await gate.OnToolResultAsync(Write("out.md")))!.Text;

        Assert.Contains("\nin a search result, not fetched: https://seen.com/y" + LastNoteForOut, third);
    }

    [Fact]
    public async Task ANOTHER_PATHS_FIRST_NOTE_DOES_NOT_SAY_LAST()
    {
        var ledger = new CitationLedger(() => null);
        var gate = ArmedGate(ledger, "cite https://fab.com/x here");

        for (var i = 0; i < 3; i++) await gate.OnToolResultAsync(Write("a.md"));

        Assert.DoesNotContain("Last note", (await gate.OnToolResultAsync(Write("b.md")))!.Text);
    }

    [Fact]
    public async Task A_different_file_keeps_its_own_counter()
    {
        var ledger = new CitationLedger(() => null);
        var gate = ArmedGate(ledger, "cite https://fab.com/x here");

        await gate.OnToolResultAsync(Write("a.md"));
        await gate.OnToolResultAsync(Write("a.md"));
        await gate.OnToolResultAsync(Write("a.md"));
        Assert.Null(await gate.OnToolResultAsync(Write("a.md")));        //three reminders are the budget for a.md, so the fourth call is silent

        Assert.NotNull(await gate.OnToolResultAsync(Write("b.md")));     //b.md has its own budget, so the reminder still fires for it
    }

    [Fact]
    public async Task Disarmed_returns_null_with_zero_ledger_reads()
    {
        var counting = new CountingLedger();
        var gate = new CitationGate(counting, _ => "cite https://fab.com/x here") { Armed = false };

        Assert.Null(await gate.OnToolResultAsync(Write("out.md")));
        Assert.Equal(0, counting.EntriesReads);

        //re-arming mid-session makes the gate read the ledger again, which is what proves it read nothing while disarmed.
        gate.Armed = true;
        Assert.NotNull(await gate.OnToolResultAsync(Write("out.md")));
        Assert.True(counting.EntriesReads > 0);
    }

    [Fact]
    public async Task Error_result_is_left_untouched()
    {
        var ledger = new CitationLedger(() => null);
        var gate = ArmedGate(ledger, "cite https://fab.com/x here");

        Assert.Null(await gate.OnToolResultAsync(Write("out.md", "permission denied", isError: true)));
    }

    [Fact]
    public async Task Non_deliverable_tool_is_ignored()
    {
        var ledger = new CitationLedger(() => null);
        var gate = new CitationGate(ledger, _ => "cite https://fab.com/x here") { Armed = true };

        var payload = new HookPayload(
            Call: new ToolCall("c1", "read_file", "{\"path\":\"out.md\"}"),
            Result: new ToolResult("file contents"));
        Assert.Null(await gate.OnToolResultAsync(payload));
    }

    [Fact]
    public async Task No_citations_in_the_file_returns_null()
    {
        var ledger = new CitationLedger(() => null);
        var gate = ArmedGate(ledger, "A plain deliverable with no URLs at all.");

        Assert.Null(await gate.OnToolResultAsync(Write("out.md")));
    }

    //the gate reads back what the write tools wrote, by their own resolver, a ~ path included
    [Fact]
    public async Task A_WRITE_TO_A_TILDE_PATH_IS_READ_BACK_WHERE_IT_WENT()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "gate-cwd");
        var written = ToolArgs.Resolve(cwd, "~\\notes.md");
        var asked = new List<string>();
        var gate = new CitationGate(new CitationLedger(() => null), p => { asked.Add(p); return p == written ? "cite https://fab.com/x here" : null; },
            p => ToolArgs.Resolve(cwd, p)) { Armed = true };

        var res = await gate.OnToolResultAsync(Write("~\\\\notes.md"));

        Assert.Equal([written], asked);
        Assert.Contains("not fetched and in no search result", res!.Text);
    }

    //one file written by two spellings, another case or a leading .\, spends one budget
    [Theory]
    [InlineData("research\\\\Notes.md", "research\\\\notes.md")]
    [InlineData(".\\\\a.md", "a.md")]
    public async Task TWO_SPELLINGS_OF_ONE_FILE_SHARE_ITS_THREE_NOTES(string first, string second)
    {
        var cwd = Path.Combine(Path.GetTempPath(), "gate-cwd");
        var gate = new CitationGate(new CitationLedger(() => null), _ => "cite https://fab.com/x here", p => ToolArgs.Resolve(cwd, p)) { Armed = true };

        for (var i = 0; i < 3; i++) Assert.NotNull(await gate.OnToolResultAsync(Write(first)));

        Assert.Null(await gate.OnToolResultAsync(Write(second)));
    }

    //a path the resolver refuses leaves nothing to read, and the write goes through untouched
    [Fact]
    public async Task A_PATH_THE_RESOLVER_REFUSES_WRITES_NO_NOTE()
    {
        var gate = new CitationGate(new CitationLedger(() => null), _ => "cite https://fab.com/x here",
            _ => throw new ArgumentException("bad path")) { Armed = true };

        Assert.Null(await gate.OnToolResultAsync(Write("out.md")));
    }
}
