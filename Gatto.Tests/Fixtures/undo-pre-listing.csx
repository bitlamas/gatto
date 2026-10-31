//undo, bundled with gatto. a snapshot of a file's bytes before every write the tools make, and /undo to put one back.
//your copy is your code. delete this file and undo is off.
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Gatto.Core.Tools;

const int MaxSnapshots = 500;
const long MaxBytes = 200L * 1024 * 1024;
const long MaxFileBytes = 50L * 1024 * 1024;
const int KeepNewest = 10;
const int ListCount = 10;

var cwd = Path.GetFullPath(Gatto.Cwd).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
var root = Path.Combine(Gatto.Home, "undo");
var me = Process.GetCurrentProcess();
var myFolder = Path.Combine(root, $"{me.StartTime:yyyyMMdd-HHmmss}-{me.Id}");
var myIndex = Path.Combine(myFolder, "index.jsonl");
var n = 0;
//the n of each row a call wrote, held from its tool_call to its tool_result. the call id is the server's and may repeat, so it never reaches the disk
var pending = new Dictionary<string, List<int>>(StringComparer.Ordinal);
var sync = new object();

//a full path, a leading ~ the user's folder as gatto's file tools read it, and whether it is under the working folder, compared whole segments
Func<string, string> full = p =>
{
    if (p == "~" || p.StartsWith("~\\", StringComparison.Ordinal) || p.StartsWith("~/", StringComparison.Ordinal))
        p = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + p[1..];
    return Path.GetFullPath(Path.IsPathRooted(p) ? p : Path.Combine(cwd, p));
};
Func<string, bool> inside = f =>
    f.Equals(cwd, StringComparison.OrdinalIgnoreCase)
    || f.StartsWith(cwd + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

Action<string> appendLine = line =>
{
    Directory.CreateDirectory(myFolder);
    File.AppendAllText(myIndex, line + "\n", new UTF8Encoding(false));
};

Func<Dictionary<string, object?>, string> json = o =>
{
    using var ms = new MemoryStream();
    using (var w = new Utf8JsonWriter(ms))
    {
        w.WriteStartObject();
        foreach (var (k, v) in o)
        {
            if (v is null) continue;
            switch (v)
            {
                case string s: w.WriteString(k, s); break;
                case int i: w.WriteNumber(k, i); break;
                case long l: w.WriteNumber(k, l); break;
                case bool b: w.WriteBoolean(k, b); break;
                case IEnumerable<int> list: w.WriteStartArray(k); foreach (var x in list) w.WriteNumberValue(x); w.WriteEndArray(); break;
            }
        }
        w.WriteEndObject();
    }
    return Encoding.UTF8.GetString(ms.ToArray());
};

//what load measured across the tree, plus this process's rows and bytes since, so an append reads nothing until the sum passes a cap
long measuredBytes = 0;
int measuredCount = 0;
long mineBytes = 0;
int mineCount = 0;

//one row: a copy of the file, or the absent marker. a fault logs one line and the call proceeds
Action<string, string, string> snapshot = (callId, tool, given) =>
{
    string f;
    try { f = full(given); }
    catch (Exception ex) { Gatto.Log($"undo: {given}: {ex.Message}"); return; }
    //a path outside the folder, a directory or a file above the cap is past the limits the description states once, so it is passed over in silence
    if (!inside(f) || Directory.Exists(f)) return;
    lock (sync)
    {
        try
        {
            var row = new Dictionary<string, object?>
            {
                ["n"] = n + 1,
                ["ts"] = DateTimeOffset.Now.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture),
                ["tool"] = tool, ["path"] = given, ["full"] = f,
            };
            if (File.Exists(f))
            {
                var len = new FileInfo(f).Length;
                if (len > MaxFileBytes) return;
                var name = $"{n + 1}-{Path.GetFileName(f)}";
                Directory.CreateDirectory(myFolder);
                File.Copy(f, Path.Combine(myFolder, name), overwrite: true);
                row["bytes"] = len;
                row["file"] = name;
            }
            else row["absent"] = true;
            n++;
            appendLine(json(row));
            if (!pending.TryGetValue(callId, out var ns)) pending[callId] = ns = new List<int>();
            ns.Add(n);
            mineCount++;
            if (row.TryGetValue("bytes", out var added) && added is long addedBytes) mineBytes += addedBytes;
        }
        catch (Exception ex) { Gatto.Log($"undo: {given}: {ex.Message}"); }
    }
    trim(false);
};

