using Gatto.Core.Client;
using Gatto.Core.Home;
using Gatto.Core.Loop;
using Xunit;

namespace Gatto.Tests;

public sealed class ResumeUpdateTests
{
    private static BaselineSources S(string? date = "2026-09-28", string? memory = "- fact one\n- fact two",
        (string Path, string Sha)[]? files = null, (string Ext, string Line)[]? policy = null, string role = "r", string model = "m") =>
        new(date, memory,
            (files ?? new[] { (@"C:\proj\GATTO.md", "aa") }).Select(f => new ContextFileMark(f.Path, f.Sha)).ToList(),
            (policy ?? new[] { ("web_search", "search when asked") }).Select(p => new PolicyMark(p.Ext, p.Line)).ToList(), role, model);

    private static string? Text(BaselineSources was, BaselineSources now, string? role = null, string? model = null) =>
        ResumeUpdate.Text(ResumePlan.Delta(was, now), role, model);

    [Fact]
    public void No_change_is_no_update()
    {
        Assert.True(ResumePlan.Delta(S(), S()).IsEmpty);
        Assert.Null(Text(S(), S()));
    }

    [Fact]
    public void Each_source_alone_gives_its_own_line()
    {
        const string first = "[gatto: this session was resumed. These facts changed since it began.]";
        Assert.Equal(first + "\nToday's date is now 2026-09-29.", Text(S(), S(date: "2026-09-29")));
        Assert.Equal(first + "\nProject memory changed. Added: - fact three Removed: - fact one",
            Text(S(), S(memory: "- fact two\n- fact three")));
        Assert.Equal(first + @"\nContext file changed: C:\proj\GATTO.md (read it again before relying on it).".Replace(@"\n", "\n"),
            Text(S(), S(files: new[] { (@"C:\proj\GATTO.md", "bb") })));
        Assert.Equal(first + @"\nContext file added: C:\proj\b.md. Context file removed: C:\proj\GATTO.md.".Replace(@"\n", "\n"),
            Text(S(), S(files: new[] { (@"C:\proj\b.md", "cc") })));
        Assert.Equal(first + "\nExtension policy for web_search is now: search only when asked. Extension policy for ask_user no longer applies.",
            Text(S(policy: new[] { ("ask_user", "ask"), ("web_search", "search when asked") }), S(policy: new[] { ("web_search", "search only when asked") })));
        Assert.Equal(first + "\nThe role instructions changed. The current text replaces the earlier one:\nbe a coder",
            Text(S(), S(role: "r2"), role: "be a coder"));
        Assert.Equal(first + "\nThe model instructions changed. The current text replaces the earlier one:\nthink first",
            Text(S(), S(model: "m2"), model: "think first"));
    }

    [Fact]
    public void Every_source_at_once_keeps_the_order()
    {
        var text = Text(S(), S(date: "2026-09-29", memory: "- fact one", files: new[] { (@"C:\proj\GATTO.md", "bb") },
            policy: Array.Empty<(string, string)>(), role: "r2", model: "m2"), "role text", "model text")!;
        var order = new[] { "Today's date", "Project memory", "Context file changed", "Extension policy", "The role instructions", "The model instructions" }
            .Select(s => text.IndexOf(s, StringComparison.Ordinal)).ToList();
        Assert.DoesNotContain(-1, order);
        Assert.Equal(order.OrderBy(i => i), order);
    }

    [Fact]
    public void A_context_file_is_named_and_never_quoted()
    {
        var content = "PLANTED CONTEXT 7c1e";
        var text = Text(S(), S(files: new[] { (@"C:\proj\GATTO.md", BaselineMarks.Sha256Hex(content)) }))!;
        Assert.Contains(@"C:\proj\GATTO.md", text, StringComparison.Ordinal);
        Assert.DoesNotContain(content, text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_truncation_note_is_not_a_fact()
    {
        Assert.True(ResumePlan.Delta(S(memory: "- fact one\n[index truncated at budget - 2 facts not shown]"), S(memory: "- fact one\n[index truncated at budget - 3 facts not shown]")).IsEmpty);
    }

    [Fact]
    public void The_effective_sources_are_the_newest_update_under_this_baseline()
    {
        var stored = new SessionBaseline(2, "generalist", "test-model", "all", new ThinkingMark(null, null), new List<ToolMark>(), S());
        var older = new SessionUpdate("older", S(date: "2026-09-01"), 1);
        var current = new SessionUpdate("current", S(date: "2026-09-29"), 2);
        var newest = new SessionUpdate("newest", S(date: "2026-09-30"), 2);
        ChatMessage U(SessionUpdate? u) => new("user", "typed", Update: u);
        Assert.Equal("2026-09-28", ResumePlan.EffectiveSources(stored, new[] { U(null), U(older) }).Date);
        Assert.Equal("2026-09-30", ResumePlan.EffectiveSources(stored, new[] { U(current), U(older), U(newest) }).Date);
    }
}
