namespace Gatto.Core.Home;

//the words a resume tells the model, one line per changed source in a fixed order, and a context file by its path only
public static class ResumeUpdate
{
    public const string FirstLine = "[gatto: this session was resumed. These facts changed since it began.]";

    public static string? Text(ResumeDelta delta, string? roleAppend, string? modelAppend)
    {
        if (delta.IsEmpty) return null;
        var lines = new List<string> { FirstLine };
        if (delta.Date is { } date) lines.Add($"Today's date is now {date}.");
        if (delta.MemoryAdded.Count > 0 || delta.MemoryRemoved.Count > 0)
            lines.Add("Project memory changed."
                + (delta.MemoryAdded.Count > 0 ? " Added: " + string.Join(" ", delta.MemoryAdded) : "")
                + (delta.MemoryRemoved.Count > 0 ? " Removed: " + string.Join(" ", delta.MemoryRemoved) : ""));
        foreach (var path in delta.ContextChanged) lines.Add($"Context file changed: {path} (read it again before relying on it).");
        var moved = delta.ContextAdded.Select(p => $"Context file added: {p}.").Concat(delta.ContextRemoved.Select(p => $"Context file removed: {p}.")).ToList();
        if (moved.Count > 0) lines.Add(string.Join(" ", moved));
        var policy = delta.PolicySet.Select(p => $"Extension policy for {p.Extension} is now: {p.Line}.")
            .Concat(delta.PolicyDropped.Select(e => $"Extension policy for {e} no longer applies.")).ToList();
        if (policy.Count > 0) lines.Add(string.Join(" ", policy));
        if (delta.RoleAppendChanged) lines.Add("The role instructions changed. The current text replaces the earlier one:\n" + (roleAppend ?? ""));
        if (delta.ModelAppendChanged) lines.Add("The model instructions changed. The current text replaces the earlier one:\n" + (modelAppend ?? ""));
        return string.Join("\n", lines);
    }

    private const string SentAgain = ": the conversation is sent again in full";

    //the line the user reads under the resume marker, from the decision and the update that was set, and none on an exact replay. a null folder was never recorded
    public static string? Line(ResumeDecision decision, ResumeDelta? carried, string? sessionFolder, string cwd)
    {
        switch (decision.Reset)
        {
            case null:
                if (carried is null || carried.IsEmpty) return null;
                var items = new List<string>();
                if (carried.Date is not null) items.Add("the date");
                var notes = carried.MemoryAdded.Count + carried.MemoryRemoved.Count;
                if (notes > 0) items.Add(notes == 1 ? "1 memory note" : $"{notes} memory notes");
                items.AddRange(carried.ContextChanged.Concat(carried.ContextAdded).Concat(carried.ContextRemoved).Select(p => Path.GetFileName(p)));
                if (carried.PolicySet.Count + carried.PolicyDropped.Count > 0) items.Add("the extension policy");
                if (carried.RoleAppendChanged) items.Add("the role instructions");
                if (carried.ModelAppendChanged) items.Add("the model instructions");
                return "told the model what changed: " + string.Join(", ", items);
            case ResumeReset.Tools: return $"the tool list changed ({string.Join(", ", decision.ToolChanges)})" + SentAgain;
            case ResumeReset.Model: return "the model changed" + SentAgain;
            case ResumeReset.Role: return "the role changed" + SentAgain;
            case ResumeReset.Effort: return "the effort changed" + SentAgain;
            case ResumeReset.ReasoningHistory: return "the reasoning history setting changed" + SentAgain;
            case ResumeReset.OlderSession: return "this session predates the resume baseline: the conversation is sent again in full, once";
            default: return sessionFolder is null
                ? $"this session does not record its folder: staying in {cwd}, the conversation is sent again in full"
                : $"the session's folder {sessionFolder} no longer exists: staying in {cwd}, the conversation is sent again in full";
        }
    }
}
