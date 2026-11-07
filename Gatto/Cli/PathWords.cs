namespace Gatto.Cli;

//how a screen spells a path, display only. nothing written to disk goes through here
internal static class PathWords
{
    //a path under the user's folder as ~ and the rest, the way a shell spells it. anything outside it, or the folder's own letters run on (you2), prints whole
    public static string Home(string path, string profile)
    {
        if (path.Length == 0 || profile.Length == 0) return path;
        var root = profile.TrimEnd('\\', '/');
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return path;
        if (path.Length == root.Length) return "~";
        return path[root.Length] is '\\' or '/' ? "~" + path[root.Length..] : path;
    }
}
