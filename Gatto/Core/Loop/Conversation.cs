using Gatto.Core.Client;
using Gatto.Core.Tools;

namespace Gatto.Core.Loop;

public sealed class Conversation
{
    private readonly List<ChatMessage> _messages = new();

    //stamp a message's Ts when it is created, a write-time stamp would re-date every old record with the latest save clock
    private readonly Func<DateTime> _now;

    public Conversation(string? systemPrompt, Func<DateTime>? now = null, SessionBaseline? baseline = null)
    {
        _now = now ?? (() => DateTime.UtcNow);
        if (systemPrompt is { Length: > 0 })
            _messages.Add(new ChatMessage("system", systemPrompt, Ts: _now()));
        Baseline = baseline;
    }

    public IReadOnlyList<ChatMessage> Messages => _messages;
    public int Count => _messages.Count;

    //what the system message was composed from, set only with the text so the two cannot part
    public SessionBaseline? Baseline { get; private set; }

    //an effort change rewrites the thinking in place, the text is unchanged so the sequence stays
    public void SetThinking(ThinkingMark thinking)
    {
        if (Baseline is { } b) Baseline = b with { Thinking = thinking };
    }

    //the role stamped on assistant and tool messages from here on, so a replay shows each reply in its own tint
    public string? GattoRole { get; private set; }
    public void SetGattoRole(string? role) => GattoRole = role;

    //the note a resume carries, attached to the next user message and held again when that message is dropped before a reply
    public SessionUpdate? PendingUpdate { get; private set; }
    private ChatMessage? _updateCarrier;

    public void SetPendingUpdate(SessionUpdate? update) { PendingUpdate = update; _updateCarrier = null; }

    //images are references to files named in the message, and null on nearly every turn so the serialised form stays unchanged
    public void AddUser(string text, IReadOnlyList<ImageRef>? images = null)
    {
        var m = new ChatMessage("user", text, Ts: _now(), Images: images, Update: PendingUpdate);
        if (PendingUpdate is not null) { _updateCarrier = m; PendingUpdate = null; }
        _messages.Add(m);
    }
    //stripped says how many control tokens already came out of text, callers pass pre-stripped text and the strip stays at the loop seam
    public void AddAssistant(string text, IReadOnlyList<ToolCall>? toolCalls = null,
        Usage? usage = null, string? timings = null, string? reasoningContent = null, int stripped = 0) =>
        _messages.Add(new ChatMessage("assistant", text.Length > 0 ? text : null, toolCalls,
            GattoRole: GattoRole, Ts: _now(), Usage: usage, Timings: timings,
            ReasoningContent: string.IsNullOrEmpty(reasoningContent) ? null : reasoningContent,
            Stripped: stripped));
    public void AddToolResult(string toolCallId, ToolResult result) =>
        _messages.Add(new ChatMessage("tool", result.Text, ToolCallId: toolCallId,
            GattoRole: GattoRole, IsError: result.IsError, Gloss: result.Gloss, Ts: _now(), View: result.View));

    //each message keeps the Ts it was parsed with, so resumed history holds its original times
    public void Load(IEnumerable<ChatMessage> messages) => _messages.AddRange(messages);

    //re-seed the composed prompt, replacing the system message at index 0 or inserting one there
    public void ReplaceSystem(string text, SessionBaseline? baseline)
    {
        if (_messages.Count > 0 && _messages[0].Role == "system")
            _messages[0] = new ChatMessage("system", text, Ts: _now());
        else
            _messages.Insert(0, new ChatMessage("system", text, Ts: _now()));
        Baseline = baseline;
        SetPendingUpdate(null);
    }

    //roll back to a mark, callers that keep only index 0 rely on the system message always sitting there
    public void TruncateTo(int count)
    {
        if (count < 0 || count >= _messages.Count) return;
        //only the record this conversation attached counts, a record loaded from the file is never it, so a second resume cannot repeat an old note
        if (_updateCarrier is { } carrier)
            for (var i = count; i < _messages.Count; i++)
                if (ReferenceEquals(_messages[i], carrier)) { PendingUpdate = carrier.Update; _updateCarrier = null; break; }
        _messages.RemoveRange(count, _messages.Count - count);
    }

    //swap the whole transcript in place, a running loop holds this object so a fresh Conversation would be invisible
    public void ReplaceAll(string systemText, IReadOnlyList<ChatMessage> messages, SessionBaseline? baseline)
    {
        Baseline = baseline;
        SetPendingUpdate(null);
        _messages.Clear();
        _messages.Add(new ChatMessage("system", systemText, Ts: _now()));
        //handler-built messages arrive bare, so stamp the ones without a Ts and leave an existing Ts alone
        foreach (var m in messages)
            _messages.Add(m.Ts is null ? m with { Ts = _now() } : m);
    }
}
