using System.Text.Json;
using Gatto.Core.Client;
using Gatto.Tests.Fakes;

namespace Gatto.Tests;

public class OpenAiCompatClientTests
{
    private static string Chunk(string deltaJson, string? finish = null, string? usage = null, string? timings = null) =>
        $"data: {{\"choices\":[{{\"index\":0,\"delta\":{deltaJson},\"finish_reason\":{(finish is null ? "null" : $"\"{finish}\"")}}}]{(usage is null ? "" : $",\"usage\":{usage}")}{(timings is null ? "" : $",\"timings\":{timings}")}}}\n\n";

    private static OpenAiCompatClient Client(FakeOpenAiServer s, string name = "local") =>
        new(new HttpClient(), name, new EndpointConfig(s.BaseUrl));

    private static async Task<List<StreamEvent>> Collect(IChatClient c, ChatRequest req, CancellationToken ct = default)
    {
        var events = new List<StreamEvent>();
        await foreach (var e in c.StreamAsync(req, ct)) events.Add(e);
        return events;
    }

    private static ChatRequest Req() => new("test-model", new[] { new ChatMessage("user", "hi") });

    [Fact]
    public async Task Serializes_reasoning_content_on_assistant_messages()
    {
        //an assistant message must resend reasoning_content, so a model that reasons from past thinking keeps reasoning next turn
        await using var s = new FakeOpenAiServer();
        s.Enqueue(new FakeResponse(Frames: new[]
        {
            Chunk("{\"content\":\"ok\"}", finish: "stop"),
            "data: [DONE]\n\n",
        }));
        var req = new ChatRequest("m", new[]
        {
            new ChatMessage("user", "hi"),
            new ChatMessage("assistant", "prior answer", ReasoningContent: "prior thinking"),
            new ChatMessage("user", "again"),
        });

        await Collect(Client(s), req);

        var msgs = s.LastRequestBody!.Value.GetProperty("messages").EnumerateArray().ToList();
        var asst = msgs.First(m => m.GetProperty("role").GetString() == "assistant");
        Assert.Equal("prior thinking", asst.GetProperty("reasoning_content").GetString());
    }

    [Fact]
    public async Task Without_api_key_the_request_carries_no_authorization_header()
    {
        //an endpoint with neither ApiKey nor KeyEnv must send no authorization header, and the absence is asserted so one cannot appear later
        await using var s = new FakeOpenAiServer();
        s.Enqueue(new FakeResponse(Frames: new[] { Chunk("{\"content\":\"ok\"}", finish: "stop"), "data: [DONE]\n\n" }));

        await Collect(Client(s), Req());

        Assert.Null(s.LastAuthorization);
    }

    [Fact]
    public async Task With_api_key_the_request_carries_a_bearer_authorization_header()
    {
        await using var s = new FakeOpenAiServer();
        s.Enqueue(new FakeResponse(Frames: new[] { Chunk("{\"content\":\"ok\"}", finish: "stop"), "data: [DONE]\n\n" }));
        var client = new OpenAiCompatClient(
            new HttpClient(), "local", new EndpointConfig(s.BaseUrl, ApiKey: "s3cr3t-value"));

        await Collect(client, Req());

        Assert.Equal("Bearer s3cr3t-value", s.LastAuthorization);
    }

    [Fact]
    public async Task With_api_key_the_props_probe_is_authenticated_too()
    {
        //the server guards /props as well as chat, so the context probe sends the same credential or it 401s into context unknown
        await using var s = new FakeOpenAiServer
        {
            PropsResponse = new FakeResponse(Body: """{"default_generation_settings":{"n_ctx":4096}}"""),
        };
        var client = new OpenAiCompatClient(
            new HttpClient(), "local", new EndpointConfig(s.BaseUrl, ApiKey: "s3cr3t-value"));

        Assert.Equal(4096, await client.TryGetContextLengthAsync());
        Assert.Equal("Bearer s3cr3t-value", s.LastAuthorization);
    }

    [Fact]
    public async Task Endpoint_api_key_wins_over_key_env()
    {
        //when both are set the endpoint ApiKey wins, it is the key gatto handed to the server it launched
        var envName = "GATTO_TEST_KEY_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(envName, "from-env");
        try
        {
            await using var s = new FakeOpenAiServer();
            s.Enqueue(new FakeResponse(Frames: new[] { Chunk("{\"content\":\"ok\"}", finish: "stop"), "data: [DONE]\n\n" }));
            var client = new OpenAiCompatClient(
                new HttpClient(), "local", new EndpointConfig(s.BaseUrl, KeyEnv: envName, ApiKey: "from-model"));

            await Collect(client, Req());

            Assert.Equal("Bearer from-model", s.LastAuthorization);
        }
        finally { Environment.SetEnvironmentVariable(envName, null); }
    }

    [Fact]
    public void EndpointConfig_ToString_lists_every_member_and_redacts_the_secret_ones()
    {
        //every public member must show in ToString, and a name holding Key or Secret must not print its value
        var config = new EndpointConfig(
            "http://127.0.0.1:1235", KeyEnv: "GATTO_SOME_KEY_ENV", Context: 8192,
            Thinking: null, ApiKey: "s3cr3t-value");

        var text = config.ToString();

        foreach (var prop in typeof(EndpointConfig).GetProperties(
                     System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            Assert.Contains(prop.Name, text);

            //the KeyEnv member holds the name of an environment variable, which is not a credential, so it renders in full
            if (prop.Name == nameof(EndpointConfig.KeyEnv)) continue;

            //only the property name is matched, so a secret named Token or Password prints in full. a new secret needs Key or Secret in its name
            if (!prop.Name.Contains("Key", StringComparison.OrdinalIgnoreCase)
                && !prop.Name.Contains("Secret", StringComparison.OrdinalIgnoreCase))
                continue;

            var value = prop.GetValue(config)?.ToString();
            if (!string.IsNullOrEmpty(value))
                Assert.DoesNotContain(value, text);
        }

        Assert.Contains("GATTO_SOME_KEY_ENV", text);   //the non-secret name still renders, so the redaction does not over-fire
    }

    [Fact]
    public void EndpointConfig_ToString_never_renders_the_api_key()
    {
        //the synthesized ToString would print the key into any log line or failing assert, so the override exists
        var text = new EndpointConfig("http://127.0.0.1:1235", ApiKey: "s3cr3t-value").ToString();

        Assert.DoesNotContain("s3cr3t-value", text);
        Assert.Contains("***", text);
    }

    [Fact]
    public async Task Streams_reasoning_from_the_reasoning_delta_key()
    {
        //a hosted gateway streams thinking under reasoning and a local server under reasoning_content, so both keys feed the same event
        await using var s = new FakeOpenAiServer();
        s.Enqueue(new FakeResponse(Frames: new[]
        {
            Chunk("{\"reasoning\":\"hmm\",\"reasoning_details\":[{\"type\":\"reasoning.text\",\"text\":\"hmm\"}]}"),
            Chunk("{\"content\":\"ok\"}"),
            Chunk("{}", finish: "stop"),
            "data: [DONE]\n\n",
        }));

        var events = await Collect(Client(s), Req());

        Assert.Equal("hmm", Assert.IsType<StreamEvent.ReasoningDelta>(events[0]).Text);
        Assert.Equal("ok", Assert.IsType<StreamEvent.TextDelta>(events[1]).Text);
        Assert.IsType<StreamEvent.Finished>(events[2]);
    }

    [Fact]
    public async Task A_NULL_REASONING_CONTENT_FALLS_THROUGH_TO_THE_REASONING_KEY()
    {
        //a null reasoning_content must fall through to reasoning, or the text in the second key is hidden
        await using var s = new FakeOpenAiServer();
        s.Enqueue(new FakeResponse(Frames: new[]
        {
            Chunk("{\"reasoning_content\":null,\"reasoning\":\"hmm\"}"),
            Chunk("{\"content\":\"ok\"}"),
            Chunk("{}", finish: "stop"),
            "data: [DONE]\n\n",
        }));

        var events = await Collect(Client(s), Req());

        Assert.Equal("hmm", Assert.IsType<StreamEvent.ReasoningDelta>(events[0]).Text);
        Assert.Single(events, e => e is StreamEvent.ReasoningDelta);
        Assert.Equal("ok", Assert.IsType<StreamEvent.TextDelta>(events[1]).Text);
    }

    [Fact]
    public async Task Streams_text_reasoning_and_finish_with_usage()
    {
        await using var s = new FakeOpenAiServer();
        s.Enqueue(new FakeResponse(Frames: new[]
        {
            Chunk("{\"reasoning_content\":\"hmm\"}"),
            Chunk("{\"content\":\"hel\"}"),
            Chunk("{\"content\":\"lo\"}"),
            Chunk("{}", finish: "stop", usage: "{\"prompt_tokens\":5,\"completion_tokens\":2}"),
            "data: [DONE]\n\n",
        }));

        var events = await Collect(Client(s), Req());

        Assert.Equal("hmm", Assert.IsType<StreamEvent.ReasoningDelta>(events[0]).Text);
        Assert.Equal("hel", Assert.IsType<StreamEvent.TextDelta>(events[1]).Text);
        Assert.Equal("lo", Assert.IsType<StreamEvent.TextDelta>(events[2]).Text);
        var fin = Assert.IsType<StreamEvent.Finished>(events[3]);
        Assert.Equal("stop", fin.FinishReason);
        Assert.Equal(new Usage(5, 2), fin.Usage);
        Assert.Null(fin.Timings);   //the fixture has no timings, so the field must be null rather than an empty object.
    }

    //timings must pass through as raw JSON, its field set varies by server version
    [Fact]
    public async Task Finished_carries_timings_verbatim_alongside_usage()
    {
        await using var s = new FakeOpenAiServer();
        s.Enqueue(new FakeResponse(Frames: new[]
        {
            Chunk("{\"content\":\"hi\"}"),
            Chunk("{}", finish: "stop",
                usage: "{\"prompt_tokens\":5,\"completion_tokens\":2}",
                timings: "{\"predicted_per_second\":12.31,\"predicted_n\":7}"),
            "data: [DONE]\n\n",
        }));

        var events = await Collect(Client(s), Req());

        var fin = Assert.IsType<StreamEvent.Finished>(events[^1]);
        Assert.NotNull(fin.Timings);
        using var doc = JsonDocument.Parse(fin.Timings!);
        Assert.Equal(12.31, doc.RootElement.GetProperty("predicted_per_second").GetDouble());
        Assert.Equal(7, doc.RootElement.GetProperty("predicted_n").GetInt32());
    }

    [Fact]
    public async Task Assembles_tool_call_from_fragments_across_frames()
    {
        await using var s = new FakeOpenAiServer();
        s.Enqueue(new FakeResponse(Frames: new[]
        {
            Chunk("{\"tool_calls\":[{\"index\":0,\"id\":\"c1\",\"function\":{\"name\":\"read_file\",\"arguments\":\"{\\\"pa\"}}]}"),
            Chunk("{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"th\\\":\\\"a.txt\\\"}\"}}]}"),
            Chunk("{}", finish: "tool_calls"),
            "data: [DONE]\n\n",
        }));

        var events = await Collect(Client(s), Req());

        //find the ready call by type, the liveness deltas come first, and there is exactly one with complete arguments
        var ready = Assert.IsType<StreamEvent.ToolCallReady>(events.Single(e => e is StreamEvent.ToolCallReady));
        Assert.Equal(new ToolCall("c1", "read_file", "{\"pa" + "th\":\"a.txt\"}"), ready.Call);
        Assert.IsType<StreamEvent.Finished>(events[^1]);
    }

    [Fact]
    public async Task ToolCallFragments_YieldToolCallDeltaPerChunk()
    {
        await using var s = new FakeOpenAiServer();
        s.Enqueue(new FakeResponse(Frames: new[]
        {
            Chunk("{\"content\":\"hi\"}"),
            Chunk("{\"tool_calls\":[{\"index\":0,\"id\":\"c1\",\"function\":{\"name\":\"write_file\",\"arguments\":\"{\\\"pa\"}}]}"),
            Chunk("{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"th\\\":1}\"}}]}"),
            Chunk("{}", finish: "tool_calls"),
            "data: [DONE]\n\n",
        }));

        var events = await Collect(Client(s), Req());

        Assert.Equal(2, events.OfType<StreamEvent.ToolCallDelta>().Count());
        //pins the order: every liveness delta arrives before the assembled ready event.
        var firstDelta = events.FindIndex(e => e is StreamEvent.ToolCallDelta);
        var ready = events.FindIndex(e => e is StreamEvent.ToolCallReady);
        Assert.True(firstDelta >= 0 && firstDelta < ready);
    }

    [Fact]
    public async Task Dropped_stream_mid_tool_call_yields_truncated_and_never_tool_ready()
    {
        await using var s = new FakeOpenAiServer();
        s.Enqueue(new FakeResponse(Frames: new[]
        {
            Chunk("{\"content\":\"working...\"}"),
            Chunk("{\"tool_calls\":[{\"index\":0,\"id\":\"c1\",\"function\":{\"name\":\"shell\",\"arguments\":\"{\\\"cm\"}}]}"),
            Chunk("{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"d...never finished\"}}]}"),
        }, DropAfterFrames: 3));

        var events = await Collect(Client(s), Req());

        Assert.DoesNotContain(events, e => e is StreamEvent.ToolCallReady);
        var trunc = Assert.IsType<StreamEvent.Truncated>(events[^1]);
        Assert.True(trunc.DroppedInFlightToolCall);
    }

    [Fact]
    public async Task Malformed_json_chunk_truncates_instead_of_crashing()
    {
        await using var s = new FakeOpenAiServer();
        s.Enqueue(new FakeResponse(Frames: new[]
        {
            Chunk("{\"content\":\"ok so far\"}"),
            "data: {this is not json\n\n",
            Chunk("{}", finish: "stop"),
        }));

        var events = await Collect(Client(s), Req());

        Assert.IsType<StreamEvent.Truncated>(events[^1]);
        Assert.DoesNotContain(events, e => e is StreamEvent.Finished);
    }

    [Fact]
    public async Task Connection_refused_local_gives_serve_hint()
    {
        var dead = new OpenAiCompatClient(new HttpClient(), "local",
            new EndpointConfig("http://127.0.0.1:1"));
        var ex = await Assert.ThrowsAsync<GattoConnectionException>(() => Collect(dead, Req()));
        Assert.Contains("gatto serve start test-model", ex.Message);
    }

    [Fact]
    public async Task Connection_refused_cloud_gives_endpoint_message()
    {
        var dead = new OpenAiCompatClient(new HttpClient(), "openrouter",
            new EndpointConfig("http://127.0.0.1:1"));
        var ex = await Assert.ThrowsAsync<GattoConnectionException>(() => Collect(dead, Req()));
        Assert.Contains("endpoint openrouter unreachable", ex.Message);
    }

    //a handler whose SendAsync a test drives, since SocketsHttpHandler surfaces a connect timeout as TaskCanceledException with the caller's token still live
    private sealed class FuncHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> fn)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => fn(request, ct);
    }

    [Fact]
    public async Task ConnectTimeout_OceWithoutUserCancel_MapsToConnectionError_Local()
    {
        //a timeout arrives as a cancellation with the caller's token still live, and it must map to the local hint gatto prints
        var http = new HttpClient(new FuncHandler((_, _) => throw new TaskCanceledException("connect timed out")));
        var client = new OpenAiCompatClient(http, "local", new EndpointConfig("http://127.0.0.1:1235"));
        var ex = await Assert.ThrowsAsync<GattoConnectionException>(() => Collect(client, Req()));
        Assert.Contains("gatto serve start test-model", ex.Message);
    }

    [Fact]
    public async Task ConnectTimeout_OceWithoutUserCancel_MapsToConnectionError_Cloud()
    {
        var http = new HttpClient(new FuncHandler((_, _) => throw new TaskCanceledException("connect timed out")));
        var client = new OpenAiCompatClient(http, "openrouter", new EndpointConfig("http://example.invalid"));
        var ex = await Assert.ThrowsAsync<GattoConnectionException>(() => Collect(client, Req()));
        Assert.Contains("endpoint openrouter unreachable", ex.Message);
    }

    [Fact]
    public async Task GenuineUserCancellation_PropagatesAsOce_NotConnectionError()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        //a real Ctrl+C cancels the caller's token, and the guard must let it propagate instead of calling it a connection error
        var http = new HttpClient(new FuncHandler((_, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage());
        }));
        var client = new OpenAiCompatClient(http, "local", new EndpointConfig("http://127.0.0.1:1235"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Collect(client, Req(), cts.Token));
    }

    [Fact]
    public async Task Request_body_is_snake_case_with_stream_and_tools()
    {
        await using var s = new FakeOpenAiServer();
        s.Enqueue(new FakeResponse(Frames: new[] { Chunk("{}", finish: "stop"), "data: [DONE]\n\n" }));

        var schema = JsonDocument.Parse("{\"type\":\"object\"}").RootElement;
        var req = new ChatRequest("m", new[] { new ChatMessage("user", "hi") },
            new[] { new ToolSpec("read_file", "reads", schema) });
        await Collect(Client(s), req);

        var body = s.LastRequestBody!.Value;
        Assert.True(body.GetProperty("stream").GetBoolean());
        Assert.True(body.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
        var tool = body.GetProperty("tools")[0];
        Assert.Equal("function", tool.GetProperty("type").GetString());
        Assert.Equal("read_file", tool.GetProperty("function").GetProperty("name").GetString());
    }

    //the prefill chunk has no delta and holds prompt_progress, which becomes a progress event ahead of the text that follows
    [Fact]
    public async Task A_prompt_progress_chunk_becomes_a_progress_event()
    {
        await using var s = new FakeOpenAiServer();
        s.Enqueue(new FakeResponse(Frames: new[]
        {
            "data: {\"choices\":[],\"prompt_progress\":{\"total\":15063,\"cache\":0,\"processed\":4138,\"time_ms\":47407}}\n\n",
            Chunk("{\"content\":\"ok\"}", finish: "stop"),
            "data: [DONE]\n\n",
        }));

        var events = await Collect(Client(s), Req());

        var progress = Assert.IsType<StreamEvent.PromptProgress>(events[0]);
        Assert.Equal(15063, progress.Total);
        Assert.Equal(4138, progress.Processed);
        Assert.Equal("ok", Assert.IsType<StreamEvent.TextDelta>(events[1]).Text);
    }

    //the body asks for prefill progress only when the client was built for it, which gatto does for its own local server
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Return_progress_is_in_the_body_only_when_the_client_asks(bool returnProgress)
    {
        await using var s = new FakeOpenAiServer();
        s.Enqueue(new FakeResponse(Frames: new[] { Chunk("{}", finish: "stop"), "data: [DONE]\n\n" }));
        var client = new OpenAiCompatClient(new HttpClient(), returnProgress ? "local" : "remote",
            new EndpointConfig(s.BaseUrl), returnProgress: returnProgress);

        await Collect(client, Req());

        var body = s.LastRequestBody!.Value;
        Assert.Equal(returnProgress, body.TryGetProperty("return_progress", out var rp));
        if (returnProgress) Assert.True(rp.GetBoolean());
    }

    //a body override must not write return_progress twice when the client already wrote it
    [Fact]
    public async Task A_body_override_cannot_write_return_progress_a_second_time()
    {
        await using var s = new FakeOpenAiServer();
        s.Enqueue(new FakeResponse(Frames: new[] { Chunk("{}", finish: "stop"), "data: [DONE]\n\n" }));
        var client = new OpenAiCompatClient(new HttpClient(), "local", new EndpointConfig(s.BaseUrl),
            returnProgress: true);
        var req = new ChatRequest("m", new[] { new ChatMessage("user", "hi") },
            BodyOverrides: JsonDocument.Parse("{\"return_progress\":false}").RootElement);

        await Collect(client, req);

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(s.LastRequestRaw!, "\"return_progress\""));
        Assert.True(s.LastRequestBody!.Value.GetProperty("return_progress").GetBoolean());
    }

    [Fact]
    public async Task Cancellation_propagates_and_emits_no_tool_ready()
    {
        await using var s = new FakeOpenAiServer();
        var frames = new List<string> { Chunk("{\"content\":\"a\"}") };
        for (var i = 0; i < 200; i++) frames.Add(Chunk("{\"content\":\"x\"}"));
        s.Enqueue(new FakeResponse(Frames: frames.ToArray()));

        using var cts = new CancellationTokenSource();
        var events = new List<StreamEvent>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var e in Client(s).StreamAsync(Req(), cts.Token))
            {
                events.Add(e);
                if (events.Count == 1) cts.Cancel();
            }
        });
        Assert.DoesNotContain(events, e => e is StreamEvent.ToolCallReady);
    }

    [Theory]
    [InlineData("{\"choices\":null}")]
    [InlineData("{\"choices\":[null]}")]
    [InlineData("{\"choices\":[{\"delta\":null,\"finish_reason\":null}]}")]
    [InlineData("{\"choices\":[{\"delta\":{\"tool_calls\":null},\"finish_reason\":null}]}")]
    [InlineData("{\"choices\":[{\"delta\":{\"tool_calls\":[null]},\"finish_reason\":null}]}")]
    [InlineData("{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":null}]},\"finish_reason\":null}]}")]
    public async Task Valid_json_wrong_shape_chunks_are_skipped_not_crashing(string badChunkJson)
    {
        await using var s = new FakeOpenAiServer();
        s.Enqueue(new FakeResponse(Frames: new[]
        {
            $"data: {badChunkJson}\n\n",
            Chunk("{\"content\":\"still here\"}"),
            Chunk("{}", finish: "stop"),
            "data: [DONE]\n\n",
        }));

        var events = await Collect(Client(s), Req());

        Assert.Contains(events, e => e is StreamEvent.TextDelta t && t.Text == "still here");
        Assert.IsType<StreamEvent.Finished>(events[^1]);
    }

    [Fact]
    public async Task No_overrides_body_shape_is_unchanged_from_plan1()
    {
        await using var s = new FakeOpenAiServer();
        s.Enqueue(new FakeResponse(Frames: new[] { Chunk("{}", finish: "stop"), "data: [DONE]\n\n" }));

        await Collect(Client(s), Req());

        var body = s.LastRequestBody!.Value;
        var names = body.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal);
        Assert.Equal(new[] { "messages", "model", "stream", "stream_options" }, names);
    }

    [Fact]
    public async Task SamplingOverrides_land_top_level()
    {
        await using var s = new FakeOpenAiServer();
        s.Enqueue(new FakeResponse(Frames: new[] { Chunk("{}", finish: "stop"), "data: [DONE]\n\n" }));

        var sampling = JsonDocument.Parse("""{"temperature":0.3,"top_p":0.9}""").RootElement;
        var req = Req() with { SamplingOverrides = sampling };
        await Collect(Client(s), req);

        var body = s.LastRequestBody!.Value;
        Assert.Equal(0.3, body.GetProperty("temperature").GetDouble());
        Assert.Equal(0.9, body.GetProperty("top_p").GetDouble());
    }

    [Fact]
    public async Task BodyOverrides_land_top_level()
    {
        await using var s = new FakeOpenAiServer();
        s.Enqueue(new FakeResponse(Frames: new[] { Chunk("{}", finish: "stop"), "data: [DONE]\n\n" }));

        var thinkingBody = JsonDocument.Parse("""{"chat_template_kwargs":{"enable_thinking":true}}""").RootElement;
        var req = Req() with { BodyOverrides = thinkingBody };
        await Collect(Client(s), req);

        var body = s.LastRequestBody!.Value;
        Assert.True(body.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
    }

    [Fact]
    public async Task BodyOverrides_win_collision_with_SamplingOverrides()
    {
        await using var s = new FakeOpenAiServer();
        s.Enqueue(new FakeResponse(Frames: new[] { Chunk("{}", finish: "stop"), "data: [DONE]\n\n" }));

        var sampling = JsonDocument.Parse("""{"temperature":0.3}""").RootElement;
        var thinkingBody = JsonDocument.Parse("""{"temperature":0.9}""").RootElement;
        var req = Req() with { SamplingOverrides = sampling, BodyOverrides = thinkingBody };
        await Collect(Client(s), req);

        var body = s.LastRequestBody!.Value;
        Assert.Equal(0.9, body.GetProperty("temperature").GetDouble());
    }

    [Fact]
    public async Task Empty_object_overrides_add_no_keys()
    {
        await using var s = new FakeOpenAiServer();
        s.Enqueue(new FakeResponse(Frames: new[] { Chunk("{}", finish: "stop"), "data: [DONE]\n\n" }));

        var empty = JsonDocument.Parse("{}").RootElement;
        var req = Req() with { SamplingOverrides = empty, BodyOverrides = empty };
        await Collect(Client(s), req);

        var body = s.LastRequestBody!.Value;
        var names = body.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal);
        Assert.Equal(new[] { "messages", "model", "stream", "stream_options" }, names);
    }

    [Fact]
    public async Task Overrides_colliding_with_harness_managed_keys_do_not_win()
    {
        await using var s = new FakeOpenAiServer();
        s.Enqueue(new FakeResponse(Frames: new[] { Chunk("{}", finish: "stop"), "data: [DONE]\n\n" }));

        var sneaky = JsonDocument.Parse("""{"model":"hijacked","stream":false}""").RootElement;
        var req = Req() with { SamplingOverrides = sneaky };
        await Collect(Client(s), req);

        var body = s.LastRequestBody!.Value;
        Assert.Equal("test-model", body.GetProperty("model").GetString());
        Assert.True(body.GetProperty("stream").GetBoolean());
    }

    [Fact]
    public async Task Non_object_override_throws_not_swallowed()
    {
        await using var s = new FakeOpenAiServer();
        s.Enqueue(new FakeResponse(Frames: new[] { Chunk("{}", finish: "stop"), "data: [DONE]\n\n" }));

        var notAnObject = JsonDocument.Parse("[1,2,3]").RootElement;
        var req = Req() with { SamplingOverrides = notAnObject };

        await Assert.ThrowsAsync<ArgumentException>(() => Collect(Client(s), req));
    }

    [Fact]
    public async Task Poisoned_assistant_message_is_omitted_from_request_body()
    {
        //an assistant turn with no content and no tool calls is dropped from the body, which heals the session without orphaning a tool message
        await using var s = new FakeOpenAiServer();
        s.Enqueue(new FakeResponse(Frames: new[] { Chunk("{}", finish: "stop"), "data: [DONE]\n\n" }));

        var req = new ChatRequest("m", new[]
        {
            new ChatMessage("user", "hi"),
            new ChatMessage("assistant", null, null),   //the empty assistant turn this test is about
        });
        await Collect(Client(s), req);

        var messages = s.LastRequestBody!.Value.GetProperty("messages").EnumerateArray().ToList();
        Assert.Single(messages);
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
    }

    //a context-overflow 400 needs its own type so the rescue path and the compactor can catch it, and it stays a GattoConnectionException subtype
    [Fact]
    public async Task Overflow400_BecomesTypedException_WithCounts()
    {
        //the body is replayed exactly as the server sent it, so the matcher faces the real shape
        await using var s = new FakeOpenAiServer();
        const string body = """{"error":{"code":400,"message":"request (69716 tokens) exceeds the available context size (65536 tokens), try increasing it","type":"exceed_context_size_error","n_prompt_tokens":69716,"n_ctx":65536}}""";
        s.Enqueue(new FakeResponse(Status: 400, Body: body));

        var ex = await Assert.ThrowsAsync<GattoContextOverflowException>(() => Collect(Client(s), Req()));

        Assert.Equal(69716, ex.PromptTokens);
        Assert.Equal(65536, ex.ContextSize);
    }

    [Fact]
    public async Task Overflow400_TypeMatchButNoCounts_FallsBackToMessageIntegers()
    {
        await using var s = new FakeOpenAiServer();
        const string body = """{"error":{"code":400,"message":"request (7000 tokens) exceeds the available context size (4096 tokens)","type":"exceed_context_size_error"}}""";
        s.Enqueue(new FakeResponse(Status: 400, Body: body));

        var ex = await Assert.ThrowsAsync<GattoContextOverflowException>(() => Collect(Client(s), Req()));

        Assert.Equal(7000, ex.PromptTokens);
        Assert.Equal(4096, ex.ContextSize);
    }

    [Fact]
    public async Task Overflow400_NoCountsAnywhere_TypedWithNulls()
    {
        await using var s = new FakeOpenAiServer();
        const string body = """{"error":{"message":"too big","type":"exceed_context_size_error"}}""";
        s.Enqueue(new FakeResponse(Status: 400, Body: body));

        var ex = await Assert.ThrowsAsync<GattoContextOverflowException>(() => Collect(Client(s), Req()));

        Assert.Null(ex.PromptTokens);
        Assert.Null(ex.ContextSize);
    }

    [Fact]
    public async Task NonOverflow400_KeepsGenericConnectionException()
    {
        await using var s = new FakeOpenAiServer();
        const string body = """{"error":{"code":400,"message":"bad request","type":"invalid_request_error"}}""";
        s.Enqueue(new FakeResponse(Status: 400, Body: body));

        //the status reply throws a subclass, so ThrowsAnyAsync is needed and IsType asserts the exact type below
        var ex = await Assert.ThrowsAnyAsync<GattoConnectionException>(() => Collect(Client(s), Req()));

        Assert.IsNotType<GattoContextOverflowException>(ex);
        //the status code comes from the typed field, the message text is not parsed
        Assert.Equal(400, Assert.IsType<GattoHttpStatusException>(ex).StatusCode);
    }

    [Fact]
    public async Task Overflow500_IsNotMapped()   //only a 400 maps to the overflow type.
    {
        await using var s = new FakeOpenAiServer();
        const string body = """{"error":{"type":"exceed_context_size_error"}}""";
        s.Enqueue(new FakeResponse(Status: 500, Body: body));

        var ex = await Assert.ThrowsAnyAsync<GattoConnectionException>(() => Collect(Client(s), Req()));

        Assert.IsNotType<GattoContextOverflowException>(ex);
        Assert.Equal(500, Assert.IsType<GattoHttpStatusException>(ex).StatusCode);
    }

    //a call to GetString on a numeric element throws InvalidOperationException, so a numeric type must still reach the generic path
    [Fact]
    public async Task Overflow400_NonStringType_FallsBackToGenericConnectionException()
    {
        await using var s = new FakeOpenAiServer();
        const string body = """{"error":{"code":400,"message":"x","type":123}}""";
        s.Enqueue(new FakeResponse(Status: 400, Body: body));

        var ex = await Assert.ThrowsAnyAsync<GattoConnectionException>(() => Collect(Client(s), Req()));

        Assert.IsNotType<GattoContextOverflowException>(ex);
        Assert.Equal(400, Assert.IsType<GattoHttpStatusException>(ex).StatusCode);
    }

    [Fact]
    public async Task GattoRole_IsError_Gloss_never_reach_the_wire()
    {
        //ts and usage belong to the session file, and none of them may reach the request body sent to the model
        await using var s = new FakeOpenAiServer();
        s.Enqueue(new FakeResponse(Frames: new[] { Chunk("{}", finish: "stop"), "data: [DONE]\n\n" }));

        var req = new ChatRequest("m", new[]
        {
            new ChatMessage("tool", "boom", ToolCallId: "c1",
                GattoRole: "coder", IsError: true, Gloss: "exit 1",
                Ts: DateTime.UtcNow,
                Usage: new Usage(5, 2), Timings: "{\"predicted_per_second\":12.3}"),
        });
        await Collect(Client(s), req);

        var msg = s.LastRequestBody!.Value.GetProperty("messages")[0];
        var names = msg.EnumerateObject().Select(p => p.Name).ToList();
        Assert.DoesNotContain("gatto_role", names);
        Assert.DoesNotContain("is_error", names);
        Assert.DoesNotContain("gloss", names);
        Assert.DoesNotContain("ts", names);
        Assert.DoesNotContain("usage", names);
        Assert.DoesNotContain("timings", names);
    }

    //the failure line shows the server's own sentence, since a printed envelope buries the useful half in braces
    [Theory]
    //the error.message shape that LM Studio and OpenAI-compatible servers send
    [InlineData("{\"error\":{\"message\":\"No models loaded. Please load a model.\",\"type\":\"invalid_request_error\"}}",
                "No models loaded. Please load a model.")]
    //some servers flatten error to a bare string, which this row covers
    [InlineData("{\"error\":\"model not found\"}", "model not found")]
    //a capitalised Error beside a code and an empty details object, as a provider's quota refusal sends it
    [InlineData("{\"Code\":2028,\"Error\":\"You have reached your daily quota\",\"Details\":{}}", "You have reached your daily quota")]
    //a top-level message with no error key at all
    [InlineData("{\"message\":\"Rate limit exceeded\",\"status\":429}", "Rate limit exceeded")]
    //an error object whose sentence sits under a capitalised Message
    [InlineData("{\"Error\":{\"Message\":\"quota exhausted\"}}", "quota exhausted")]
    public void THE_FAILURE_LINE_CARRIES_THE_SERVERS_OWN_SENTENCE(string body, string expected) =>
        Assert.Equal(expected, Gatto.Core.Client.OpenAiCompatClient.ServerSaid(body));

    //an unfamiliar body must be shown whole, or the user loses the only evidence of the failure
    [Theory]
    [InlineData("Service Unavailable")]              //a plain-text body that is not JSON at all
    [InlineData("{\"detail\":\"nope\"}")]           //the body is json without the error.message shape
    [InlineData("{ this is not json at all")]        //the body looks like JSON yet fails to parse.
    [InlineData("")]                                  //the fixture is the empty string.
    public void AN_UNFAMILIAR_BODY_IS_SHOWN_WHOLE_never_swallowed(string body) =>
        Assert.Equal(body, Gatto.Core.Client.OpenAiCompatClient.ServerSaid(body));
}
