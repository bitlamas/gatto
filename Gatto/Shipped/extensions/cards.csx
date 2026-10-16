//cards, bundled with gatto. /card <path> writes a role file from a character card, V2 or V3, PNG or JSON.
//your copy is your code. delete this file and /card is off.
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Gatto.Core;

var rolesDir = Path.Combine(Gatto.Home, "roles");
byte[] pngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

Func<string, string> full = p => Path.GetFullPath(Path.IsPathRooted(p) ? p : Path.Combine(Gatto.Cwd, p));

//the card's text chunk, ccv3 before chara since a V3 card carries both and the V2 one is the older copy. the crc is not checked
Func<byte[], string?> readPng = bytes =>
{
    if (bytes.Length < 8 || !bytes.AsSpan(0, 8).SequenceEqual(pngSignature)) return null;
    string? ccv3 = null, chara = null;
    var offset = 8;
    while (offset + 8 <= bytes.Length)
    {
        var length = (long)bytes[offset] << 24 | (long)bytes[offset + 1] << 16 | (long)bytes[offset + 2] << 8 | bytes[offset + 3];
        var type = Encoding.ASCII.GetString(bytes, offset + 4, 4);
        var start = offset + 8;
        if (type == "IEND" || start + length > bytes.Length) break;
        if (type == "tEXt")
        {
            var nul = Array.IndexOf(bytes, (byte)0, start, (int)length);
            if (nul >= 0)
            {
                var keyword = Encoding.Latin1.GetString(bytes, start, nul - start);
                var text = Encoding.Latin1.GetString(bytes, nul + 1, start + (int)length - nul - 1);
                if (keyword == "ccv3") ccv3 = text;
                else if (keyword == "chara") chara = text;
            }
        }
        offset = start + (int)length + 4;
    }
    return ccv3 ?? chara;
};

//a string field trimmed, null when it is absent, not a string or blank
Func<JsonElement, string, string?> str = (data, key) =>
    data.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && v.GetString()!.Trim() is { Length: > 0 } t ? t : null;

