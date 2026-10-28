namespace Gatto.Repl.Render;

//a watched letter read for the screen: its frame's header fields, its body without the two markers, and the path the user reads
public sealed record LetterText(string Path, IReadOnlyList<(string Key, string Value)> Fields, IReadOnlyList<string> Body)
{
    //the header keys a letter's frame holds, in the order the format writes them
    private static readonly string[] Keys = ["to", "from", "date", "re", "kind"];

    //the record's text is the path, a blank line and the file. a file with no frame keeps every line as its body
    public static LetterText Parse(string path, string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        if (lines.Count > 0 && lines[0] == path)
        {
            lines.RemoveAt(0);
            if (lines.Count > 0 && lines[0].Length == 0) lines.RemoveAt(0);
        }
        var at = 0;
        while (at < lines.Count && lines[at].Trim().Length == 0) at++;
        var fields = new List<(string, string)>();
        if (at < lines.Count && IsOpening(lines[at]))
        {
            at++;
            while (at < lines.Count && Field(lines[at]) is { } f) { fields.Add(f); at++; }
        }
        var body = lines.Skip(at).ToList();
        var end = body.FindLastIndex(l => l.Trim().Length > 0);
        if (end >= 0 && IsClosing(body[end])) body.RemoveAt(end);
        while (body.Count > 0 && body[0].Trim().Length == 0) body.RemoveAt(0);
        while (body.Count > 0 && body[^1].Trim().Length == 0) body.RemoveAt(body.Count - 1);
        return new LetterText(path, fields, body);
    }

    private static bool IsOpening(string line) =>
        line.Trim() is var t && t.StartsWith("<!-- letter:", StringComparison.Ordinal) && t.EndsWith("-->", StringComparison.Ordinal);

    private static bool IsClosing(string line) => line.Trim() == "<!-- /letter -->";

    private static (string, string)? Field(string line)
    {
        var colon = line.IndexOf(':');
        if (colon <= 0) return null;
        var key = line[..colon].Trim();
        return Keys.Contains(key) ? (key, line[(colon + 1)..].Trim()) : null;
    }

    //the value of one header field, or null when the frame does not hold it
    public string? Get(string key) => Fields.FirstOrDefault(f => f.Key == key).Value;

    //the part after the last mailbox folder, which names the inbox and the letter. a path outside a mailbox keeps its folder and its name
    public static string ShortPath(string path)
    {
        var parts = path.Split('\\', '/');
        var box = Array.FindLastIndex(parts, p => p.Equals("mailbox", StringComparison.OrdinalIgnoreCase));
        var from = box >= 0 && box < parts.Length - 1 ? box + 1 : Math.Max(0, parts.Length - 2);
        if (from < parts.Length - 1 && parts[from].EndsWith(':')) from++;   //a drive is no folder to name
        return System.IO.Path.Combine(parts[from..]);
    }

    //what the queue row and the block's head say, the same words in both places
    public static string Head(string path) => "New mail: " + ShortPath(path);
}
