namespace Gatto.Repl.Term;

//reads .git/HEAD with no process spawn, and any IO error gives null so the status line can't take the REPL down
public static class GitBranch
{
    private const string RefHeadsPrefix = "ref: refs/heads/";
    private const string GitdirPrefix = "gitdir:";

    //from cwd upward, find a .git directory or file and read HEAD, giving a branch name or a 7-character sha when detached
    public static string? Read(string cwd)
    {
        try
        {
            var gitDir = FindGitDir(cwd);
            if (gitDir is null) return null;
            var headPath = Path.Combine(gitDir, "HEAD");
            if (!File.Exists(headPath)) return null;
            var head = File.ReadAllText(headPath).Trim();

            if (head.StartsWith(RefHeadsPrefix, StringComparison.Ordinal))
                return head[RefHeadsPrefix.Length..];

            return IsFullSha(head) ? head[..7] : null;
        }
        catch (Exception)
        {
            return null;   //a bad path, a permission denial or garbage content all give null
        }
    }

    //the .git directory, or the gitdir path inside a .git file for a worktree or submodule, resolved against that file's directory
    private static string? FindGitDir(string cwd)
    {
        var dir = Path.GetFullPath(cwd);
        while (true)
        {
            var dotGit = Path.Combine(dir, ".git");
            if (Directory.Exists(dotGit)) return dotGit;

            if (File.Exists(dotGit))
            {
                var content = File.ReadAllText(dotGit).Trim();
                if (!content.StartsWith(GitdirPrefix, StringComparison.Ordinal)) return null;
                var target = content[GitdirPrefix.Length..].Trim();
                if (target.Length == 0) return null;
                var resolved = Path.IsPathRooted(target) ? target : Path.GetFullPath(Path.Combine(dir, target));
                return Directory.Exists(resolved) ? resolved : null;
            }

            var parent = Directory.GetParent(dir);
            if (parent is null) return null;
            dir = parent.FullName;
        }
    }

    private static bool IsFullSha(string s)
    {
        if (s.Length != 40) return false;
        foreach (var c in s)
            if (!Uri.IsHexDigit(c)) return false;
        return true;
    }
}
