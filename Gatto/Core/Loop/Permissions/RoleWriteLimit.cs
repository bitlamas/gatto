using System.Text.Json;
using Gatto.Core.Client;
using Gatto.Core.Tools;

namespace Gatto.Core.Loop.Permissions;

//the folder a role's write_file and edit_file stay under, one function for the launch and a child. the disk is a parameter, so a test needs no real junction
public static class RoleWriteLimit
{
    //the two tools that write a file at a path the model chooses
    private static readonly HashSet<string> Writers = new(StringComparer.Ordinal) { "write_file", "edit_file" };

    //null when the call may go on, else the sentence the model reads. attributes returns null for a path with nothing there and throws for any other fault, which then blocks the call in the system's words
    public static string? Refusal(string cwd, IReadOnlyList<string> writeDirs, ToolCall call, Func<string, FileAttributes?> attributes)
    {
        if (writeDirs.Count == 0 || !Writers.Contains(call.Name) || PathOf(call) is not { } typed) return null;
        //the resolver the tools write through, so the check and the write cannot read a path two ways
        var target = Ended(ToolArgs.Resolve(cwd, typed));
        foreach (var dir in writeDirs)
            if (string.Equals(target, Ended(Path.GetFullPath(Path.Combine(cwd, dir))), StringComparison.OrdinalIgnoreCase))
                return $"{dir} is the folder this role writes under";
        foreach (var dir in writeDirs)
            if (!target.StartsWith(Ended(Path.GetFullPath(Path.Combine(cwd, dir))), StringComparison.OrdinalIgnoreCase))
                return writeDirs.Count == 1 ? $"this role writes only under {dir}" : $"a write must be inside both {string.Join(" and ", writeDirs)}";
        return ThroughLink(Ended(Path.GetFullPath(cwd)), target, attributes);
    }

    //each path below the working folder down to the target is asked, so a junction anywhere on the way refuses. the working folder and above are the user's choice of where to run
    private static string? ThroughLink(string root, string target, Func<string, FileAttributes?> attributes)
    {
        var asked = "";
        foreach (var segment in target[root.Length..].TrimEnd(Path.DirectorySeparatorChar).Split(Path.DirectorySeparatorChar))
        {
            //a name with a stream reads as absent while the write opens the stream through the link, so the segment is asked without it
            var colon = segment.IndexOf(':');
            var name = colon >= 0 ? segment[..colon] : segment;
            if (name.Length == 0) continue;
            asked = asked.Length == 0 ? name : Path.Combine(asked, name);
            if (attributes(root + asked) is { } found && IsLink(found))
                return $"{asked} is a junction or a link, and this role does not write through one";
        }
        return null;
    }

    //the one test of what a path is, over the attributes alone
    public static bool IsLink(FileAttributes attributes) => (attributes & FileAttributes.ReparsePoint) != 0;

    //the disk's answer, nothing asked before it, since an existence test is not the question and only the attributes say reparse
    public static FileAttributes? OnDisk(string path)
    {
        try { return File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    //the path argument as a string, or null so the loop and the tool report an argument they cannot read in their own words
    private static string? PathOf(ToolCall call)
    {
        try
        {
            using var doc = JsonDocument.Parse(call.ArgumentsJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String
                ? p.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    private static string Ended(string path) => path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;
}
