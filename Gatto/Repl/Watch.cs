using System.Text;
using Gatto.Core.Tools;

namespace Gatto.Repl;

//one folder watched for letters, each new .md handed over as its path and body. one pump alone reads and marks files, so two events for one name cannot both pass
internal sealed class Watch : IDisposable
{
    //the letters delivered since the last typed message before the watch pauses, so two seats writing to each other stop
    public const int K = 6;

    //the first and the longest wait before a locked or empty letter is tried again
    private const int FirstRetryMs = 50;
    private const int LastRetryMs = 2000;

    private readonly Func<Watch, string, bool> _enqueue;
    private readonly Action<string> _row;
    private readonly object _lock = new();
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _kick = new(0, 1);
    private readonly CancellationTokenSource _off = new();
    private FileSystemWatcher? _events;
    private int _count;     //letters taken since the last typed message
    private int _pending;   //letters handed over and not yet taken, so the queue never holds more than the budget
    private bool _paused;

    public string Folder { get; }

    public bool Paused { get { lock (_lock) return _paused; } }

    //the letters taken since the last typed message, read against K
    public int Count { get { lock (_lock) return _count; } }

    //the letters this watch has handed over, by file name in name order
    public IReadOnlyList<string> Seen
    {
        get { lock (_lock) return [.. _seen.Select(Path.GetFileName).OfType<string>().Order(StringComparer.OrdinalIgnoreCase)]; }
    }

    private Watch(string folder, Func<Watch, string, bool> enqueue, Action<string> row)
    {
        Folder = folder;
        _enqueue = enqueue;
        _row = row;
    }

    //creates the folder and its .gitignore when absent and builds the watcher, a throw arms nothing. the enqueue sink drops a letter for a watch that is no longer the armed one
    public static Watch Arm(string folder, Func<Watch, string, bool> enqueue, Action<string> row)
    {
        var full = Path.GetFullPath(folder);
        Directory.CreateDirectory(full);
        var ignore = Path.Combine(full, ".gitignore");
        if (!File.Exists(ignore)) File.WriteAllText(ignore, "*\n");
        var w = new Watch(full, enqueue, row);
        var events = new FileSystemWatcher(full, "*.md") { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName };
        //a buffer overflow loses events and raises Error, so every event only wakes the pump and the pump rescans the folder
        events.Created += (_, _) => w.Kick();
        events.Renamed += (_, _) => w.Kick();
        events.Error += (_, _) => w.Rescan();
        w._events = events;
        return w;
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
                if (_off.IsCancellationRequested || _paused || _count + _pending >= K) return again;
                if (_seen.Contains(path)) continue;
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
        return body.Length > GrepTool.MaxTotalChars
            ? path + "\n\n" + $"this letter holds {body.Length} characters, more than a tool result may carry; read_file reads it"
            : path + "\n\n" + body;
    }

    //how many letters a turn may take now
    public int Budget()
    {
        lock (_lock) return _paused ? 0 : Math.Max(0, K - _count);
    }

    //what was taken, in queue order: a letter counts toward K, a typed message resets the count. the pause says so once
    public void Taken(IEnumerable<EntryKind> kinds)
    {
        var paused = false;
        var freed = false;
        lock (_lock)
        {
            foreach (var kind in kinds)
            {
                if (kind == EntryKind.Watch)
                {
                    _pending = Math.Max(0, _pending - 1);
                    _count++;
                    if (_count >= K && !_paused) _paused = paused = true;
                }
                else if (kind == EntryKind.Plain)
                {
                    freed |= _count > 0;
                    _count = 0;
                }
            }
            freed &= !_paused;
        }
        if (paused) _row($"watch paused after {K} letters, /watch to resume");
        if (freed) Kick();
    }

    public void Typed() => Taken([EntryKind.Plain]);

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
        lock (_lock) _seen.Clear();
    }
}
