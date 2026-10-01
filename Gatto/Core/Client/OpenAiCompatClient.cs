using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Gatto.Core.Home;

namespace Gatto.Core.Client;

public sealed class OpenAiCompatClient : IChatClient
{
    private readonly HttpClient http;
    private readonly string endpointName;
    private readonly EndpointConfig endpoint;
    private readonly string baseUrl;
    private readonly IWireLog? wireLog;
    private readonly string? downHint;
    private readonly bool returnProgress;

    //demands a resolved BaseUrl, and downHint replaces the advice for an endpoint gatto built itself (the status branches keep the server's own words)
    public OpenAiCompatClient(HttpClient http, string endpointName, EndpointConfig endpoint,
        IWireLog? wireLog = null, string? downHint = null, bool returnProgress = false)
    {
        if (endpoint.BaseUrl is null)
            throw new GattoConfigException($"endpoint '{endpointName}' has no base_url resolved");
        this.http = http;
        this.endpointName = endpointName;
        this.endpoint = endpoint;
        this.wireLog = wireLog;
        this.downHint = downHint;
        this.returnProgress = returnProgress;
        baseUrl = endpoint.BaseUrl;
    }

    //the one sentence for nothing answering, so the two catch blocks can't drift apart on it
    private string DownMessage(string model) =>
        downHint
        ?? (endpointName == "local"
            //say which terminal to run it in, this line is read inside a running gatto. the word is "another" rather than "new" for -p, which already runs in a terminal
            ? $"server down — in another terminal, run: gatto serve start {model}"
            : $"endpoint {endpointName} unreachable — check network/base_url");

