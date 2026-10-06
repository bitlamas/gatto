using System.Text.Json;
using Gatto.Core.Client;
using Gatto.Core.Home;
using Gatto.Core.Loop;
using Gatto.Repl.Render;
using Gatto.Repl.Term;
using Gatto.Terminal;
using Xunit;

namespace Gatto.Tests;

public sealed class SessionBaselineRecordTests : IDisposable
{
    private static readonly Theme T = new(new TermCaps(true, true));
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-baseline-record-").FullName;
    public void Dispose() { try { Directory.Delete(_home, true); } catch { } }

    private static BaselineSources Full() => new("2026-09-29", "- a fact\n- another",
        new List<ContextFileMark> { new(@"C:\proj\GATTO.md", "aa"), new(@"C:\proj\sub\GATTO.md", "bb") },
        new List<PolicyMark> { new("web_search", "use it \"quoted\"") }, "r1", "m1");

    private static BaselineSources Bare() => new(null, null, new List<ContextFileMark>(), new List<PolicyMark>(), "r0", "m0");

    private static SessionBaseline Baseline(BaselineSources sources, ThinkingMark? thinking = null) =>
        new(3, "generalist", "test-model", "none", thinking ?? new ThinkingMark("high", "{\"chat_template_kwargs\":{\"enable_thinking\":true}}"),
            new List<ToolMark> { new("read_file", "t1"), new("shell", "t2") }, sources);

    //the round trip compares every field, a list element by element
    private static bool SameSources(BaselineSources a, BaselineSources b) =>
        a.Date == b.Date && a.Memory == b.Memory && a.ContextFiles.SequenceEqual(b.ContextFiles) && a.Policy.SequenceEqual(b.Policy)
        && a.RoleAppendSha256 == b.RoleAppendSha256 && a.ModelAppendSha256 == b.ModelAppendSha256;

    private static void AssertSame(SessionBaseline expected, SessionBaseline? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal((expected.Seq, expected.Role, expected.Model, expected.ReasoningHistory, expected.Thinking),
            (actual!.Seq, actual.Role, actual.Model, actual.ReasoningHistory, actual.Thinking));
        Assert.True(BaselineMarks.SameTools(expected.Tools, actual.Tools));
        Assert.True(SameSources(expected.Sources, actual.Sources));
    }

    private static SessionBaseline? RoundTrip(SessionBaseline b)
    {
        var line = SessionStore.ChatJson(new ChatMessage("system", "sys"), @"C:\proj", b);
        using var doc = JsonDocument.Parse(line);
        return SessionStore.ParseBaseline(doc.RootElement);
    }

    [Fact]
    public void Both_writers_write_the_same_system_record()
    {
        var store = new SessionStore(_home, @"C:\proj");
        var convo = new Conversation("sys", baseline: Baseline(Full()));
        convo.AddUser("u");
        store.Save(convo);
        var saved = File.ReadAllLines(store.CurrentPath!)[0];
        var built = TranscriptStore.BuildLines(new TranscriptModel("generalist"), convo, @"C:\proj", store.Identity)[0];
        Assert.Equal(saved, built);
        Assert.Contains("\"baseline\":", saved, StringComparison.Ordinal);
        Assert.Contains($"\"session_id\":\"{store.Identity.IdText}\"", saved, StringComparison.Ordinal);
    }

    [Fact]
    public void A_baseline_with_every_field_set_and_one_with_every_optional_field_null_survive_a_round_trip()
    {
        AssertSame(Baseline(Full()), RoundTrip(Baseline(Full())));
        var bare = Baseline(Bare(), new ThinkingMark(null, null));
        AssertSame(bare, RoundTrip(bare));
        var line = SessionStore.ChatJson(new ChatMessage("system", "sys"), null, bare);
        foreach (var absent in new[] { "\"level\"", "\"body\"", "\"date\"", "\"memory\"" })
            Assert.DoesNotContain(absent, line, StringComparison.Ordinal);
    }

    [Fact]
    public void A_record_with_no_baseline_reads_as_null()
    {
        Assert.Null(SessionStore.LoadBaseline(Path.Combine(AppContext.BaseDirectory, "Fixtures", "legacy-session-7d209d82.jsonl")));
        Assert.DoesNotContain("\"baseline\"", SessionStore.ChatJson(new ChatMessage("system", "sys"), @"C:\proj"), StringComparison.Ordinal);
    }

    [Fact]
    public void An_update_survives_a_save_and_a_load()
    {
        var update = new SessionUpdate("[gatto: this session was resumed.]\nToday's date is now 2026-09-29.", Full(), 3);
        var line = SessionStore.ChatJson(new ChatMessage("user", "what I typed", Update: update));
        using var doc = JsonDocument.Parse(line);
        var back = SessionStore.ParseChatElement(doc.RootElement);
        Assert.Equal("what I typed", back.Content);
        Assert.Equal((update.Text, update.BaseSeq), (back.Update!.Text, back.Update.BaseSeq));
        Assert.True(SameSources(update.Sources, back.Update.Sources));
        using var plain = JsonDocument.Parse(SessionStore.ChatJson(new ChatMessage("user", "no update")));
        Assert.Null(SessionStore.ParseChatElement(plain.RootElement).Update);
    }

    [Fact]
    public void A_rebuilt_session_shows_the_typed_text_and_no_update_text()
    {
        var convo = new Conversation("sys", baseline: Baseline(Full()));
        convo.Load(new[] { new ChatMessage("user", "what I typed", Update: new SessionUpdate("PLANTED UPDATE 91ab", Full(), 3)) });
        convo.AddAssistant("an answer");
        var (rebuiltConvo, rebuilt) = TranscriptStore.Rebuild(TranscriptStore.BuildLines(new TranscriptModel("generalist"), convo), T, "generalist", GlyphSet.Unicode);
        var rows = rebuilt.Items.SelectMany(i => i.Render(100, T, glyphs: GlyphSet.Unicode)).Select(TermText.StripAnsiForWidth).ToList();
        Assert.Contains(rows, r => r.Contains("what I typed", StringComparison.Ordinal));
        Assert.DoesNotContain(rows, r => r.Contains("PLANTED UPDATE 91ab", StringComparison.Ordinal));
        AssertSame(Baseline(Full()), rebuiltConvo.Baseline);
        Assert.Equal("PLANTED UPDATE 91ab", rebuiltConvo.Messages.Single(m => m.Role == "user").Update!.Text);
    }
}
