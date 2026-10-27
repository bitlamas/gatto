using Gatto.Core.Client;

namespace Gatto.Core.Loop;

//why compaction runs, Proactive before a round starts and Overflow after the server rejected the request as too large
public enum CompactionReason { Proactive, Overflow }

//implemented outside Core so this project holds no summarization or session code, and a null return must leave the caller's state untouched
public interface ICompactionHandler
{
    //null if compaction itself failed, the caller must surface the original error rather than continue on stale state
    Task<CompactionResult?> CompactAsync(
        CompactionReason reason, string currentUserPrompt, ITurnObserver observer, CancellationToken ct);

    //the same compaction under the turn span that triggered it, which a tracing handler hands its compactor and every other handler ignores
    Task<CompactionResult?> CompactAsync(
        CompactionReason reason, string currentUserPrompt, ITurnObserver observer, long traceParent, CancellationToken ct) =>
        CompactAsync(reason, currentUserPrompt, observer, ct);

    //the same compaction told the whole turn so far, which a rebuilding handler continues from and every other handler reads as its prompt alone
    Task<CompactionResult?> CompactAsync(
        CompactionReason reason, TurnSoFar turn, ITurnObserver observer, long traceParent, CancellationToken ct) =>
        CompactAsync(reason, turn.Prompt, observer, traceParent, ct);

    //why the last null came back in words the user can read, or null when the handler cannot tell
    string? LastFailure => null;
}

//the shape ReplaceAll expects, SystemText as the only system message at index 0 and Messages as the rest
public sealed record CompactionResult(string SystemText, IReadOnlyList<ChatMessage> Messages, SessionBaseline? Baseline);

//what a mid-turn rebuild continues: the turn's own prompt and images, and each message delivered inside the turn, verbatim and in order
public sealed record TurnSoFar(string Prompt, IReadOnlyList<string> Delivered, IReadOnlyList<ImageRef>? Images);
