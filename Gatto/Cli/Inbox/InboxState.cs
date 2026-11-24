using System.Text;
using System.Text.Json;

namespace Gatto.Cli.Inbox;

//the state of the other harness's seats inside gatto's home: one folder holding records, arms, locks, pid files and logs, never a letter. every write moves a temp into place, so a reader never sees half a file
internal sealed class InboxState
{
    //a process is its pid and its start ticks, windows reuses pids. null when the pid names no live process or its start can't be read
    internal readonly record struct Proc(int Pid, long StartTicks)
    {
        public static Proc? Of(int pid)
        {
            try
            {
                using var p = System.Diagnostics.Process.GetProcessById(pid);
                return new Proc(pid, p.StartTime.ToUniversalTime().Ticks);
            }
            catch (Exception) { return null; }
        }
    }

    //one letter handed over: its name, its session, that session's harness process, and the transcript with its length at the handover. an older notice under the same name proves nothing
    internal record DeliveredEntry(string Name, string SessionId, Proc Claude, string TranscriptPath, long From = 0);

    //the session record: the folder fixed at creation, the transcript, the harness process set by every session start, the last known model, the armed inbox and the two name lists
    internal record Session(string ProjectFolder, string TranscriptPath, Proc Claude, string? Model,
        string? Inbox, string[] Known, DeliveredEntry[] Delivered);

    //the arm: one session holding one inbox, the last open wins
    internal record Arm(string Seat, string Inbox, string SessionId, Proc Claude);

    //the six file extensions one session owns, an arm or a temp names nothing
    private static readonly string[] SessionExtensions = [".json", ".lock", ".rec.lock", ".pid", ".log", ".failed"];

    private readonly string _dir;

    public InboxState(string home) => _dir = Path.Combine(home, "claude");

    public Session? ReadSession(string sessionId) => Read<Session>(RecordPath(sessionId));

    //the only way a record changes: read, change, write under the per-session gate. a change returning null writes nothing, an unchanged text writes nothing
    public void Update(string sessionId, Func<Session?, Session?> change)
    {
        Directory.CreateDirectory(_dir);
        using var gate = RecLock(sessionId);
        var path = RecordPath(sessionId);
        var raw = ReadRaw(path);
        var next = change(P<Session>(raw));
        if (next is null) return;
        var text = JsonSerializer.Serialize(next);
        if (raw == text) return;
        WriteRaw(path, text);
    }

    //the arm of one inbox, its last write time carried for the row that measures whether a watcher died since the arm
    public (Arm Arm, DateTime Written)? ArmFor(string inbox)
    {
        foreach (var file in ArmFiles())
        {
            if (Read<Arm>(file) is not { } arm || !SameInbox(arm.Inbox, inbox)) continue;
            return (arm, File.GetLastWriteTime(file));
        }
        return null;
    }

    //every arm naming this session, so an open can drop the session's other arms
    public IReadOnlyList<Arm> ArmsNaming(string sessionId) => [.. Arms().Where(a => a.SessionId == sessionId)];

    //the newest record of one harness process other than the given session, the session a clear left
    public Session? LatestOf(Proc claude, string exceptSession)
    {
        Session? latest = null;
        var at = DateTime.MinValue;
        foreach (var file in FilesOf(_dir, ".json"))
        {
            var leaf = Path.GetFileNameWithoutExtension(file);
            if (!leaf.StartsWith("claude-", StringComparison.Ordinal) || leaf["claude-".Length..] == exceptSession) continue;
            if (Read<Session>(file) is not { } record || !record.Claude.Equals(claude) || WriteTime(file) <= at) continue;
            latest = record;
            at = WriteTime(file);
        }
        return latest;
    }

    //the arm this very process armed before, a reused pid reads as another process
    public Arm? ArmByClaude(Proc claude) => Arms().FirstOrDefault(a => a.Claude.Equals(claude));

    //writing the arm is the takeover, the file name carries the seat and the inbox hash
    public void WriteArm(Arm arm) => WriteRaw(ArmPath(arm), JsonSerializer.Serialize(arm));

    //a refused delete throws, so no row says an arm closed while its file still stands
    public void DeleteArm(Arm arm) => File.Delete(ArmPath(arm));

