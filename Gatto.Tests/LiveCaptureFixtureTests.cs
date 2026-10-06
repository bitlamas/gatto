using System.Text.Json;
using Gatto.Core.Client;
using Gatto.Core.Loop;
using Gatto.Core.Tools;
using Gatto.Tests.Fakes;

namespace Gatto.Tests;

//the props and sse fixtures are real server captures, verbatim apart from the rewritten model paths and the include_usage request the sse capture needs
public class LiveCaptureFixtureTests
{
    private const string PropsFixture = "props-laguna-b1-2bd55d0.json";
    private const string SseFixture = "sse-toolcall-laguna-b1-2bd55d0.txt";

    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static string ReadFixture(string name) => File.ReadAllText(FixturePath(name));

    //rebuilds the capture as wire frames, each data: line with the blank line that follows it
    private static string[] SseFrames() =>
        ReadFixture(SseFixture)
            .Replace("\r\n", "\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line + "\n\n")
            .ToArray();

    [Fact]
    public async Task RealProps_ParsesIntoALoadedModel()
    {
        await using var server = new FakeOpenAiServer();
        server.PropsResponse = new FakeResponse(Body: ReadFixture(PropsFixture));
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

        //the assertions read the probe's answer, so its deadline is long, or the suite's load turns them into a race
        var loaded = await ServeProbe.ProbeAsync(http, server.BaseUrl, CancellationToken.None, deadline: TimeSpan.FromSeconds(30));

        Assert.NotNull(loaded);
        Assert.EndsWith("laguna-s-2.1-Q4_K_M.gguf", loaded!.ModelPath, StringComparison.Ordinal);
        Assert.Equal(131072, loaded.NCtx);
    }

    [Fact]
    public void RealProps_KeepsNCtxNestedUnderDefaultGenerationSettings()
    {
        //on a real server n_ctx sits under default_generation_settings, so pin that shape against the capture
        using var doc = JsonDocument.Parse(ReadFixture(PropsFixture));
        var root = doc.RootElement;

        Assert.False(root.TryGetProperty("n_ctx", out _), "n_ctx must not be a root key");
        Assert.True(root.TryGetProperty("default_generation_settings", out var dgs));
        Assert.Equal(131072, dgs.GetProperty("n_ctx").GetInt32());
    }

    [Fact]
    public void RealProps_CarriesTheChatTemplateTheCapabilitySniffReads()
    {
        using var doc = JsonDocument.Parse(ReadFixture(PropsFixture));
        var template = doc.RootElement.GetProperty("chat_template").GetString();

        Assert.False(string.IsNullOrWhiteSpace(template));
        //only the presence of the field the capability sniff reads, since the answer is a real server's
        Assert.Contains("laguna", template!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RealSseToolCall_AssemblesTheFragmentedArgumentsExactly()
    {
        //a real server splits the arguments json anywhere, so a parser that assumes one fragment per argument passes an invented fixture and fails here
        await using var server = new FakeOpenAiServer();
        server.Enqueue(new FakeResponse(Frames: SseFrames()));
        var client = new OpenAiCompatClient(new HttpClient(), "local", new EndpointConfig(server.BaseUrl));

        var calls = new List<ToolCall>();
        var finish = (string?)null;
        var deltas = 0;
        await foreach (var ev in client.StreamAsync(
            new ChatRequest("m", new List<ChatMessage> { new("user", "weather?") }), CancellationToken.None))
        {
            switch (ev)
            {
                case StreamEvent.ToolCallDelta: deltas++; break;
                case StreamEvent.ToolCallReady r: calls.Add(r.Call); break;
                case StreamEvent.Finished f: finish = f.FinishReason; break;
            }
        }

        //the delta count is what proves the arguments arrived in pieces, the property this fixture exists for
        Assert.True(deltas > 1, $"expected a fragmented tool call, saw {deltas} delta chunk(s)");

        Assert.Equal("tool_calls", finish);
        var call = Assert.Single(calls);
        Assert.Equal("get_weather", call.Name);
        using var args = JsonDocument.Parse(call.ArgumentsJson);
        Assert.Equal("Paris", args.RootElement.GetProperty("city").GetString());
    }

    [Fact]
    public async Task RealSseToolCall_ReportsTheServersOwnUsage()
    {
        //the usage ratio, the compaction trigger and the warning all rest on prompt_tokens arriving, so pin the parse against the real final chunk
        await using var server = new FakeOpenAiServer();
        server.Enqueue(new FakeResponse(Frames: SseFrames()));
        var client = new OpenAiCompatClient(new HttpClient(), "local", new EndpointConfig(server.BaseUrl));

        Usage? usage = null;
        await foreach (var ev in client.StreamAsync(
            new ChatRequest("m", new List<ChatMessage> { new("user", "weather?") }), CancellationToken.None))
        {
            if (ev is StreamEvent.Finished f && f.Usage is { } u) usage = u;
        }

        Assert.NotNull(usage);
        Assert.Equal(140, usage!.PromptTokens);
    }

    [Fact]
    public void RealSseCapture_WasTakenWithUsageRequested()
    {
        //a re-capture without the usage request has no usage in its final chunk, so this fails until it matches how the client asks
        var raw = ReadFixture(SseFixture);
        Assert.Contains("\"usage\":", raw, StringComparison.Ordinal);
        Assert.Contains("\"prompt_tokens\":140", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RealStream_NamesTheToolLongBeforeTheCallIsComplete()
    {
        //the function name must arrive in an early fragment, so pin that against the captured stream
        await using var server = new FakeOpenAiServer();
        server.Enqueue(new FakeResponse(Frames: SseFrames()));
        var client = new OpenAiCompatClient(new HttpClient(), "local", new EndpointConfig(server.BaseUrl));

        string? nameFirstSeenAt = null;
        var deltasBeforeName = 0;
        var deltasTotal = 0;
        var readyReached = false;

        await foreach (var ev in client.StreamAsync(
            new ChatRequest("m", new List<ChatMessage> { new("user", "weather?") }), CancellationToken.None))
        {
            switch (ev)
            {
                case StreamEvent.ToolCallDelta d:
                    deltasTotal++;
                    if (d.Name is { Length: > 0 } && nameFirstSeenAt is null) nameFirstSeenAt = d.Name;
                    if (nameFirstSeenAt is null) deltasBeforeName++;
                    break;
                case StreamEvent.ToolCallReady:
                    readyReached = true;
                    break;
            }
        }

        Assert.True(readyReached, "the captured stream should still complete its call");
        Assert.NotNull(nameFirstSeenAt);
        //the name must arrive early, since a name in the last fragment still paints the row late
        Assert.True(deltasBeforeName < deltasTotal - 1,
            $"the name arrived after {deltasBeforeName} of {deltasTotal} delta chunks — too late to be worth painting");
    }

    //built with every key and value kind of a live response to the exact request the extension sends, with invented titles, text and addresses

    private const string SearxngFixture = "searxng-search.json";
    private const string TavilyFixture = "tavily-search.json";

    [Fact]
    public void SearxngFixture_CarriesTheFieldsARealResponseCarries()
    {
        //a real payload has fields the parser never reads, so a fixture that loses them must fail this check
        using var doc = JsonDocument.Parse(ReadFixture(SearxngFixture));
        var root = doc.RootElement;

        foreach (var section in new[] { "query", "results", "answers", "corrections", "infoboxes", "suggestions", "unresponsive_engines" })
            Assert.True(root.TryGetProperty(section, out _), $"SearXNG responses carry a top-level '{section}'");

        var first = root.GetProperty("results")[0];
        Assert.Equal(JsonValueKind.Array, first.GetProperty("parsed_url").ValueKind);
        Assert.Equal(JsonValueKind.Array, first.GetProperty("engines").ValueKind);
        Assert.Equal(JsonValueKind.Array, first.GetProperty("positions").ValueKind);
        foreach (var field in new[] { "template", "engine", "score", "category", "publishedDate", "thumbnail", "img_src" })
            Assert.True(first.TryGetProperty(field, out _), $"SearXNG results carry '{field}'");

        //the fixture holds more results than any default count, so the cap meets a payload of real size
        Assert.True(root.GetProperty("results").GetArrayLength() > 10);
    }

    [Fact]
    public void TavilyFixture_CarriesTheFieldsARealResponseCarries()
    {
        using var doc = JsonDocument.Parse(ReadFixture(TavilyFixture));
        var root = doc.RootElement;

        foreach (var section in new[] { "query", "follow_up_questions", "answer", "images", "results", "response_time", "request_id" })
            Assert.True(root.TryGetProperty(section, out _), $"Tavily responses carry a top-level '{section}'");

        var first = root.GetProperty("results")[0];
        foreach (var field in new[] { "url", "title", "content", "score", "raw_content" })
            Assert.True(first.TryGetProperty(field, out _), $"Tavily results carry '{field}'");

        //the extension sends include_answer:false, so a real response holds an explicit null answer, where a double would omit the key
        Assert.Equal(JsonValueKind.Null, root.GetProperty("answer").ValueKind);
    }

    [Fact]
    public void SearchFixtures_CarryNoCredentialAndNoAccountIdentifier()
    {
        //neither fixture may hold an authorization header or an account-linked request id, only the synthetic placeholder
        var searx = ReadFixture(SearxngFixture);
        var tavily = ReadFixture(TavilyFixture);

        Assert.DoesNotContain("Bearer", searx, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Bearer", tavily, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("00000000-0000-4000-8000-000000000000", tavily, StringComparison.Ordinal);
    }
}
