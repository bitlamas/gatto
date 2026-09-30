using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Gatto.Core.Memory;

namespace Gatto.Core.Tools;

//the model's search over its own project's past, live on session and memory files with no index or cache. memory_write is the main store, this covers the rest
public sealed class RecallMemoryTool : ITool
{
    public string Name => "recall_memory";

    public string Description =>
        "Search this project's past sessions and memory files (.gatto\\memory\\); returns " +
        "pointers and short excerpts, never full turns. Results name absolute file paths " +
        "readable with read_file. Args: query (string), optional limit (default 5).";

    public JsonElement ParametersSchema => ToolArgs.Schema("""
        {"type":"object","properties":{"query":{"type":"string"},"limit":{"type":"integer"}},"required":["query"]}
        """);

    private const int ExcerptCap = 300;
    private static readonly Regex NonAlnum = new(@"[^\p{L}\p{N}]+", RegexOptions.Compiled);

    public Task<ToolResult> ExecuteAsync(JsonElement args, IToolContext ctx, CancellationToken ct)
    {
        var query = ToolArgs.RequiredString(args, "query");
        var limit = ToolArgs.OptionalInt(args, "limit") ?? 5;

        var terms = Tokenize(query);
        var root = MemoryDir.FindProjectRoot(ctx.Cwd);

        var hits = ScanSessions(ctx, root, terms, ct);
        hits.AddRange(ScanMemoryFiles(root, terms));

        var top = hits.OrderByDescending(h => h.Score).Take(limit).ToList();
        if (top.Count == 0) return Task.FromResult(new ToolResult("no matches"));

        var json = JsonSerializer.Serialize(top.Select(h => new
        {
            session = h.Session,
            turn = h.Turn,
            when = h.When,
            excerpt = h.Excerpt,
            score = Math.Round(h.Score, 2)
        }));
        return Task.FromResult(new ToolResult(json, Gloss: Plural.Of(top.Count, "hit")));
    }

    private sealed record Hit(string Session, int Turn, string When, string Excerpt, double Score);

    private enum Scope { InScope, OutOfScope, Unreadable }

