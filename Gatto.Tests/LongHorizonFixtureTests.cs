using System.Text.Json;
using Gatto.Core.Client;
using Gatto.Core.Loop;
using Gatto.Core.Tools;
using Gatto.Tests.Fakes;

namespace Gatto.Tests;

//long-horizon defects only appear at length, so this tier drives many fat tool rounds against a fake server
public sealed class LongHorizonFixtureTests
{
    //a fake tool whose result is large enough to press the context window.
    private sealed class FatTool(int chars) : ITool
    {
        public string Name => "fat";
        public string Description => "returns a large result";
        public JsonElement ParametersSchema => JsonDocument.Parse("{\"type\":\"object\"}").RootElement;
        public Task<ToolResult> ExecuteAsync(JsonElement a, IToolContext c, CancellationToken ct) =>
            Task.FromResult(new ToolResult(new string('r', chars)));
    }

    private static AgentLoop NewLoop(
        FakeChatClient client, ITool tool, int? budgetTokens = null,
        ICompactionHandler? compaction = null, ContextUsageState? usageState = null, double? autoCompactAt = null)
    {
        var tools = new ToolRegistry();
        tools.Register(tool);
        return new AgentLoop(client, tools, new HookBus(), new TestToolContext(Path.GetTempPath()), "m",
            budgetTokens: budgetTokens, compaction: compaction, usageState: usageState, autoCompactAt: autoCompactAt);
    }

    //queues a run of tool-call rounds and closes it with a plain text answer.
    private static void ScriptToolRounds(FakeChatClient client, int rounds, Usage? usage = null)
    {
        for (var i = 0; i < rounds; i++)
            client.EnqueueTurn(
                new StreamEvent.ToolCallReady(new ToolCall($"c{i}", "fat", "{}")),
                new StreamEvent.Finished("tool_calls", usage));
        client.EnqueueTurn(new StreamEvent.TextDelta("done"), new StreamEvent.Finished("stop", usage));
    }

    //a long cut result keeps its head above the marker, so the marker can sit after the first line
    private static bool IsStub(ChatMessage m) =>
        m.Role == "tool" && m.Content is { } c && c.Contains("[elided:", StringComparison.Ordinal);

    private static HashSet<int> StubIndices(ChatRequest r) =>
        r.Messages.Select((m, i) => (m, i)).Where(t => IsStub(t.m)).Select(t => t.i).ToHashSet();

    [Fact]
    public async Task Elision_fires_inside_a_long_turn()
    {
        //the protection boundary must be re-checked inside the turn, since a boundary fixed at turn start never elides a long turn
        var client = new FakeChatClient();
        ScriptToolRounds(client, rounds: 10);
        var loop = NewLoop(client, new FatTool(8_000), budgetTokens: 6_000);
        var observer = new RecordingObserver();

        var result = await loop.RunTurnAsync(new Conversation("sys"), "go", observer, CancellationToken.None);

        Assert.Equal(TurnOutcome.Completed, result.Outcome);
        Assert.Contains(observer.Warnings, w => w.Contains("elided", StringComparison.Ordinal));
        Assert.Contains(client.Requests, r => r.Messages.Any(IsStub));
    }

    [Fact]
    public async Task Elision_is_oldest_first_and_never_touches_the_newest_result()
    {
        //the newest result must stay intact and older ones age out first, so the test asserts the shape, since the protected window counts rounds
        var client = new FakeChatClient();
        ScriptToolRounds(client, rounds: 12);
        var loop = NewLoop(client, new FatTool(8_000), budgetTokens: 6_000);

        await loop.RunTurnAsync(new Conversation("sys"), "go", new RecordingObserver(), CancellationToken.None);

        var tools = client.Requests[^1].Messages.Where(m => m.Role == "tool").ToList();
        Assert.True(tools.Count > ContextBudget.RecentRounds, "the scenario must outlive the protected window");

        Assert.False(IsStub(tools[^1]), "the newest result — what the model is reasoning over now — was elided");
        Assert.Contains(tools, IsStub);   //some older results must have aged out in this scenario.

        //stubs form a prefix, so once a result survives, nothing after it may be elided
        var firstKept = tools.FindIndex(m => !IsStub(m));
        Assert.All(tools.Skip(firstKept), m => Assert.False(IsStub(m), "an elided result appeared after a kept one"));
    }

    [Fact]
    public async Task Stub_set_is_monotone_across_every_round_of_a_turn()
    {
        //each round's stub set must be a superset of the last. a reverted stub changes an already-cached prefix and forces a full re-prefill
        var client = new FakeChatClient();
        ScriptToolRounds(client, rounds: 14);
        var loop = NewLoop(client, new FatTool(8_000), budgetTokens: 6_000);

        await loop.RunTurnAsync(new Conversation("sys"), "go", new RecordingObserver(), CancellationToken.None);

        var previous = new HashSet<int>();
        for (var round = 0; round < client.Requests.Count; round++)
        {
            var current = StubIndices(client.Requests[round]);
            Assert.True(previous.IsSubsetOf(current),
                $"round {round}: stub set shrank — {string.Join(",", previous.Except(current))} un-elided");
            previous = current;
        }
        Assert.NotEmpty(previous);   //the scenario must actually have elided something
    }

    //size the first request near 5,000 estimated tokens, so a scripted count of 5_000 gives a ratio near 1 and the arithmetic stays readable
    private const int UserPromptChars = 20_000;

    private static (FakeChatClient Client, RecordingCompactionHandler Handler, ContextUsageState Usage) TriggerFixture(int toolChars)
    {
        var client = new FakeChatClient();
        //round 1 reports a prompt of 5_000, well under the 8_000 threshold, so round 2 is where the check that matters happens
        client.EnqueueTurn(
            new StreamEvent.ToolCallReady(new ToolCall("c0", "fat", "{}")),
            new StreamEvent.Finished("tool_calls", new Usage(5_000, 10)));
        client.EnqueueTurn(new StreamEvent.TextDelta("done"), new StreamEvent.Finished("stop", new Usage(5_000, 10)));
        return (client, new RecordingCompactionHandler(), new ContextUsageState());
    }

    private sealed class RecordingCompactionHandler : ICompactionHandler
    {
        public int Calls;
        public Task<CompactionResult?> CompactAsync(CompactionReason reason, string prompt, ITurnObserver o, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult<CompactionResult?>(new CompactionResult("compacted", new[] { new ChatMessage("user", prompt) }, null));
        }
    }

    [Fact]
    public async Task Proactive_trigger_catches_a_fat_result_that_landed_since_the_last_reading()
    {
        //the count the server reports is for the previous request, so the trigger must also weigh what this round appended
        var (client, handler, usage) = TriggerFixture(toolChars: 20_000);
        var loop = NewLoop(client, new FatTool(20_000), budgetTokens: 10_000,
            compaction: handler, usageState: usage, autoCompactAt: 0.8);

        await loop.RunTurnAsync(new Conversation("s"), new string('u', UserPromptChars),
            new RecordingObserver(), CancellationToken.None);

        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Proactive_trigger_stays_quiet_when_nothing_fat_landed()
    {
        //the same reading with a tiny tool result must not compact, so the trigger watches the growth in a round
        var (client, handler, usage) = TriggerFixture(toolChars: 50);
        var loop = NewLoop(client, new FatTool(50), budgetTokens: 10_000,
            compaction: handler, usageState: usage, autoCompactAt: 0.8);

        await loop.RunTurnAsync(new Conversation("s"), new string('u', UserPromptChars),
            new RecordingObserver(), CancellationToken.None);

        Assert.Equal(0, handler.Calls);
    }
}
