using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Gatto.Core;

//one timeline per session beside its file, off unless GATTO_TRACE=1, and every call site pays one field read when off
public sealed class Trace : IDisposable
{
    public const string Turn = "turn";
    public const string Shape = "shape";
    public const string Compact = "compact";
    public const string Request = "request";
    public const string Tool = "tool";
    public const string ToolGate = "tool.gate";
    public const string ToolExec = "tool.exec";
    public const string ShellDrain = "shell.drain";
    public const string ToolResult = "tool.result";
    public const string Hook = "hook";
    public const string SessionWrite = "session.write";
    public const string Resume = "resume";
    public const string Launch = "launch";
    public const string Delta = "delta";

    //the one disabled instance, so a caller with no trace still holds a non-null one
    public static readonly Trace Off = new();

    public readonly bool Enabled;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private readonly long _start;
    private readonly DateTimeOffset _wallStart;
    private readonly Func<string?> _path = () => null;
    private readonly Func<string> _session = () => "";
    private readonly string _build = "";
    private readonly int _pid;
    private readonly object _gate = new();
    private readonly List<string> _pending = new();
    private long _nextId;
    private int _afterResume;
    private string? _openPath;

    private Trace() { }

    //the path is the sidecar of the live session file, null while none exists, and read again at each flush
    public Trace(long startTimestamp, DateTimeOffset wallStart, Func<string?> sidecarPath, Func<string> sessionId, string build, int pid)
    {
        Enabled = true;
        _start = startTimestamp;
        _wallStart = wallStart;
        _path = sidecarPath;
        _session = sessionId;
        _build = build;
        _pid = pid;
    }

    public Span Open(string name, long parent = 0) =>
        Enabled ? new Span(this, name, Interlocked.Increment(ref _nextId), parent, Stopwatch.GetTimestamp()) : default;

    //a span that began before the trace existed, the launch span, which starts at process entry
    public Span OpenFrom(string name, long startTimestamp, long parent = 0) =>
        Enabled ? new Span(this, name, Interlocked.Increment(ref _nextId), parent, startTimestamp) : default;

    //the next request span reads this once, so the reader joins the first request after a resume to its resume span
    public void MarkResume()
    {
        if (Enabled) Volatile.Write(ref _afterResume, 1);
    }

    public bool TakeResume() => Enabled && Interlocked.Exchange(ref _afterResume, 0) == 1;

    internal long Micros(long timestamp) => (timestamp - _start) * 1_000_000 / Stopwatch.Frequency;

    internal void Add(string line)
    {
        lock (_gate) _pending.Add(line);
    }

    //after each session write and at exit, and a failed write drops the lines, since an instrument never ends a turn
    public void Flush()
    {
        if (!Enabled) return;
        //read outside the gate, the path takes the session store's lock and a span closing under that lock takes this gate
        if (_path() is not { } path) return;
        var session = _session();
        lock (_gate)
        {
            //opened and closed per flush, once a turn, so a reader can open the file while the session runs
            try
            {
                using var writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read), Utf8NoBom) { NewLine = "\n" };
                if (path != _openPath)
                {
                    _openPath = path;
                    writer.WriteLine(Header(session));
                }
                foreach (var line in _pending) writer.WriteLine(line);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            _pending.Clear();
        }
    }

    private string Header(string session)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteNumber("trace", 1);
            w.WriteString("session", session);
            w.WriteString("build", _build);
            w.WriteString("t0", _wallStart.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture));
            w.WriteNumber("pid", _pid);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public void Dispose() => Flush();
}

//one span, written with its start and duration when it closes. default is the off span, and every member is a no-op on it
public readonly struct Span : IDisposable
{
    private readonly Trace? _trace;
    private readonly string? _name;
    private readonly long _parent;
    private readonly long _startTs;
    private readonly StringBuilder? _fields;

    public long Id { get; }

    internal Span(Trace trace, string name, long id, long parent, long startTs)
    {
        _trace = trace;
        _name = name;
        Id = id;
        _parent = parent;
        _startTs = startTs;
        _fields = new StringBuilder();
    }

    public void Set(string key, long value)
    {
        if (_fields is null) return;
        _fields.Append(",\"").Append(key).Append("\":").Append(value.ToString(CultureInfo.InvariantCulture));
    }

    //a string field holds a member of a closed set, a span or tool name, never model text or file content
    public void Set(string key, string value)
    {
        if (_fields is null) return;
        _fields.Append(",\"").Append(key).Append("\":").Append(JsonSerializer.Serialize(value));
    }

    //a number from the server's own JSON, written as its token so a fraction keeps its digits, and anything else is left out
    public void Set(string key, JsonElement number)
    {
        if (_fields is null || number.ValueKind != JsonValueKind.Number) return;
        _fields.Append(",\"").Append(key).Append("\":").Append(number.GetRawText());
    }

    public void Dispose()
    {
        if (_trace is null) return;
        var end = Stopwatch.GetTimestamp();
        var t = _trace.Micros(_startTs);
        var line = new StringBuilder("{\"t\":").Append(t.ToString(CultureInfo.InvariantCulture))
            .Append(",\"d\":").Append((_trace.Micros(end) - t).ToString(CultureInfo.InvariantCulture))
            .Append(",\"s\":\"").Append(_name).Append("\",\"id\":").Append(Id.ToString(CultureInfo.InvariantCulture))
            .Append(",\"p\":").Append(_parent.ToString(CultureInfo.InvariantCulture))
            .Append(_fields).Append('}');
        _trace.Add(line.ToString());
    }
}
