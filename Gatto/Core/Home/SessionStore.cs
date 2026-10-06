using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Gatto.Core.Client;
using Gatto.Core.Loop;

namespace Gatto.Core.Home;

public sealed class SessionStore(string homePath, string cwd)
{
    //the session schema version, written on the leading system record every session has, rather than a line of its own
    public const int SchemaVersion = 1;

    private string SessionsDir => Path.Combine(homePath, "sessions");
    private string CwdKey { get; } = ProjectKey.Of(cwd);

    //this store's cwd, normalized once here and stamped on every session's leading system record, so both writers agree
    public string Cwd { get; } = Path.GetFullPath(cwd);

    private string? _currentFile;
    private readonly object _saveLock = new();

    //the file the session will write and the session it belongs to, chosen when the session begins, so a shell call before the first save knows both
    private (string File, SessionIdentity Identity) _begun = Begin(homePath, cwd, null);

    //a fresh file name, and with no identity carried over a new session named after that file
    private static (string File, SessionIdentity Identity) Begin(string home, string cwd, SessionIdentity? carried)
    {
        var file = Path.Combine(home, "sessions", $"{ProjectKey.Of(cwd)}-{DateTime.UtcNow.Ticks:d19}.jsonl");
        //on a whole second, the transcript's resolution, so the live identity and one read back from a file are the same value
        var now = DateTimeOffset.Now;
        var start = now.AddTicks(-(now.Ticks % TimeSpan.TicksPerSecond));
        return (file, carried ?? new SessionIdentity(SessionIdentity.Project(Path.GetFileName(file)), start));
    }

    //the file the next Save writes to, or null before the first Save and after StartNew. read this before StartNew, which nulls it
    public string? CurrentPath { get { lock (_saveLock) return _currentFile; } }

    //the live session, which every transcript of it names and every shell child reads
    public SessionIdentity Identity { get { lock (_saveLock) return _begun.Identity; } }

    //a new session in a new file, which is what /new asks for
    public void StartNew()
    {
        lock (_saveLock)
        {
            _currentFile = null;
            _begun = Begin(homePath, cwd, null);
        }
    }

    //a new file for the same session, which is what a compaction asks for, so the session keeps its id across the chain
    public void StartSuccessor()
    {
        lock (_saveLock)
        {
            _currentFile = null;
            _begun = Begin(homePath, cwd, _begun.Identity);
        }
    }

    //a resumed session keeps its own identity, though its next save opens a new file
    public void Adopt(SessionIdentity identity)
    {
        lock (_saveLock) _begun = (_begun.File, identity);
    }

    public void Save(Conversation convo)
    {
        var identity = Identity;
        WriteLines(convo.Messages.Select(m => ChatJson(m, Cwd, convo.Baseline, identity)));
    }

    //rewrite the session file atomically from the given JSONL lines, ordered by the caller. core knows nothing about the display records, it writes lines
    public void WriteLines(IEnumerable<string> lines)
    {
        lock (_saveLock)
        {
            Directory.CreateDirectory(SessionsDir);
            _currentFile ??= _begun.File;
            var sb = new StringBuilder();
            foreach (var line in lines) sb.AppendLine(line);
            var tmp = _currentFile + ".tmp";
            File.WriteAllText(tmp, sb.ToString());
            File.Move(tmp, _currentFile, overwrite: true);
        }
    }