Func<JsonElement, string, string?> str = (a, key) =>
    a.ValueKind == JsonValueKind.Object && a.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

Func<string, IReadOnlyList<(string Path, string Why)>> shellTargets = null!;

Gatto.On("tool_call", payload =>
{
    var call = payload.Call;
    if (call is null) return Task.CompletedTask;
    JsonElement args;
    try { args = JsonDocument.Parse(call.ArgumentsJson).RootElement; }
    catch { return Task.CompletedTask; }
    if (call.Name is "write_file" or "edit_file")
    {
        if (str(args, "path") is { } p) snapshot(call.Id, call.Name, p);
    }
    else if (call.Name == "shell" && str(args, "command") is { } cmd)
    {
        //a throw here would block the user's command, and the net never blocks
        IReadOnlyList<(string Path, string Why)> targets;
        try { targets = shellTargets(cmd); }
        catch (Exception ex) { Gatto.Log($"undo: shell: {ex.Message}"); return Task.CompletedTask; }
        foreach (var (p, why) in targets)
            if (why.Length == 0) snapshot(call.Id, "shell", p);
    }
    return Task.CompletedTask;
});

//the state of a call's rows: blocked when nothing ran, failed when a file tool erred, ran otherwise. a result with no rows is ignored
Gatto.On("tool_result", payload =>
{
    var call = payload.Call;
    var result = payload.Result;
    if (call is null || result is null) return Task.CompletedTask;
    lock (sync)
    {
        if (!pending.Remove(call.Id, out var ns)) return Task.CompletedTask;
        var state = result.Text.StartsWith("blocked:", StringComparison.Ordinal) ? "blocked"
            : result.IsError && call.Name != "shell" ? "failed"
            : "ran";
        try { appendLine(json(new Dictionary<string, object?> { ["mark"] = ns, ["state"] = state })); }
        catch (Exception ex) { Gatto.Log($"undo: mark: {ex.Message}"); }
    }
    return Task.CompletedTask;
});

//a process folder's rows with their marks folded on, a half-written last line skipped
Func<string, List<Dictionary<string, JsonElement>>> readFolder = folder =>
{
    var rows = new List<Dictionary<string, JsonElement>>();
    var marks = new Dictionary<int, string>();
    var index = Path.Combine(folder, "index.jsonl");
    if (!File.Exists(index)) return rows;
    string[] lines;
    try { lines = File.ReadAllLines(index); } catch { return rows; }
    foreach (var line in lines)
    {
        if (line.Length == 0) continue;
        try
        {
            var e = JsonDocument.Parse(line).RootElement;
            if (e.TryGetProperty("mark", out var m))
            {
                var state = e.GetProperty("state").GetString()!;
                var ns = m.EnumerateArray().Select(x => x.GetInt32()).ToList();
                foreach (var x in ns) marks[x] = state;
                continue;
            }
            var row = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var p in e.EnumerateObject()) row[p.Name] = p.Value;
            //a row the rest of the script cannot read is skipped here, so a hand-edited line never breaks the listing or the trim
            if (!(row.TryGetValue("n", out var rn) && rn.ValueKind == JsonValueKind.Number && rn.TryGetInt32(out _))) continue;
            if (!(row.TryGetValue("ts", out var rt) && rt.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(rt.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out _))) continue;
            if (!(row.TryGetValue("tool", out var rk) && rk.ValueKind == JsonValueKind.String)) continue;
            //the writer stores a resolved path, so one holding .. or a relative piece was not written here and could reach outside the folder
            if (!(row.TryGetValue("full", out var rf) && rf.ValueKind == JsonValueKind.String && rf.GetString() is { } fullPath
                && Path.IsPathFullyQualified(fullPath) && fullPath == Path.GetFullPath(fullPath))) continue;
            //a copy has its size and its name together, and the name is the bare one this script gives, so a trim never deletes outside the folder
            var hasFile = row.TryGetValue("file", out var rc);
            if (hasFile != row.TryGetValue("bytes", out var rb)) continue;
            if (hasFile && !(rb.ValueKind == JsonValueKind.Number && rb.TryGetInt64(out var copied) && copied >= 0)) continue;
            if (hasFile && !(rc.ValueKind == JsonValueKind.String && rc.GetString() is { } copy
                && copy == Path.GetFileName(copy) && copy.StartsWith($"{rn.GetInt32()}-", StringComparison.Ordinal))) continue;
            rows.Add(row);
        }
        catch { }
    }
    foreach (var row in rows)
        if (row.TryGetValue("n", out var rn) && marks.TryGetValue(rn.GetInt32(), out var st))
            row["state"] = JsonDocument.Parse($"\"{st}\"").RootElement;
    return rows;
};

