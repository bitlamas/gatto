using System.Text.Json;
using Gatto.Core.Loop;
using Gatto.Core.Loop.Permissions;

namespace Gatto.Roles.Audition;

//holds one task's tools inside its folder, a file tool by its resolved path and a shell command only by its text
internal sealed class ScratchBoundary(string scratch)
{
    //what the model reads when a call is refused, worded as a fact about the folder
    public const string Refused = "that is outside the current directory, and this task has nothing outside it";

    private readonly string _root = Terminate(Path.GetFullPath(scratch));

    //the gate's own reading of a command's text, one copy of the patterns, with the folder as the only place inside and no grants
    private readonly WorkspaceBoundary _shell = new(scratch, _ => false, home: null);

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
    internal bool Leaves(string command) => _shell.Leaves(command);

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
}
