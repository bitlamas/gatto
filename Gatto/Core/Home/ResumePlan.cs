using Gatto.Core.Loop;

namespace Gatto.Core.Home;

public enum ResumeReset { Folder, OlderSession, Model, Role, Tools, ReasoningHistory, Effort }

//replay when Reset is null, and ToolChanges names what moved when the reason is Tools
public sealed record ResumeDecision(ResumeReset? Reset, IReadOnlyList<string> ToolChanges)
{
    public static readonly ResumeDecision Replay = new(null, Array.Empty<string>());
}

//what changed since the model last saw the sources, a new date or null, and the lists empty when nothing moved
public sealed record ResumeDelta(
    string? Date, IReadOnlyList<string> MemoryAdded, IReadOnlyList<string> MemoryRemoved,
    IReadOnlyList<string> ContextChanged, IReadOnlyList<string> ContextAdded, IReadOnlyList<string> ContextRemoved,
    IReadOnlyList<PolicyMark> PolicySet, IReadOnlyList<string> PolicyDropped,
    bool RoleAppendChanged, bool ModelAppendChanged)
{
    public bool IsEmpty => Date is null && MemoryAdded.Count == 0 && MemoryRemoved.Count == 0
        && ContextChanged.Count == 0 && ContextAdded.Count == 0 && ContextRemoved.Count == 0
        && PolicySet.Count == 0 && PolicyDropped.Count == 0 && !RoleAppendChanged && !ModelAppendChanged;
}

//whether a resumed session sends its stored prefix again or composes a fresh one, the first reason that holds wins
public static class ResumePlan
{
    public static ResumeDecision Decide(SessionBaseline? stored, SessionBaseline launch, bool folderExists, bool effortFlagGiven)
    {
        if (!folderExists) return Reset(ResumeReset.Folder);
        if (stored is null) return Reset(ResumeReset.OlderSession);
        if (!string.Equals(stored.Model, launch.Model, StringComparison.Ordinal)) return Reset(ResumeReset.Model);
        if (!string.Equals(stored.Role, launch.Role, StringComparison.Ordinal)) return Reset(ResumeReset.Role);
        if (!BaselineMarks.SameTools(stored.Tools, launch.Tools)) return new ResumeDecision(ResumeReset.Tools, ToolChanges(stored.Tools, launch.Tools));
        if (!string.Equals(stored.ReasoningHistory, launch.ReasoningHistory, StringComparison.Ordinal)) return Reset(ResumeReset.ReasoningHistory);
        if (effortFlagGiven && !string.Equals(stored.Thinking.BodyJson, launch.Thinking.BodyJson, StringComparison.Ordinal)) return Reset(ResumeReset.Effort);
        return ResumeDecision.Replay;
    }

    private static ResumeDecision Reset(ResumeReset reason) => new(reason, Array.Empty<string>());

    //a stored list is data from disk and can repeat a key, so the last entry wins where ToDictionary would throw and end the resume
    private static Dictionary<string, string> LastWins<T>(IEnumerable<T> items, Func<T, string> key, Func<T, string> value)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in items) d[key(item)] = value(item);
        return d;
    }

    //what the model last saw: the sources of the newest update written under this baseline, or the baseline's own
    public static BaselineSources EffectiveSources(SessionBaseline stored, IReadOnlyList<Gatto.Core.Client.ChatMessage> records)
    {
        for (var i = records.Count - 1; i >= 0; i--)
            if (records[i].Update is { } u && u.BaseSeq == stored.Seq) return u.Sources;
        return stored.Sources;
    }

    //each source compared on its own, memory as sets of index lines with the truncation note left out
    public static ResumeDelta Delta(BaselineSources effective, BaselineSources launch)
    {
        static List<string> Lines(string? memory) => (memory ?? "").Split('\n')
            .Where(l => l.Trim().Length > 0 && !l.StartsWith("[index truncated", StringComparison.Ordinal)).ToList();
        var was = Lines(effective.Memory);
        var now = Lines(launch.Memory);
        var wasFiles = LastWins(effective.ContextFiles, f => f.Path, f => f.Sha256);
        var nowFiles = LastWins(launch.ContextFiles, f => f.Path, f => f.Sha256);
        var wasPolicy = LastWins(effective.Policy, p => p.Extension, p => p.Line);
        var nowPolicy = LastWins(launch.Policy, p => p.Extension, p => p.Line);
        return new ResumeDelta(
            launch.Date is not null && launch.Date != effective.Date ? launch.Date : null,
            now.Where(l => !was.Contains(l)).ToList(),
            was.Where(l => !now.Contains(l)).ToList(),
            launch.ContextFiles.Where(f => wasFiles.TryGetValue(f.Path, out var sha) && sha != f.Sha256).Select(f => f.Path).ToList(),
            launch.ContextFiles.Where(f => !wasFiles.ContainsKey(f.Path)).Select(f => f.Path).ToList(),
            effective.ContextFiles.Where(f => !nowFiles.ContainsKey(f.Path)).Select(f => f.Path).ToList(),
            launch.Policy.Where(p => !wasPolicy.TryGetValue(p.Extension, out var line) || line != p.Line).ToList(),
            effective.Policy.Where(p => !nowPolicy.ContainsKey(p.Extension)).Select(p => p.Extension).ToList(),
            launch.RoleAppendSha256 != effective.RoleAppendSha256,
            launch.ModelAppendSha256 != effective.ModelAppendSha256);
    }

    //added, removed and changed by name, and reordered only when the same tools arrive in another order
    private static IReadOnlyList<string> ToolChanges(IReadOnlyList<ToolMark> stored, IReadOnlyList<ToolMark> launch)
    {
        var was = LastWins(stored, t => t.Name, t => t.Sha256);
        var now = LastWins(launch, t => t.Name, t => t.Sha256);
        var changes = new List<string>();
        changes.AddRange(launch.Where(t => !was.ContainsKey(t.Name)).Select(t => $"added {t.Name}"));
        changes.AddRange(stored.Where(t => !now.ContainsKey(t.Name)).Select(t => $"removed {t.Name}"));
        changes.AddRange(launch.Where(t => was.TryGetValue(t.Name, out var sha) && sha != t.Sha256).Select(t => $"changed {t.Name}"));
        if (changes.Count == 0) changes.Add("reordered");
        return changes;
    }
}
