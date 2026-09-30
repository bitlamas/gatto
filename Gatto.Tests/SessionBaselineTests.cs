using System.Text.Json;
using Gatto.Core.Client;
using Gatto.Core.Loop;
using Gatto.Core.Tools;
using Gatto.Tests.Fakes;
using Xunit;

namespace Gatto.Tests;

public sealed class SessionBaselineTests
{
    //built fresh each call, so no two results share a list instance
    private static BaselineSources Sources(string? date = "2026-09-29", string? memory = "- a fact", string context = "abc", string policy = "use it",
        string role = "r", string model = "m") =>
        new(date, memory,
            new List<ContextFileMark> { new(@"C:\proj\GATTO.md", BaselineMarks.Sha256Hex(context)) },
            new List<PolicyMark> { new("web_search", policy) },
            BaselineMarks.Sha256Hex(role), BaselineMarks.Sha256Hex(model));

    private static List<ToolMark> Tools(params (string Name, string Body)[] tools) =>
        tools.Select(t => new ToolMark(t.Name, BaselineMarks.Sha256Hex(t.Body))).ToList();

    private static SessionBaseline Baseline(int seq) =>
        new(seq, "generalist", "test-model", "all", new ThinkingMark("medium", null), Tools(("a", "1")), Sources());

    private sealed class Handler(CompactionResult result) : ICompactionHandler
    {
        public Task<CompactionResult?> CompactAsync(CompactionReason reason, string currentUserPrompt, ITurnObserver observer, CancellationToken ct) =>
            Task.FromResult<CompactionResult?>(result);
    }

    [Fact]
    public void The_conversation_holds_the_baseline_it_was_given_with_its_text()
    {
        var convo = new Conversation("s", baseline: Baseline(1));
        Assert.Equal(1, convo.Baseline!.Seq);
        convo.AddUser("u");
        convo.TruncateTo(1);
        convo.Load(new[] { new ChatMessage("user", "loaded") });
        Assert.Equal(1, convo.Baseline!.Seq);
        convo.SetThinking(new ThinkingMark("high", "{\"a\":1}"));
        Assert.Equal((1, "high", "{\"a\":1}"), (convo.Baseline!.Seq, convo.Baseline.Thinking.Level, convo.Baseline.Thinking.BodyJson));
        convo.ReplaceSystem("s2", Baseline(2));
        Assert.Equal(2, convo.Baseline!.Seq);
        convo.ReplaceAll("s3", Array.Empty<ChatMessage>(), Baseline(3));
        Assert.Equal(3, convo.Baseline!.Seq);
        var bare = new Conversation("s");
        bare.SetThinking(new ThinkingMark("high", null));
        Assert.Null(bare.Baseline);
    }

    [Fact]
    public void A_role_switch_hands_the_conversation_its_baseline()
    {
        var convo = new Conversation("s", baseline: Baseline(1));
        Gatto.Repl.Repl.ApplyRoleSwitch(convo, _ => new Gatto.Repl.RoleSwitchResult(true, "coder", "test-model", "coder system", null, null, Baseline(2)), "coder");
        Assert.Equal(("coder system", 2), (convo.Messages[0].Content, convo.Baseline!.Seq));
    }

