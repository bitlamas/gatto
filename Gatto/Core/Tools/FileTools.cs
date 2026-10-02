using System.Text.Json;

namespace Gatto.Core.Tools;

internal static class ToolArgs
{
    public static string RequiredString(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object &&
        args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()!
            : throw new ArgumentException(MissingParameter(name));

    //the words a tool says for an absent argument, which the permission gate says too when it refuses the call before asking
    public static string MissingParameter(string name) => $"missing required parameter: {name}";

    public static int? OptionalInt(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object &&
        args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32() : null;

    public static string Resolve(IToolContext ctx, string path) =>
        Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(ctx.Cwd, path));

    public static JsonElement Schema(string json) => JsonDocument.Parse(json).RootElement;

    //the last line of a capped text, one source for the cap and the parser
    internal const string TruncatedMarker = "[truncated]";

    //never split a surrogate pair, a lone one corrupts the request JSON downstream
    public static int SafeCut(string s, int cut) =>
        cut > 0 && cut < s.Length && char.IsHighSurrogate(s[cut - 1]) ? cut - 1 : cut;

    //the one place a tool body is capped, cut surrogate-safe and marked so the model knows it lost the rest
    public static string HeadCap(string s, int cap) =>
        s.Length <= cap ? s : s[..SafeCut(s, cap)] + Environment.NewLine + TruncatedMarker;
}

public sealed class ReadFileTool : ITool
{
    public string Name => "read_file";
    public string Description => "Read a file. Args: path (string), optional offset/limit (1-based line window).";
    public JsonElement ParametersSchema => ToolArgs.Schema("""
        {"type":"object","properties":{"path":{"type":"string"},"offset":{"type":"integer"},"limit":{"type":"integer"}},"required":["path"]}
        """);

    public async Task<ToolResult> ExecuteAsync(JsonElement args, IToolContext ctx, CancellationToken ct)
    {
        var path = ToolArgs.Resolve(ctx, ToolArgs.RequiredString(args, "path"));
        if (!File.Exists(path)) throw new FileNotFoundException($"file not found: {path}");
        //refuse unusable bytes before reading the body, the loop turns the throw into an IsError result with this message
        if (BinarySniff.DescribeFile(path) is string kind)
            throw new InvalidOperationException(
                $"{Path.GetFileName(path)} is a {kind} — read_file reads text, and decoding these bytes "
                + "would fill the context with unusable output. Use the shell for binary inspection "
                + "(file size, hashes, metadata).");
        var offset = ToolArgs.OptionalInt(args, "offset");
        var limit = ToolArgs.OptionalInt(args, "limit");
        string text;
        if (offset is null && limit is null)
            text = await File.ReadAllTextAsync(path, ct);
        else
        {
            var lines = await File.ReadAllLinesAsync(path, ct);
            var window = lines.Skip((offset ?? 1) - 1).Take(limit ?? lines.Length);
            text = string.Join(Environment.NewLine, window);
        }
        //the gloss counts the lines of the body the model actually received
        const int cap = 50_000;
        var truncated = text.Length > cap;
        var body = truncated ? text[..ToolArgs.SafeCut(text, cap)] : text;
        var lineCount = body.Length == 0 ? 0 : body.Split('\n').Length;
        var capped = truncated ? body + Environment.NewLine + "[truncated]" : body;
        return new ToolResult(capped, Gloss: truncated
            ? $"{Plural.Of(lineCount, "line")} (truncated)"
            : Plural.Of(lineCount, "line"));
    }
}

public sealed class WriteFileTool : ITool
{
    public string Name => "write_file";
    public string Description => "Write (create or overwrite) a file. Args: path, content.";
    public JsonElement ParametersSchema => ToolArgs.Schema("""
        {"type":"object","properties":{"path":{"type":"string"},"content":{"type":"string"}},"required":["path","content"]}
        """);

    public async Task<ToolResult> ExecuteAsync(JsonElement args, IToolContext ctx, CancellationToken ct)
    {
        var path = ToolArgs.Resolve(ctx, ToolArgs.RequiredString(args, "path"));
        var content = ToolArgs.RequiredString(args, "content");
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        //looked at before the write, so the block can say overwrote. the model's text keeps one word for both
        var existed = File.Exists(path);
        await File.WriteAllBytesAsync(path, bytes, ct);
        return new ToolResult($"wrote {Plural.Of(bytes.Length, "byte")} to {path}",
            Gloss: $"{(existed ? "overwrote" : "wrote")} {Plural.Of(bytes.Length, "byte")}");
    }
}

public sealed class EditFileTool : ITool
{
    public string Name => "edit_file";
    public string Description => "Replace an exact, unique string in a file. Args: path, old_string, new_string.";
    public JsonElement ParametersSchema => ToolArgs.Schema("""
        {"type":"object","properties":{"path":{"type":"string"},"old_string":{"type":"string"},"new_string":{"type":"string"}},"required":["path","old_string","new_string"]}
        """);

    public async Task<ToolResult> ExecuteAsync(JsonElement args, IToolContext ctx, CancellationToken ct)
    {
        var path = ToolArgs.Resolve(ctx, ToolArgs.RequiredString(args, "path"));
        var oldS = ToolArgs.RequiredString(args, "old_string");
        var newS = ToolArgs.RequiredString(args, "new_string");
        if (oldS.Length == 0) throw new ArgumentException(EditLocate.EmptyOld);
        if (!File.Exists(path)) throw new FileNotFoundException(EditLocate.NoFile(path));
        var text = await File.ReadAllTextAsync(path, ct);

        var (match, count) = EditLocate.Find(text, oldS, newS, ct);
        if (count == 0) throw new InvalidOperationException(EditLocate.NotFound(path));
        if (count > 1) throw new InvalidOperationException(EditLocate.NotUnique(count, path));
        //the view is taken from the text before the write, where the replaced lines still are
        var view = EditLocate.View(text, match!);
        await File.WriteAllTextAsync(path, text.Replace(match!.Old, match.New), ct);
        return new ToolResult($"edited {path}", Gloss: "ok", View: view);
    }
}
