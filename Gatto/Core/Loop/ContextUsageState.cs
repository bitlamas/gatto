using Gatto.Core.Client;

namespace Gatto.Core.Loop;

//the elision frontier and the token ratio a request went out with, kept on its reply's record so a resume shapes its first request the same way
public sealed record ShapeMark(int ElidedThrough, double Ratio);

//prompt-only tokens of the last round plus the estimate it went out with, cleared on compaction, /new and a model switch
public sealed class ContextUsageState
{
    public int? LastPromptTokens { get; private set; }
    public double Ratio { get; private set; } = 1.0;

    //how many messages that request held, so a caller can measure what was appended since
    public int? LastMessageCount { get; private set; }

    //every elidable tool result before this index went out trimmed, and the next request trims them again even if the ratio fell
    public int ElidedThrough { get; private set; }

    public void Record(int promptTokens, int estimateAtRequest, int messageCount)
    {
        LastPromptTokens = promptTokens;
        LastMessageCount = messageCount;
        Ratio = estimateAtRequest > 0 ? Math.Max(1.0, (double)promptTokens / estimateAtRequest) : 1.0;
    }

    //only ever moves forward, an earlier index would bring a trimmed result back and change the prefix the server holds
    public void NoteElided(int through) => ElidedThrough = Math.Max(ElidedThrough, through);

    //a resumed session's frontier and ratio, with no server reading, so the proactive trigger still waits for a fresh one
    public void Seed(int elidedThrough, double ratio)
    {
        ElidedThrough = Math.Max(0, elidedThrough);
        Ratio = Math.Max(1.0, ratio);
    }

    //the last reply decides, since a later reply sent uncut means the frontier was cleared since, and none at all leaves the defaults
    public void SeedFrom(IReadOnlyList<ChatMessage> messages)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].Role != "assistant") continue;
            if (messages[i].Shape is { } shape) Seed(shape.ElidedThrough, shape.Ratio);
            return;
        }
    }

    public void Clear()
    {
        LastPromptTokens = null;
        LastMessageCount = null;
        Ratio = 1.0;
        ElidedThrough = 0;
    }
}
