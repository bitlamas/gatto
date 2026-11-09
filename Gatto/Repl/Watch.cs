using System.Text;
using Gatto.Core.Tools;

namespace Gatto.Repl;

//one folder watched for letters, each new .md handed over as its path and body by one pump, so two events for one name cannot both pass. a held arm keeps back the letters present at the arm
internal sealed class Watch : IDisposable
{
    //the most letters handed over and not yet taken. with the pause on, also the letters delivered since the last typed message before the watch pauses, so two seats writing to each other stop
    public const int K = 6;

    //the first and the longest wait before a locked or empty letter is tried again
    private const int FirstRetryMs = 50;
    private const int LastRetryMs = 2000;

    private readonly Func<Watch, string, bool> _enqueue;
    private readonly Action<string> _row;
    private readonly object _lock = new();
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);   //the delivery guard by path, a name leaves it when its letter is archived
    private readonly HashSet<string> _handed = new(StringComparer.OrdinalIgnoreCase);   //every name handed over, for the panel, never removed
    private readonly HashSet<string> _held = new(StringComparer.OrdinalIgnoreCase);   //the paths present at a held arm, skipped until the release
    private readonly bool _pause;
    private readonly SemaphoreSlim _kick = new(0, 1);
    private readonly CancellationTokenSource _off = new();
    private FileSystemWatcher? _events;
    private int _count;     //letters taken since the last typed message
    private int _pending;   //letters handed over and not yet taken, so the queue never holds more than the budget
    private bool _paused;

    public string Folder { get; }

    public bool Paused { get { lock (_lock) return _paused; } }

    //whether this watch counts letters toward a pause at all, which the home's inbox_pause key turns on
    public bool Pauses => _pause;

    //the letters present at a held arm and not yet released
    public int Held { get { lock (_lock) return _held.Count; } }

    //the letters taken since the last typed message, read against K
    public int Count { get { lock (_lock) return _count; } }

    //the letters this watch has handed over, by file name in name order
    public IReadOnlyList<string> Seen
    {
        get { lock (_lock) return [.. _handed.Order(StringComparer.OrdinalIgnoreCase)]; }
    }

    //the letter left the inbox, so its name is free: a new letter written under it is a new letter. the kick delivers one that landed before this call, whose event a scan already spent
    public void Archived(string path)
    {
        lock (_lock) _seen.Remove(path);
        Kick();
    }

    private Watch(string folder, Func<Watch, string, bool> enqueue, Action<string> row, bool pause)
    {
        Folder = folder;
        _enqueue = enqueue;
        _row = row;
        _pause = pause;
    }

    //creates the folder and its .gitignore when absent and builds the watcher, a throw arms nothing. the enqueue sink drops a letter for a watch that is no longer the armed one. hold takes the letters present before the first scan can
    public static Watch Arm(string folder, Func<Watch, string, bool> enqueue, Action<string> row, bool hold = false, bool pause = false)
    {
        var full = Path.GetFullPath(folder);
        CreateFolder(full);
        var w = new Watch(full, enqueue, row, pause);
        if (hold)
            foreach (var path in Directory.GetFiles(full, "*.md", SearchOption.TopDirectoryOnly))
                if (Path.GetExtension(path).Equals(".md", StringComparison.OrdinalIgnoreCase)) w._held.Add(path);
        var events = new FileSystemWatcher(full, "*.md") { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName };
        //a buffer overflow loses events and raises Error, so every event only wakes the pump and the pump rescans the folder
        events.Created += (_, _) => w.Kick();
        events.Renamed += (_, _) => w.Kick();
        events.Error += (_, _) => w.Rescan();
        w._events = events;
        return w;
    }

    //the folder and a .gitignore holding * in it, so a git add of the project never takes an inbox or an archive
    internal static void CreateFolder(string full)
    {
        Directory.CreateDirectory(full);
        var ignore = Path.Combine(full, ".gitignore");
        if (!File.Exists(ignore)) File.WriteAllText(ignore, "*\n");
    }

    //starts the events and the first scan off the caller's thread, so arming never waits on a file
    public void Start()
    {
        if (_events is { } events) events.EnableRaisingEvents = true;
        _ = Task.Run(PumpAsync);
    }

    //what a lost event costs: one scan of the folder, which hands over only the names not yet seen
    internal void Rescan() => Kick();

    private void Kick()
    {
        try { _kick.Release(); }
        catch (SemaphoreFullException) { }   //a wake already waits, one scan reads everything
        catch (ObjectDisposedException) { }
    }

    private async Task PumpAsync()
    {
        var retry = FirstRetryMs;
        try
        {
            while (true)
            {
                if (Scan())
                {
                    await _kick.WaitAsync(retry, _off.Token);
                    retry = Math.Min(retry * 2, LastRetryMs);
                }
                else
                {
                    await _kick.WaitAsync(_off.Token);
                    retry = FirstRetryMs;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    //one pass in name order, each unseen letter within the budget read and handed over. true when one was locked or empty and needs another try
    private bool Scan()
    {
        string[] paths;
        try { paths = Directory.GetFiles(Folder, "*.md", SearchOption.TopDirectoryOnly); }
        catch (Exception) { return true; }   //a folder gone or locked for a moment is tried again
        Array.Sort(paths, StringComparer.OrdinalIgnoreCase);
        var again = false;
        foreach (var path in paths)
        {
            if (!Path.GetExtension(path).Equals(".md", StringComparison.OrdinalIgnoreCase)) continue;
            lock (_lock)
            {
                if (_off.IsCancellationRequested || _paused || (_pause ? _count + _pending : _pending) >= K) return again;
                if (_seen.Contains(path) || _held.Contains(path)) continue;
            }
            string? text;
            try { text = Read(path); }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { continue; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { again = true; continue; }
            if (text is null) { again = true; continue; }
            lock (_lock)
            {
                if (_off.IsCancellationRequested || _paused) return again;
                _seen.Add(path);
                _handed.Add(Path.GetFileName(path));
                _pending++;
            }
            if (!_enqueue(this, text))
                lock (_lock) { _seen.Remove(path); _pending--; }
        }
        return again;
    }

    //the path, a blank line and the body. a writer holding the file refuses a shared read, so a letter is never read mid-write, and an empty file is one not written yet
    private static string? Read(string path)
    {
        string body;
        using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var reader = new StreamReader(file, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true))
            body = reader.ReadToEnd();
        if (body.Length == 0) return null;
        //a letter past what a tool result may carry goes as its path, so a file dropped in the folder cannot fill the context by itself
        if (body.Length <= GrepTool.MaxTotalChars) return path + "\n\n" + body;
        var said = $"this letter holds {body.Length} characters, more than a tool result may carry; read_file reads it";
        //the record keeps the inbox path, so the sentence names where the file will be once the turn moves it
        return path + "\n\n" + (Archive.PathFor(path) is { } kept ? said + $"; when this turn ends gatto moves it to {kept}" : said);
    }

    //how many letters a turn may take now
    public int Budget()
    {
        lock (_lock) return _paused ? 0 : Math.Max(0, K - _count);
    }

    //what was taken, in queue order. a typed message releases the held letters once. with the pause on a letter counts toward K, a typed message resets the count and the pause says so once. with it off a taken letter frees its place
    public void Taken(IEnumerable<EntryKind> kinds)
    {
        var paused = false;
        var freed = false;
        var released = false;
        lock (_lock)
        {
            foreach (var kind in kinds)
            {
                if (kind == EntryKind.Watch)
                {
                    _pending = Math.Max(0, _pending - 1);
                    if (!_pause) { freed = true; continue; }
                    _count++;
                    if (_count >= K && !_paused) _paused = paused = true;
                }
                else if (kind == EntryKind.Plain)
                {
                    released |= _held.Count > 0;
                    _held.Clear();
                    freed |= _count > 0;
                    _count = 0;
                }
            }
            freed &= !_paused;
        }
        if (paused) _row($"inbox paused after {K} letters, /inbox to resume");
        if (freed || released) Kick();
    }

    public void Typed() => Taken([EntryKind.Plain]);

    //the held letters become unseen ones, and the scan hands them over in name order within the budget
    public void Release()
    {
        lock (_lock) _held.Clear();
        Kick();
    }

    //a paused watch goes on from zero, and the scan delivers what landed during the pause
    public void Lift()
    {
        lock (_lock)
        {
            _paused = false;
            _count = 0;
        }
        Kick();
    }

    public void Dispose()
    {
        _off.Cancel();
        _events?.Dispose();
        lock (_lock) { _seen.Clear(); _handed.Clear(); }
    }
}
