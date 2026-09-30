using System.Text;
using System.Text.RegularExpressions;

namespace Gatto.Roles;

//the score of a restatement against a brief's anchors. the missing list holds all of them, so a caller can show what the model left out
public sealed record RestatementScore(
    int K, int N, IReadOnlyList<string> Matched, IReadOnlyList<string> Missing, bool Passed);

//the grounding gate's mechanical check, pure functions with no I/O. a restatement that misses the brief's anchors fails at turn 1
public static partial class Grounding
{
    //words that never count as anchors, English and French
    private static readonly HashSet<string> Common = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "that", "this", "your", "you", "will", "shall", "must", "should",
        "from", "into", "each", "them", "they", "then", "than", "when", "what", "which", "where", "who",
        "write", "read", "make", "give", "list", "task", "file", "files", "note", "notes", "code", "test",
        "tests", "spec", "section", "sections", "step", "steps", "use", "used", "using", "have", "has", "not",
        "any", "all", "one", "two", "three", "end", "ending", "mandatory", "self", "check", "docs", "doc",
        "les", "des", "une", "pour", "avec", "dans", "vous", "nous", "est", "sont", "cette", "leur", "sur",
        "page", "pages", "line", "lines", "value", "values", "example", "output", "input", "here", "there",
        "their", "please", "answer", "answers", "question", "questions", "brief", "requirement",
        "requirements", "acceptance", "criteria", "schema", "security", "following", "above", "below",
    };

    [GeneratedRegex("`([^`\\n]+)`")] private static partial Regex BacktickSpan();
    [GeneratedRegex("[A-Za-z_][A-Za-z0-9_.]{2,}")] private static partial Regex Identifier();
    [GeneratedRegex(@"\b[A-Za-z0-9_-]+(?:[./][A-Za-z0-9_-]+)+\b")] private static partial Regex PathLike();
    [GeneratedRegex(@"\b\d+(?:\.\d+)?[A-Za-z%]{0,6}\b")] private static partial Regex NumberWithUnit();
    [GeneratedRegex(@"\b[A-Z][A-Za-z0-9]{2,}(?:[A-Z][A-Za-z0-9]*)*\b")] private static partial Regex ProperNoun();
    [GeneratedRegex("[A-Z].*[A-Z]")] private static partial Regex TwoCaps();
    [GeneratedRegex("[_./]")] private static partial Regex IdentifierPunct();
    [GeneratedRegex(@"\d")] private static partial Regex HasDigit();

    //pull the brief's identifying tokens, at most max of them, rarest first
    public static IReadOnlyList<string> ExtractAnchors(string brief, int max = 12)
    {
        brief = StripFencedBlocks(brief);
        var candidates = new List<string>();

        foreach (Match m in BacktickSpan().Matches(brief))
        {
            var span = m.Groups[1].Value.Trim();
            candidates.Add(span);
            foreach (Match id in Identifier().Matches(span)) candidates.Add(id.Value);
        }
        foreach (Match m in PathLike().Matches(brief)) candidates.Add(m.Value);
        foreach (Match m in NumberWithUnit().Matches(brief)) candidates.Add(m.Value);
        foreach (Match m in ProperNoun().Matches(brief)) candidates.Add(m.Value);

        //dedupe case-insensitively, the longest spelling wins, and short or common tokens are dropped
        var kept = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in candidates)
        {
            var token = raw.Trim();
            if (token.Length < 3 || token.Length > 60 || Common.Contains(token)) continue;
            if (token.Contains('\n') || token.Contains('\r')) continue;
            if (!kept.TryGetValue(token, out var existing) || token.Length > existing.Length)
                kept[token] = token;
        }

        return kept.Values
            .OrderByDescending(Rarity)
            .ThenBy(t => t, StringComparer.Ordinal)   //ordinal, so equal rarity always comes out in the same order
            .Take(max)
            .ToList();
    }

    //passes at threshold of the anchors matched. a brief with fewer than four anchors is too small to judge, so it passes regardless
    public static RestatementScore CheckRestatement(
        string restatement, IReadOnlyList<string> anchors, double threshold = 0.5)
    {
        var matched = new List<string>();
        var missing = new List<string>();
        foreach (var a in anchors)
        {
            if (restatement.Contains(a, StringComparison.OrdinalIgnoreCase)) matched.Add(a);
            else missing.Add(a);
        }
        var n = anchors.Count;
        var k = matched.Count;
        var passed = n < 4 || (double)k / n >= threshold;
        return new RestatementScore(k, n, matched, missing, passed);
    }

    //fenced blocks are examples rather than task anchors, so strip them first. line-based, so an unterminated fence can't backtrack the regex
    private static string StripFencedBlocks(string brief)
    {
        var lines = brief.Split('\n');
        var sb = new StringBuilder();
        var inFence = false;
        foreach (var line in lines)
        {
            var isFenceLine = line.StartsWith("```", StringComparison.Ordinal);
            if (!inFence && isFenceLine) { inFence = true; continue; }
            if (inFence)
            {
                if (isFenceLine) inFence = false;
                continue;
            }
            sb.Append(line).Append('\n');
        }
        return sb.ToString();
    }

    //longer tokens with capitals, separators or digits count as more identifying, so they sort first
    private static int Rarity(string a)
    {
        var s = a.Length;
        if (TwoCaps().IsMatch(a)) s += 6;
        if (IdentifierPunct().IsMatch(a)) s += 6;
        if (HasDigit().IsMatch(a)) s += 3;
        return s;
    }
}
