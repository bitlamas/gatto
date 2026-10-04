using System.Text.Json;
using System.Text.RegularExpressions;
using Gatto.Core.Loop;

namespace Gatto.Roles.Audition;

//holds one task's tools inside its folder, a file tool by its resolved path and a shell command only by its text
internal sealed partial class ScratchBoundary(string scratch)
{
    //what the model reads when a call is refused, worded as a fact about the folder
    public const string Refused = "that is outside the current directory, and this task has nothing outside it";

    private readonly string _root = Terminate(Path.GetFullPath(scratch));

    //the argument that names where each file tool works, an absent one means the folder itself
    private static readonly Dictionary<string, string> PathArgument = new(StringComparer.Ordinal)
    {
        ["read_file"] = "path", ["write_file"] = "path", ["edit_file"] = "path",
        ["glob"] = "root", ["grep"] = "root",
    };

    public Task CheckAsync(HookPayload payload)
    {
        if (payload.Call is not { } call) return Task.CompletedTask;

        JsonElement args;
        try
        {
            using var doc = JsonDocument.Parse(call.ArgumentsJson.Length > 0 ? call.ArgumentsJson : "{}");
            args = doc.RootElement.Clone();
        }
        catch (JsonException) { return Task.CompletedTask; }   //a call that does not parse never runs, the loop reports it in its own words

        if (PathArgument.TryGetValue(call.Name, out var name))
        {
            if (TryGetString(args, name, out var path) && !Inside(path)) throw new InvalidOperationException(Refused);
        }
        else if (call.Name == "shell" && TryGetString(args, "command", out var command) && Leaves(command))
            throw new InvalidOperationException(Refused);

        return Task.CompletedTask;
    }

    //resolved the way the file tools resolve it, then compared by whole segments (a sibling with a longer name is outside)
    internal bool Inside(string path)
    {
        string full;
        try { full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(_root, path)); }
        catch (Exception) { return false; }
        return Terminate(full).StartsWith(_root, StringComparison.OrdinalIgnoreCase);
    }

    //true when the command's text reaches for something outside the folder. a path built at run time from parts is not seen here
    internal bool Leaves(string command)
    {
        var text = command.Replace('/', '\\');
        if (Upward().IsMatch(text) || Elsewhere().IsMatch(command)) return true;

        foreach (Match m in Rooted().Matches(text))
            if (!text.AsSpan(m.Index).StartsWith(_root.AsSpan(0, _root.Length - 1), StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static string Terminate(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;

    private static bool TryGetString(JsonElement obj, string name, out string value)
    {
        value = "";
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String)
            return false;
        value = v.GetString()!;
        return true;
    }

    //a drive letter and a separator, or a share, each hit is then compared with the folder
    [GeneratedRegex(@"(?<![A-Za-z0-9])[A-Za-z]:\\|(?<![\\\w])\\\\\w")]
    private static partial Regex Rooted();

    //two dots as a whole segment, or a path from the drive's root (one or two letters after a slash are a native flag)
    [GeneratedRegex(@"(^|[\s""'=(\\])\.\.($|[\s""'\\;)|])|(^|[\s""'=(])\\($|[\s""';)|]|\w{3,})")]
    private static partial Regex Upward();

    //the home folder and the environment by any of their names, a tilde counts only where a path can start
    [GeneratedRegex(@"(^|[\s""'=(])~($|[\s""'\\/])|\$home\b|\$env:|%\w+%|\[environment\]|\$psscriptroot|\$pshome", RegexOptions.IgnoreCase)]
    private static partial Regex Elsewhere();
}
