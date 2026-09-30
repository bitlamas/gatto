using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Gatto.Core.Web;

//one citation event: a URL the model fetched or saw in a search result
public sealed record LedgerEntry(DateTimeOffset At, string Ref, string? Sha256, string? Content, bool SearchOnly);

//record the URLs the model fetches or sees in search, in memory and in a sidecar. the path is asked again on every Record, so a null one keeps it in memory
public class CitationLedger(Func<string?> pathProvider)
{
    private readonly List<LedgerEntry> _entries = new();

    //the recorded entries, virtual on a non-sealed class so a test can watch reads. the gate must read nothing here while disarmed
    public virtual IReadOnlyList<LedgerEntry> Entries => _entries;

    //add the entry in memory and, when the provider gives a path, one JSON line to that file. the hash is lowercase hex of the content, null when there is none
    public void Record(string reference, string? content = null, bool searchOnly = false)
    {
        var sha256 = content is null
            ? null
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
        var entry = new LedgerEntry(DateTimeOffset.UtcNow, reference, sha256, content, searchOnly);
        _entries.Add(entry);

        var path = pathProvider();
        if (path is null) return;

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("at", entry.At);
            w.WriteString("ref", entry.Ref);
            if (entry.Sha256 is not null) w.WriteString("sha256", entry.Sha256); else w.WriteNull("sha256");
            if (entry.Content is not null) w.WriteString("content", entry.Content); else w.WriteNull("content");
            w.WriteBoolean("searchOnly", entry.SearchOnly);
            w.WriteEndObject();
        }
        File.AppendAllText(path, Encoding.UTF8.GetString(ms.ToArray()) + Environment.NewLine);
    }

    //load a ledger file into the in-memory list. a missing file changes nothing and a malformed line is skipped, so a corrupt append cannot block a resume
    public void RestoreFrom(string path)
    {
        if (!File.Exists(path)) return;

        foreach (var line in File.ReadLines(path))
        {
            if (line.Trim().Length == 0) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var el = doc.RootElement;
                var at = el.GetProperty("at").GetDateTimeOffset();
                var reference = el.GetProperty("ref").GetString()!;
                var sha256 = el.TryGetProperty("sha256", out var s) && s.ValueKind == JsonValueKind.String
                    ? s.GetString() : null;
                var content = el.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
                    ? c.GetString() : null;
                var searchOnly = el.TryGetProperty("searchOnly", out var so) && so.ValueKind == JsonValueKind.True;
                _entries.Add(new LedgerEntry(at, reference, sha256, content, searchOnly));
            }
            catch (Exception)
            {
                //skip a malformed line, one corrupt entry must not kill the restore
            }
        }
    }
}
