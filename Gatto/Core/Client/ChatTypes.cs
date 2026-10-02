using System.Text.Json;

namespace Gatto.Core.Client;

public sealed record ToolCall(string Id, string Name, string ArgumentsJson);
public sealed record Usage(int PromptTokens, int CompletionTokens);
public sealed record ToolSpec(string Name, string Description, JsonElement ParametersSchema);
public sealed record ChatMessage(string Role, string? Content,
    IReadOnlyList<ToolCall>? ToolCalls = null, string? ToolCallId = null,
    string? GattoRole = null, bool IsError = false, string? Gloss = null,
    DateTime? Ts = null,
    Usage? Usage = null, string? Timings = null,
    //this turn's reasoning, kept and resent in history so a model that conditions on prior thinking keeps reasoning
    string? ReasoningContent = null,
    //control tokens Strip removed from this message, evidence only, written above zero and added to across resumes
    int Stripped = 0,
    //images the user attached, held as references, null when there are none so content stays a plain string
    IReadOnlyList<ImageRef>? Images = null,
    //where an edit landed in its file, display only, written to the session and never into a request
    Gatto.Core.Tools.EditView? View = null,
    //the note a resume carried on this message, sent ahead of the typed text and never shown
    Gatto.Core.Loop.SessionUpdate? Update = null);
public sealed record ChatRequest(string Model, IReadOnlyList<ChatMessage> Messages,
    IReadOnlyList<ToolSpec>? Tools = null,
    JsonElement? SamplingOverrides = null, JsonElement? BodyOverrides = null,
    //none keeps the tools listed, so the prompt's prefix holds, while the model may not call one
    string? ToolChoice = null);

public abstract record StreamEvent
{
    public sealed record TextDelta(string Text) : StreamEvent;
    public sealed record ReasoningDelta(string Text) : StreamEvent;
    //prefill progress from llama-server when the request asks for return_progress, it arrives before any delta
    public sealed record PromptProgress(long Total, long Processed) : StreamEvent;
    //a tool call being assembled, the partial arguments are raw mid-stream JSON and must be treated as unparseable text
    public sealed record ToolCallDelta(string? Name = null, string? PartialArguments = null) : StreamEvent;
    public sealed record ToolCallReady(ToolCall Call) : StreamEvent;
    //the Timings argument defaults to null so existing 2-arg call sites stay source-compatible
    public sealed record Finished(string? FinishReason, Usage? Usage, string? Timings = null) : StreamEvent;
    public sealed record Truncated(bool DroppedInFlightToolCall) : StreamEvent;
}
