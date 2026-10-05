using System.Text.RegularExpressions;

namespace Gatto.Core.Loop.Permissions;

//where a tool may reach: a file tool by its resolved path, a shell command only by its text, so the shell half is a screen and not a wall
internal sealed partial class WorkspaceBoundary(string workingFolder, Func<string, bool> granted, string? home)
{
    private readonly string _root = Terminate(Path.GetFullPath(workingFolder));
    private readonly string? _home = home is { Length: > 0 } h ? Terminate(Path.GetFullPath(h)) : null;

    //under the working folder or a folder the project granted, compared whole segments so a sibling with a longer name is outside
    public bool Inside(string fullPath) => Under(fullPath, _root) || granted(fullPath);

    //under gatto's own folder, which no tool writes
    public bool InHome(string fullPath) => _home is not null && Under(fullPath, _home);

    private static bool Under(string fullPath, string root) =>
        Terminate(Path.GetFullPath(fullPath)).StartsWith(root, StringComparison.OrdinalIgnoreCase);

    //true when the command's text reaches for something outside the working folder or a grant. a path built at run time from parts is not seen here
    public bool Leaves(string command)
    {
        var text = command.Replace('/', '\\');
        if (Upward().IsMatch(text) || Elsewhere().IsMatch(command)) return true;

        foreach (Match m in Rooted().Matches(text))
        {
            var rest = text[m.Index..];
            //the working folder spelled in full passes even with a space in it, a grant is read up to the first space or quote
            if (rest.StartsWith(_root[..^1], StringComparison.OrdinalIgnoreCase)
                && (rest.Length == _root.Length - 1 || rest[_root.Length - 1] is '\\' or ' ' or '"' or '\'' or ';' or '|' or ')')) continue;
            var end = rest.IndexOfAny([' ', '\t', '"', '\'', ';', '|', ')', ',']);
            if (!StartsWithRoot(end < 0 ? rest : rest[..end])) return true;
        }
        return false;
    }

    //true when the command's text names gatto's own folder, by its full path or by the names a shell expands to it
    public bool NamesHome(string command)
    {
        if (_home is null) return false;
        var text = command.Replace('/', '\\');
        var full = Regex.Escape(_home[..^1]) + @"(\\|$|[\s""';)|])";
        return Regex.IsMatch(text, full, RegexOptions.IgnoreCase) || HomeByName().IsMatch(text);
    }

    private bool StartsWithRoot(string path)
    {
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception) { return false; }
        return Inside(full);
    }

    private static string Terminate(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;

    //a drive letter and a separator, or a share. each hit is then compared with the working folder and the grants, since a model may spell them in full
    [GeneratedRegex(@"(?<![A-Za-z0-9])[A-Za-z]:\\|(?<![\\\w])\\\\\w")]
    private static partial Regex Rooted();

    //two dots as a whole segment, or a path that starts at the drive's root. one or two letters after a slash are a native flag and pass
    [GeneratedRegex(@"(^|[\s""'=(\\])\.\.($|[\s""'\\;)|])|(^|[\s""'=(])\\($|[\s""';)|]|\w{3,})")]
    private static partial Regex Upward();

    //the home folder and the environment by any of their names, a tilde counts only where a path can start
    [GeneratedRegex(@"(^|[\s""'=(])~($|[\s""'\\/])|\$home\b|\$env:|%\w+%|\[environment\]|\$psscriptroot|\$pshome", RegexOptions.IgnoreCase)]
    private static partial Regex Elsewhere();

    //the shell names for gatto's folder: the user's home then .gatto, or the variable that points the home elsewhere
    [GeneratedRegex(@"(~|\$home|\$env:userprofile|%userprofile%)\\\.gatto(\\|$|[\s""';)|])|\$env:gatto_home\b|%gatto_home%", RegexOptions.IgnoreCase)]
    private static partial Regex HomeByName();
}
