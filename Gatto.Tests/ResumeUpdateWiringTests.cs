using Gatto.Core.Client;
using Gatto.Core.Home;
using Gatto.Core.Loop;
using Gatto.Tests.Fakes;
using Xunit;

namespace Gatto.Tests;

//the update rides the first user message after a resume, the request copy sends it ahead of the typed text, and a dropped message hands it on
public sealed class ResumeUpdateWiringTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-update-home-").FullName;
    private readonly string _cwd = Directory.CreateTempSubdirectory("gatto-update-cwd-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_home, true); } catch { }
        try { Directory.Delete(_cwd, true); } catch { }
    }

    private static BaselineSources Sources() => new("2026-09-29", null, new List<ContextFileMark>(), new List<PolicyMark>(), "r", "m");

    private static SessionUpdate Update(string text) => new(text, Sources(), 1);

    [Fact]
    public void The_pending_update_rides_the_next_user_message_once()
    {
        var convo = new Conversation("s");
        convo.SetPendingUpdate(Update("UPDATE"));
        convo.AddUser("first");
        convo.AddUser("second");
        Assert.Equal("UPDATE", convo.Messages[1].Update!.Text);
        Assert.Equal("first", convo.Messages[1].Content);
        Assert.Null(convo.Messages[2].Update);
        Assert.Null(convo.PendingUpdate);
    }

    [Fact]
    public void A_truncate_restores_only_the_update_this_conversation_attached()
    {
        var convo = new Conversation("s");
        convo.SetPendingUpdate(Update("UPDATE"));
        convo.AddUser("dropped");
        convo.TruncateTo(1);
        Assert.Equal("UPDATE", convo.PendingUpdate!.Text);

        var loaded = new Conversation("s");
        loaded.Load(new[] { new ChatMessage("user", "from the file", Update: Update("OLD")) });
        loaded.TruncateTo(1);
        Assert.Null(loaded.PendingUpdate);
    }

    [Fact]
    public void A_new_system_message_clears_the_pending_update()
    {
        var convo = new Conversation("s");
        convo.SetPendingUpdate(Update("UPDATE"));
        convo.ReplaceSystem("s2", null);
        Assert.Null(convo.PendingUpdate);
        convo.SetPendingUpdate(Update("UPDATE"));
        convo.ReplaceAll("s3", Array.Empty<ChatMessage>(), null);
        Assert.Null(convo.PendingUpdate);
    }

    [Fact]
    public void An_update_on_a_message_with_an_image_keeps_the_image()
    {
        var image = new ImageRef(@"C:\proj\a.png", 10, "abc", "image/png");
        var shaped = ContextBudget.ShapeUpdates(new[] { new ChatMessage("user", "look at this", Images: new[] { image }, Update: Update("UPDATE")) });
        Assert.Equal("UPDATE\n\nlook at this", shaped[0].Content);
        Assert.Equal(new[] { image }, shaped[0].Images);
        var plain = new[] { new ChatMessage("user", "no update") };
        Assert.Same(plain, ContextBudget.ShapeUpdates(plain));
    }

    [Fact]
    public async Task The_summary_request_holds_the_update_ahead_of_the_typed_text()
    {
        var client = new FakeChatClient();
        client.EnqueueTurn(new StreamEvent.TextDelta("the summary"), new StreamEvent.Finished("stop", null));
        var convo = new Conversation("sys");
        convo.SetPendingUpdate(Update("UPDATE 5e2d"));
        convo.AddUser("typed");
        convo.AddAssistant("answer");
        await Gatto.Repl.Repl.CompactAsync(new Compactor(client, "m"), convo, null, () => Composed.Text("FRESH"), new RecordingObserver(), default);
        Assert.Contains(client.Requests[0].Messages, m => m.Role == "user" && m.Content == "UPDATE 5e2d\n\ntyped");
    }

    [Fact]
    public async Task A_dropped_first_message_hands_the_update_to_the_next()
    {
        var h = new RichReplHarness(_cwd);
        h.Convo.SetPendingUpdate(Update("UPDATE 91ab"));
        h.Client.EnqueueThrow(new GattoConnectionException("the server went away"));
        h.Client.EnqueueTurn(new StreamEvent.TextDelta("second answer"), new StreamEvent.Finished("stop", null));
        h.Keys.Line("first").Line("second");
        await h.RunUntilAsync(() => h.Client.Requests.Count >= 2);
        var last = h.Client.Requests[1].Messages.Last(m => m.Role == "user");
        Assert.Equal("UPDATE 91ab\n\nsecond", last.Content);
    }

    [Fact]
    public async Task A_second_resume_through_the_repl_sends_no_update_when_nothing_changed()
    {
        var stored = new Conversation("STORED");
        stored.SetPendingUpdate(Update("THE EARLIER UPDATE"));
        stored.AddUser("first question");
        stored.AddAssistant("first answer");
        new SessionStore(_home, _cwd).Save(stored);
        var path = Directory.GetFiles(Path.Combine(_home, "sessions"), "*.jsonl").Single(f => !f.EndsWith(".ledger.jsonl", StringComparison.Ordinal));

        var h = new RichReplHarness(_cwd, systemPrompt: "STORED", resumedFrom: Path.GetFileName(path), resumedPath: path);
        //the records the launch loads before the rebuild drops them, as the interactive entry does
        h.Convo.Load(new SessionStore(_home, _cwd).LoadPath(path).Where(m => m.Role != "system"));
        h.Client.EnqueueTurn(new StreamEvent.TextDelta("second answer"), new StreamEvent.Finished("stop", null));
        h.Keys.Line("second question");
        await h.RunUntilAsync(() => h.Client.Requests.Count >= 1);
        var users = h.Client.Requests[0].Messages.Where(m => m.Role == "user").Select(m => m.Content).ToList();
        Assert.Equal("THE EARLIER UPDATE\n\nfirst question", users[0]);
        Assert.Equal("second question", users[1]);
    }
}