    //the one chat-record shape, so both writers produce byte-identical JSON. cwd goes only on the system record, and a null cwd writes nothing
    public static string ChatJson(ChatMessage m, string? cwd = null, SessionBaseline? baseline = null,
        SessionIdentity? identity = null)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("role", m.Role);
            if (m.Content is not null) w.WriteString("content", m.Content);
            //keep the reasoning on disk so a --continue resume sees the prior turn's thinking
            if (m.ReasoningContent is not null) w.WriteString("reasoning_content", m.ReasoningContent);
            //written only when positive, so a record with nothing stripped stays byte-identical to an older one
            if (m.Stripped > 0) w.WriteNumber("stripped", m.Stripped);
            if (m.ToolCallId is not null) w.WriteString("tool_call_id", m.ToolCallId);
            //images go in as references, so a screenshot-heavy session stays a file you can grep. sha256 is what lets a restore see the picture on disk changed
            if (m.Images is { Count: > 0 } imgs)
            {
                w.WriteStartArray("images");
                foreach (var img in imgs)
                {
                    w.WriteStartObject();
                    w.WriteString("path", img.Path);
                    w.WriteNumber("bytes", img.Bytes);
                    w.WriteString("sha256", img.Sha256);
                    w.WriteString("media_type", img.MediaType);
                    if (img.DisplayName is not null) w.WriteString("name", img.DisplayName);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            if (m.ToolCalls is { Count: > 0 })
            {
                w.WriteStartArray("tool_calls");
                foreach (var tc in m.ToolCalls)
                {
                    w.WriteStartObject();
                    w.WriteString("id", tc.Id);
                    w.WriteString("name", tc.Name);
                    w.WriteString("arguments", tc.ArgumentsJson);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            if (m.GattoRole is not null) w.WriteString("gatto_role", m.GattoRole);
            if (m.IsError) w.WriteBoolean("is_error", true);
            if (m.Gloss is not null) w.WriteString("gloss", m.Gloss);
            if (m.Update is { } update)
            {
                w.WriteStartObject("gatto_update");
                w.WriteString("text", update.Text);
                w.WriteNumber("base_seq", update.BaseSeq);
                WriteSources(w, update.Sources);
                w.WriteEndObject();
            }
            if (m.View is { } v)
            {
                w.WriteStartObject("view");
                w.WriteNumber("start", v.Start);
                w.WriteString("head", v.Head);
                w.WriteString("tail", v.Tail);
                w.WriteStartArray("before");
                foreach (var l in v.Before) w.WriteStringValue(l);
                w.WriteEndArray();
                w.WriteStartArray("after");
                foreach (var l in v.After) w.WriteStringValue(l);
                w.WriteEndArray();
                w.WriteEndObject();
            }
            if (m.Usage is not null)
            {
                w.WriteStartObject("usage");
                w.WriteNumber("prompt_tokens", m.Usage.PromptTokens);
                w.WriteNumber("completion_tokens", m.Usage.CompletionTokens);
                w.WriteEndObject();
            }
            //the llama.cpp timings object goes in as raw JSON rather than a quoted string, so the file matches the wire shape
            if (m.Timings is not null)
            {
                w.WritePropertyName("timings");
                w.WriteRawValue(m.Timings);
            }
            //written only when the request was cut, so an uncut record stays byte-identical, and the ratio in full so a resume cuts the same results
            if (m.Shape is { } shape)
            {
                w.WriteNumber("elided_through", shape.ElidedThrough);
                w.WriteNumber("token_ratio", shape.Ratio);
            }
            //every write stamps this build's version and nothing reads it back, kept for a future version gate. resaving a future-schema file rewrites it to this version
            if (m.Role == "system")
            {
                w.WriteNumber("schema_version", SchemaVersion);
                if (cwd is not null) w.WriteString("cwd", cwd);
                if (baseline is not null) WriteBaseline(w, baseline);
                if (identity is not null)
                {
                    w.WriteString("session_id", identity.IdText);
                    w.WriteString("session_start", identity.StartText);
                }
            }
            if (m.Ts is not null) w.WriteString("ts", m.Ts.Value.ToUniversalTime().ToString("o"));
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    //one chat record back to a ChatMessage, and the caller skips event records since they have no role
    public static ChatMessage ParseChatElement(JsonElement el)
    {
        List<ToolCall>? calls = null;
        if (el.TryGetProperty("tool_calls", out var tcs))
        {
            calls = new List<ToolCall>();
            foreach (var tc in tcs.EnumerateArray())
                calls.Add(new ToolCall(
                    tc.GetProperty("id").GetString()!,
                    tc.GetProperty("name").GetString()!,
                    tc.GetProperty("arguments").GetString()!));
        }
        Usage? usage = null;
        if (el.TryGetProperty("usage", out var us) && us.ValueKind == JsonValueKind.Object)
            usage = new Usage(
                us.TryGetProperty("prompt_tokens", out var pt) && pt.ValueKind == JsonValueKind.Number ? pt.GetInt32() : 0,
                us.TryGetProperty("completion_tokens", out var ct) && ct.ValueKind == JsonValueKind.Number ? ct.GetInt32() : 0);

        var role = el.GetProperty("role").GetString()!;
        var content = el.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        var reasoning = el.TryGetProperty("reasoning_content", out var rc) && rc.ValueKind == JsonValueKind.String ? rc.GetString() : null;
        var strippedTotal = el.TryGetProperty("stripped", out var st) && st.ValueKind == JsonValueKind.Number ? st.GetInt32() : 0;

        //strip control tokens from assistant records only, since a tool result can legitimately hold those strings
        if (role == "assistant")
        {
            if (content is not null)
            {
                var (t, n) = ControlTokens.Strip(content);
                strippedTotal += n;
                //content that strips to nothing becomes null with tool calls and an empty string without them. a call-less record with null content fails on every replay
                content = t.Length > 0 ? t : (calls is { Count: > 0 } ? null : "");
            }
            if (reasoning is not null)
            {
                var (t, n) = ControlTokens.Strip(reasoning);
                strippedTotal += n;
                reasoning = t.Length > 0 ? t : null;
            }
        }

        return new ChatMessage(
            role,
            content,
            calls,
            el.TryGetProperty("tool_call_id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null,
            el.TryGetProperty("gatto_role", out var gr) && gr.ValueKind == JsonValueKind.String ? gr.GetString() : null,
            el.TryGetProperty("is_error", out var ie) && ie.ValueKind is JsonValueKind.True or JsonValueKind.False && ie.GetBoolean(),
            el.TryGetProperty("gloss", out var gl) && gl.ValueKind == JsonValueKind.String ? gl.GetString() : null,
            el.TryGetProperty("ts", out var ts) && ts.ValueKind == JsonValueKind.String
                && DateTime.TryParse(ts.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed : (DateTime?)null,
            usage,
            el.TryGetProperty("timings", out var tm) && tm.ValueKind == JsonValueKind.Object ? tm.GetRawText() : null,
            reasoning,
            strippedTotal,
            ReadImages(el),
            ReadView(el),
            ReadUpdate(el),
            ReadShape(el));
    }

    //both fields or neither, an absent pair is a reply whose request went out uncut
    private static Gatto.Core.Loop.ShapeMark? ReadShape(JsonElement el) =>
        el.TryGetProperty("elided_through", out var et) && et.ValueKind == JsonValueKind.Number
        && el.TryGetProperty("token_ratio", out var tr) && tr.ValueKind == JsonValueKind.Number
            ? new Gatto.Core.Loop.ShapeMark(et.GetInt32(), tr.GetDouble())
            : null;

    //what a system record was composed from, written only when the conversation holds one
    private static void WriteBaseline(Utf8JsonWriter w, SessionBaseline b)
    {
        w.WriteStartObject("baseline");
        w.WriteNumber("seq", b.Seq);
        w.WriteString("role", b.Role);
        w.WriteString("model", b.Model);
        w.WriteString("reasoning_history", b.ReasoningHistory);
        w.WriteStartObject("thinking");
        if (b.Thinking.Level is not null) w.WriteString("level", b.Thinking.Level);
        if (b.Thinking.BodyJson is not null) { w.WritePropertyName("body"); w.WriteRawValue(b.Thinking.BodyJson); }
        w.WriteEndObject();
        w.WriteStartArray("tools");
        foreach (var t in b.Tools) { w.WriteStartObject(); w.WriteString("name", t.Name); w.WriteString("sha256", t.Sha256); w.WriteEndObject(); }
        w.WriteEndArray();
        WriteSources(w, b.Sources);
        if (b.Endpoint is not null) w.WriteString("endpoint", b.Endpoint);
        w.WriteEndObject();
    }

    private static void WriteSources(Utf8JsonWriter w, BaselineSources s)
    {
        w.WriteStartObject("sources");
        if (s.Date is not null) w.WriteString("date", s.Date);
        if (s.Memory is not null) w.WriteString("memory", s.Memory);
        w.WriteStartArray("context_files");
        foreach (var f in s.ContextFiles) { w.WriteStartObject(); w.WriteString("path", f.Path); w.WriteString("sha256", f.Sha256); w.WriteEndObject(); }
        w.WriteEndArray();
        w.WriteStartArray("policy");
        foreach (var p in s.Policy) { w.WriteStartObject(); w.WriteString("extension", p.Extension); w.WriteString("line", p.Line); w.WriteEndObject(); }
        w.WriteEndArray();
        w.WriteString("role_append_sha256", s.RoleAppendSha256);
        w.WriteString("model_append_sha256", s.ModelAppendSha256);
        w.WriteEndObject();
    }

    //a system record's baseline, null when the record predates baselines or the field does not parse, and both reset once
    public static SessionBaseline? ParseBaseline(JsonElement el)
    {
        if (!el.TryGetProperty("baseline", out var b) || b.ValueKind != JsonValueKind.Object) return null;
        try
        {
            var th = b.GetProperty("thinking");
            var thinking = new ThinkingMark(
                th.TryGetProperty("level", out var lv) ? lv.GetString() : null,
                th.TryGetProperty("body", out var body) ? body.GetRawText() : null);
            var tools = b.GetProperty("tools").EnumerateArray()
                .Select(t => new ToolMark(t.GetProperty("name").GetString()!, t.GetProperty("sha256").GetString()!)).ToList();
            return new SessionBaseline(b.GetProperty("seq").GetInt32(), b.GetProperty("role").GetString()!, b.GetProperty("model").GetString()!,
                b.GetProperty("reasoning_history").GetString()!, thinking, tools, ReadSources(b.GetProperty("sources")),
                b.TryGetProperty("endpoint", out var ep) && ep.ValueKind == JsonValueKind.String ? ep.GetString() : null);   //absent in a record from before the field, and a required read would reset every such session
        }
        catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or FormatException) { return null; }
    }

    private static BaselineSources ReadSources(JsonElement s) => new(
        s.TryGetProperty("date", out var d) ? d.GetString() : null,
        s.TryGetProperty("memory", out var m) ? m.GetString() : null,
        s.GetProperty("context_files").EnumerateArray().Select(f => new ContextFileMark(f.GetProperty("path").GetString()!, f.GetProperty("sha256").GetString()!)).ToList(),
        s.GetProperty("policy").EnumerateArray().Select(p => new PolicyMark(p.GetProperty("extension").GetString()!, p.GetProperty("line").GetString()!)).ToList(),
        s.GetProperty("role_append_sha256").GetString()!,
        s.GetProperty("model_append_sha256").GetString()!);

    //the update a user record carried, or null, and a malformed one is dropped since it only repeats what the model was told
    private static SessionUpdate? ReadUpdate(JsonElement el)
    {
        if (!el.TryGetProperty("gatto_update", out var u) || u.ValueKind != JsonValueKind.Object) return null;
        try { return new SessionUpdate(u.GetProperty("text").GetString()!, ReadSources(u.GetProperty("sources")), u.GetProperty("base_seq").GetInt32()); }
        catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or FormatException) { return null; }
    }

    //the baseline of the first record whose role is system, never read by line position
    public static SessionBaseline? BaselineOf(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            if (line.Length == 0) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("role", out var r) && r.GetString() == "system") return ParseBaseline(doc.RootElement);
            }
            catch (JsonException) { }
        }
        return null;
    }

    public static SessionBaseline? LoadBaseline(string path) => BaselineOf(File.ReadLines(path));

    //an edit's view, or null when the record has none or it is malformed, since a view is display only
    private static Gatto.Core.Tools.EditView? ReadView(JsonElement el)
    {
        if (!el.TryGetProperty("view", out var v) || v.ValueKind != JsonValueKind.Object) return null;
        if (!v.TryGetProperty("start", out var s) || !s.TryGetInt32(out var start)) return null;
        static string Str(JsonElement o, string key) => o.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString()! : "";
        static string[] Arr(JsonElement o, string key) => o.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.Array
            ? p.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToArray() : Array.Empty<string>();
        return new Gatto.Core.Tools.EditView(start, Str(v, "head"), Str(v, "tail"), Arr(v, "before"), Arr(v, "after"));
    }

    //the image references, or null when the record has none. a malformed entry is skipped so one bad record can't make a session unresumable
    private static IReadOnlyList<ImageRef>? ReadImages(JsonElement el)
    {
        if (!el.TryGetProperty("images", out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
        List<ImageRef>? list = null;
        foreach (var e in arr.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object) continue;
            if (!e.TryGetProperty("path", out var pp) || pp.ValueKind != JsonValueKind.String) continue;
            if (!e.TryGetProperty("sha256", out var sh) || sh.ValueKind != JsonValueKind.String) continue;
            var bytes = e.TryGetProperty("bytes", out var b) && b.ValueKind == JsonValueKind.Number ? b.GetInt64() : 0;
            var media = e.TryGetProperty("media_type", out var mt) && mt.ValueKind == JsonValueKind.String
                ? mt.GetString()! : "application/octet-stream";
            var display = e.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String
                ? nm.GetString() : null;
            (list ??= new()).Add(new ImageRef(pp.GetString()!, bytes, sh.GetString()!, media, display));
        }
        return list;
    }

    //how many session files exist for this store's cwd, which is the number gatto status shows
    public int CountForCwd() => SessionFilesForCwd().Count();

    //the session files for this cwd, minus the ledger sidecars. a sidecar sorts after its session and would otherwise shadow it and inflate the count
    private IEnumerable<string> SessionFilesForCwd() =>
        Directory.Exists(SessionsDir)
            ? Directory.EnumerateFiles(SessionsDir, $"{CwdKey}-*.jsonl")
                .Where(f => !f.EndsWith(".ledger.jsonl", StringComparison.OrdinalIgnoreCase))
            : Enumerable.Empty<string>();

    //the newest session file for this cwd, or null when there is none. read-only, so it leaves CurrentPath alone and the next Save still forks a fresh file
    public string? LatestPathForCwd() =>
        SessionFilesForCwd().OrderBy(f => f, StringComparer.Ordinal).LastOrDefault();

    public IReadOnlyList<ChatMessage>? LoadLatestForCwd()
    {
        var latest = LatestPathForCwd();
        return latest is null ? null : LoadPath(latest);
    }

    //parse one session file by path, the seam --continue needs, and LoadLatestForCwd comes through here too
    public IReadOnlyList<ChatMessage> LoadPath(string path)
    {
        var messages = new List<ChatMessage>();
        foreach (var line in File.ReadLines(path))
        {
            if (line.Trim().Length == 0) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var el = doc.RootElement;
                if (!el.TryGetProperty("role", out _)) continue;   //skip event and anchor records
                messages.Add(ParseChatElement(el));
            }
            catch (Exception) { } //skip unparseable line
        }
        var torn = TornRecords(messages);
        return torn.Count == 0 ? messages : [.. messages.Where((_, i) => !torn.Contains(i))];
    }

    //the positions of torn rounds, where a tool call was never answered. the assistant record and its answered calls go too, so what remains is balanced
    public static IReadOnlySet<int> TornRecords(IReadOnlyList<ChatMessage> messages)
    {
        var torn = new HashSet<int>();
        for (var i = 0; i < messages.Count; i++)
        {
            if (messages[i] is not { Role: "assistant", ToolCalls: { Count: > 0 } calls }) continue;
            var answers = new List<int>();
            var unanswered = false;
            foreach (var call in calls)
            {
                var at = -1;
                for (var j = i + 1; j < messages.Count && at < 0; j++)
                    if (messages[j] is { Role: "tool" } t && t.ToolCallId == call.Id) at = j;
                if (at < 0) unanswered = true;
                else answers.Add(at);
            }
            if (!unanswered) continue;
            torn.Add(i);
            foreach (var a in answers) torn.Add(a);
        }
        return torn;
    }

    //an id to a session path from the home alone, exact stem first then a unique prefix, across every cwd. zero or several matches refuse, and the ledger sidecars are left out
    public static string ResolvePathById(string home, string id)
    {
        var sessionsDir = Path.Combine(home, "sessions");
        var trimmed = id.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) ? id[..^".jsonl".Length] : id;

        var files = Directory.Exists(sessionsDir)
            ? Directory.EnumerateFiles(sessionsDir, "*.jsonl")
                .Where(f => !f.EndsWith(".ledger.jsonl", StringComparison.OrdinalIgnoreCase))
                .ToList()
            : new List<string>();

        //exact stem match first, so an id that is also a prefix of another session resolves to the one the caller named
        var exact = files.FirstOrDefault(f =>
            string.Equals(Path.GetFileNameWithoutExtension(f), trimmed, StringComparison.Ordinal));
        if (exact is not null) return exact;

        var prefixMatches = files
            .Where(f => Path.GetFileNameWithoutExtension(f).StartsWith(trimmed, StringComparison.Ordinal))
            .ToList();
        if (prefixMatches.Count == 1) return prefixMatches[0];
        if (prefixMatches.Count > 1)
            throw new GattoConfigException(
                $"--continue {id}: matches {prefixMatches.Count} saved sessions — use a longer id to disambiguate");

        throw new GattoConfigException($"--continue {id}: no saved session matches this id");
    }

