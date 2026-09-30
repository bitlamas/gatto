namespace Gatto.Core.Memory;

//compose the model-facing index in the prompt from the fact files, capped at chars÷4, with no aggregate file stored on disk
public static class MemoryIndex
{
    //the same chars÷4 estimate ContextBudget.CharsPerToken and MemoryWriteTool use, so retune the ratio in all three places
    private const int CharsPerToken = 4;

    //null Text means the caller omits the whole ## Memory block, and TruncatedLines counts the whole lines the budget dropped
    public sealed record LoadResult(string? Text, int TruncatedLines);

    //whole lines only, so a partial fact never surfaces, and a file whose first line is blank contributes nothing
    public static LoadResult Compose(string projectRoot, int budgetTokens)
    {
        var lines = BuildLines(projectRoot);

        if (lines.Count == 0) return new LoadResult(null, 0);

        var capChars = budgetTokens * CharsPerToken;
        var included = 0;
        var cumulative = 0;
        foreach (var line in lines)
        {
            var cost = line.Length + 1;   //the newline after the line counts toward the cap too
            if (cumulative + cost > capChars) break;
            cumulative += cost;
            included++;
        }

        //an empty slice joins to "" so an over-budget index still shows its truncation note
        return new LoadResult(string.Join('\n', lines.Take(included)), lines.Count - included);
    }

    //the whole index with no cap, so memory_write's advisory can report over 100% (Compose truncates at the budget, so it never can)
    public static string ComposeUncapped(string projectRoot) =>
        string.Join('\n', BuildLines(projectRoot));

    //model-authored facts first and machine-fed c- facts after, since Compose drops from the tail and the harvested facts should go first
    private static List<string> BuildLines(string projectRoot)
    {
        var lines = new List<string>();

        var ordered = MemoryDir.FactFiles(projectRoot)
            .OrderBy(p => Path.GetFileName(p).StartsWith(FactSlug.Prefix, StringComparison.Ordinal) ? 1 : 0)
            .ThenBy(Path.GetFileName, StringComparer.Ordinal);

        foreach (var path in ordered)
        {
            string raw;
            try
            {
                raw = File.ReadAllText(path);
            }
            //skip one unreadable fact (locked, ACL-denied, a directory at the path) without losing the facts beside it
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            var split = raw.Split('\n');
            var first = StripCr(split.Length > 0 ? split[0] : "");
            if (string.IsNullOrWhiteSpace(first)) continue;

            //only point at the file when a line after the first has content, an empty invitation costs a turn
            var hasDetail = split.Skip(1).Any(l => !string.IsNullOrWhiteSpace(StripCr(l)));
            lines.Add(hasDetail ? $"{first} → {Path.GetFileName(path)}" : first);
        }

        return lines;
    }

    private static string StripCr(string line) => line.EndsWith('\r') ? line[..^1] : line;
}