    //rank recency over the in-scope session files alone, so the newest of them scores 1.0. another project's session in the same folder must not change that
    private static List<Hit> ScanSessions(IToolContext ctx, string root, string[] terms, CancellationToken ct)
    {
        var hits = new List<Hit>();
        var sessionsDir = Path.Combine(ctx.HomePath, "sessions");
        if (!Directory.Exists(sessionsDir)) return hits;   //no session was ever saved, so the empty result is right

        //a ledger sidecar shares this folder and also ends in .jsonl, so it must not be read as a session
        var included = Directory.EnumerateFiles(sessionsDir, "*.jsonl")
            .Where(f => !f.EndsWith(".ledger.jsonl", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal)   //filenames embed UTC ticks, so ordinal order is chronological
            .Where(f => ClassifyScope(f, root) == Scope.InScope)
            .ToArray();

        for (var i = 0; i < included.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var recencyRank = (double)(i + 1) / included.Length;   //the newest file among these scores 1.0
            hits.AddRange(ScanFileRecords(included[i], terms, recencyRank));
        }
        return hits;
    }

    //skip records with no role, a rotated session leads with display events. a cwd at or under root is in scope, and a cwd missing or under another root is out
    private static Scope ClassifyScope(string file, string root)
    {
        try
        {
            foreach (var line in File.ReadLines(file))
            {
                if (line.Trim().Length == 0) continue;
                JsonDocument doc;
                try { doc = JsonDocument.Parse(line); }
                catch (JsonException) { return Scope.Unreadable; }
                using (doc)
                {
                    //a display or anchor record has no role, which is what filters it out here
                    if (!doc.RootElement.TryGetProperty("role", out _)) continue;
                    if (!doc.RootElement.TryGetProperty("cwd", out var c) || c.ValueKind != JsonValueKind.String)
                        return Scope.OutOfScope;   //a record with no usable cwd belongs to another project, so it is out of scope
                    return IsAtOrUnder(c.GetString()!, root) ? Scope.InScope : Scope.OutOfScope;
                }
            }
        }
        //an ACL denial or an AV lock throws UnauthorizedAccessException, which is not an IOException and needs its own catch here
        catch (IOException) { return Scope.Unreadable; }
        catch (UnauthorizedAccessException) { return Scope.Unreadable; }

        return Scope.Unreadable;   //empty, or nothing but display records
    }

    //compare path segments, so C:\proj does not match C:\proj-other, which a plain StartsWith(root) would
    private static bool IsAtOrUnder(string candidateCwd, string root)
    {
        string c, r;
        try { c = NormalizeForCompare(candidateCwd); }
        catch (ArgumentException) { return false; }   //an unparseable recorded cwd can't sit under any root
        r = NormalizeForCompare(root);

        if (string.Equals(c, r, StringComparison.OrdinalIgnoreCase)) return true;
        return c.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeForCompare(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    //read the whole file before scoring, so a failure mid-read drops this file and keeps the other hits. turn is the 1-based count of role-bearing records
    private static List<Hit> ScanFileRecords(string file, string[] terms, double recencyRank)
    {
        var hits = new List<Hit>();
        List<string> lines;
        try { lines = File.ReadLines(file).ToList(); }
        //both exception types need a catch here, so this file gives no hits and the other files still count
        catch (IOException) { return hits; }
        catch (UnauthorizedAccessException) { return hits; }

        var turn = 0;
        foreach (var raw in lines)
        {
            if (raw.Trim().Length == 0) continue;

            string content;
            string? ts;
            //role is declared here because the hit is built after the catch, and a declaration inside the try would fail definite assignment
            string? role;
            try
            {
                using var doc = JsonDocument.Parse(raw);
                var el = doc.RootElement;
                if (!el.TryGetProperty("role", out var roleEl)) continue;   //a record with no role is an event or anchor row, so it is skipped
                role = roleEl.ValueKind == JsonValueKind.String ? roleEl.GetString() : null;
                content = el.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
                    ? c.GetString()! : "";
                ts = el.TryGetProperty("ts", out var t) && t.ValueKind == JsonValueKind.String
                    ? t.GetString() : null;
            }
            catch (JsonException) { continue; }   //skip a line that will not parse, with no warning

            turn++;
            var overlap = CountOverlap(content, terms);
            if (overlap == 0) continue;

            var score = (double)overlap / terms.Length + 0.5 * recencyRank;
            //strip control tokens from assistant excerpts only, since role=tool records hold them legitimately. scoring ran on the raw text, so the match set is unchanged
            var excerpt = role == "assistant" ? ControlTokens.Strip(content).Text : content;
            hits.Add(new Hit(file, turn, ExtractWhen(ts, file), Sanitize(excerpt), score));
        }
        return hits;
    }

    //score a memory file's lines like a session, with recency always at 1.0, and report only its best line. a tie goes to the lowest line number
    private static List<Hit> ScanMemoryFiles(string root, string[] terms)
    {
        var hits = new List<Hit>();
        var dir = Path.Combine(root, ".gatto", "memory");
        if (!Directory.Exists(dir)) return hits;

        foreach (var file in Directory.EnumerateFiles(dir, "*.md"))
        {
            string[] lines;
            //a fact file mid-write can fail the read, so skip this file and keep the other hits. an ACL denial or AV lock needs its own catch, it is not an IOException
            try { lines = File.ReadAllLines(file); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            var when = File.GetLastWriteTimeUtc(file).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            Hit? best = null;
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.Trim().Length == 0) continue;
                var overlap = CountOverlap(line, terms);
                if (overlap == 0) continue;
                var score = (double)overlap / terms.Length + 0.5;   //memory lines always score at full recency
                if (best is null || score > best.Score)
                    best = new Hit(file, i + 1, when, Sanitize(line), score);
            }
            if (best is not null) hits.Add(best);
        }
        return hits;
    }

    private static int CountOverlap(string content, string[] terms)
    {
        if (terms.Length == 0) return 0;
        var lower = content.ToLowerInvariant();
        var n = 0;
        foreach (var term in terms)
            if (lower.Contains(term, StringComparison.Ordinal)) n++;
        return n;
    }

    private static string ExtractWhen(string? ts, string file) =>
        ts is not null && DateTime.TryParse(ts, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt)
            ? dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : File.GetLastWriteTimeUtc(file).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string[] Tokenize(string query) =>
        NonAlnum.Split(query.ToLowerInvariant())
            .Where(t => t.Length > 0)
            .Distinct()
            .ToArray();

    //strip control characters except tab, then cut to ExcerptCap through SafeCut so a clipped astral character leaves no lone surrogate
    private static string Sanitize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (ch == '\t') { sb.Append(ch); continue; }
            if (ch <= '\u001F' || (ch >= '\u007F' && ch <= '\u009F')) continue;
            sb.Append(ch);
        }
        var stripped = sb.ToString();
        return stripped.Length <= ExcerptCap ? stripped : stripped[..ToolArgs.SafeCut(stripped, ExcerptCap)];
    }

}
