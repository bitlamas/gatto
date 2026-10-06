using System.Text;
using System.Text.Json;
using Gatto.Core.Client;
using Gatto.Core.Home;
using Gatto.Core.Memory;
using System.Linq;

namespace Gatto.Core.Loop;

//session compaction, by /compact or automatic: a failed summary returns null, so the caller aborts and the session stays untouched. the loop's shape keeps the summary request on the server's cache
public sealed class Compactor(IChatClient client, string model, RequestShape? shape = null)
{
    //the summarizer prompt is compiled in, so a role or user text can't steer the summarizer off task
    private const string TemplatePrompt =
        "Summarize this session so it can be continued in a fresh context with nothing important " +
        "lost. Write plain prose under these headings, in this order:\n\n" +
        "Task / goal: what the user is trying to accomplish.\n" +
        "Findings & facts learned: concrete things discovered — file contents, API/function " +
        "signatures, config values, repo/module structure, article claims, command output, exact " +
        "error text. Preserve the specifics themselves, not a description that they exist. " +
        "(Central for research/exploration turns; may be brief for pure execution.)\n" +
        "Decisions & actions taken: choices made and why; commands run, edits applied, services " +
        "touched. (Central for execution turns.)\n" +
        "Files / systems touched: paths created or modified, hosts/services acted on.\n" +
        "What worked / what failed: outcomes, errors, dead ends.\n" +
        //keep this section in the template, nothing else tells the fresh context about quirks found mid-session
        "Environment gotchas learned: shell/tool quirks discovered this session, stated exactly " +
        "(e.g. 'PowerShell 5.1 has no &&').\n" +
        "Current state: where things stand right now.\n" +
        NextStepHeading + ": the single most useful thing to do next.\n" +
        //keep this section last, the extractor takes everything after its heading. one template and one extractor, so it is emitted even with memory off
        MemoryPiggyback.Heading + ": durable project facts learned this session that the next session would " +
        "otherwise rediscover. Exactly one \"- \" bullet per fact, no commentary, or exactly the " +
        "word none. This must be the last section.\n\n" +
        "Weight the headings to the work: an exploration/research turn must preserve findings in " +
        "specific detail; an execution turn should emphasize decisions, actions, and state. Fill " +
        "what applies; don't pad the rest. Be concrete and specific — a reader with only this " +
        "summary must be able to continue without re-reading anything. Do not call tools. Keep " +
        "the whole summary under 1200 words (shorter when the work is simple).";

    //the model infers the in-flight task from the turn's prompt, calls and results, the harness writes none
    private const string MidTurnAddendum =
        "\nIn-flight task: the session was interrupted mid-task. From the latest user request, " +
        "tool calls, and results, state what the task is, what has been done so far, and what " +
        "remains. Be precise about the immediate next action.";

    //the do-not-repeat list covers the candidates section only, don't edit this wording without a new gate
    internal const string SuppressionPreamble =
        "The following facts are already saved in project memory — do not list them, or " +
        "restatements of them, under " + MemoryPiggyback.Heading + ":";

    //one request's addendum, or null when the index text is empty (an empty do-not-repeat list says nothing)
    public static string? SuppressionAddendum(MemoryIndex.LoadResult index) =>
        string.IsNullOrWhiteSpace(index.Text) ? null : "\n\n" + SuppressionPreamble + "\n" + index.Text;

    internal static int TemplatePromptLengthForTests => TemplatePrompt.Length;
    internal static string TemplatePromptForTests => TemplatePrompt;

    //why the last SummarizeAsync returned null, in words a warning line can carry, and null after a summary
    public string? LastFailure { get; private set; }

    private const string OverflowFailure = "the summary request did not fit the context window";

    //the continuation directive names this heading too
    internal const string NextStepHeading = "Immediate next step";

    //a reply holding none of the template's headings is not a summary, the template lets a summary skip any one of them
    internal static readonly string[] Headings =
    {
        "Task / goal", "Findings & facts learned", "Decisions & actions taken", "Files / systems touched",
        "What worked / what failed", "Environment gotchas learned", "Current state", NextStepHeading, MemoryPiggyback.Heading,
    };