    [Fact]
    public async Task A_compact_hands_the_new_conversation_the_recomposed_baseline()
    {
        var client = new FakeChatClient();
        client.EnqueueTurn(new StreamEvent.TextDelta("the summary"), new StreamEvent.Finished("stop", null));
        var convo = new Conversation("sys", baseline: Baseline(1));
        convo.AddUser("q");
        convo.AddAssistant("a");
        var fresh = await Gatto.Repl.Repl.CompactAsync(new Compactor(client, "m"), convo, null,
            () => new ComposedSystem("FRESH", Baseline(2)), new RecordingObserver(), default);
        Assert.Equal(2, fresh!.Baseline!.Seq);
        Assert.StartsWith("FRESH", fresh.Messages[0].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Both_auto_compaction_sites_hand_the_conversation_the_baseline()
    {
        var rebuilt = new CompactionResult("s2", new[] { new ChatMessage("user", "u") }, Baseline(2));

        var usage = new ContextUsageState();
        var proactiveClient = new FakeChatClient();
        proactiveClient.EnqueueTurn(new StreamEvent.ToolCallReady(new ToolCall("c1", "read_file", "{}")), new StreamEvent.Finished("tool_calls", new Usage(55_000, 200)));
        proactiveClient.EnqueueTurn(new StreamEvent.TextDelta("done"), new StreamEvent.Finished("stop", null));
        var proactive = new AgentLoop(proactiveClient, new ToolRegistry(), new HookBus(), new TestToolContext(Path.GetTempPath()), "m",
            budgetTokens: 65_536, compaction: new Handler(rebuilt), usageState: usage, autoCompactAt: 0.8);
        var a = new Conversation("s", baseline: Baseline(1));
        await proactive.RunTurnAsync(a, "explore", new RecordingObserver(), default);
        Assert.Equal(2, a.Baseline!.Seq);

        var overflowClient = new FakeChatClient();
        overflowClient.EnqueueThrow(new GattoContextOverflowException("over", 69_716, 65_536));
        overflowClient.EnqueueTurn(new StreamEvent.TextDelta("done"), new StreamEvent.Finished("stop", null));
        var overflow = new AgentLoop(overflowClient, new ToolRegistry(), new HookBus(), new TestToolContext(Path.GetTempPath()), "m", compaction: new Handler(rebuilt));
        var b = new Conversation("s", baseline: Baseline(1));
        await overflow.RunTurnAsync(b, "original prompt", new RecordingObserver(), default);
        Assert.Equal(2, b.Baseline!.Seq);
    }

    [Fact]
    public void Two_baselines_read_from_separate_instances_compare_equal()
    {
        Assert.True(BaselineMarks.SameTools(Tools(("a", "1"), ("b", "2")), Tools(("a", "1"), ("b", "2"))));
        //a record compares its list members by reference, so == on two equal values is false and no caller may use it
        Assert.False(Sources() == Sources());
    }

    [Fact]
    public void One_changed_tool_makes_the_tools_differ()
    {
        var tools = Tools(("a", "1"), ("b", "2"));
        Assert.False(BaselineMarks.SameTools(tools, Tools(("a", "1"), ("b", "3"))));
        Assert.False(BaselineMarks.SameTools(tools, Tools(("a", "1"), ("c", "2"))));
        Assert.False(BaselineMarks.SameTools(tools, Tools(("a", "1"))));
    }

    [Fact]
    public void The_same_tools_in_another_order_are_not_the_same()
    {
        Assert.False(BaselineMarks.SameTools(Tools(("a", "1"), ("b", "2")), Tools(("b", "2"), ("a", "1"))));
    }

    [Fact]
    public void Compose_records_the_values_it_composed_from()
    {
        var role = new Gatto.Roles.RoleFile("r", "p", null, Array.Empty<string>(), false, null, Gatto.Roles.ThinkingLevel.Medium, null);
        var c = Gatto.Roles.RoleComposition.Compose(role, null,
            new[] { (@"C:\proj\GATTO.md", "context text") }, null, cwd: @"C:\proj",
            date: new DateTime(2026, 9, 29), memoryIndex: "- a fact\n- another", memoryTruncatedLines: 2,
            policyLines: new[] { ("zeta", "zeta line"), ("alpha", "alpha line") });
        Assert.Equal("2026-09-29", c.Sources.Date);
        Assert.Equal("- a fact\n- another", c.Sources.Memory);
        Assert.Equal(new[] { new ContextFileMark(@"C:\proj\GATTO.md", BaselineMarks.Sha256Hex("context text")) }, c.Sources.ContextFiles);
        Assert.Equal(new[] { new PolicyMark("alpha", "alpha line"), new PolicyMark("zeta", "zeta line") }, c.Sources.Policy);
        Assert.Equal(BaselineMarks.Sha256Hex(role.Append ?? ""), c.Sources.RoleAppendSha256);
        Assert.Equal(BaselineMarks.Sha256Hex("\n"), c.Sources.ModelAppendSha256);
        var bare = Gatto.Roles.RoleComposition.Compose(role, null, Array.Empty<(string, string)>(), null, cwd: null);
        Assert.Null(bare.Sources.Date);
        Assert.Null(bare.Sources.Memory);
    }

    [Fact]
    public async Task The_tool_hash_is_the_hash_of_the_bytes_the_request_holds()
    {
        await using var server = new FakeOpenAiServer();
        server.Enqueue(new FakeResponse(Frames: new[] { "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n", "data: [DONE]\n\n" }));
        var specs = new[]
        {
            new ToolSpec("read_file", "reads a file \"quoted\" and <angled>", JsonDocument.Parse("{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"}}}").RootElement),
            new ToolSpec("echo", "echoes", JsonDocument.Parse("{\"type\":\"object\"}").RootElement),
        };
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var client = new OpenAiCompatClient(http, "local", new EndpointConfig(server.BaseUrl));
        await foreach (var _ in client.StreamAsync(new ChatRequest("m", new[] { new ChatMessage("user", "hi") }, specs), default)) { }
        var sent = server.LastRequestBody!.Value.GetProperty("tools").EnumerateArray().Select(t => BaselineMarks.Sha256Hex(t.GetRawText())).ToList();
        Assert.Equal(sent, BaselineMarks.ToolsOf(specs).Select(t => t.Sha256).ToList());
    }
}
