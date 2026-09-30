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
}

//the shape ReplaceAll expects, SystemText as the only system message at index 0 and Messages as the rest
public sealed record CompactionResult(string SystemText, IReadOnlyList<ChatMessage> Messages, SessionBaseline? Baseline);
