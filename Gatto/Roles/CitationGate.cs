using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Gatto.Core.Client;
using Gatto.Core.Loop;
using Gatto.Core.Tools;
using Gatto.Core.Web;

namespace Gatto.Roles;

//checks every URL a model writes against the ledger of what it fetched, warns on an invented source, and stops after three rounds
public sealed class CitationGate(CitationLedger ledger, Func<string, string?> readFile)
{
    private const int MaxRounds = 3;

    //the tools whose written result is checked, write_file and edit_file
    private static readonly HashSet<string> DeliverableTools =
        new(StringComparer.Ordinal) { "write_file", "edit_file" };

    //http and https URLs, without whitespace or the punctuation that never belongs inside one
    private static readonly Regex UrlRe = new("https?://[^\\s<>()\\[\\]\"'`]+", RegexOptions.Compiled);
    //a straight-double-quoted span of fifteen characters or more
    private static readonly Regex QuoteRe = new("\"([^\"]{15,})\"", RegexOptions.Compiled);
    //a blank line, the paragraph delimiter, a newline with only horizontal whitespace before the next
    private static readonly Regex ParagraphSplit = new("\\r?\\n[^\\S\\r\\n]*\\r?\\n", RegexOptions.Compiled);
    private static readonly Regex WsRun = new("\\s+", RegexOptions.Compiled);

    //one gate instance serves one session, so the fabricated-round count is keyed by file path only
    private readonly Dictionary<string, int> _rounds = new(StringComparer.Ordinal);

    //whether the gate is live for the current role. the app sets it from the launch role's gates, and /role re-arms it, so a disarmed gate does no ledger reads
    public bool Armed { get; set; }

    private enum Verdict { Verified, Unread, Fabricated }

    //appends the reminder to a successful write that cites a fabricated URL, and returns null otherwise so a call is never blocked
    public Task<ToolResult?> OnToolResultAsync(HookPayload payload)
    {
        //all the guards come first, before any ledger read, so a disarmed gate stays inert
        if (!Armed) return Null;
        if (payload.Result is not { } result || result.IsError) return Null;
        var name = payload.Call?.Name;
        if (name is null || !DeliverableTools.Contains(name)) return Null;
        var path = PathArg(payload.Call);
        if (path is null) return Null;
        var text = readFile(path);
        if (text is null) return Null;

        var paragraphs = ParagraphSplit.Split(text);

        //the only ledger read, so every check below runs off these entries
        var entries = ledger.Entries;
        var reads = new List<LedgerEntry>();
        var searches = new List<LedgerEntry>();
        foreach (var e in entries) (e.SearchOnly ? searches : reads).Add(e);

        var verdicts = new Dictionary<string, Verdict>(StringComparer.Ordinal);
        var readEntryOf = new Dictionary<string, LedgerEntry>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var url in UrlsIn(text))
        {
            if (verdicts.ContainsKey(url)) continue;
            order.Add(url);
            var read = reads.FirstOrDefault(e => TrimSlash(e.Ref) == TrimSlash(url));
            if (read is not null)
            {
                verdicts[url] = Verdict.Verified;
                readEntryOf[url] = read;
            }
            else if (searches.Any(e => e.Content is { } c && c.Contains(url, StringComparison.Ordinal)))
                verdicts[url] = Verdict.Unread;
            else
                verdicts[url] = Verdict.Fabricated;
        }

        var fabricated = order.Where(u => verdicts[u] == Verdict.Fabricated).ToList();
        //only a fabricated URL starts a reminder round, the warnings for unread URLs and quote mismatches are added to it
        if (fabricated.Count == 0) return Null;

        var seen = _rounds.GetValueOrDefault(path);
        if (seen >= MaxRounds) return Null;   //this file's reminder rounds are spent, stay silent
        _rounds[path] = seen + 1;

        var unread = order.Where(u => verdicts[u] == Verdict.Unread).ToList();
        var mismatches = QuoteMismatches(paragraphs, verdicts, readEntryOf);

        var sb = new StringBuilder(result.Text);
        sb.Append("\n\n[citation gate] FABRICATED (never seen this session): ")
          .Append(string.Join(", ", fabricated))
          .Append(". Fetch and read each with web_fetch, replace it with a source you did read, or delete the claim.");
        if (unread.Count > 0)
            sb.Append("\nUNREAD (seen only in search results): ").Append(string.Join(", ", unread));
        foreach (var m in mismatches)
            sb.Append("\nQUOTE_MISMATCH: ").Append(m);

        return Task.FromResult<ToolResult?>(result with { Text = sb.ToString() });
    }

    //verified citations flagged for a quoted span that the source content does not contain
    private static List<string> QuoteMismatches(
        string[] paragraphs, Dictionary<string, Verdict> verdicts, Dictionary<string, LedgerEntry> readEntryOf)
    {
        var flagged = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var para in paragraphs)
        {
            var quotes = QuoteRe.Matches(para).Select(m => NormWs(m.Groups[1].Value)).ToList();
            if (quotes.Count == 0) continue;
            foreach (var url in UrlsIn(para).Distinct())
            {
                if (seen.Contains(url)) continue;
                if (verdicts.GetValueOrDefault(url) != Verdict.Verified) continue;
                if (readEntryOf[url].Content is not { } content) continue;
                var haystack = NormWs(content);
                if (quotes.Any(q => !haystack.Contains(q, StringComparison.Ordinal)))
                {
                    flagged.Add(url);
                    seen.Add(url);
                }
            }
        }
        return flagged;
    }

    private static IEnumerable<string> UrlsIn(string text)
    {
        foreach (Match m in UrlRe.Matches(text))
            yield return TrimTrailingPunctuation(m.Value);
    }

    //sentence punctuation glued to the end of a URL is not part of it
    private static string TrimTrailingPunctuation(string url) => url.TrimEnd('.', ',', ';', ':', '!', '?', ')');

    private static string TrimSlash(string s) => s.EndsWith('/') ? s[..^1] : s;

    private static string NormWs(string s) => WsRun.Replace(s, " ").Trim();

    private static string? PathArg(ToolCall? call)
    {
        if (call is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(call.ArgumentsJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("path", out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();
        }
        catch (JsonException) { } //unparseable args leave no path to gate
        return null;
    }

    private static Task<ToolResult?> Null => Task.FromResult<ToolResult?>(null);
}
