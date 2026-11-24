using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Gatto.Cli.Inbox;

//a seat of the other harness: the folder a model id gives, what counts as a letter, and what proves a letter arrived, read off its transcript
internal static class ClaudeSeat
{
    //the seat folder from a model id: drop the claude- lead and a trailing bracket tail, then the repl's mailbox name. null for a model with no seat here
    public static string? FromModelId(string? id)
    {
        if (string.IsNullOrEmpty(id) || !id.StartsWith("claude-", StringComparison.Ordinal)) return null;
        var rest = id["claude-".Length..];
        var bracket = rest.IndexOf('[');
        if (bracket >= 0) rest = rest[..bracket];
        var seat = Repl.Repl.MailboxName(rest);
        return seat.Length == 0 ? null : seat;
    }

    //message.model of the newest assistant line naming a claude model, synthetic lines skipped. null when the file holds none
    public static string? NewestModel(string transcriptPath)
    {
        var latest = "";
        foreach (var line in Lines(transcriptPath, 0))
        {
            //a line that cannot be an answer is never parsed, the transcript holds far more of them
            if (!line.Contains("\"assistant\"", StringComparison.Ordinal)) continue;
            if (Parse(line) is not JsonObject node || Text(node, "type") != "assistant") continue;
            if (node["message"] is JsonObject msg && Text(msg, "model") is { Length: > 0 } model
                && model.StartsWith("claude-", StringComparison.Ordinal))
                latest = model;
        }
        return latest.Length == 0 ? null : latest;
    }

    //the proof of a receipt: a line written after the handover whose top-level type is user holds the notice text as json writes it. the queue record, the request blob and the model quoting the line itself are not proof
    public static bool Received(string transcriptPath, string letterPath, long from)
    {
        //the relaxed encoder keeps a path raw: the default one escapes apostrophes, ampersands, pluses and non-ascii, which the transcript holds plain
        var quoted = JsonSerializer.Serialize("inbox: " + letterPath, RawJson);
        var needle = quoted[1..^1];
        foreach (var line in Lines(transcriptPath, from))
        {
            if (!line.Contains(needle, StringComparison.Ordinal)) continue;
            if (Text(Parse(line), "type") == "user") return true;
        }
        return false;
    }

    //the seat's inbox under the project's mailbox. the project folder is always the full path the record carries
    public static string Inbox(string projectFolder, string seat) =>
        Path.Combine(projectFolder, ".gatto", "mailbox", "claude", seat);

    //the inbox as the open and off rows show it, the path read from the project folder
    public static string Shown(string inbox, string projectFolder) => Path.GetRelativePath(projectFolder, inbox);

    //the .md names directly in the inbox, the same filter the watch applies, so a .md.tmp is never a letter
    public static IReadOnlyList<string> Letters(string inbox) =>
        [.. Directory.GetFiles(inbox, "*.md", SearchOption.TopDirectoryOnly)
            .Select(f => Path.GetFileName(f)!)
            .Where(n => Path.GetExtension(n).Equals(".md", StringComparison.OrdinalIgnoreCase))];

    //the transcript's length now, where a notice handed over from here on will be written. an absent file is empty
    public static long Length(string transcriptPath)
    {
        try { return new FileInfo(transcriptPath) is { Exists: true } info ? info.Length : 0; }
        catch (Exception) { return 0; }
    }

    //a transcript streamed line by line from a byte offset, under shares that never block the writer appending to it. an offset past the end reads from the start, a file replaced since, and an absent or unreadable file reads as no lines
    private static IEnumerable<string> Lines(string path, long from)
    {
        FileStream? file = null;
        try { file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
        catch (Exception) { }
        if (file is null) yield break;
        using (file)
        {
            if (from > 0 && from <= file.Length) file.Seek(from, SeekOrigin.Begin);
            using var reader = new StreamReader(file, new UTF8Encoding(false));
            while (Next(reader) is { } line) yield return line;
        }
    }

    //one line, null at the end or on a read that fails midway
    private static string? Next(StreamReader reader)
    {
        try { return reader.ReadLine(); }
        catch (Exception) { return null; }
    }

    private static readonly JsonSerializerOptions RawJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    //one json line, an unparsable line is no record at all
    private static JsonNode? Parse(string line)
    {
        try { return JsonNode.Parse(line); }
        catch (JsonException) { return null; }
    }

    //a string member of an object node, null for anything else
    private static string? Text(JsonNode? node, string name) =>
        node is JsonObject o && o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
