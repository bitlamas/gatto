namespace Gatto.Core.Loop;

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

    public void Clear()
    {
        LastPromptTokens = null;
        LastMessageCount = null;
        Ratio = 1.0;
        ElidedThrough = 0;
    }
}
