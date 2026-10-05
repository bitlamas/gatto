using System.Text.Json;
using Gatto.Core.Client;
using Gatto.Core.Home;
using Gatto.Core.Loop;
using Gatto.Core.Tools;
using Gatto.Terminal;

namespace Gatto.Repl.Render;

//model-order session writing and replay, kept here because it reads the display model. each event record goes right after its anchor, matched by reference
public static class TranscriptStore
{
    public static void Save(TranscriptModel model, Conversation convo, SessionStore store) =>
        store.WriteLines(BuildLines(model, convo, store.Cwd));

    //the cwd defaults to null so a caller building lines alone writes the same bytes. save, the only production caller, passes SessionStore.Cwd through
    public static IReadOnlyList<string> BuildLines(TranscriptModel model, Conversation convo, string? cwd = null)
    {
        var byAnchor = new Dictionary<ChatMessage, List<EventItem>>(ReferenceEqualityComparer.Instance);
        var leading = new List<EventItem>();
        foreach (var it in model.Items)
        {
            if (it is CommandEchoItem { Transient: true }) continue;   //session-local chrome, so it is never saved
            if (it is EventItem ev)
            {
                //an event anchored to the system record is written leading, since the system record is never displayed and a leading event survives ReplaceSystem swapping it
                if (ev.After is null || ev.After.Role == "system") leading.Add(ev);
                else (byAnchor.TryGetValue(ev.After, out var l) ? l : byAnchor[ev.After] = new List<EventItem>()).Add(ev);
            }
        }

        var lines = new List<string>();
        foreach (var ev in leading) lines.Add(EventJson(ev));
        foreach (var m in convo.Messages)
        {
            lines.Add(SessionStore.ChatJson(m, cwd, convo.Baseline));
            if (byAnchor.TryGetValue(m, out var evs)) foreach (var ev in evs) lines.Add(EventJson(ev));
        }
        return lines;   //an event whose After is no longer in the convo is dropped
    }

    //the glyph set travels with the theme, so a resumed session draws its glosses in the same vocabulary as the rows after it
    public static (Conversation Convo, TranscriptModel Model) Rebuild(IEnumerable<string> fileLines,
        Theme theme, string role, GlyphSet? glyphs)   //two drifts: prose split by reasoning rebuilds as one block, since reasoning is not saved, and a mid-round system line sits before that round's prose
    {
        var lines = fileLines as IReadOnlyList<string> ?? fileLines.ToList();
        var convo = new Conversation(null, baseline: SessionStore.BaselineOf(lines));
        var model = new TranscriptModel(role);
        ChatMessage? last = null;
        var pendingTool = new Dictionary<string, (string Name, string Args, string RawArgs)>(StringComparer.Ordinal);
        //a torn round is rebuilt into neither the conversation nor the display, and the check uses the same predicate the load path reads
        var torn = SessionStore.TornRecords([.. ChatRecords(lines)]);
        var chatAt = -1;

        foreach (var line in lines)
        {
            var lineKind = Classify(line, out var el);
            if (lineKind == LineKind.Skip) continue;
            if (lineKind == LineKind.Event)
            {
                if (ParseEvent(el.GetProperty("event").GetString()!, el, last) is { } ev) model.Append(ev);
                continue;
            }
            if (torn.Contains(++chatAt)) continue;

            var m = SessionStore.ParseChatElement(el);
            convo.Load(new[] { m });
            last = m;
            switch (m.Role)
            {
                case "user":
                    if (m.Content is { Length: > 0 } u) model.Append(new UserEchoItem(u.Split('\n')));
                    break;
                case "assistant":
                    if (m.Content is { Length: > 0 } a) model.Append(new AssistantBlockItem(a.Split('\n'), m.GattoRole ?? role));
                    if (m.ToolCalls is { } calls)
                        //the raw arguments are kept so the tool block can derive its write preview, the record holds them already
                        foreach (var c in calls)
                            pendingTool[c.Id] = (c.Name, ItemRender.CompactArgs(c.ArgumentsJson), c.ArgumentsJson);
                    break;
                case "tool":
                    //the gatto role, the error flag and the gloss are the stamped fields. role is the fallback for a record written before those fields existed
                    var stored = new ToolResult(m.Content ?? "", m.IsError, m.Gloss);
                    //a refusal is read back from the gate's words, so it draws the denied or cancelled row and the red bullet it drew live
                    var (refused, reason) = Refusal.Of(stored);
                    var refusal = Refusal.Gloss(refused, reason, theme, glyphs ?? GlyphSet.Unicode);
                    var gloss = refusal ?? ItemRender.ToolGloss(stored, theme, glyphs);
                    var parts = refusal is null ? ItemRender.ToolGlossParts(stored, theme, glyphs) : new ToolGlossParts(refusal, "", null, false);
                    var toolRole = m.GattoRole ?? role;
                    if (m.ToolCallId is { } id && pendingTool.Remove(id, out var pt))
                    {
                        var (editOld, editNew, writeContent) = EditArgs.Of(pt.Name, pt.RawArgs);
                        model.Append(new ToolBlockItem(pt.Name, pt.Args, gloss, HasResult: true, toolRole)
                            { Collapsed = !(!m.IsError && pt.Name is "write_file" or "edit_file"),   //a write or an edit that happened opens on its change, as on the live path
                              FullResult = m.Content ?? "", Parts = parts,   //the full result round-trips from the tool record
                              RawGloss = refusal is null ? m.Gloss : null,   //the record's gloss carries the exit code, so a resumed shell block reads the same row as the live one
                              BulletTint = refusal is null ? null : Theme.Err,
                              FullArgs = ItemRender.FullArgsOf(pt.Name, pt.RawArgs),
                              GrepPattern = GrepRows.PatternOf(pt.Name, pt.RawArgs), BodyStart = ToolBody.StartOf(pt.Name, pt.RawArgs),
                              EditOld = editOld, EditNew = editNew, WriteContent = writeContent,
                              FileLanguage = SyntaxHighlight.LanguageOfToolCall(pt.Name, pt.RawArgs), EditAt = m.View, IsError = m.IsError });
                    }
                    else
                        model.Append(new ToolBlockItem("(tool)", "", gloss, HasResult: true, toolRole)
                            { Collapsed = true, FullResult = m.Content ?? "", Parts = parts });
                    break;
                //the system role and any other go into the conversation only, and a tool call still waiting is dropped
            }
        }
        return (convo, model);
    }