//trimmed, lowercase, each space a hyphen, and only letters, digits and hyphens kept
Func<string, string> slug = name =>
{
    var sb = new StringBuilder();
    foreach (var c in name.Trim().ToLowerInvariant())
    {
        if (c == ' ') sb.Append('-');
        else if (c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-') sb.Append(c);
    }
    return sb.ToString();
};

Func<int, string, string, string> plural = (n, one, many) => $"{n} {(n == 1 ? one : many)}";

Func<string, string> card = (arg) =>
{
    var a = arg.Trim();
    if (a.Length == 0) return "usage: /card <path>";
    //a path with a space is typed quoted, as the shell asks, so one pair of quotes comes off
    var unquoted = a.Length >= 2 && (a[0] == '"' || a[0] == '\'') && a[^1] == a[0] ? a[1..^1] : a;
    var path = full(unquoted);
    if (!File.Exists(path)) return $"no file at {path}";

    var ext = Path.GetExtension(path);
    var isPng = ext.Equals(".png", StringComparison.OrdinalIgnoreCase);
    if (!isPng && !ext.Equals(".json", StringComparison.OrdinalIgnoreCase)) return $"not a PNG or a JSON file: {path}";

    string json;
    if (isPng)
    {
        var chunk = readPng(File.ReadAllBytes(path));
        if (chunk is null) return $"no character card in {path}";
        try { json = Encoding.UTF8.GetString(Convert.FromBase64String(chunk)); }
        catch (FormatException) { return $"{path} is not a V2 or V3 character card"; }
    }
    else json = File.ReadAllText(path);

    JsonElement data;
    try
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("spec", out var spec) || spec.ValueKind != JsonValueKind.String
            || spec.GetString() is not ("chara_card_v2" or "chara_card_v3")
            || !root.TryGetProperty("data", out var d) || d.ValueKind != JsonValueKind.Object)
            return $"{path} is not a V2 or V3 character card";
        data = d.Clone();
    }
    catch (JsonException) { return $"{path} is not a V2 or V3 character card"; }

    var name = str(data, "name");
    var stem = name is null ? "" : slug(name);
    if (stem.Length == 0) return "the card has no name";
    var target = Path.Combine(rolesDir, stem + ".json");
    if (File.Exists(target)) return $"{Path.Combine("roles", stem + ".json")} exists, delete it first";

    //a card writes the user as a third-person name, so the placeholder becomes the name User and the grammar holds
    Func<string, string> fill = text => text
        .Replace("{{char}}", name, StringComparison.OrdinalIgnoreCase)
        .Replace("{{user}}", "User", StringComparison.OrdinalIgnoreCase);

    //only a constant entry is always in the prompt, so it alone goes in, in the card's insertion order
    var lore = new List<(double Order, string Content)>();
    var needKeyword = 0;
    var disabled = 0;
    if (data.TryGetProperty("character_book", out var book) && book.ValueKind == JsonValueKind.Object
        && book.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
        foreach (var e in entries.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object) continue;
            if (e.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.False) { disabled++; continue; }
            if (!(e.TryGetProperty("constant", out var co) && co.ValueKind == JsonValueKind.True)) { needKeyword++; continue; }
            var order = e.TryGetProperty("insertion_order", out var io) && io.ValueKind == JsonValueKind.Number ? io.GetDouble() : 0;
            if (str(e, "content") is { } content) lore.Add((order, content));
        }
    var loreText = lore.OrderBy(l => l.Order).Select(l => l.Content).ToList();

    var parts = new List<string>();
    var lines = new List<string>();
    foreach (var key in new[] { "system_prompt", "description", "personality", "scenario" })
        if (str(data, key) is { } part) { parts.Add(fill(part)); lines.Add($"append: {key}"); }
    if (loreText.Count > 0)
    {
        parts.AddRange(loreText.Select(fill));
        lines.Add($"append: {plural(loreText.Count, "lorebook entry", "lorebook entries")}");
    }
    if (str(data, "mes_example") is { } example) { parts.Add(fill(example)); lines.Add("append: mes_example"); }
    var append = parts.Count > 0 ? string.Join("\n\n", parts) : null;
    if (append is not null) lines.Add($"append: {Encoding.UTF8.GetByteCount(append).ToString("N0", CultureInfo.InvariantCulture)} bytes");

    //the role loader refuses a first message holding a control token, so it is dropped here and the file always loads
    var first = str(data, "first_mes") is { } fm ? fill(fm) : null;
    var leaked = first is not null && ControlTokens.Strip(first).Count > 0;
    if (leaked) first = null;
    if (first is not null) lines.Add("first_message: first_mes");
    if (append is null && first is null) lines.Add("raw only, the card has no text");

    var greetings = data.TryGetProperty("alternate_greetings", out var ag) && ag.ValueKind == JsonValueKind.Array
        ? ag.EnumerateArray().Count(g => g.ValueKind == JsonValueKind.String && g.GetString()!.Trim().Length > 0) : 0;
    if (needKeyword > 0) lines.Add($"dropped: {plural(needKeyword, "lorebook entry that needs", "lorebook entries that need")} a keyword");
    if (disabled > 0) lines.Add($"dropped: {plural(disabled, "disabled lorebook entry", "disabled lorebook entries")}");
    if (str(data, "post_history_instructions") is not null) lines.Add("dropped: post_history_instructions");
    if (greetings > 0) lines.Add($"dropped: {plural(greetings, "alternate greeting", "alternate greetings")}");
    if (leaked) lines.Add("dropped: first_mes, it holds a control token");
    if (isPng) lines.Add("dropped: the picture");

    //the escaping is relaxed so the card's own script stays readable in Notepad
    using var ms = new MemoryStream();
    using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true, NewLine = "\n", Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
    {
        w.WriteStartObject();
        w.WriteBoolean("raw", true);
        if (append is not null) w.WriteString("append", append);
        if (first is not null) w.WriteString("first_message", first);
        w.WriteEndObject();
    }
    ms.WriteByte((byte)'\n');
    Directory.CreateDirectory(rolesDir);
    File.WriteAllBytes(target, ms.ToArray());

    return string.Join("\n", new[] { $"wrote {target}" }.Concat(lines));
};

Gatto.Command("/card", "<path>", "write a role file from a character card", (arg, ct) =>
{
    try { return Task.FromResult(card(arg)); }
    catch (Exception ex) { return Task.FromResult($"card: {ex.Message}"); }
});
