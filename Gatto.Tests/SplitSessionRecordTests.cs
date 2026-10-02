using System.Text.Json;
using Gatto.Core.Home;
using Gatto.Core.Loop;

namespace Gatto.Tests;

//a session record is one line: a thinking body from an indented profile is written compact
public sealed class SplitSessionRecordTests : IDisposable
{
    private readonly string _base = Directory.CreateTempSubdirectory("gatto-split-").FullName;
    private string Home => Path.Combine(_base, "home");
    private string Proj => Path.Combine(_base, "proj");
    public void Dispose() { try { Directory.Delete(_base, true); } catch { } }

    private const string Indented = "{\r\n      \"chat_template_kwargs\": {\r\n        \"enable_thinking\": true\r\n      }\r\n    }";
    private const string Compact = "{\"chat_template_kwargs\":{\"enable_thinking\":true}}";

    private static SessionBaseline Baseline(string? body) =>
        new(1, "generalist", "m", "all", new ThinkingMark("medium", body), new List<ToolMark>(),
            new BaselineSources(null, null, new List<ContextFileMark>(), new List<PolicyMark>(), "r", "m"));

    [Fact]
    public void A_thinking_mark_holds_its_body_compact()
    {
        Assert.Equal(Compact, new ThinkingMark("medium", Indented).BodyJson);
        Assert.Equal(new ThinkingMark("medium", Compact), new ThinkingMark("medium", Indented));
        Assert.Null(new ThinkingMark("medium", null).BodyJson);
    }

    [Fact]
    public void A_saved_system_record_is_one_line_when_the_body_was_indented()
    {
        var store = new SessionStore(Home, Proj);
        var convo = new Conversation("sys", baseline: Baseline(Indented));
        convo.AddUser("u");
        store.Save(convo);

        foreach (var line in File.ReadAllLines(store.CurrentPath!))
            JsonDocument.Parse(line).Dispose();
        Assert.Equal(Compact, SessionStore.LoadBaseline(store.CurrentPath!)!.Thinking.BodyJson);
    }
}
