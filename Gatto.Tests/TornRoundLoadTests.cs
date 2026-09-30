using System.Text.Json;
using Gatto.Core.Client;
using Gatto.Core.Home;
using Gatto.Core.Loop;
using Gatto.Core.Tools;
using Gatto.Tests.Fakes;

namespace Gatto.Tests;

//a load drops the assistant record whose call has no result, and the results that answer its other calls, but the file keeps them
public sealed class TornRoundLoadTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-home-").FullName;
    private readonly string _cwd = Directory.CreateTempSubdirectory("gatto-cwd-").FullName;

    public TornRoundLoadTests() => Directory.CreateDirectory(Path.Combine(_home, "sessions"));

    public void Dispose()
    {
        try { Directory.Delete(_home, true); } catch (Exception) { }
        try { Directory.Delete(_cwd, true); } catch (Exception) { }
    }

    private IReadOnlyList<ChatMessage> SaveAndLoad(Conversation convo)
    {
        new SessionStore(_home, _cwd).Save(convo);
        return new SessionStore(_home, _cwd).LoadLatestForCwd()!;
    }

    private static Conversation Asked(params string[] ids)
    {
        var c = new Conversation("sys");
        c.AddUser("go");
        c.AddAssistant("", ids.Select(id => new ToolCall(id, "probe", "{}")).ToList());
        return c;
    }

    [Fact]
    public void A_TOOL_CALL_WITH_NO_RESULT_IS_NOT_LOADED()
    {
        var loaded = SaveAndLoad(Asked("c1"));

        Assert.Equal(new[] { "system", "user" }, loaded.Select(m => m.Role));
    }

    [Fact]
    public void A_TORN_ROUND_IS_NOT_LOADED_WITH_ITS_ANSWERED_HALF()
    {
        var convo = Asked("c1", "c2");
        convo.AddToolResult("c1", new ToolResult("one"));

        var loaded = SaveAndLoad(convo);

        Assert.Equal(new[] { "system", "user" }, loaded.Select(m => m.Role));
    }

    [Fact]
    public void A_WHOLE_ROUND_BEFORE_A_TORN_ONE_IS_KEPT()
    {
        var convo = Asked("c1");
        convo.AddToolResult("c1", new ToolResult("one"));
        convo.AddAssistant("", new[] { new ToolCall("c2", "probe", "{}") });

        var loaded = SaveAndLoad(convo);

        Assert.Equal(new[] { "system", "user", "assistant", "tool" }, loaded.Select(m => m.Role));
        Assert.Equal("c1", loaded[3].ToolCallId);
    }

    //the next request answers every tool call it sends, and the check reads the body the server received
    [Fact]
    public async Task THE_NEXT_REQUEST_CARRIES_NO_UNANSWERED_CALL()
    {
        var loaded = SaveAndLoad(Asked("c1"));
        var convo = new Conversation("sys");
        convo.Load(loaded.Where(m => m.Role != "system"));
        await using var s = new FakeOpenAiServer();
        s.Enqueue(new FakeResponse(Frames: new[]
        {
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}\n\n",
            "data: [DONE]\n\n",
        }));
        var loop = new AgentLoop(
            new OpenAiCompatClient(new HttpClient(), "local", new EndpointConfig(s.BaseUrl)),
            new ToolRegistry(), new HookBus(), new TestToolContext(_cwd), "m");

        await loop.RunTurnAsync(convo, "next", new RecordingObserver(), default);

        var messages = s.LastRequestBody!.Value.GetProperty("messages").EnumerateArray().ToList();
        //the body holds the new turn, so the loop below is checking a request that really arrived
        Assert.Contains(messages, m => m.TryGetProperty("content", out var c)
                                       && c.ValueKind == JsonValueKind.String && c.GetString() == "next");
        for (var i = 0; i < messages.Count; i++)
        {
            if (!messages[i].TryGetProperty("tool_calls", out var calls)) continue;
            foreach (var call in calls.EnumerateArray())
            {
                var id = call.GetProperty("id").GetString();
                Assert.True(messages.Skip(i + 1).Any(m => m.TryGetProperty("tool_call_id", out var t) && t.GetString() == id),
                    $"the request carries tool call {id} with no result after it");
            }
        }
    }
}
