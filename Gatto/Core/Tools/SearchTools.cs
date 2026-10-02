using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Gatto.Core.Tools;

internal static class Globbing
{
    //keep this list short, a skipped directory hides real files. tmp is in it because test output goes to tmp/testbin, and LooksBinary is the general guard
    private static readonly string[] SkipDirs = { ".git", "bin", "obj", "node_modules", "tmp" };

    public static Regex ToRegex(string glob)
    {
        var sb = new StringBuilder("^");
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                sb.Append(".*");
                i++;
                if (i + 1 < glob.Length && (glob[i + 1] == '/' || glob[i + 1] == '\\')) i++; //the slash in **/ is optional, so it also matches zero directories
            }
            else if (c == '*') sb.Append(@"[^/\\]*");
            else if (c == '?') sb.Append(@"[^/\\]");
            else if (c == '/' || c == '\\') sb.Append(@"[/\\]");
            else sb.Append(Regex.Escape(c.ToString()));
        }
        return new Regex(sb.Append('$').ToString(), RegexOptions.IgnoreCase);
    }

    public static IEnumerable<string> Walk(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            string[] subs, files;
            try
            {
                subs = Directory.GetDirectories(dir);
                files = Directory.GetFiles(dir);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or DirectoryNotFoundException)
            {
                continue;   //an unreadable or vanished directory is skipped, the walk continues
            }
            foreach (var sub in subs)
            {
                if (SkipDirs.Contains(Path.GetFileName(sub))) continue;
                if ((new DirectoryInfo(sub).Attributes & FileAttributes.ReparsePoint) != 0) continue;   //skip junction and symlink directories so the walk cannot loop
                stack.Push(sub);
            }
            foreach (var file in files) yield return file;
        }
    }

    public const string NoMatches = "no matches";

    public static string Capped(List<string> lines, int cap = 200) =>
        lines.Count == 0 ? NoMatches
        : lines.Count <= cap ? string.Join(Environment.NewLine, lines)
        : string.Join(Environment.NewLine, lines.Take(cap)) + Environment.NewLine + $"[capped at {cap} matches]";

    //a NUL byte in the first SniffBytes marks a file as binary, so a grep never reads an assembly as text. a UTF-16 BOM comes first, its text is full of NULs
    public static bool LooksBinary(string path)
    {
        const int SniffBytes = 8192;
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> head = stackalloc byte[SniffBytes];
            var read = fs.Read(head);
            if (read >= 2 && ((head[0] == 0xFF && head[1] == 0xFE) || (head[0] == 0xFE && head[1] == 0xFF)))
                return false;   //the BOM check comes first because UTF-16 text is full of NULs
            return head[..read].IndexOf((byte)0) >= 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }
}

public sealed class GlobTool : ITool
{
    public string Name => "glob";
    public string Description => "Find files by glob pattern (** crosses directories). Args: pattern, optional root.";
    public JsonElement ParametersSchema => ToolArgs.Schema("""
        {"type":"object","properties":{"pattern":{"type":"string"},"root":{"type":"string"}},"required":["pattern"]}
        """);

    public Task<ToolResult> ExecuteAsync(JsonElement args, IToolContext ctx, CancellationToken ct)
    {
        var pattern = ToolArgs.RequiredString(args, "pattern");
        var root = ToolArgs.Resolve(ctx,
            args.TryGetProperty("root", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString()! : ".");
        if (!Directory.Exists(root)) throw new ArgumentException($"root not found: {root}");
        var regex = Globbing.ToRegex(pattern);
        var matches = new List<string>();
        foreach (var f in Globbing.Walk(root))
        {
            ct.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(root, f);
            if (regex.IsMatch(rel))
                matches.Add(rel);
        }
        matches.Sort(StringComparer.OrdinalIgnoreCase);
        return Task.FromResult(new ToolResult(Globbing.Capped(matches), Gloss: Plural.Of(matches.Count, "file")));
    }
}

public sealed class GrepTool : ITool
{
    public string Name => "grep";
    public string Description => "Search file contents by regex. Args: pattern (regex), optional root, optional glob filter (matches the path relative to root; a slashless pattern like *.cs matches at any depth).";
    public JsonElement ParametersSchema => ToolArgs.Schema("""
        {"type":"object","properties":{"pattern":{"type":"string"},"root":{"type":"string"},"glob":{"type":"string"}},"required":["pattern"]}
        """);

    public Task<ToolResult> ExecuteAsync(JsonElement args, IToolContext ctx, CancellationToken ct)
    {
        var pattern = ToolArgs.RequiredString(args, "pattern");
        Regex regex;
        try { regex = new Regex(pattern, RegexOptions.None, TimeSpan.FromSeconds(2)); }
        catch (ArgumentException ex) { throw new ArgumentException($"invalid regex: {ex.Message}"); }
        var root = ToolArgs.Resolve(ctx,
            args.TryGetProperty("root", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString()! : ".");
        if (!Directory.Exists(root)) throw new ArgumentException($"root not found: {root}");
        Regex? fileFilter = null;
        if (args.TryGetProperty("glob", out var g) && g.ValueKind == JsonValueKind.String)
        {
            var globStr = g.GetString()!;
            if (!globStr.Contains('/') && !globStr.Contains('\\')) globStr = "**/" + globStr;
            fileFilter = Globbing.ToRegex(globStr);
        }

        var outLines = new List<string>();
        var totalChars = 0;
        foreach (var file in Globbing.Walk(root))
        {
            ct.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(root, file);
            if (fileFilter is not null && !fileFilter.IsMatch(rel)) continue;
            if (Globbing.LooksBinary(file)) continue;
            string[] lines;
            try { lines = File.ReadAllLines(file); } catch { continue; }
            for (var i = 0; i < lines.Length; i++)
                if (regex.IsMatch(lines[i]))
                {
                    var row = $"{rel}:{i + 1}: {Clip(lines[i].TrimEnd())}";
                    //both caps are ceilings on what is delivered, so the row that would pass one is the one cut and named
                    if (outLines.Count == MaxMatches) return Done(outLines, $"[capped at {MaxMatches} matches]");
                    if (totalChars + row.Length > MaxTotalChars) return Done(outLines, $"[capped at {MaxTotalChars} chars — narrow the pattern or the glob]");
                    outLines.Add(row);
                    totalChars += row.Length;
                }
        }
        return Task.FromResult(new ToolResult(Globbing.Capped(outLines), Gloss: Plural.Of(outLines.Count, "match", "matches")));
    }

    //all three bounds matter, a match count cap alone still lets a few enormous lines through
    internal const int MaxMatches = 200;
    internal const int MaxLineChars = 300;    //path:line is the payload, and the model can read_file for more
    internal const int MaxTotalChars = 50_000;   //the same budget read_file and shell use

    private static string Clip(string line) =>
        line.Length <= MaxLineChars ? line : line[..SafeCut(line, MaxLineChars)] + "…";

    //a cut index that keeps a surrogate pair together, so the result JSON never holds a lone surrogate
    private static int SafeCut(string s, int cut) => char.IsHighSurrogate(s[cut - 1]) ? cut - 1 : cut;

    private static Task<ToolResult> Done(List<string> lines, string why) =>
        Task.FromResult(new ToolResult(
            string.Join(Environment.NewLine, lines) + Environment.NewLine + why,
            Gloss: Plural.Of(lines.Count, "match", "matches")));
}