    //the session a file belongs to, from its system record. a file written before the record named its session is its own origin: its name and its first time
    public static SessionIdentity? IdentityOf(string path)
    {
        DateTimeOffset? first = null;
        foreach (var line in File.ReadLines(path))
        {
            if (line.Length == 0) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var el = doc.RootElement;
                if (el.TryGetProperty("role", out var r) && r.GetString() == "system"
                    && SessionIdentity.Parse(Text(el, "session_id"), Text(el, "session_start")) is { } named)
                    return named;
                if (first is null && Text(el, "ts") is { } ts
                    && DateTimeOffset.TryParse(ts, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
                    first = at;
            }
            catch (JsonException) { }
        }
        return first is { } start ? new SessionIdentity(SessionIdentity.Project(Path.GetFileName(path)), start) : null;

        static string? Text(JsonElement el, string name) =>
            el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    //the folder a session was saved in, the cwd of its first system record, or null when the record holds none
    public static string? CwdOf(string path)
    {
        foreach (var line in File.ReadLines(path))
        {
            if (line.Length == 0) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var el = doc.RootElement;
                if (!el.TryGetProperty("role", out var r) || r.GetString() != "system") continue;
                return el.TryGetProperty("cwd", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            }
            catch (JsonException) { }
        }
        return null;
    }
}
