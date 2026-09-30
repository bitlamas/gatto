using Gatto.Core.Client;
using Gatto.Core.Home;
using Gatto.Core.Loop;
using Gatto.Tests.Fakes;
using Xunit;

namespace Gatto.Tests;

//the rich path rebuilds the records from the file, and the request it sends must hold them as the file holds them
public sealed class ResumeEqualityReplTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-resume-repl-home-").FullName;
    private readonly string _cwd = Directory.CreateTempSubdirectory("gatto-resume-repl-cwd-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_home, true); } catch { }
        try { Directory.Delete(_cwd, true); } catch { }
    }

    [Fact]
    public async Task A_resume_through_the_repl_sends_the_same_prefix()
    {
        var stored = new Conversation("STORED SYSTEM TEXT");
        stored.AddUser("first question");
        stored.AddAssistant("first answer");
        stored.AddUser("second question");
        stored.AddAssistant("second answer");
        new SessionStore(_home, _cwd).Save(stored);
        var path = Directory.GetFiles(Path.Combine(_home, "sessions"), "*.jsonl").Single(f => !f.EndsWith(".ledger.jsonl", StringComparison.Ordinal));

        var h = new RichReplHarness(_cwd, systemPrompt: "STORED SYSTEM TEXT", resumedFrom: Path.GetFileName(path), resumedPath: path);
        h.Client.EnqueueTurn(new StreamEvent.TextDelta("third answer"), new StreamEvent.Finished("stop", null));
        h.Keys.Line("third question");
        await h.RunUntilAsync(() => h.Client.Requests.Count >= 1);

        var expected = stored.Messages.Select(m => RequestJson.SerialiseMessage(m)).ToList();
        var sent = h.Client.Requests[0].Messages.Select(m => RequestJson.SerialiseMessage(m)).ToList();
        //the request holds the conversation's own list, which the reply grows after it is sent, so only the records up to the new message are compared
        Assert.True(sent.Count > expected.Count, $"sent {sent.Count} messages:\n" + string.Join("\n", sent));
        for (var i = 0; i < expected.Count; i++)
            Assert.True(expected[i] == sent[i], $"message {i} differs:\n  saved: {expected[i]}\n  sent:  {sent[i]}");
        Assert.Equal("{\"role\":\"user\",\"content\":\"third question\"}", sent[expected.Count]);
    }
}
