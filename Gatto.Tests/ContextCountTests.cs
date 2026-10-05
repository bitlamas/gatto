using System.Text.Json;
using Gatto.Core.Client;
using Gatto.Core.Loop;
using Gatto.Core.Tools;

namespace Gatto.Tests;

//the figures /context draws, from a counter that stands in for llama-server or from the characters
public class ContextCountTests
{
    //counts a request as its total and each text as one token per two characters, and can fail one text
    private sealed class FakeCounter(int total, string? failOn = null) : ITokenCounter
    {
        public ChatRequest? Counted;
        public Task<int?> CountInputTokensAsync(ChatRequest request, CancellationToken ct) { Counted = request; return Task.FromResult<int?>(total); }
        public Task<int?> TokenizeCountAsync(string text, CancellationToken ct) =>
            Task.FromResult<int?>(failOn is not null && text.Contains(failOn) ? null : text.Length / 2);
    }

    private static readonly ToolSpec Read = new("read_file", "reads", JsonDocument.Parse("{\"type\":\"object\"}").RootElement);
    private static readonly ToolSpec Ext = new("ask_user", "asks", JsonDocument.Parse("{\"type\":\"object\"}").RootElement);

    private static ContextInputs Inputs(IReadOnlyList<ChatMessage>? sent, int? lastPrompt = null, double ratio = 1.0, string? timings = null, Usage? usage = null) => new(
        "m", "sys text", [new ContextPartText("system prompt", ContextGroup.Prefix, new string('s', 400))],
        [Read], [Ext],
        new RequestShape([Read, Ext], null, null, ReasoningHistory.All, sent),
        65_536, 0.8, ratio, lastPrompt, timings, usage);

    private static readonly ChatMessage[] Sent =
    [
        new("system", "sys text"),
        new("user", new string('u', 100)),
        new("assistant", new string('a', 60), [new ToolCall("c1", "read_file", "{}")], ReasoningContent: new string('r', 200)),
        new("tool", new string('t', 1000), ToolCallId: "c1"),
        new("tool", new string('t', 600), ToolCallId: "c2"),
    ];

    [Fact]
    public async Task On_the_exact_path_each_part_is_the_servers_count_and_the_template_takes_the_rest()
    {
        var counter = new FakeCounter(2_000);
        var f = await ContextCount.BuildAsync(Inputs(Sent), counter, CancellationToken.None);

        Assert.True(f.Exact);
        Assert.False(f.FellBack);
        Assert.Equal(2_000, f.Total);
        Assert.Equal(2_000, f.Parts.Sum(p => p.Tokens));
        var markup = Assert.Single(f.Parts, p => p.Label == ContextCount.TemplateMarkup);
        Assert.Equal(ContextGroup.Prefix, markup.Group);
        Assert.Equal(200, Assert.Single(f.Parts, p => p.Label == "system prompt").Tokens);
        Assert.Equal(800, Assert.Single(f.Parts, p => p.Label == "tool results" && p.Detail == "2").Tokens);
        Assert.Equal(100, Assert.Single(f.Parts, p => p.Label == "reasoning").Tokens);
        Assert.Contains(f.Parts, p => p.Label == "tools" && p.Detail == "1 built-in");
        Assert.Contains(f.Parts, p => p.Label == "tools" && p.Detail == "1 extensions");
        //the count describes the request as sent, so the counter is handed the last-sent messages and the tools
        Assert.Same(Sent, counter.Counted!.Messages);
        Assert.Equal(2, counter.Counted.Tools!.Count);
    }

    [Fact]
    public async Task On_the_estimate_path_parts_are_characters_over_four_scaled_by_the_ratio_and_the_total_is_the_servers()
    {
        var f = await ContextCount.BuildAsync(Inputs(Sent, lastPrompt: 5_000, ratio: 2.0), null, CancellationToken.None);

        Assert.False(f.Exact);
        Assert.False(f.FellBack);
        Assert.Equal(5_000, f.Total);
        Assert.Equal(200, Assert.Single(f.Parts, p => p.Label == "system prompt").Tokens);
        Assert.DoesNotContain(f.Parts, p => p.Label == ContextCount.TemplateMarkup);
    }

