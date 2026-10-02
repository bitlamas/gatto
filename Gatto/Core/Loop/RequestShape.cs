using System.Text.Json;
using Gatto.Core.Client;

namespace Gatto.Core.Loop;

//what the loop's every request carries besides its messages, which a compaction reuses so its request starts as the loop's did and the server's cache holds
public sealed record RequestShape(IReadOnlyList<ToolSpec> Tools, JsonElement? SamplingOverrides, JsonElement? BodyOverrides,
    ReasoningHistory ReasoningHistory,
    IReadOnlyList<ChatMessage>? LastSent = null,   //the messages of the loop's last request as sent, its suffix and elision included
    IReadOnlyList<ChatMessage>? SentFrom = null);  //the conversation's messages at that send, so a reader can tell the conversation since is only appended
