using System.Text;
using System.Text.Json;

namespace Gatto.Repl.Input;

//the persisted input history, one JSON string per line with the oldest first
public sealed class History
{
    private const int Cap = 500;
    private readonly string _path;
    private readonly List<string> _entries = new();
    private int _walk = -1;            //the index into _entries during history navigation, -1 at rest
    private string? _draft;

    public IReadOnlyList<string> Entries => _entries;

    public History(string filePath)
    {
        _path = filePath;
        if (!File.Exists(_path)) return;
        try
        {
            foreach (var line in File.ReadAllLines(_path, Encoding.UTF8))
            {
                if (line.Length == 0) continue;
                try { if (JsonSerializer.Deserialize<string>(line) is { } s) _entries.Add(s); }
                catch (JsonException) { }
            }
        }
        catch (Exception) { }          //an unreadable file just leaves the history empty
    }

    public void Record(string entry)
    {
        if (entry.Length == 0) return;
        if (_entries.Count > 0 && _entries[^1] == entry) { ResetWalk(); return; }
        _entries.Add(entry);
        if (_entries.Count > Cap) _entries.RemoveRange(0, _entries.Count - Cap);
        ResetWalk();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllLines(_path, _entries.Select(e => JsonSerializer.Serialize(e)), Encoding.UTF8);
        }
        catch (Exception) { }          //a failed save loses history, which is acceptable
    }

    public string? Older(string currentDraft)
    {
        if (_entries.Count == 0) return null;
        if (_walk == -1) { _draft = currentDraft; _walk = _entries.Count - 1; return _entries[_walk]; }
        if (_walk == 0) return null;
        return _entries[--_walk];
    }

    public string? Newer()
    {
        if (_walk == -1) return null;
        if (_walk >= _entries.Count - 1) { _walk = -1; var d = _draft; _draft = null; return d; }
        return _entries[++_walk];
    }

    public void ResetWalk() { _walk = -1; _draft = null; }
}
