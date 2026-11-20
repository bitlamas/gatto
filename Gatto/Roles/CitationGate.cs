using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Gatto.Core.Client;
using Gatto.Core.Loop;
using Gatto.Core.Tools;
using Gatto.Core.Web;

namespace Gatto.Roles;

//checks every URL a model writes against the ledger of what it fetched, warns on a source it never fetched, and stops after three rounds. resolve is the write tools' own resolver, so the gate reads back the file the write made
public sealed class CitationGate(CitationLedger ledger, Func<string, string?> readFile, Func<string, string>? resolve = null)
{
    //the name a role writes in its gates to arm this gate
    public const string Name = "citations";

    private const int MaxRounds = 3;

    //the tools whose written result is checked, write_file and edit_file
    private static readonly HashSet<string> DeliverableTools =
        new(StringComparer.Ordinal) { "write_file", "edit_file" };

    //a straight-double-quoted span of fifteen characters or more
    private static readonly Regex QuoteRe = new("\"([^\"]{15,})\"", RegexOptions.Compiled);
    //a blank line, the paragraph delimiter, a newline with only horizontal whitespace before the next
    private static readonly Regex ParagraphSplit = new("\\r?\\n[^\\S\\r\\n]*\\r?\\n", RegexOptions.Compiled);
    private static readonly Regex WsRun = new("\\s+", RegexOptions.Compiled);

    //the process keeps one gate across /new, so Reset clears what belongs to the session that ended. keyed by the resolved path without case, as Windows names a file
    private readonly Dictionary<string, int> _rounds = new(StringComparer.OrdinalIgnoreCase);

    //every link the session was shown: tool results, the user's records and the head of the prompt
    private readonly SeenLinks _seen = new();

    //the files this session wrote, so reading one back shows the model nothing it did not write itself
    private readonly HashSet<string> _written = new(StringComparer.OrdinalIgnoreCase);

    //a new session starts with every file's notes unspent and nothing shown
    public void Reset()
    {
        _rounds.Clear();
        _seen.Clear();
        _written.Clear();
    }

    //whether the gate is live for the current role. the app sets it from the launch role's gates, and /role re-arms it, so a disarmed gate does no ledger reads
    public bool Armed { get; set; }

    //whether the session was shown this link, compared whole
    public bool WasShown(string link) => _seen.Seen(link);

    private enum Verdict { Verified, Unread, Fabricated }

