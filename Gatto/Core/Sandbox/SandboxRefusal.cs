using System.Text;

namespace Gatto.Core.Sandbox;

//the line a sandboxed result gains when its stderr shows a refused write, read from the stderr section alone
internal static class SandboxRefusal
{
    //type names and error ids, which a localised Windows keeps in English. no bare phrase alone, since a file read back can hold one
    private static readonly string[] WriteIds =
        ["UnauthorizedAccessException", "PermissionDenied", "MSB3191", "MSB3021", "PermissionError:[Errno13]", "EPERM"];

    //each is a refusal only beside the words that say a write was refused
    private static readonly string[] NativeIds = ["NativeCommandFailed"];
    private static readonly string[] AccessWords = ["Accessisdenied", "Accesstothepath"];

    //git and other POSIX tools say it in these words, with no id of their own
    private const string PosixWords = "Permissiondenied";

    //the hive's name arrives cut to HKEY_CURRENT_US... in the CategoryInfo line, so the prefix is what matches
    private static readonly string[] RegistryWords = ["HKEY_CURRENT_US", "HKCU:", "Registry::"];

    public static string WriteLine(string project) =>
        $"sandbox: writes outside {project}, the NuGet cache and sandbox_grants are refused.";

    public const string RegistryLine = "sandbox: writes to the registry are refused.";

    //null when nothing matched, one line otherwise, the registry's when the refusal names a key. whitespace goes before matching, since a long id wraps at the console's width
    public static string? Note(string stderr, string project, IReadOnlyList<string>? roots = null)
    {
        var flat = Flatten(stderr);
        var refused = WriteIds.Any(id => flat.Contains(id, StringComparison.Ordinal))
            || (NativeIds.Any(id => flat.Contains(id, StringComparison.Ordinal)) && AccessWords.Any(w => flat.Contains(w, StringComparison.Ordinal)))
            || flat.Contains(PosixWords, StringComparison.Ordinal);
        if (!refused) return null;
        if (RegistryWords.Any(w => flat.Contains(w, StringComparison.OrdinalIgnoreCase))) return RegistryLine;
        //a refused path inside the labelled folders is not the sandbox's refusal but a file the user labelled or another process holds
        var paths = RefusedPaths(stderr);
        IReadOnlyList<string> inside = roots is { Count: > 0 } ? roots : [project];
        return paths.Count > 0 && paths.All(p => inside.Any(r => Under(p, r))) ? null : WriteLine(project);
    }

    //the paths the refusal names, in the forms .NET, MSBuild, python and the POSIX tools write them. lines are joined since PowerShell wraps at the console's width
    internal static IReadOnlyList<string> RefusedPaths(string stderr)
    {
        var text = stderr.Replace("\r", "").Replace("\n", "");
        var found = new List<string>();
        Collect(text, "Access to the path", '\'', '\'', found);
        Collect(text, "Unable to create directory", '"', '"', found);
        Collect(text, "\" to", '"', '"', found);
        Collect(text, "Permission denied:", '\'', '\'', found);
        for (var at = text.IndexOf("': Permission denied", StringComparison.Ordinal); at > 0;
             at = text.IndexOf("': Permission denied", at + 1, StringComparison.Ordinal))
            if (text.LastIndexOf('\'', at - 1) is var open and >= 0) Keep(text[(open + 1)..at], found);
        return found;
    }

    //the text between the quotes that follow the marker, spaces allowed before the opening one
    private static void Collect(string text, string marker, char open, char close, List<string> found)
    {
        for (var at = text.IndexOf(marker, StringComparison.Ordinal); at >= 0; at = text.IndexOf(marker, at + 1, StringComparison.Ordinal))
        {
            var i = at + marker.Length;
            while (i < text.Length && text[i] == ' ') i++;
            if (i >= text.Length || text[i] != open) continue;
            var end = text.IndexOf(close, i + 1);
            if (end > i) Keep(text[(i + 1)..end], found);
        }
    }

    //only a full path can be placed inside or outside a folder, so a relative one counts as no path
    private static void Keep(string candidate, List<string> found)
    {
        try { if (Path.IsPathFullyQualified(candidate)) found.Add(Path.GetFullPath(candidate)); }
        catch (ArgumentException) { }
    }

    private static bool Under(string path, string root)
    {
        var r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        return path.Equals(r, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string Flatten(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text) if (!char.IsWhiteSpace(c)) sb.Append(c);
        return sb.ToString();
    }
}