//a folder is live when its pid runs and started within 2 seconds of the stamp. an unreadable start time counts as live, so nothing live is ever trimmed
Func<string, bool> live = folder =>
{
    var name = Path.GetFileName(folder);
    var dash = name.LastIndexOf('-');
    if (dash < 0 || !int.TryParse(name[(dash + 1)..], out var pid)) return false;
    if (!DateTime.TryParseExact(name[..dash], "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var stamp)) return false;
    try
    {
        using var p = Process.GetProcessById(pid);
        try { return Math.Abs((p.StartTime - stamp).TotalSeconds) < 2; }
        catch { return true; }
    }
    catch { return false; }
};

//oldest first across dead folders, then this folder down to its newest rows, until both caps hold
void trim(bool force)
{
    lock (sync)
    {
        try
        {
            if (!force && measuredCount + mineCount <= MaxSnapshots && measuredBytes + mineBytes <= MaxBytes) return;
            if (!Directory.Exists(root)) return;
            var all = new List<(string Folder, Dictionary<string, JsonElement> Row, bool Mine)>();
            foreach (var folder in Directory.GetDirectories(root))
            {
                var mine = folder.Equals(myFolder, StringComparison.OrdinalIgnoreCase);
                if (!mine && live(folder)) continue;
                foreach (var row in readFolder(folder)) all.Add((folder, row, mine));
            }
            long bytes = all.Sum(a => a.Row.TryGetValue("bytes", out var b) ? b.GetInt64() : 0L);
            var count = all.Count;
            if (count <= MaxSnapshots && bytes <= MaxBytes) { measuredCount = count; measuredBytes = bytes; mineCount = 0; mineBytes = 0; return; }
            var mineLeft = all.Count(a => a.Mine);
            var ordered = all.OrderBy(a => a.Mine ? 1 : 0).ThenBy(a => a.Row["ts"].GetString(), StringComparer.Ordinal).ThenBy(a => a.Row["n"].GetInt32()).ToList();
            var drop = new List<(string Folder, Dictionary<string, JsonElement> Row, bool Mine)>();
            foreach (var a in ordered)
            {
                if (count <= MaxSnapshots && bytes <= MaxBytes) break;
                if (a.Mine && mineLeft <= KeepNewest) break;
                drop.Add(a);
                count--;
                if (a.Mine) mineLeft--;
                bytes -= a.Row.TryGetValue("bytes", out var b) ? b.GetInt64() : 0L;
            }
            foreach (var group in drop.GroupBy(d => d.Folder))
            {
                var gone = new HashSet<int>(group.Select(d => d.Row["n"].GetInt32()));
                foreach (var d in group)
                    if (d.Row.TryGetValue("file", out var f)) { try { File.Delete(Path.Combine(group.Key, f.GetString()!)); } catch { } }
                var index = Path.Combine(group.Key, "index.jsonl");
                //the rows that stay, then only the marks that still name one of them, so a folder of marks alone never lingers
                var keptRows = new List<string>();
                var keptNs = new HashSet<int>();
                var markLines = new List<(string Line, int[] Ns)>();
                foreach (var l in File.ReadAllLines(index))
                {
                    try
                    {
                        var e = JsonDocument.Parse(l).RootElement;
                        if (e.TryGetProperty("mark", out var m)) { markLines.Add((l, m.EnumerateArray().Select(x => x.GetInt32()).ToArray())); continue; }
                        var rowN = e.GetProperty("n").GetInt32();
                        if (gone.Contains(rowN)) continue;
                        keptRows.Add(l);
                        keptNs.Add(rowN);
                    }
                    catch { }
                }
                var kept = keptRows.Concat(markLines.Where(m => m.Ns.Any(keptNs.Contains)).Select(m => m.Line)).ToList();
                if (keptRows.Count == 0 && !group.Key.Equals(myFolder, StringComparison.OrdinalIgnoreCase)) Directory.Delete(group.Key, recursive: true);
                else File.WriteAllText(index, string.Join("\n", kept) + (kept.Count > 0 ? "\n" : ""), new UTF8Encoding(false));
            }
            measuredCount = count;
            measuredBytes = bytes;
            mineCount = 0;
            mineBytes = 0;
        }
        catch (Exception ex) { Gatto.Log($"undo: retention: {ex.Message}"); }
    }
}

//the write forms, keyed by command head in lower case: which arguments are targets
var pathHeads = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
{
    ["set-content"] = "p", ["sc"] = "p", ["add-content"] = "p", ["ac"] = "p", ["out-file"] = "p",
    ["tee-object"] = "p", ["tee"] = "p", ["new-item"] = "p", ["ni"] = "p", ["clear-content"] = "p", ["clc"] = "p",
    ["remove-item"] = "all", ["rm"] = "all", ["del"] = "all", ["erase"] = "all", ["ri"] = "all", ["rd"] = "all", ["rmdir"] = "all",
    ["rename-item"] = "p", ["rni"] = "p", ["ren"] = "p",
    ["copy-item"] = "d", ["cp"] = "d", ["copy"] = "d",
    ["move-item"] = "pd", ["mv"] = "pd", ["move"] = "pd",
};
var noValue = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    { "-nonewline", "-force", "-append", "-passthru", "-raw", "-noclobber", "-recurse", "-r", "-whatif", "-confirm", "-container", "-verbose", "-debug" };
var gitWrites = new HashSet<string>(StringComparer.Ordinal) { "checkout", "restore", "reset", "clean" };

//one statement into tokens: a quoted string, a here-string or a parenthesised group stays one token, parsing stops at an unquoted ; | newline } or )
Func<string, List<string>> tokens = text =>
{
    var list = new List<string>();
    var sb = new StringBuilder();
    var i = 0;
    Action flush = () => { if (sb.Length > 0) { list.Add(sb.ToString()); sb.Clear(); } };
    while (i < text.Length)
    {
        var c = text[i];
        if (c == '@' && i + 1 < text.Length && (text[i + 1] == '\'' || text[i + 1] == '"'))
        {
            var q = text[i + 1];
            var end = text.IndexOf(q + "@", i + 2, StringComparison.Ordinal);
            if (end < 0) end = text.Length - 2;
            sb.Append(text, i, Math.Min(text.Length, end + 2) - i);
            i = Math.Min(text.Length, end + 2);
            continue;
        }
        if (c == '\'' || c == '"')
        {
            var end = text.IndexOf(c, i + 1);
            if (end < 0) end = text.Length - 1;
            sb.Append(text, i, end + 1 - i);
            i = end + 1;
            continue;
        }
        if (c == '(')
        {
            var depth = 0;
            var start = i;
            while (i < text.Length) { if (text[i] == '(') depth++; else if (text[i] == ')' && --depth == 0) { i++; break; } i++; }
            sb.Append(text, start, i - start);
            continue;
        }
        if (c == ';' || c == '|' || c == '\n' || c == '}' || c == ')') break;
        if (c == '&' && i + 1 < text.Length && text[i + 1] == '&') break;
        if (char.IsWhiteSpace(c)) { flush(); i++; continue; }
        sb.Append(c);
        i++;
    }
    flush();
    return list;
};

//the statements of a command: each run of text after a ; | && { ( or newline, trimmed, so every command head is seen
Func<string, List<string>> statements = text =>
{
    var list = new List<string>();
    var i = 0;
    while (i < text.Length)
    {
        while (i < text.Length && (char.IsWhiteSpace(text[i]) || text[i] is ';' or '|' or '&' or '{' or '(' or '}' or ')')) i++;
        if (i >= text.Length) break;
        var start = i;
        var depthQ = '\0';
        while (i < text.Length)
        {
            var c = text[i];
            if (depthQ != '\0') { if (c == depthQ) depthQ = '\0'; i++; continue; }
            if (c == '\'' || c == '"') { depthQ = c; i++; continue; }
            if (c is ';' or '|' or '\n' or '{' or '}' or ')') break;
            if (c == '&' && i + 1 < text.Length && text[i + 1] == '&') break;
            i++;
        }
        list.Add(text[start..i].Trim());
    }
    return list;
};

//a target token's value: a $ or a leading ( or @ unreadable, a comma list split outside quotes, then one pair of quotes stripped from each
Func<string, IEnumerable<string>> values = tok =>
{
    if (tok.Length == 0 || tok[0] == '(' || tok[0] == '@' || tok.Contains('$')) return Array.Empty<string>();
    var parts = new List<string>();
    var sb = new StringBuilder();
    char? quote = null;
    foreach (var c in tok)
    {
        if (quote is { } q) { if (c == q) quote = null; sb.Append(c); continue; }
        if (c == '\'' || c == '"') { quote = c; sb.Append(c); continue; }
        if (c == ',') { parts.Add(sb.ToString()); sb.Clear(); continue; }
        sb.Append(c);
    }
    parts.Add(sb.ToString());
    return parts.Select(s => s.Trim()).Where(s => s.Length > 0)
        .Select(s => s.Length >= 2 && (s[0] == '\'' || s[0] == '"') && s[^1] == s[0] ? s[1..^1] : s);
};

//a redirection's target in one statement: > or >> not preceded by = - or >, not a 2>& stream join, and not followed by =, the target a path and not a stream or the null device
Func<string, string?> redirectTarget = st =>
{
    for (var i = 0; i < st.Length; i++)
    {
        if (st[i] != '>') continue;
        if (i > 0 && st[i - 1] is '=' or '-' or '>') continue;
        if (i > 0 && st[i - 1] == '2' && i + 1 < st.Length && st[i + 1] == '&') continue;
        var j = i + 1;
        if (j < st.Length && st[j] == '>') j++;
        if (j < st.Length && st[j] == '=') continue;
        while (j < st.Length && char.IsWhiteSpace(st[j])) j++;
        var start = j;
        if (start < st.Length && (st[start] == '\'' || st[start] == '"'))
        {
            var end = st.IndexOf(st[start], start + 1);
            return end < 0 ? null : st[(start + 1)..end];
        }
        while (j < st.Length && !char.IsWhiteSpace(st[j]) && st[j] is not ('|' or ';' or '&' or '(' or ')' or '{' or '}' or '<' or '>')) j++;
        var target = st[start..j];
        if (target.Length == 0 || target[0] == '&' || target.Contains('$')) return null;
        if (target.Equals("NUL", StringComparison.OrdinalIgnoreCase) || target == "/dev/null") return null;
        if (!(char.IsLetterOrDigit(target[0]) || target[0] is '.' or '\\' or '/' or '~')) return null;
        return target;
    }
    return null;
};

//every target one shell command names, by the write forms, the redirections and the git and directory exclusions
shellTargets = command =>
{
    var found = new List<(string Path, string Why)>();
    foreach (var st in statements(command))
    {
        var toks = tokens(st);
        if (toks.Count == 0) continue;
        var head = toks[0];
        if (head.Equals("git", StringComparison.OrdinalIgnoreCase))
        {
            if (toks.Count > 1 && gitWrites.Contains(toks[1])) found.Add((st, "git"));
            else if (redirectTarget(st) is { } gr) found.Add((gr, ""));
            continue;
        }
        var bracket = head.IndexOf("::", StringComparison.Ordinal);
        if (bracket > 0 && (head.StartsWith("[IO.File]", StringComparison.OrdinalIgnoreCase) || head.StartsWith("[System.IO.File]", StringComparison.OrdinalIgnoreCase)))
        {
            var member = head[(bracket + 2)..];
            if (member.StartsWith("Write", StringComparison.OrdinalIgnoreCase) || member.StartsWith("Append", StringComparison.OrdinalIgnoreCase))
            {
                var open = st.IndexOf('(');
                if (open >= 0)
                {
                    var inner = tokens(st[(open + 1)..].TrimStart());
                    var first = inner.Count > 0 ? inner[0].TrimEnd(',') : "";
                    foreach (var v in values(first)) found.Add((v, ""));
                }
            }
            continue;
        }
        if (pathHeads.TryGetValue(head, out var kind))
        {
            var positional = new List<string>();
            var named = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var directory = false;
            for (var i = 1; i < toks.Count; i++)
            {
                var t = toks[i];
                if (t.StartsWith('-') && t.Length > 1 && !char.IsDigit(t[1]))
                {
                    //a switch with its value attached, -Confirm:$false or -Recurse:$true, takes no token
                    if (noValue.Contains(t) || t.Contains(':')) { continue; }
                    if (i + 1 < toks.Count)
                    {
                        if (t.Equals("-ItemType", StringComparison.OrdinalIgnoreCase) && toks[i + 1].Equals("Directory", StringComparison.OrdinalIgnoreCase)) directory = true;
                        named[t] = toks[i + 1];
                        i++;
                    }
                    continue;
                }
                positional.Add(t);
            }
            if (directory) { found.Add((positional.Count > 0 ? positional[0] : head, "directory")); continue; }
            string? pathTok = named.TryGetValue("-Path", out var np) ? np : named.TryGetValue("-LiteralPath", out var nl) ? nl : named.TryGetValue("-FilePath", out var nf) ? nf : null;
            string? destTok = named.TryGetValue("-Destination", out var nd) ? nd : null;
            var pos = 0;
            if (pathTok is null && positional.Count > pos) pathTok = positional[pos++];
            if (destTok is null && kind is "d" or "pd" && positional.Count > pos) destTok = positional[pos++];
            if (kind == "all")
            {
                foreach (var p in positional) foreach (var v in values(p)) found.Add((v, ""));
                if (named.TryGetValue("-Path", out var ap)) foreach (var v in values(ap)) found.Add((v, ""));
                if (named.TryGetValue("-LiteralPath", out var al)) foreach (var v in values(al)) found.Add((v, ""));
                continue;
            }
            if (kind is "p" or "pd" && pathTok is not null) foreach (var v in values(pathTok)) found.Add((v, ""));
            if (kind is "d" or "pd" && destTok is not null) foreach (var v in values(destTok)) found.Add((v, ""));
            continue;
        }
        if (redirectTarget(st) is { } r) found.Add((r, ""));
    }
    //a wildcard names no one file, so it is logged and never recorded as a missing one
    return found.Select(f => f.Why.Length == 0 && f.Path.IndexOfAny(['*', '?']) >= 0 ? (f.Path, "wildcard") : f).ToList();
};

trim(true);

//a size in B, KB or MB, whole numbers below a thousand of the unit
Func<long, string> size = b => b < 1024 ? $"{b} B" : b < 1024 * 1024 ? $"{b / 1024} KB" : $"{b / (1024 * 1024)} MB";

//the rows /undo may list, newest first: ran, or unmarked in this process or a dead one. a live sibling's unmarked row is a call in flight
Func<List<(string Folder, Dictionary<string, JsonElement> Row, bool Unknown, bool Mine)>> listing = () =>
{
    var rows = new List<(string Folder, Dictionary<string, JsonElement> Row, bool Unknown, bool Mine)>();
    if (!Directory.Exists(root)) return rows;
    foreach (var folder in Directory.GetDirectories(root))
    {
        var mine = folder.Equals(myFolder, StringComparison.OrdinalIgnoreCase);
        var alive = !mine && live(folder);
        foreach (var row in readFolder(folder))
        {
            if (!row.TryGetValue("full", out var f) || !inside(f.GetString()!)) continue;
            var state = row.TryGetValue("state", out var s) ? s.GetString() : null;
            if (state == "ran") rows.Add((folder, row, false, mine));
            else if (state is null && !alive) rows.Add((folder, row, true, mine));
        }
    }
    return rows.OrderByDescending(r => r.Row["ts"].GetString(), StringComparer.Ordinal).ThenByDescending(r => r.Row["n"].GetInt32()).Take(ListCount).ToList();
};

Func<string, string> relative = f => Path.GetRelativePath(cwd, f);

Func<(string Folder, Dictionary<string, JsonElement> Row, bool Unknown, bool Mine), int, string> listLine = (r, i) =>
{
    var ts = DateTimeOffset.Parse(r.Row["ts"].GetString()!, CultureInfo.InvariantCulture);
    var when = ts.Date == DateTimeOffset.Now.Date ? ts.ToString("HH:mm:ss", CultureInfo.InvariantCulture) : ts.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    var tool = r.Row["tool"].GetString() + (r.Unknown ? "?" : "");
    var what = r.Row.TryGetValue("bytes", out var b) ? size(b.GetInt64()) : "absent";
    //the other process's start, read from its folder name and shown as a time. a handler returns text, so it cannot be dim
    var start = "";
    if (!r.Mine && DateTime.TryParseExact(Path.GetFileName(r.Folder)[..15], "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var began))
        start = "  " + (began.Date == DateTime.Now.Date ? began.ToString("HH:mm:ss", CultureInfo.InvariantCulture) : began.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
    return $"{i} {when} {tool} {relative(r.Row["full"].GetString()!)} {what}{start}";
};

//the file's current bytes go into this process's folder first, so a restore is itself undoable, then the snapshot's bytes come back
Func<(string Folder, Dictionary<string, JsonElement> Row, bool Unknown, bool Mine), string> restore = r =>
{
    var target = r.Row["full"].GetString()!;
    var ts = DateTimeOffset.Parse(r.Row["ts"].GetString()!, CultureInfo.InvariantCulture).ToString("HH:mm:ss", CultureInfo.InvariantCulture);
    var callId = $"undo-{Guid.NewGuid():N}";
    snapshot(callId, "undo", target);
    lock (sync)
    {
        if (pending.Remove(callId, out var ns)) appendLine(json(new Dictionary<string, object?> { ["mark"] = ns, ["state"] = "ran" }));
    }
    if (r.Row.TryGetValue("file", out var f))
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(Path.Combine(r.Folder, f.GetString()!), target, overwrite: true);
        return $"restored {relative(target)} to {ts} ({size(r.Row["bytes"].GetInt64())})";
    }
    if (File.Exists(target)) File.Delete(target);
    return $"deleted {relative(target)}, it did not exist before {ts}";
};

Gatto.Command("/undo", "[n|path]", "list snapshots for this folder, or restore one", (arg, ct) =>
{
    try
    {
        var rows = listing();
        var a = arg.Trim();
        if (a.Length == 0)
            return Task.FromResult(rows.Count == 0 ? "no snapshots for this folder" : string.Join("\n", rows.Select((r, i) => listLine(r, i + 1))));
        if (int.TryParse(a, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var pick))
            return Task.FromResult(pick >= 1 && pick <= rows.Count ? restore(rows[pick - 1]) : $"no snapshot {a} in the list");
        //a path with a space is typed quoted, as the shell asks, so one pair of quotes comes off
        var unquoted = a.Length >= 2 && (a[0] == '"' || a[0] == '\'') && a[^1] == a[0] ? a[1..^1] : a;
        string wanted;
        try { wanted = full(unquoted); } catch { return Task.FromResult($"no snapshot of {a}"); }
        var byPath = rows.FirstOrDefault(r => r.Row["full"].GetString()!.Equals(wanted, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(byPath.Row is null ? $"no snapshot of {a}" : restore(byPath));
    }
    catch (Exception ex) { return Task.FromResult($"undo: {ex.Message}"); }
});
