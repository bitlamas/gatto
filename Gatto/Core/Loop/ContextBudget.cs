using Gatto.Core.Client;

namespace Gatto.Core.Loop;

//the All policy resends every reasoning block and None strips them, both decide the same way on every turn so the prefix cache holds
public enum ReasoningHistory { All, None }

//shape a copy of the request, the conversation and the session file keep the full tool results
public static class ContextBudget
{
    //the one place the window fractions live, elide at or above 90% and still over is past 100%
    private const double ElideAtFraction = 0.90;

    //same chars/4 heuristic as MemoryIndex.CharsPerToken but a separate constant, the two must be retuned together
    private const int CharsPerToken = 4;

    //the newest three rounds are safe from elision. this window is for elision only, a per-turn use would slide the prefix every turn
    public const int RecentRounds = 3;

    //a result below this many chars isn't worth eliding, each elision invalidates the KV prefix from there on
    private const int MinElidableChars = 200;

    //everything before this index is old enough to elide, and it never moves backwards, a retreat would un-elide a result and re-prefill
    public static int ElideBefore(IReadOnlyList<int> roundStarts, int turnUserIndex) =>
        roundStarts.Count > RecentRounds ? roundStarts[^RecentRounds] : turnUserIndex;

    //counts what the given messages hold, callers that want the size of the real request must estimate the shaped copy
    public static int Estimate(IEnumerable<ChatMessage> messages)
    {
        long chars = 0;
        foreach (var m in messages)
        {
            chars += m.Content?.Length ?? 0;
            chars += m.ReasoningContent?.Length ?? 0;
            if (m.ToolCalls is { } calls)
                foreach (var c in calls)
                    chars += c.ArgumentsJson?.Length ?? 0;
        }
        return (int)(chars / CharsPerToken);
    }

    //the json each tool definition adds around its name, description and schema, an approximation of the request body
    internal const int ToolEnvelopeChars = 60;

    //the tool definitions every request carries, by the same chars/4 rule. left out, the measured ratio reads this fixed cost as message density
    public static int EstimateTools(IReadOnlyList<ToolSpec> tools)
    {
        long chars = 0;
        foreach (var t in tools)
            chars += t.Name.Length + t.Description.Length + t.ParametersSchema.GetRawText().Length + ToolEnvelopeChars;
        return (int)(chars / CharsPerToken);
    }

    //estimated tokens added since the last server reading, scaled by the ratio, and 0 when there is no reading yet
    public static int GrowthSinceLastRequest(Conversation convo, ContextUsageState usage) =>
        usage.LastMessageCount is int from && from < convo.Messages.Count
            ? (int)(Estimate(convo.Messages.Skip(from)) * usage.Ratio)
            : 0;

    //one source for the context figure, server truth plus what has been appended since, and never strip the growth term
    public static int? UsedTokens(Conversation convo, ContextUsageState? usage) =>
        usage?.LastPromptTokens is int lastPrompt
            ? lastPrompt + GrowthSinceLastRequest(convo, usage)
            : null;

    //the request copy only, a record with an update goes out as the update, a blank line, then the typed text
    public static IReadOnlyList<ChatMessage> ShapeUpdates(IReadOnlyList<ChatMessage> messages)
    {
        ChatMessage[]? working = null;
        for (var i = 0; i < messages.Count; i++)
        {
            if (messages[i].Update is not { } u) continue;
            working ??= messages.ToArray();
            working[i] = working[i] with { Content = u.Text + "\n\n" + (working[i].Content ?? "") };
        }
        return working ?? messages;
    }

    //the request copy only, and no policy may decide a message from what comes after it, that slides the boundary and re-prefills
    public static IReadOnlyList<ChatMessage> ShapeReasoning(
        IReadOnlyList<ChatMessage> messages, ReasoningHistory policy)
    {
        if (policy == ReasoningHistory.All) return messages;

        ChatMessage[]? working = null;
        for (var i = 0; i < messages.Count; i++)
        {
            if (messages[i].ReasoningContent is not { Length: > 0 }) continue;
            working ??= messages.ToArray();
            working[i] = working[i] with { ReasoningContent = null };
        }
        return working ?? messages;
    }

