namespace Gatto.Core.Memory;

//what one compaction's piggyback banked, with HeadingFound recording whether the model wrote a candidate section at all
public sealed record MemoryBankOutcome(bool HeadingFound, int Candidates, int Banked, int Dropped);

//harvest memory free from the /compact summary by extracting the bullets under one extra heading, with no second model call
public static class MemoryPiggyback
{
    //ceiling on candidates from one summary, an unbounded harvest would leak context silently
    public const int MaxLines = 10;

    //the template heading and the marker the region scan looks for, one string so the prompt and the extractor can't drift apart
    public const string Heading = "Memory candidates";

    //the stripped summary must lose the candidate section even when nothing banks, and HeadingFound separates a missing section from one that yielded nothing
    public sealed record Extraction(
        IReadOnlyList<string> Candidates, string StrippedSummary, int Dropped, bool HeadingFound);

    //everything after the last line that matches the heading, so a summary that merely quotes the phrase can't open a bogus region
    public static Extraction Extract(string summary)
    {
        var lines = summary.Split('\n');
        var headingIdx = -1;
        for (var i = 0; i < lines.Length; i++)
            if (IsHeadingLine(lines[i].Trim()))
                headingIdx = i;

        if (headingIdx < 0) return new Extraction([], summary, 0, HeadingFound: false);

        var candidates = new List<string>();
        var dropped = 0;
        for (var i = headingIdx + 1; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.Length == 0) continue;
            if (IsNothing(trimmed)) continue;         //a line that says "none" or "nothing", bulleted or not, is dropped

            var body = StripMarker(trimmed);
            //a marker with no text behind it holds no fact, an empty bullet would sit in the index forever
            if (body.Length == 0) continue;

            if (candidates.Count >= MaxLines) { dropped++; continue; }
            candidates.Add("- " + body);
        }

        //slice the original string so a CRLF summary keeps its own line endings up to the cut
        var offset = 0;
        for (var i = 0; i < headingIdx; i++) offset += lines[i].Length + 1;   //the +1 counts the newline that Split dropped
        return new Extraction(candidates, summary[..offset].TrimEnd(), dropped, HeadingFound: true);
    }

    //match the heading case-insensitively after optional #s and a bold opener, and let a bulleted quote stay non-matching
    private static bool IsHeadingLine(string trimmedLine)
    {
        var i = 0;
        var hashes = 0;
        while (i < trimmedLine.Length && trimmedLine[i] == '#' && hashes < 6) { i++; hashes++; }
        if (hashes > 0)
            while (i < trimmedLine.Length && char.IsWhiteSpace(trimmedLine[i])) i++;

        if (i + 1 < trimmedLine.Length &&
            ((trimmedLine[i] == '*' && trimmedLine[i + 1] == '*') ||
             (trimmedLine[i] == '_' && trimmedLine[i + 1] == '_')))
            i += 2;

        while (i < trimmedLine.Length && char.IsWhiteSpace(trimmedLine[i])) i++;

        return trimmedLine.AsSpan(i).StartsWith(Heading, StringComparison.OrdinalIgnoreCase);
    }

    //the template's "nothing to record" answer in any casing, with a bullet marker or trailing punctuation
    private static bool IsNothing(string trimmedLine)
    {
        var probe = StripMarker(trimmedLine.ToLowerInvariant()).TrimEnd('.', '!').TrimEnd();
        return probe is "none" or "nothing";
    }

    //drop one leading bullet marker only, a fact that starts with a dash keeps the second one
    private static string StripMarker(string trimmedLine) =>
        trimmedLine.Length > 0 && (trimmedLine[0] == '-' || trimmedLine[0] == '*')
            ? trimmedLine[1..].TrimStart()
            : trimmedLine;

    //one c- fact file per candidate, line 1 only, deduped by exact line-1 match against every existing fact file
    public static (int Banked, int Skipped) BankCandidates(
        string projectRoot, IReadOnlyList<string> candidates)
    {
        if (candidates.Count == 0) return (0, 0);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in MemoryDir.FactFiles(projectRoot))
        {
            try
            {
                seen.Add((File.ReadLines(path).FirstOrDefault() ?? "").Trim());
            }
            //an unreadable fact costs its dedup entry only, one prunable duplicate beats losing the candidate
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        int banked = 0, skipped = 0;
        foreach (var candidate in candidates)
        {
            if (!seen.Add(candidate.Trim())) { skipped++; continue; }

            MemoryDir.Write(projectRoot, UniqueSlug(projectRoot, FactSlug.FromFactLine(candidate)), candidate);
            banked++;
        }
        return (banked, skipped);
    }

    //the minted slug or the first free -2/-3 variant, base trimmed so the name stays inside the 40-character cap
    private static string UniqueSlug(string projectRoot, string slug)
    {
        if (!File.Exists(MemoryDir.PathFor(projectRoot, slug))) return slug;

        for (var n = 2; ; n++)
        {
            var suffix = $"-{n}";
            var baseSlug = slug.Length + suffix.Length > 40
                ? slug[..(40 - suffix.Length)].TrimEnd('-')
                : slug;
            var candidate = baseSlug + suffix;
            if (!File.Exists(MemoryDir.PathFor(projectRoot, candidate))) return candidate;
        }
    }
}
