using Gatto.Core.Client;

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

    //the frontier the last reading was taken at. a request that cut and gave no reading leaves it behind, and its count would hold the cut results whole
    public int? ReadingFrontier { get; private set; }

    //what the conversation adds between two requests, read from the growth of both counts, so the fixed head's density never scales a tool result. no floor at 1, prose runs lighter than the estimate
    public double GrowthRatio { get; private set; } = 1.0;

    //the estimate the last reading went out with, the other half of a growth pair
    private int? lastEstimate;

    //a growth under this many estimated tokens teaches nothing, the template's markup around each message would swing the ratio
    internal const int MinGrowthEstimate = 512;

    //one odd pair is held to this range, so a single reading cannot make the count absurd. the low edge decides how cold the count can run
    internal const double MinGrowthRatio = 0.75;
    internal const double MaxGrowthRatio = 3.0;

    //comparable is the caller's word that this request finished and added only conversation text since the last reading
    public void Record(int promptTokens, int estimateAtRequest, int messageCount, bool comparable = false)
    {
        if (comparable && LastPromptTokens is int previousPrompt && lastEstimate is int previousEstimate && LastMessageCount is int previousCount
            && messageCount > previousCount && estimateAtRequest - previousEstimate >= MinGrowthEstimate && promptTokens > previousPrompt)
            GrowthRatio = Math.Clamp((double)(promptTokens - previousPrompt) / (estimateAtRequest - previousEstimate), MinGrowthRatio, MaxGrowthRatio);
        LastPromptTokens = promptTokens;
        LastMessageCount = messageCount;
        ReadingFrontier = ElidedThrough;
        lastEstimate = estimateAtRequest;
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
        ReadingFrontier = null;
        lastEstimate = null;
        Ratio = 1.0;
        GrowthRatio = 1.0;
        ElidedThrough = 0;
    }
}