    public async IAsyncEnumerable<StreamEvent> StreamAsync(
        ChatRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var msg = new HttpRequestMessage(HttpMethod.Post,
            $"{baseUrl.TrimEnd('/')}/v1/chat/completions");
        var body = BuildBody(request, imageCache, OnImageUnavailable, returnProgress);
        //tee the same string that becomes the body, so the log is diffable against what the server received
        if (wireLog is not null)
        {
            try { wireLog.Record(body); }
            catch (Exception) { } //fail-open, a debug instrument must never end a turn
        }
        msg.Content = new StringContent(body, Encoding.UTF8, "application/json");
        await AuthorizeAsync(msg, ct);

        HttpResponseMessage resp;
        try
        {
            resp = await http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException)
        {
            throw new GattoConnectionException(DownMessage(request.Model));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            //a connect timeout throws an OCE rather than HttpRequestException, so only a cancel the caller did not ask for becomes the connection error
            throw new GattoConnectionException(DownMessage(request.Model));
        }

        using var _ = resp;
        if (!resp.IsSuccessStatusCode)
        {
            //parse the whole body before truncating, the trailing n_ctx field sits past the display limit
            var errBody = await resp.Content.ReadAsStringAsync(ct);
            var display = $"endpoint {endpointName} returned {(int)resp.StatusCode}: {Truncate(ServerSaid(errBody), 200)}";
            if ((int)resp.StatusCode == 400 && TryParseContextOverflow(errBody, out var np, out var nc))
                throw new GattoContextOverflowException(display, np, nc, (int)resp.StatusCode, errBody);
            //the server answered, so the status travels typed, no caller re-reads it out of the display string
            throw new GattoHttpStatusException(display, (int)resp.StatusCode, errBody);
        }

        var pending = new SortedDictionary<int, (string Id, string Name, StringBuilder Args)>();
        string? finishReason = null;
        Usage? usage = null;
        JsonElement? usageRaw = null;
        string? timings = null;
        var stream = await resp.Content.ReadAsStreamAsync(ct);

        //an IO failure ends the turn as Truncated, a cancelled token propagates
        await using var enumerator = SseReader.ReadDataPayloads(stream, ct).GetAsyncEnumerator(ct);
        while (true)
        {
            string payload;
            var streamDied = false;
            try
            {
                if (!await enumerator.MoveNextAsync()) break;
                payload = enumerator.Current;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) //server aborted or the network died mid-stream
            {
                streamDied = true;
                payload = "";
            }
            if (streamDied)
            {
                yield return new StreamEvent.Truncated(pending.Count > 0);
                yield break;
            }

            if (payload == "[DONE]") continue;
            JsonDocument? doc = null;
            var malformed = false;
            try { doc = JsonDocument.Parse(payload); }
            catch (JsonException) { malformed = true; }
            if (malformed)
            {
                //degrade rather than crash, a garbled chunk ends the turn as truncation
                yield return new StreamEvent.Truncated(pending.Count > 0);
                yield break;
            }
            using var _doc = doc!;
            var root = doc!.RootElement;

            //a prefill chunk has prompt_progress and no delta, so read it here before the choices check skips it
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("prompt_progress", out var pp) && pp.ValueKind == JsonValueKind.Object
                && pp.TryGetProperty("total", out var ppTotal) && ppTotal.ValueKind == JsonValueKind.Number
                && pp.TryGetProperty("processed", out var ppDone) && ppDone.ValueKind == JsonValueKind.Number
                && ppTotal.TryGetInt64(out var promptTotal) && ppDone.TryGetInt64(out var promptDone))
                yield return new StreamEvent.PromptProgress(promptTotal, promptDone);

            //buffer the chunk's events, yield return is illegal inside a try/catch, so a shape oddity skips the chunk
            var chunkEvents = new List<StreamEvent>();
            try
            {
                if (root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
                {
                    //the whole object is kept for OnUsage, a provider's limits and cost sit beside the two counts the loop reads
                    usageRaw = u.Clone();
                    usage = new Usage(
                        u.TryGetProperty("prompt_tokens", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : 0,
                        u.TryGetProperty("completion_tokens", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 0);
                }

                //the server's timings arrive in the same final chunk and stay raw JSON, gatto has no opinion on their shape
                if (root.TryGetProperty("timings", out var tm) && tm.ValueKind == JsonValueKind.Object)
                    timings = tm.GetRawText();

                if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                    continue;
                var choice = choices[0];
                if (choice.ValueKind != JsonValueKind.Object) continue;

                if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
                {
                    //a local server streams thinking as reasoning_content and a gateway as reasoning, and only the first non-empty one becomes an event
                    if ((NonEmptyText(delta, "reasoning_content") ?? NonEmptyText(delta, "reasoning")) is { } rs)
                        chunkEvents.Add(new StreamEvent.ReasoningDelta(rs));
                    if (delta.TryGetProperty("content", out var t) &&
                        t.ValueKind == JsonValueKind.String && t.GetString() is { Length: > 0 } ts)
                        chunkEvents.Add(new StreamEvent.TextDelta(ts));
                    if (delta.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
                    {
                        var sawFragment = false;
                        foreach (var call in calls.EnumerateArray())
                        {
                            sawFragment = true;
                            if (call.ValueKind != JsonValueKind.Object) continue;
                            if (!call.TryGetProperty("index", out var indexProp) || indexProp.ValueKind != JsonValueKind.Number)
                                continue;
                            var idx = indexProp.GetInt32();
                            if (!pending.TryGetValue(idx, out var acc)) acc = ("", "", new StringBuilder());
                            if (call.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                                acc.Id = id.GetString()!;
                            if (call.TryGetProperty("function", out var fn) && fn.ValueKind == JsonValueKind.Object)
                            {
                                if (fn.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                                    acc.Name += n.GetString();
                                if (fn.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.String)
                                    acc.Args.Append(a.GetString());
                            }
                            pending[idx] = acc;
                        }

                        //emit after the accumulation so the event has what is known so far, the highest index is the newest call
                        if (sawFragment && pending.Count > 0)
                        {
                            var live = pending[pending.Keys.Max()];
                            chunkEvents.Add(new StreamEvent.ToolCallDelta(
                                live.Name.Length > 0 ? live.Name : null,
                                live.Args.Length > 0 ? live.Args.ToString() : null));
                        }
                        else if (sawFragment)
                        {
                            chunkEvents.Add(new StreamEvent.ToolCallDelta());
                        }
                    }
                }

                if (choice.TryGetProperty("finish_reason", out var f) && f.ValueKind == JsonValueKind.String)
                    finishReason = f.GetString();
            }
            catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException or FormatException)
            {
                continue;
            }

            foreach (var e in chunkEvents)
                yield return e;
        }

        if (finishReason is null)
        {
            //no finish_reason means the stream was cut short, discard the partial tool calls
            yield return new StreamEvent.Truncated(pending.Count > 0);
            yield break;
        }

        foreach (var (_, acc) in pending)
            yield return new StreamEvent.ToolCallReady(new ToolCall(acc.Id, acc.Name, acc.Args.ToString()));
        if (usageRaw is { } reported && OnUsage is { } onUsage)
        {
            try { onUsage(reported); }
            catch (Exception) { }   //fail-open, the footer's feed must never end a turn
        }
        yield return new StreamEvent.Finished(finishReason, usage, timings);
    }

    //add the endpoint's credential to the request, its header callback first, then ApiKey, then KeyEnv, and no header at all when none is set
    private Task AuthorizeAsync(HttpRequestMessage msg, CancellationToken ct) => EndpointAuth.ApplyAsync(msg, endpoint, endpointName, ct);

    public async Task<int?> TryGetContextLengthAsync(CancellationToken ct = default)
    {
        //a cloud endpoint is not a llama-server and has no /props, its window comes from its configuration
        if (CloudEndpoint.Is(endpoint)) return null;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            //authorize this probe too, llama-server's --api-key middleware guards /props and a 401 would read as unknown
            using var probe = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl.TrimEnd('/')}/props");
            await AuthorizeAsync(probe, cts.Token);
            using var resp = await http.SendAsync(probe, cts.Token);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
            return doc.RootElement.TryGetProperty("default_generation_settings", out var g)
                && g.ValueKind == JsonValueKind.Object
                && g.TryGetProperty("n_ctx", out var n) && n.ValueKind == JsonValueKind.Number
                && n.TryGetInt32(out var v) ? v : null;
        }
        catch (Exception) { return null; }
    }

    //the server's own sentence pulled out of the JSON envelope, display only (the raw body still goes on the typed exception)
    internal static string ServerSaid(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return body;
        try
        {
            using var doc = JsonDocument.Parse(body);
            //providers differ in case and nesting, so error.message, a bare error and a top-level message are all looked for, any case
            if (doc.RootElement.ValueKind is JsonValueKind.Object)
            {
                if (Prop(doc.RootElement, "error") is { } err)
                {
                    if (err.ValueKind is JsonValueKind.String
                        && err.GetString() is { Length: > 0 } flat) return flat;
                    if (err.ValueKind is JsonValueKind.Object
                        && Prop(err, "message") is { ValueKind: JsonValueKind.String } m
                        && m.GetString() is { Length: > 0 } text) return text;
                }
                if (Prop(doc.RootElement, "message") is { ValueKind: JsonValueKind.String } top
                    && top.GetString() is { Length: > 0 } said) return said;
            }
        }
        catch (JsonException) { } //not JSON, the body itself is the only evidence there is
        return body;
    }

    //the first property whose name matches ignoring case, an exact match wins when both spellings are present
    private static JsonElement? Prop(JsonElement o, string name)
    {
        if (o.TryGetProperty(name, out var exact)) return exact;
        foreach (var p in o.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p.Value;
        return null;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    private static string? NonEmptyText(JsonElement o, string key) =>
        o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;

    //true only for llama.cpp's exceed_context_size_error, counts come from the fields or the first two integers of the message, false for anything unparseable
    private static bool TryParseContextOverflow(string body, out int? promptTokens, out int? contextSize)
    {
        promptTokens = null; contextSize = null;
        if (body.Length > 4096) return false;   //bounded parse, real llama.cpp bodies are about 250 chars
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("error", out var err) || err.ValueKind != JsonValueKind.Object)
                return false;
            if (!err.TryGetProperty("type", out var t) || t.ValueKind != JsonValueKind.String
                || t.GetString() != "exceed_context_size_error")
                return false;
            if (err.TryGetProperty("n_prompt_tokens", out var p) && p.ValueKind == JsonValueKind.Number)
                promptTokens = p.GetInt32();
            if (err.TryGetProperty("n_ctx", out var c) && c.ValueKind == JsonValueKind.Number)
                contextSize = c.GetInt32();
            if ((promptTokens is null || contextSize is null)
                && err.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                && m.GetString() is { } msg)
            {
                var nums = Regex.Matches(msg, "[0-9]+");
                if (nums.Count >= 2)
                {
                    promptTokens ??= int.TryParse(nums[0].Value, out var a) ? a : null;
                    contextSize ??= int.TryParse(nums[1].Value, out var b) ? b : null;
                }
            }
            return true;
        }
        catch (JsonException) { return false; }
    }

    //per-client image cache, a client lives as long as the session and that is the scope prefix stability needs
    private readonly ImageCache imageCache = new();

    //raised when an attached image can't be sent, the caller prints a line and the request takes a text stub
    public Action<string>? OnImageUnavailable { get; set; }

    //the raw usage of each finished request, once and at the end, so a run_agent child on this client feeds it too
    public Action<JsonElement>? OnUsage { get; set; }

    private static string BuildBody(ChatRequest request, ImageCache images, Action<string>? onImageUnavailable,
        bool returnProgress)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("model", request.Model);
            w.WriteBoolean("stream", true);
            w.WriteStartObject("stream_options");
            w.WriteBoolean("include_usage", true);
            w.WriteEndObject();
            //when asked, llama-server streams prompt_progress during prefill
            if (returnProgress) w.WriteBoolean("return_progress", true);
            w.WriteStartArray("messages");
            foreach (var m in request.Messages)
            {
                //skip an assistant message with no content and no tool calls, llama.cpp 400s it on replay and the session stays wedged
                if (m.Role == "assistant" && m.Content is null && m.ToolCalls is not { Count: > 0 })
                    continue;
                //one message, one writer
                RequestJson.WriteMessage(w, m, images, onImageUnavailable);
            }
            w.WriteEndArray();
            if (request.Tools is { Count: > 0 })
            {
                w.WriteStartArray("tools");
                foreach (var t in request.Tools) RequestJson.WriteTool(w, t);
                w.WriteEndArray();
            }

            //shallow merge with body winning, harness keys (model, messages, stream, tools) never yield to an override
            var overrides = new Dictionary<string, JsonElement>();
            MergeOverrideProps(overrides, request.SamplingOverrides, nameof(request.SamplingOverrides));
            MergeOverrideProps(overrides, request.BodyOverrides, nameof(request.BodyOverrides));
            foreach (var (key, value) in overrides)
            {
                //an override of return_progress would write the key twice
                if (ReservedBodyKeys.Contains(key) || (returnProgress && key == "return_progress")) continue;
                w.WritePropertyName(key);
                value.WriteTo(w);
            }

            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static readonly HashSet<string> ReservedBodyKeys =
        new(StringComparer.Ordinal) { "model", "messages", "stream", "stream_options", "tools" };

    private static void MergeOverrideProps(Dictionary<string, JsonElement> merged, JsonElement? overrideElement, string paramName)
    {
        if (overrideElement is not { } el) return;
        if (el.ValueKind != JsonValueKind.Object)
            throw new ArgumentException($"ChatRequest.{paramName} must be a JSON object, got {el.ValueKind}", paramName);
        foreach (var prop in el.EnumerateObject())
            merged[prop.Name] = prop.Value;
    }
}