    //one failed call drops the whole report to the estimate, never a mix of exact and estimated rows
    [Fact]
    public async Task A_failed_count_falls_back_to_the_estimate_and_says_so()
    {
        var f = await ContextCount.BuildAsync(Inputs(Sent, lastPrompt: 900), new FakeCounter(2_000, failOn: "ttt"), CancellationToken.None);

        Assert.False(f.Exact);
        Assert.True(f.FellBack);
        Assert.Equal(900, f.Total);
    }

    [Fact]
    public async Task Before_the_first_request_the_report_is_the_system_and_the_tools_only()
    {
        var counter = new FakeCounter(700);
        var f = await ContextCount.BuildAsync(Inputs(null), counter, CancellationToken.None);

        Assert.True(f.BeforeFirstRequest);
        Assert.DoesNotContain(f.Parts, p => p.Group != ContextGroup.Prefix);
        var only = Assert.Single(counter.Counted!.Messages);
        Assert.Equal("system", only.Role);
        Assert.Equal("sys text", only.Content);
    }

    //a cloud server reports the cached part of the prompt in its usage, and llama-server's timings win where both are present
    [Fact]
    public async Task The_cache_line_reads_a_cloud_servers_cached_tokens()
    {
        var f = await ContextCount.BuildAsync(Inputs(Sent, usage: new Usage(1_000, 5, 800)), null, CancellationToken.None);
        Assert.Equal(new ContextCache(800, 200), f.Cache);

        var both = await ContextCount.BuildAsync(Inputs(Sent, timings: "{\"cache_n\":40,\"prompt_n\":2}", usage: new Usage(1_000, 5, 800)), null, CancellationToken.None);
        Assert.Equal(new ContextCache(40, 2), both.Cache);

        var silent = await ContextCount.BuildAsync(Inputs(Sent, usage: new Usage(1_000, 5)), null, CancellationToken.None);
        Assert.Null(silent.Cache);
    }

    [Fact]
    public async Task The_cache_line_reads_the_last_timings()
    {
        var f = await ContextCount.BuildAsync(Inputs(Sent, timings: "{\"cache_n\":40880,\"prompt_n\":222}"), null, CancellationToken.None);
        Assert.Equal(new ContextCache(40_880, 222), f.Cache);

        var none = await ContextCount.BuildAsync(Inputs(Sent, timings: "{\"predicted_n\":5}"), null, CancellationToken.None);
        Assert.Null(none.Cache);
    }

    [Fact]
    public void The_registry_tells_an_extension_tool_from_a_built_in_one()
    {
        var tools = new ToolRegistry();
        tools.Register(new ReadFileTool());
        tools.Register(new SpyExtension(), extension: true);

        Assert.False(tools.IsExtension("read_file"));
        Assert.True(tools.IsExtension("spy_ext"));
    }

    private sealed class SpyExtension : ITool
    {
        public string Name => "spy_ext";
        public string Description => "spy";
        public JsonElement ParametersSchema => JsonDocument.Parse("{\"type\":\"object\"}").RootElement;
        public Task<ToolResult> ExecuteAsync(JsonElement args, IToolContext ctx, CancellationToken ct) => Task.FromResult(new ToolResult("ok"));
    }

    //the composition names each block by its source, the parts /context counts
    [Fact]
    public void The_composition_splits_its_system_text_by_source()
    {
        var role = new Gatto.Roles.RoleFile("coder", null, null, Array.Empty<string>(), false, "role words", Gatto.Roles.ThinkingLevel.Medium);
        var c = Gatto.Roles.RoleComposition.Compose(role, null, [("a.md", "alpha"), ("b.md", "beta")], null, cwd: null,
            memoryIndex: "- a fact");

        var labels = c.SystemParts!.Select(p => (p.Label, p.Detail)).ToList();
        Assert.Equal([("system prompt", null), ("role", "coder"), ("context files", "2"), ("memory index", null)], labels);
        Assert.All(c.SystemParts!, p => Assert.Contains(p.Text.Split("\n\n")[0], c.SystemText));
        Assert.Equal(c.SystemText.Length, c.SystemParts!.Sum(p => p.Text.Length) + 2 * (c.SystemParts!.Count - 1) + 0);
    }
}
