namespace Gatto.Core.Loop;

//prompt-only tokens of the last round plus the estimate it went out with, cleared on compaction, /new and a model switch
public sealed class ContextUsageState
{
    public int? LastPromptTokens { get; private set; }
    public double Ratio { get; private set; } = 1.0;

    //how many messages that request held, so a caller can measure what was appended since
    public int? LastMessageCount { get; private set; }

    public void Record(int promptTokens, int estimateAtRequest, int messageCount)
    {
        LastPromptTokens = promptTokens;
        LastMessageCount = messageCount;
        Ratio = estimateAtRequest > 0 ? Math.Max(1.0, (double)promptTokens / estimateAtRequest) : 1.0;
    }

    public void Clear()
    {
        LastPromptTokens = null;
        LastMessageCount = null;
        Ratio = 1.0;
    }
}