    //appends the reminder to a successful write that cites a URL never fetched, and returns null otherwise so a call is never blocked
    public Task<ToolResult?> OnToolResultAsync(HookPayload payload)
    {
        var outcome = payload.Result;
        //the harvest runs in every seat and on a failed call, so a /role into a gated seat knows what came before it
        if (payload.Call is { } shownCall && outcome is not null)
            HarvestResult(shownCall.Name, shownCall.ArgumentsJson, outcome.Text, outcome.IsError, Resolve);
        //the guards come before any ledger read, so a disarmed gate reads none
        if (!Armed) return Null;
        if (outcome is not { } result || result.IsError) return Null;
        var name = payload.Call?.Name;
        if (name is null || !DeliverableTools.Contains(name)) return Null;
        var path = ArgOf(payload.Call!.ArgumentsJson, "path");
        if (path is null) return Null;
        var resolved = Resolve(path);
        if (resolved is null) return Null;
        var text = readFile(resolved);
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
        foreach (var url in SeenLinks.UrlsIn(text))
        {
            if (verdicts.ContainsKey(url)) continue;
            order.Add(url);
            var read = reads.FirstOrDefault(e => SeenLinks.TrimSlash(e.Ref) == SeenLinks.TrimSlash(url));
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
        //only a URL never fetched starts a reminder round, the warnings for search-only URLs and quote mismatches are added to it
        if (fabricated.Count == 0) return Null;

        var seen = _rounds.GetValueOrDefault(resolved);
        if (seen >= MaxRounds) return Null;   //this file's reminder rounds are spent, stay silent
        _rounds[resolved] = seen + 1;

        var unread = order.Where(u => verdicts[u] == Verdict.Unread).ToList();
        var mismatches = QuoteMismatches(paragraphs, verdicts, readEntryOf);

        //each list says what the ledger measured of its links, and neither line names a verdict
        var sb = new StringBuilder(result.Text);
        sb.Append("\n\n[citation gate] not fetched and in no search result this session: ")
          .Append(string.Join(", ", fabricated))
          .Append(". Fetch and read each with web_fetch, replace it with a source you did read, or delete the claim.");
        if (unread.Count > 0)
            sb.Append("\nin a search result, not fetched: ").Append(string.Join(", ", unread));
        foreach (var m in mismatches)
            sb.Append("\nQUOTE_MISMATCH: ").Append(m);
        //the spent budget is said on its last note, so the silence after it is not read as a clean write
        if (seen + 1 == MaxRounds)
            sb.Append("\nLast note for ").Append(path).Append(": a later write to it gets no citation note.");

        return Task.FromResult<ToolResult?>(result with { Text = sb.ToString() });
    }

    //where the links of a reply the session never showed go, the screen's slot. null under -p, where no row is drawn
    public Action<IReadOnlyList<string>>? Cited { get; set; }

    //reads the reply that ends a turn, and names each link in it the session was never shown, first seen first. the reply's own links stay unseen, so a link invented twice is named twice
    public Task OnMessageEndAsync(HookPayload payload)
    {
        if (!Armed || Cited is not { } cited || string.IsNullOrEmpty(payload.AssistantText)) return Task.CompletedTask;
        var named = new HashSet<string>(StringComparer.Ordinal);
        var unseen = SeenLinks.UrlsIn(payload.AssistantText)
            .Where(link => !_seen.Seen(link) && named.Add(SeenLinks.TrimSlash(link)))
            .ToList();
        if (unseen.Count > 0) cited(unseen);
        return Task.CompletedTask;
    }

    //a record the session put before the model: its user records, and the head of the prompt with any first_message seeded under it
    public void OnShown(ChatMessage record)
    {
        switch (record.Role)
        {
            case "system":
                _seen.Add(HeadOf(record.Content));
                break;
            case "assistant":   //the loop passes an assistant record only when it is the seeded opening
            case "user" when record.By is null:   //the pictures a tool read add nothing
                _seen.Add(record.Content);
                break;
        }
    }

    //a resume's set, from each file of the chain oldest first by the live rules, each path resolved against the folder its own file names
    public void Rebuild(IEnumerable<(IReadOnlyList<ChatMessage> Records, Func<string, string> Resolve)> files)
    {
        foreach (var (records, resolveIn) in files)
        {
            var calls = new Dictionary<string, ToolCall>(StringComparer.Ordinal);
            var userSeen = false;
            foreach (var r in records)
            {
                switch (r.Role)
                {
                    case "system":
                        _seen.Add(HeadOf(r.Content));
                        break;
                    case "assistant":
                        if (!userSeen) _seen.Add(r.Content);   //the seeded opening, the one reply that comes before every user record of its file
                        foreach (var c in r.ToolCalls ?? []) calls[c.Id] = c;
                        break;
                    case "user":
                        userSeen = true;
                        if (r.By is null) _seen.Add(r.Content);
                        break;
                    case "tool" when r.ToolCallId is { } id && calls.TryGetValue(id, out var call):
                        HarvestResult(call.Name, call.ArgumentsJson, r.Content ?? "", r.IsError, p => TryResolve(resolveIn, p));
                        break;
                }
            }
        }
    }

    //what a tool's result shows the model. run_agent returns the model's own words, a write returns gatto's confirmation and the gate's note, and a read of a file this session wrote returns the model's own links
    private void HarvestResult(string tool, string argumentsJson, string text, bool isError, Func<string, string?> resolvePath)
    {
        switch (tool)
        {
            case "run_agent":
                return;
            case "write_file" or "edit_file":
                if (!isError && ArgOf(argumentsJson, "path") is { } written && resolvePath(written) is { } at) _written.Add(at);
                return;
            case "read_file" when ArgOf(argumentsJson, "path") is { } read && resolvePath(read) is { } readAt && _written.Contains(readAt):
                return;
            case "web_fetch" when !isError && ArgOf(argumentsJson, "url") is { } url:
                _seen.AddLink(url);   //a fetched page's text rarely names its own address
                break;
        }
        _seen.Add(text);
    }

    //the composed head, never the compaction summary or the last exchange a /compact quotes after its marker
    private static string? HeadOf(string? systemText)
    {
        if (systemText is null) return null;
        var at = systemText.IndexOf(Compactor.ContextMarker, StringComparison.Ordinal);
        return at >= 0 ? systemText[..at] : systemText;
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
            foreach (var url in SeenLinks.UrlsIn(para).Distinct())
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

    private static string NormWs(string s) => WsRun.Replace(s, " ").Trim();

    private string? Resolve(string path) => resolve is null ? path : TryResolve(resolve, path);

    //a path the resolver cannot take, an illegal character or a bad drive, leaves nothing to read back
    private static string? TryResolve(Func<string, string> resolver, string path)
    {
        try { return resolver(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.IO.PathTooLongException) { return null; }
    }

    private static string? ArgOf(string argumentsJson, string key)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();
        }
        catch (JsonException) { } //unparseable args leave nothing to read
        return null;
    }

    private static Task<ToolResult?> Null => Task.FromResult<ToolResult?>(null);
}
