using System.Text.Json;

namespace Gatto.Core.Tools;

//one rule for two surfaces: the prompt shows these lines before the write and the transcript after, and both must agree
public static class FilePreview
{
    //how many leading lines a preview shows, shared by the prompt and the transcript so changing it moves both
    public const int LineCount = 5;

    //the argument holding the text write_file or edit_file will commit, and null for any other tool rather than a guessed body
    public static string? SizingArgument(string tool) => tool switch
    {
        "write_file" => "content",
        "edit_file" => "new_string",
        _ => null,
    };

    //preview from already-parsed arguments, the path the gate takes
    public static (IReadOnlyList<string>? Lines, int Total) Of(string tool, JsonElement args)
    {
        if (SizingArgument(tool) is not { } sizingArg) return (null, 0);
        if (args.ValueKind != JsonValueKind.Object
            || !args.TryGetProperty(sizingArg, out var el)
            || el.ValueKind != JsonValueKind.String
            || el.GetString() is not { } text)
            return (null, 0);

        var total = CountLines(text);
        if (total == 0) return (null, 0);

        var split = text.Split('\n');
        var take = Math.Min(LineCount, total);
        var lines = new string[take];
        Array.Copy(split, lines, take);
        return (lines, total);
    }

    //derived from the persisted arguments so no new persistence, and malformed JSON yields no preview instead of throwing
    public static (IReadOnlyList<string>? Lines, int Total) Of(string tool, string argumentsJson)
    {
        if (SizingArgument(tool) is null) return (null, 0);
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson.Length > 0 ? argumentsJson : "{}");
            return Of(tool, doc.RootElement);
        }
        catch (JsonException)
        {
            return (null, 0);
        }
    }

    //a trailing newline ends its line rather than starting an empty one, so the count matches what the reader sees
    public static int CountLines(string s)
    {
        if (s.Length == 0) return 0;
        var n = 0;
        foreach (var ch in s) if (ch == '\n') n++;
        if (s[^1] != '\n') n++;   //a final unterminated line still counts
        return n;
    }
}