    //the summary from the whole conversation or a shortened copy, null means the caller aborts and leaves the session untouched
    public async Task<string?> SummarizeAsync(
        Conversation convo, ITurnObserver observer, CancellationToken ct,
        int? windowTokens = null, double ratio = 1.0, bool midTurn = false, string? doNotRepeat = null)
    {
        //one prompt local, so the measured length covers the primary attempt and the harsh retry alike
        var prompt = (midTurn ? TemplatePrompt + MidTurnAddendum : TemplatePrompt) + (doNotRepeat ?? "");
        LastFailure = null;

        var toolTokens = (int)(ContextBudget.EstimateTools(shape?.Tools ?? []) * ratio);   //the whole attempt carries the tool definitions, the shortened copy goes without them
        //the loop's last request as sent plus what came since is the prefix the server holds, so it goes whole while the summary has room beside it
        if (Resent(convo.Messages) is { } whole
            && (windowTokens is not int fit || fit <= 0 || Measure(whole, ratio, prompt.Length) + toolTokens + SummaryRoomTokens <= fit))
        {
            var (wholeSummary, wholeOverflowed, wholeTruncated, wholeRejected) = await TryStreamAsync(new List<ChatMessage>(whole) { new("user", prompt) }, observer, ct, withTools: true);
            var retry = wholeRejected || (windowTokens is int && (wholeOverflowed || wholeTruncated));
            if (wholeSummary is not null || !retry || ct.IsCancellationRequested)
            {
                if (wholeOverflowed) LastFailure = OverflowFailure;
                return wholeSummary;
            }
            observer.OnWarning(wholeRejected
                ? "the reply beside the whole conversation was not a summary — summarizing a shortened copy"
                : "the summary did not fit beside the whole conversation — summarizing a shortened copy");
            LastFailure = null;
        }

        var shaped = windowTokens is int w and > 0
            ? ShapeForSummary(convo.Messages, w, ratio, promptChars: prompt.Length)
            : convo.Messages;

        var (summary, overflowed, _, _) = await TryStreamAsync(Compose(shaped, prompt), observer, ct, withTools: false);
        if (!overflowed) return summary;

        //the summarize request itself overflowed, one harsher retry and then abort
        if (windowTokens is not int w2 || w2 <= 0) { LastFailure = OverflowFailure; return null; }
        var (harshSummary, harshOverflowed, _, _) = await TryStreamAsync(
            Compose(HarshShape(convo.Messages, w2, ratio, promptChars: prompt.Length), prompt), observer, ct, withTools: false);
        if (harshOverflowed) LastFailure = OverflowFailure;
        return harshOverflowed ? null : harshSummary;
    }

    //the room the summary needs beside the whole conversation, its reasoning included. a summary cut short there is asked again from the shortened copy
    internal const int SummaryRoomTokens = 4096;

    //the loop's last request as sent and the conversation appended since, shaped as the loop will shape it, or null when the conversation changed under it
    private List<ChatMessage>? Resent(IReadOnlyList<ChatMessage> messages)
    {
        if (shape is not { LastSent: { } sent, SentFrom: { } from } || messages.Count < from.Count) return null;
        for (var i = 0; i < from.Count; i++)
            if (!ReferenceEquals(messages[i], from[i])) return null;
        var since = messages.Skip(from.Count).ToList();
        return new List<ChatMessage>(sent).Concat(ContextBudget.ShapeReasoning(ContextBudget.ShapeUpdates(since), shape.ReasoningHistory)).ToList();
    }

    //the summarizer reads what the model was told, an update included, shaped as the loop shapes its own, then the summary prompt as the one new tail
    private List<ChatMessage> Compose(IReadOnlyList<ChatMessage> shaped, string prompt) =>
        new(ContextBudget.ShapeReasoning(ContextBudget.ShapeUpdates(shaped), shape?.ReasoningHistory ?? ReasoningHistory.All)) { new("user", prompt) };

    //keys that limit or force the output, which never touch the prompt, so dropping them costs no cache and lets the summary run to its end
    private static readonly string[] OutputLimitKeys = { "max_tokens", "max_completion_tokens", "n_predict", "grammar", "json_schema", "response_format" };