    //the chat records of a session file in order, read through Classify so the count matches the rebuild loop and the torn positions line up
    private static IEnumerable<ChatMessage> ChatRecords(IEnumerable<string> lines)
    {
        foreach (var line in lines)
            if (Classify(line, out var el) == LineKind.Chat)
                yield return SessionStore.ParseChatElement(el);
    }

    private enum LineKind { Skip, Event, Chat }

    //what one session-file line is, an event, a chat record or a skip
    private static LineKind Classify(string line, out JsonElement el)
    {
        el = default;
        if (line.Trim().Length == 0) return LineKind.Skip;
        try { el = JsonDocument.Parse(line).RootElement; } catch (JsonException) { return LineKind.Skip; }
        if (el.TryGetProperty("event", out var kind) && kind.ValueKind == JsonValueKind.String) return LineKind.Event;
        return el.TryGetProperty("role", out _) ? LineKind.Chat : LineKind.Skip;
    }

    //event records as json

    private static string EventJson(EventItem ev)
    {
        using var ms = new System.IO.MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            switch (ev)
            {
                case SystemLineItem s: w.WriteString("event", "system"); w.WriteString("text", s.Text); break;
                case ListingItem li:
                    //a listing saves its spec so it is measured again at whatever width it is replayed into
                    w.WriteString("event", "listing");
                    w.WriteStartArray("headers");
                    foreach (var h in li.Spec.Headers) w.WriteStringValue(h);
                    w.WriteEndArray();
                    w.WriteStartArray("aligns");
                    foreach (var a in li.Spec.Alignments) w.WriteNumberValue((int)a);
                    w.WriteEndArray();
                    w.WriteStartArray("table_rows");
                    foreach (var row in li.Spec.Rows)
                    {
                        w.WriteStartArray();
                        foreach (var cell in row) w.WriteStringValue(cell);
                        w.WriteEndArray();
                    }
                    w.WriteEndArray();
                    break;
                case CompletionItem c: w.WriteString("event", "completion"); w.WriteString("text", c.Text); break;
                case SessionLeadItem l:
                    w.WriteString("event", "lead");
                    w.WriteString("text", l.Text);
                    //the field is written only when there is an outcome to record, and an older reader never sees the property because unknown ones are ignored
                    if (l.Memory is { } mem)
                    {
                        w.WriteStartObject("memory");
                        w.WriteBoolean("heading_found", mem.HeadingFound);
                        w.WriteNumber("candidates", mem.Candidates);
                        w.WriteNumber("banked", mem.Banked);
                        w.WriteNumber("dropped", mem.Dropped);
                        w.WriteEndObject();
                    }
                    break;
                case CommandEchoItem cmd:
                    w.WriteString("event", "command");
                    WriteRows(w, cmd.LogicalRows);
                    //written only for a /context report, so every other command record keeps its shape
                    if (cmd.Context is { } figures) WriteContext(w, figures);
                    break;
                default: w.WriteString("event", "unknown"); break;
            }
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());