    //elides the oldest results past 90% by the server's ratio, never from protectFromIndex, always up to elidedThrough
    public static (IReadOnlyList<ChatMessage> Messages, int ElidedCount, bool StillOver, int ElidedThrough) Apply(
        IReadOnlyList<ChatMessage> messages, int? budgetTokens, int protectFromIndex, double ratio = 1.0, int elidedThrough = 0,
        int overheadTokens = 0)
    {
        if (budgetTokens is not int budget || budget <= 0)
            return (messages, 0, false, elidedThrough);

        double Counted(IReadOnlyList<ChatMessage> m) => Counts(m, overheadTokens, ratio);
        var elideAt = budget * ElideAtFraction;
        if (elidedThrough <= 0 && Counted(messages) < elideAt)
            return (messages, 0, false, 0);   //under the threshold with nothing trimmed before, so nothing is elided and StillOver is false

        //a fresh array, records are immutable so the caller's list is never mutated, this stays the request copy only
        var working = messages.ToArray();
        var elided = 0;
        var through = elidedThrough;
        var limit = Math.Min(protectFromIndex, working.Length);

        for (var i = 0; i < limit && (i < elidedThrough || Counted(working) >= elideAt); i++)
        {
            if (working[i].Role != "tool") continue;
            var originalChars = working[i].Content?.Length ?? 0;
            var stub = Cut(working[i].Content ?? "", ToolNameFor(working, i) + " result", " — re-run if needed");
            //skip a result that isn't longer than its stub or below MinElidableChars, eliding either would grow the request or rewrite the prefix for nothing
            if (originalChars <= stub.Length || originalChars < MinElidableChars) continue;
            working[i] = working[i] with { Content = stub };
            elided++;
            through = Math.Max(through, i + 1);
        }

        var stillOver = Counted(working) > budget;   //compared with the full budget, above the 90% elision threshold
        return (elided == 0 ? messages : working, elided, stillOver, through);
    }

    //what a request of these messages costs the server, the fixed tool definitions included, in the server's tokens
    public static double Counts(IReadOnlyList<ChatMessage> messages, int overheadTokens, double ratio) =>
        (Estimate(messages) + overheadTokens) * ratio;

    //a result this long keeps its head when cut, a shorter one becomes a bare stub since its head would cost about what the cut saves
    internal const int HeadKeepChars = 300;
    private const int HeadFromChars = 4 * HeadKeepChars;

    //the stub for a cut result, with its head above the marker when the result is long, so the first line of a file is never lost
    internal static string Cut(string content, string what, string after)
    {
        if (content.Length < HeadFromChars) return $"[elided: {what}, {content.Length} chars{after}]";
        var head = Head(content);
        return head + "\n" + $"[elided: {what}, {content.Length} chars, the first {head.Length} shown above{after}]";
    }

    //the first HeadKeepChars of a result, ending on a whole line when one fits
    private static string Head(string content)
    {
        var nl = content.LastIndexOf('\n', HeadKeepChars - 1);
        return nl > 0 ? content[..nl].TrimEnd('\r') : content[..Tools.ToolArgs.SafeCut(content, HeadKeepChars)];
    }

    //looks back for the assistant call with this id, and falls back to the literal tool
    private static string ToolNameFor(IReadOnlyList<ChatMessage> messages, int toolIndex)
    {
        var id = messages[toolIndex].ToolCallId;
        if (id is null) return "tool";
        for (var j = toolIndex - 1; j >= 0; j--)
        {
            if (messages[j].ToolCalls is not { } calls) continue;
            foreach (var c in calls)
                if (c.Id == id) return c.Name;
        }
        return "tool";
    }
}