    //the one-watcher lock, held for the hook's whole life. the operating system frees it when the process ends, killed or not
    public FileStream? TryLock(string sessionId)
    {
        Directory.CreateDirectory(_dir);
        try { return new FileStream(LockPath(sessionId), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return null; }
    }

    //the write time of the running watcher's pid file, null when none stands
    public DateTime? PidWritten(string sessionId)
    {
        var path = Path.Combine(_dir, Leaf(sessionId) + ".pid");
        try { return File.Exists(path) ? File.GetLastWriteTime(path) : null; }
        catch (Exception) { return null; }
    }

    //a hook run marks the session in use, so the sweep's seven days count from its last turn and not from the record's last change
    public void Touch(string sessionId)
    {
        try { File.SetLastWriteTime(RecordPath(sessionId), DateTime.Now); }
        catch (Exception) { }
    }

    //free when a lock opens at once, so no process holds this session's watcher
    public bool LockFree(string sessionId)
    {
        var gate = TryLock(sessionId);
        gate?.Dispose();
        return gate is not null;
    }

    //the running hook writes its pid and start ticks beside the lock, the uninstall reads them to tell a hook from a session
    public void WritePid(string sessionId)
    {
        if (Proc.Of(Environment.ProcessId) is not { } me) return;
        WriteRaw(Path.Combine(_dir, Leaf(sessionId) + ".pid"), me.Pid + "\n" + me.StartTicks);
    }

    public void DeletePid(string sessionId)
    {
        try { File.Delete(Path.Combine(_dir, Leaf(sessionId) + ".pid")); } catch (Exception) { }
    }

    //one line of the session's log, the trace of what a hook did or failed at. a log write never fails a hook
    public void Log(string? sessionId, string line)
    {
        try
        {
            Directory.CreateDirectory(_dir);
            File.AppendAllText(LogPath(sessionId),
                DateTimeOffset.Now.ToString("yyyy-MM-dd'T'HH:mm:sszzz") + " " + line + "\n", new UTF8Encoding(false));
        }
        catch (Exception) { }
    }

    public string LogPath(string? sessionId) => Path.Combine(_dir, Base(sessionId) + ".log");

    //a hook's failure leaves the log line and a failed stamp, the open row reads the stamp to say no watcher runs
    public void LogFailure(string? sessionId, string line)
    {
        Log(sessionId, line);
        try { WriteRaw(Path.Combine(_dir, Base(sessionId) + ".failed"), DateTime.Now.ToString("O")); }
        catch (Exception) { }
    }

    //a watcher that took the lock outlived every failure before it, so the stamp goes
    public void ClearFailure(string? sessionId)
    {
        try { File.Delete(Path.Combine(_dir, Base(sessionId) + ".failed")); }
        catch (Exception) { }
    }

    //true when a hook failed after the given moment, the arm's write time is the usual one
    public bool FailedAfter(string? sessionId, DateTime since)
    {
        var raw = ReadRaw(Path.Combine(_dir, Base(sessionId) + ".failed"));
        return raw is not null
            && DateTime.TryParse(raw.Trim(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var at)
            && at > since;
    }

    //the state folder sheds dead weight: an aged session no arm names with a free lock and a gone process, and an arm whose process and record are gone
    public void Sweep(DateTime now, Func<Proc, bool> alive)
    {
        var aged = now.AddDays(-7);
        var armed = Arms().ToList();
        foreach (var sid in SessionIds())
        {
            var files = SessionFiles(sid);
            if (files.Any(f => File.Exists(f) && WriteTime(f) > aged)) continue;
            var gate = TryLock(sid);
            if (gate is null) continue;
            var dead = false;
            try
            {
                dead = !armed.Any(a => a.SessionId == sid) && !(ReadSession(sid) is { } record && alive(record.Claude));
                if (dead)
                    foreach (var f in files.Where(f => f != LockPath(sid))) { try { File.Delete(f); } catch (Exception) { } }
            }
            finally { gate.Dispose(); }
            //the lock goes after its handle closed, since an open handle without delete sharing refuses the delete
            if (dead) { try { File.Delete(LockPath(sid)); } catch (Exception) { } }
        }
        foreach (var arm in armed)
        {
            if (alive(arm.Claude)) continue;
            var path = RecordPath(arm.SessionId);
            try
            {
                if (File.Exists(path) && File.GetLastWriteTime(path) > aged) continue;
                File.Delete(ArmPath(arm));
            }
            catch (Exception) { }
        }
    }

    //the pids of the hooks this install runs now: the file's process lives, carries the exe's process name, and started at the ticks the file carries
    public static IReadOnlyList<int> HookPids(string home, string exeName)
    {
        var pids = new List<int>();
        foreach (var file in FilesOf(Path.Combine(home, "claude"), ".pid"))
        {
            try
            {
                var lines = File.ReadAllLines(file);
                var pid = int.Parse(lines[0]);
                var ticks = long.Parse(lines[1]);
                using var p = System.Diagnostics.Process.GetProcessById(pid);
                if (p.ProcessName.Equals(exeName, StringComparison.OrdinalIgnoreCase)
                    && p.StartTime.ToUniversalTime().Ticks == ticks) pids.Add(pid);
            }
            catch (Exception) { }   //a file that reads wrong names nothing, the sweep removes it with its session
        }
        return pids;
    }

    //gatto is leaving, so the hooks of its install end. each wait is bounded, a hook that will not die leaves its lock to the operating system
    public static void EndHooks(string home, string exeName)
    {
        foreach (var pid in HookPids(home, exeName))
        {
            try
            {
                using var p = System.Diagnostics.Process.GetProcessById(pid);
                p.Kill();
                p.WaitForExit(2000);
            }
            catch (Exception) { }
        }
    }

    //the per-record gate, held only for the read-change-write. busy past the retries is a row the user sees
    private FileStream RecLock(string sessionId)
    {
        var path = Path.Combine(_dir, Leaf(sessionId) + ".rec.lock");
        var until = Environment.TickCount64 + 2000;
        while (true)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException)
            {
                if (Environment.TickCount64 >= until) throw new IOException("the record of this session is busy");
                Thread.Sleep(50);
            }
        }
    }