        static void WriteRows(Utf8JsonWriter w, IReadOnlyList<string> rows)
        {
            w.WriteStartArray("rows");
            foreach (var r in rows) w.WriteStringValue(r);
            w.WriteEndArray();
        }
    }

    private static EventItem? ParseEvent(string kind, JsonElement el, ChatMessage? after)
    {
        string Text() => el.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : "";
        IReadOnlyList<string> Rows()
        {
            if (!el.TryGetProperty("rows", out var arr) || arr.ValueKind != JsonValueKind.Array) return System.Array.Empty<string>();
            var list = new List<string>();
            foreach (var r in arr.EnumerateArray()) if (r.ValueKind == JsonValueKind.String) list.Add(r.GetString()!);
            return list;
        }
        TableSpec ParsedSpec()
        {
            List<string> Strings(string prop)
            {
                var list = new List<string>();
                if (el.TryGetProperty(prop, out var a) && a.ValueKind == JsonValueKind.Array)
                    foreach (var v in a.EnumerateArray()) if (v.ValueKind == JsonValueKind.String) list.Add(v.GetString()!);
                return list;
            }
            var headers = Strings("headers");
            var aligns = new List<ColumnAlign>();
            if (el.TryGetProperty("aligns", out var al) && al.ValueKind == JsonValueKind.Array)
                foreach (var v in al.EnumerateArray()) if (v.TryGetInt32(out var i)) aligns.Add((ColumnAlign)i);
            while (aligns.Count < headers.Count) aligns.Add(ColumnAlign.Left);
            var tableRows = new List<IReadOnlyList<string>>();
            if (el.TryGetProperty("table_rows", out var rr) && rr.ValueKind == JsonValueKind.Array)
                foreach (var row in rr.EnumerateArray())
                {
                    var cells = new List<string>();
                    if (row.ValueKind == JsonValueKind.Array)
                        foreach (var c in row.EnumerateArray()) if (c.ValueKind == JsonValueKind.String) cells.Add(c.GetString()!);
                    tableRows.Add(cells);
                }
            return new TableSpec(headers, aligns, tableRows);
        }

        return kind switch
        {
            "system" => new SystemLineItem(Text(), after),
            //an old record may still hold a "boxed": false, and an unknown field is ignored in both directions
            "listing" => new ListingItem(ParsedSpec(), after),
            "completion" => new CompletionItem(Text(), after),
            //the memory object is written for telemetry only and read back as absent, which the schema tolerates
            "lead" => new SessionLeadItem(Text(), after),
            //a command keeps its figures when it carries them, a damaged field drops to the saved rows. a prompt record falls to the null arm
            "command" => new CommandEchoItem(Rows(), after) { Context = el.TryGetProperty("context", out var cx) ? ParseContext(cx) : null },
            _ => null,
        };
    }

    //the figures in snake_case, each field that does not apply left out
    private static void WriteContext(Utf8JsonWriter w, ContextFigures f)
    {
        w.WriteStartObject("context");
        if (f.Window is int window) w.WriteNumber("window", window);
        w.WriteNumber("total", f.Total);
        w.WriteBoolean("exact", f.Exact);
        w.WriteBoolean("fell_back", f.FellBack);
        w.WriteBoolean("before_first_request", f.BeforeFirstRequest);
        if (f.AutoCompactAt is double at) w.WriteNumber("auto_compact_at", at);
        w.WriteStartArray("parts");
        foreach (var part in f.Parts)
        {
            w.WriteStartObject();
            w.WriteString("label", part.Label);
            w.WriteString("group", GroupName(part.Group));
            w.WriteNumber("tokens", part.Tokens);
            if (part.Detail is { } detail) w.WriteString("detail", detail);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        if (f.Cache is { } cache)
        {
            w.WriteStartObject("cache");
            w.WriteNumber("from_cache", cache.FromCache);
            w.WriteNumber("fresh", cache.Fresh);
            w.WriteEndObject();
        }
        w.WriteEndObject();
    }

    private static string GroupName(ContextGroup g) => g switch
    {
        ContextGroup.Prefix => "prefix",
        ContextGroup.Messages => "messages",
        ContextGroup.Reasoning => "reasoning",
        _ => "tool_results",
    };

    //null for any field of the wrong shape, so a damaged record draws its saved rows rather than a report built from guesses
    private static ContextFigures? ParseContext(JsonElement el)
    {
        try
        {
            if (el.ValueKind != JsonValueKind.Object) return null;
            int? window = el.TryGetProperty("window", out var wv) ? wv.GetInt32() : null;
            double? at = el.TryGetProperty("auto_compact_at", out var av) ? av.GetDouble() : null;
            var parts = new List<ContextPart>();
            foreach (var p in el.GetProperty("parts").EnumerateArray())
            {
                var group = p.GetProperty("group").GetString() switch
                {
                    "prefix" => ContextGroup.Prefix,
                    "messages" => ContextGroup.Messages,
                    "reasoning" => ContextGroup.Reasoning,
                    "tool_results" => ContextGroup.ToolResults,
                    _ => throw new FormatException(),
                };
                parts.Add(new ContextPart(p.GetProperty("label").GetString()!, group, p.GetProperty("tokens").GetInt32(),
                    p.TryGetProperty("detail", out var d) ? d.GetString() : null));
            }
            ContextCache? cache = el.TryGetProperty("cache", out var c)
                ? new ContextCache(c.GetProperty("from_cache").GetInt32(), c.GetProperty("fresh").GetInt32()) : null;
            return new ContextFigures(window, el.GetProperty("total").GetInt32(), el.GetProperty("exact").GetBoolean(),
                el.GetProperty("fell_back").GetBoolean(), el.GetProperty("before_first_request").GetBoolean(), at, parts, cache);
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException or FormatException or NullReferenceException) { return null; }
    }
}
