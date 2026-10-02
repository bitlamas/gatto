using System.Text.RegularExpressions;

namespace Gatto.Core.Home;

//the project context files from cwd up to the drive root, one per directory, for the role composition to layer
public static class ContextFiles
{
    private const string Primary = "GATTO.md";
    private const string CompatFirstFallback = "AGENTS.md";
    private const string CompatSecondFallback = "CLAUDE.md";

    //cwd up to the drive root, root-first, one file per directory, with compat rolled down from any .gatto.json found on the way
    public static IReadOnlyList<(string Path, string Content)> Collect(
        string cwd, bool compat, string? homeGattoMdPath)
        => Collect(cwd, compat, homeGattoMdPath, out _);

    //the same collection plus the effective compat at cwd, read here so gatto doctor can't drift from the rule it reports
    public static IReadOnlyList<(string Path, string Content)> Collect(
        string cwd, bool compat, string? homeGattoMdPath, out bool effectiveCompatAtCwd)
    {
        var dirs = new List<string>();
        string? dir = Path.GetFullPath(cwd);
        while (dir is not null)
        {
            dirs.Add(dir);
            dir = Directory.GetParent(dir)?.FullName;
        }
        dirs.Reverse();   //root-first, so the rolling effective compat below composes correctly

        var rootFirst = new List<(string Path, string Content)>();
        var effectiveCompat = compat;
        foreach (var d in dirs)
        {
            //rolled before this directory's own candidate is resolved, a .gatto.json governs the directory that holds it as well as its descendants
            effectiveCompat = ProjectFileConfig.TryReadCompat(d) ?? effectiveCompat;

            var candidate = ResolveCandidate(d, effectiveCompat);
            if (candidate is not null && TryReadStripped(candidate, out var content))
                rootFirst.Add((candidate, content));
        }
        effectiveCompatAtCwd = effectiveCompat;   //dirs is root-first, so this is the effective compat at cwd

        //the home file is inserted first, before every ancestor, so a project's own file can't hide a user's global notes
        if (homeGattoMdPath is not null && File.Exists(homeGattoMdPath)
            && TryReadStripped(homeGattoMdPath, out var homeContent))
        {
            //normalize this path the same way as the rest, so a relative home path can't break the absolute-path guarantee
            var homeFull = Path.GetFullPath(homeGattoMdPath);
            //drop the collected copy when GATTO_HOME is an ancestor of cwd, so the home file stays at position 0 either way
            rootFirst.RemoveAll(e => string.Equals(e.Path, homeFull, StringComparison.OrdinalIgnoreCase));
            rootFirst.Insert(0, (homeFull, homeContent));
        }

        return rootFirst;
    }

    //the fallback candidates sitting beside a collected GATTO.md, which were not loaded whatever the effective compat was
    public static IReadOnlyList<string> ShadowedFallbacks(IEnumerable<string> collectedPaths)
    {
        var shadowed = new List<string>();
        foreach (var path in collectedPaths)
        {
            if (!string.Equals(Path.GetFileName(path), Primary, StringComparison.OrdinalIgnoreCase)) continue;
            var dir = Path.GetDirectoryName(path);
            if (dir is null) continue;
            foreach (var fallback in new[] { CompatFirstFallback, CompatSecondFallback })
            {
                var candidate = Path.Combine(dir, fallback);
                if (File.Exists(candidate)) shadowed.Add(candidate);
            }
        }
        return shadowed;
    }

    private static string? ResolveCandidate(string dir, bool compat)
    {
        var gatto = Path.Combine(dir, Primary);
        if (File.Exists(gatto)) return gatto;
        if (!compat) return null;

        var agents = Path.Combine(dir, CompatFirstFallback);
        if (File.Exists(agents)) return agents;

        var claude = Path.Combine(dir, CompatSecondFallback);
        if (File.Exists(claude)) return claude;

        return null;
    }

    private static readonly Regex BlockComment =
        new("<!--.*?-->", RegexOptions.Singleline | RegexOptions.Compiled);

    //block HTML comments are stripped before the file reaches the model, and an unterminated opener stays as text
    internal static string StripComments(string content) => BlockComment.Replace(content, "");

    //comment-only means the strip shortened the text and what is left is whitespace, so an already-blank file is not comment-only
    private static bool IsCommentOnly(string raw, string stripped) =>
        stripped.Length != raw.Length && string.IsNullOrWhiteSpace(stripped);

    //the read and strip every collected file goes through: a file the strip empties contributes nothing, one that was already blank keeps its entry
    private static bool TryReadStripped(string path, out string content)
    {
        if (!TryRead(path, out var raw)) { content = string.Empty; return false; }
        content = StripComments(raw);
        return !IsCommentOnly(raw, content);
    }

    private static bool TryRead(string path, out string content)
    {
        try
        {
            content = File.ReadAllText(path);
            return true;
        }
        catch (IOException)
        {
            //a locked or sharing-violating file is skipped, a launch never dies on this
        }
        catch (UnauthorizedAccessException)
        {
            //ACL denial, skipped the same way
        }

        content = string.Empty;
        return false;
    }

    //why a path the caller expected is missing from the set, and Loadable means the file would have contributed
    public enum MissingReason { Absent, Unreadable, CommentOnly, Loadable }

    //the files above cwd that the walk picks and the strip empties, by the walk Collect makes, so doctor can name what loads nothing
    public static IReadOnlyList<string> CommentOnlyAncestors(string cwd, bool compat)
    {
        var dirs = new List<string>();
        for (var dir = Directory.GetParent(Path.GetFullPath(cwd))?.FullName; dir is not null; dir = Directory.GetParent(dir)?.FullName)
            dirs.Add(dir);
        dirs.Reverse();   //root-first, the order the rolling compat composes in
        var found = new List<string>();
        var effectiveCompat = compat;
        foreach (var d in dirs)
        {
            effectiveCompat = ProjectFileConfig.TryReadCompat(d) ?? effectiveCompat;
            if (ResolveCandidate(d, effectiveCompat) is { } candidate && WhyMissing(candidate) == MissingReason.CommentOnly)
                found.Add(candidate);
        }
        return found;
    }

    //classifies a path that is not in the collected set, running the same read and strip so the answer can't drift
    public static MissingReason WhyMissing(string path)
    {
        if (!File.Exists(path)) return MissingReason.Absent;
        if (!TryRead(path, out var raw)) return MissingReason.Unreadable;
        var stripped = StripComments(raw);
        return IsCommentOnly(raw, stripped) ? MissingReason.CommentOnly : MissingReason.Loadable;
    }
}