    private string Leaf(string sessionId) => "claude-" + sessionId;

    //the log base, an unknown session id keeps its lines in one shared log
    private string Base(string? sessionId) => Leaf(string.IsNullOrEmpty(sessionId) ? "unknown" : sessionId!);

    private string RecordPath(string sessionId) => Path.Combine(_dir, Leaf(sessionId) + ".json");

    private string LockPath(string sessionId) => Path.Combine(_dir, Leaf(sessionId) + ".lock");

    private string ArmPath(Arm arm) => Path.Combine(_dir, $"claude-{arm.Seat}-{Hash(arm.Inbox)}.armed");

    //the arm's inbox anchor: twelve lower-case hex characters of the sha-256 of the full inbox path
    internal static string Hash(string inbox)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(Path.GetFullPath(inbox).ToLowerInvariant()));
        return Convert.ToHexString(bytes).ToLowerInvariant()[..12];
    }

    internal static bool SameInbox(string a, string b) =>
        Path.GetFullPath(a).ToLowerInvariant() == Path.GetFullPath(b).ToLowerInvariant();

    private List<Arm> Arms() => [.. ArmFiles().Select(Read<Arm>).OfType<Arm>()];

    private string[] ArmFiles() => FilesOf(_dir, ".armed");

    //every read opens with shares that never block the move writing the file, a missing or unparsable file reads as null
    private static string? ReadRaw(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(file, new UTF8Encoding(false));
            return reader.ReadToEnd();
        }
        catch (Exception) { return null; }
    }

    private static T? Read<T>(string path) where T : class => P<T>(ReadRaw(path));

    //a file that parses with a member missing reads as absent too, so no caller meets a null its type does not allow
    private static T? P<T>(string? raw) where T : class
    {
        if (raw is null) return null;
        try { return JsonSerializer.Deserialize<T>(raw) is { } value && Whole(value) ? value : null; }
        catch (Exception) { return null; }   //an unparsable file reads as absent
    }

    private static bool Whole(object value) => value switch
    {
        Arm a => a.Seat is not null && a.Inbox is not null && a.SessionId is not null,
        Session s => s.ProjectFolder is not null && s.TranscriptPath is not null
            && s.Known is not null && s.Known.All(n => n is not null)
            && s.Delivered is not null && s.Delivered.All(d => d is { Name: not null, SessionId: not null, TranscriptPath: not null }),
        _ => true,
    };

    private void WriteRaw(string path, string text)
    {
        Directory.CreateDirectory(_dir);
        var tmp = path + ".tmp-" + Guid.NewGuid();
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    //the session ids are read off the file names, only the six session extensions count, so an arm or a temp names nothing
    private List<string> SessionIds()
    {
        var ids = new List<string>();
        foreach (var file in FilesOf(_dir, null))
        {
            var leaf = Path.GetFileName(file);
            if (!leaf.StartsWith("claude-", StringComparison.OrdinalIgnoreCase)) continue;
            var rest = leaf["claude-".Length..];
            var dot = rest.IndexOf('.');
            if (dot <= 0) continue;
            if (!SessionExtensions.Any(e => rest[dot..].Equals(e, StringComparison.OrdinalIgnoreCase))) continue;
            var sid = rest[..dot];
            if (sid.Equals("unknown", StringComparison.OrdinalIgnoreCase)) continue;   //the shared fallback log belongs to no session
            if (!ids.Any(s => s.Equals(sid, StringComparison.OrdinalIgnoreCase))) ids.Add(sid);
        }
        return ids;
    }

    //the six state files one session owns, whether or not each stands on disk
    private List<string> SessionFiles(string sessionId) =>
        [.. SessionExtensions.Select(e => Path.Combine(_dir, Leaf(sessionId) + e))];

    private static DateTime WriteTime(string path)
    {
        try { return File.GetLastWriteTime(path); } catch (Exception) { return DateTime.MaxValue; }   //a file that will not say is treated as new
    }

    //the files of a folder with one exact extension, a missing folder is no files
    private static string[] FilesOf(string dir, string? extension)
    {
        try
        {
            var all = Directory.GetFiles(dir, "*", SearchOption.TopDirectoryOnly);
            return extension is null ? all
                : [.. all.Where(f => Path.GetExtension(f).Equals(extension, StringComparison.OrdinalIgnoreCase))];
        }
        catch (Exception) { return []; }
    }
}