    private static JsonElement? WithoutOutputLimits(JsonElement? overrides)
    {
        if (overrides is not { ValueKind: JsonValueKind.Object } o || !o.EnumerateObject().Any(p => OutputLimitKeys.Contains(p.Name)))
            return overrides;
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            foreach (var p in o.EnumerateObject())
                if (!OutputLimitKeys.Contains(p.Name)) p.WriteTo(w);
            w.WriteEndObject();
        }
        return JsonDocument.Parse(ms.ToArray()).RootElement.Clone();
    }

    //an overflow, a cut summary or a reply that is no summary is reported so the caller can retry on another request, every other failure returns null with no retry
    private async Task<(string? Summary, bool Overflowed, bool Truncated, bool Rejected)> TryStreamAsync(
        List<ChatMessage> messages, ITurnObserver observer, CancellationToken ct, bool withTools)
    {
        var text = new StringBuilder();
        var truncated = false;
        try
        {
            //only the request beside the whole conversation lists the tools, with tool_choice none, so its prefix matches the cached one
            var request = shape is { } s
                ? new ChatRequest(model, messages, withTools ? s.Tools : null, WithoutOutputLimits(s.SamplingOverrides), WithoutOutputLimits(s.BodyOverrides), ToolChoice: withTools && s.Tools.Count > 0 ? "none" : null)
                : new ChatRequest(model, messages, Tools: null);
            await foreach (var ev in client.StreamAsync(request, ct))
            {
                switch (ev)
                {
                    //only the content is kept as the summary, thinking goes to the reasoning channel so the REPL dims it
                    case StreamEvent.TextDelta t: text.Append(t.Text); observer.OnReasoningDelta(t.Text); break;
                    case StreamEvent.ReasoningDelta r: observer.OnReasoningDelta(r.Text); break;
                    case StreamEvent.Truncated: truncated = true; break;
                    case StreamEvent.Finished f: if (f.FinishReason == "length") truncated = true; break;
                }
            }
        }
        //must precede the generic catch, GattoContextOverflowException subtypes GattoConnectionException so the catch-all would swallow it and the harsher retry never runs
        catch (GattoContextOverflowException) { return (null, true, false, false); }
        catch (OperationCanceledException) { LastFailure = "stopped"; return (null, false, false, false); }  //on Ctrl+C mid-summary, abort with nothing lost
        catch (GattoConnectionException ex)                                                     //server dropped, abort with nothing lost
        {
            LastFailure = $"the server dropped the request: {ex.Message}";
            return (null, false, false, false);
        }

        if (truncated) { LastFailure = "the summary was cut at the output limit"; return (null, false, true, false); }
        //strip control tokens here, this text becomes the system prefix, replayed in full on every later request
        var (cleaned, stripped) = ControlTokens.Strip(text.ToString());
        if (stripped > 0)
            observer.OnWarning($"control tokens stripped from the compaction summary ({stripped})");
        var summary = cleaned.Trim();
        //a summary stripped to empty is not a summary, so it takes the same null abort as any other failure
        if (summary.Length == 0) { LastFailure = "the summary came back empty"; return (null, false, false, false); }
        //a model can answer with a tool call written as text, which holds no heading
        if (!Headings.Any(h => summary.Contains(h, StringComparison.OrdinalIgnoreCase)))
        {
            LastFailure = "the reply holds none of the summary's headings, so it is not a summary";
            return (null, false, false, true);
        }
        return (summary, false, false, false);
    }

    //the labeled block a fresh session leads with, composed into the system text rather than added as messages

    //opens a BuildContext block, --continue greps the prior system for this marker so a compacted session keeps its summary on resume
    public const string ContextMarker = "## Previous session summary (compacted from ";

    //closes the rebuilt user message, it names the summary's Immediate next step heading so one authority states what to do next
    public const string ContinuationDirective =
        "[Session was compacted mid-task. The summary in the system context is authoritative: facts, " +
        "file contents, and findings recorded there are already established — do not re-read files or " +
        "re-explore to reconfirm them. Continue directly with the summary's \"" + NextStepHeading + "\".]";

    //keep the prompt verbatim (the summary can be wrong) and put the label before it and the directive after
    public static string BuildContinuationPrompt(string originalPrompt) =>
        "Original request (context — already in progress; do NOT start it over):\n"
        + "\"\"\"\n" + originalPrompt + "\n\"\"\"\n\n"
        + ContinuationDirective;

    public static string BuildContext(string summary, string oldSessionFile, (string User, string Assistant)? lastExchange)
    {
        var sb = new StringBuilder();
        sb.Append(ContextMarker).Append(oldSessionFile).Append(")\n\n");
        sb.Append(summary);
        if (lastExchange is { } ex)
        {
            sb.Append("\n\n## Last exchange (verbatim)\n\n");
            sb.Append("User: ").Append(ex.User).Append("\n\n");
            sb.Append("Assistant: ").Append(ex.Assistant);
        }
        return sb.ToString();
    }

    //one rebuild for a session and -p, since --continue slices from its ContextMarker. the summary has succeeded
    public static (CompactionResult Result, string Summary) RebuildMidTurn(
        string summary, Conversation convo, string currentUserPrompt, ITurnObserver observer,
        SessionStore? sessions, Func<ComposedSystem> recompose, string dot)
    {
        //strip the memory section here, a mid-turn compaction never banks and its scaffolding must stay out of the rebuilt prefix
        summary = MemoryPiggyback.Extract(summary).StrippedSummary;
        var oldPath = sessions?.CurrentPath;                 //read before StartSuccessor, which clears the path
        sessions?.StartSuccessor();                          //later saves open a new file of the same session, so the old transcript is left alone
        //the compact block must stay the final append, --continue slices from the last ContextMarker to the end when it grafts a session
        var composed = recompose();                          //a fresh read of the context files on disk, so the prompt matches what is there now
        var systemText = composed.Text + "\n\n" + BuildContext(summary, oldPath ?? "(unsaved session)", lastExchange: null);
        //the continuation keeps this turn's images, they are the material of the task being continued rather than history that becomes text
        var carried = convo.Messages.LastOrDefault(m => m.Role == "user")?.Images;
        //the dropped count is the total minus what the continuation kept, so the line reports what actually happened
        var totalImages = convo.Messages.Sum(m => m.Images?.Count ?? 0);
        var carriedCount = carried?.Count ?? 0;
        var droppedImages = totalImages - carriedCount;
        if (droppedImages > 0 || carriedCount > 0)
            observer.OnWarning(string.Join($" {dot} ", new[]
            {
                droppedImages > 0 ? $"{droppedImages} image{(droppedImages == 1 ? "" : "s")} dropped" : null,
                carriedCount > 0 ? $"{carriedCount} carried forward" : null,
            }.Where(x => x is not null)));
        var messages = new[] { new ChatMessage("user", BuildContinuationPrompt(currentUserPrompt), Images: carried) };
        //no session_summary hook fires for a mid-turn compaction, the user did not choose this boundary
        return (new CompactionResult(systemText, messages, composed.Baseline), summary);
    }

    internal const double FitsFraction = 0.85;   //at or below this fraction the shape is a no-op, so the prefix cache survives
    internal const double TargetFraction = 0.50; //half the window, the measure is rough with no local tokenizer
    internal const double HarshFraction = 0.25;  //the budget of the single retry after a summarize overflow

    //promptChars has no default on purpose, a missed call site would compile and measure against the template alone
    private static int Measure(IReadOnlyList<ChatMessage> msgs, double ratio, int promptChars) =>
        (int)((ContextBudget.Estimate(msgs) + promptChars / 4) * ratio);

    //string arg values up to this many chars survive verbatim. longer ones are stubbed so a file body doesn't cost the whole turn
    private const int ArgValueKeepChars = 200;

    //keys and short values stay, long ones become a stub, and it only shapes the summarize request
    private static string ShrinkArgs(string argsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return HeadTruncate(argsJson);
            var sb = new StringBuilder("{");
            var first = true;
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append(JsonSerializer.Serialize(p.Name)).Append(':');
                if (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { } s && s.Length > ArgValueKeepChars)
                    sb.Append(JsonSerializer.Serialize($"[elided: {s.Length} chars]"));
                else
                    sb.Append(p.Value.GetRawText());   //short strings and all non-strings go in as they are
            }
            sb.Append('}');
            var shrunk = sb.ToString();
            return shrunk.Length < argsJson.Length ? shrunk : argsJson;
        }
        catch (JsonException) { return HeadTruncate(argsJson); }
    }

    private static string HeadTruncate(string argsJson)
    {
        if (argsJson.Length <= ArgValueKeepChars + 40) return argsJson;
        var cut = ArgValueKeepChars;
        if (char.IsHighSurrogate(argsJson[cut - 1])) cut--;   //don't split a surrogate pair at the cut
        return argsJson[..cut] + $"…[elided {argsJson.Length - cut} chars]";
    }

    //same message instance when nothing changed, SequenceEqual catches a rewrite that is the same length
    private static ChatMessage ShrinkMessageArgs(ChatMessage m)
    {
        if (m.ToolCalls is not { } calls) return m;
        var shrunk = calls.Select(c => c.ArgumentsJson.Length > ArgValueKeepChars
            ? c with { ArgumentsJson = ShrinkArgs(c.ArgumentsJson) } : c).ToList();
        return shrunk.SequenceEqual(calls) ? m : m with { ToolCalls = shrunk };
    }

    //returns the input untouched when it fits (keeps the prefix cache), otherwise elides results, stubs long args and drops older messages
    public static IReadOnlyList<ChatMessage> ShapeForSummary(
        IReadOnlyList<ChatMessage> messages, int windowTokens, double ratio, int? promptChars = null)
    {
        var promptLen = promptChars ?? TemplatePrompt.Length;
        if (windowTokens <= 0 || Measure(messages, ratio, promptLen) <= windowTokens * FitsFraction)
            return messages;

        var target = windowTokens * TargetFraction;
        var working = new List<ChatMessage>(messages);

        //pass 1: elide every tool result, oldest first, keeping its head so the summary can still name what a file began with
        for (var i = 0; i < working.Count && Measure(working, ratio, promptLen) > target; i++)
        {
            if (working[i].Role != "tool") continue;
            var content = working[i].Content ?? "";
            var stub = ContextBudget.Cut(content, "tool result", "");
            if (content.Length > stub.Length) working[i] = working[i] with { Content = stub };
        }

        //pass 1.5: shrink oversized tool-call arguments oldest first so a file body doesn't cost the whole turn
        for (var i = 0; i < working.Count && Measure(working, ratio, promptLen) > target; i++)
            working[i] = ShrinkMessageArgs(working[i]);

        //pass 2: drop oldest non-system messages whole, an assistant with tool_calls goes with its results, index 0 stays the system message
        while (working.Count > 2 && Measure(working, ratio, promptLen) > target)
        {
            var idx = working[0].Role == "system" ? 1 : 0;
            if (working[idx].ToolCalls is { } dropped)
            {
                var ids = dropped.Select(c => c.Id).ToHashSet();
                working.RemoveAt(idx);
                while (idx < working.Count && working[idx].Role == "tool"
                       && working[idx].ToolCallId is { } id && ids.Contains(id))
                    working.RemoveAt(idx);
            }
            else working.RemoveAt(idx);
        }
        return working;
    }

    //system plus the newest suffix that fits 25% of the window. the last user message always stays, and the suffix never opens on an orphaned tool result
    public static IReadOnlyList<ChatMessage> HarshShape(
        IReadOnlyList<ChatMessage> messages, int windowTokens, double ratio, int? promptChars = null)
    {
        var promptLen = promptChars ?? TemplatePrompt.Length;
        var system = messages.FirstOrDefault(m => m.Role == "system");
        var lastUserIdx = -1;
        for (var i = messages.Count - 1; i >= 0; i--)
            if (messages[i].Role == "user") { lastUserIdx = i; break; }

        if (windowTokens <= 0)
        {
            //degenerate window, return the two messages guaranteed everywhere else (system and last user) rather than nothing
            var minimal = new List<ChatMessage>();
            if (system is not null) minimal.Add(system);
            if (lastUserIdx >= 0) minimal.Add(messages[lastUserIdx]);
            return minimal;
        }

        var budget = windowTokens * HarshFraction;
        var suffix = new LinkedList<ChatMessage>();
        //the loop goes down to index 0 and skips by role, so a system-less input's first message is still eligible
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].Role == "system") continue;
            var candidate = new List<ChatMessage>(suffix.Prepend(messages[i]));
            if (system is not null) candidate.Insert(0, system);
            if (Measure(candidate, ratio, promptLen) > budget && i != lastUserIdx) break;
            suffix.AddFirst(messages[i]);
            if (i == lastUserIdx && Measure(candidate, ratio, promptLen) > budget) break; //the last user message goes in even when it busts the budget
        }
        //don't open the suffix on a tool result whose assistant was cut
        while (suffix.First is { Value.Role: "tool" }) suffix.RemoveFirst();
        //the last user message is added even when the suffix stopped short, compare by identity so two equal messages can't collide
        if (lastUserIdx >= 0 && !suffix.Any(m => ReferenceEquals(m, messages[lastUserIdx])))
            suffix.AddFirst(messages[lastUserIdx]);

        var result = new List<ChatMessage>();
        if (system is not null) result.Add(system);
        result.AddRange(suffix);
        //shrink the tool-call args here too, so a write_file's name and path survive the harsher retry
        return result.Select(ShrinkMessageArgs).ToList();
    }
}
