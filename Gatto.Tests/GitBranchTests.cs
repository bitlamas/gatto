using Gatto.Repl.Term;

namespace Gatto.Tests;

public class GitBranchTests
{
    [Fact]
    public void RefHead_ReturnsBranchName()
    {
        using var repo = TempRepo.Create();
        repo.WriteGitDir();
        repo.WriteHead("ref: refs/heads/main\n");
        Assert.Equal("main", GitBranch.Read(repo.Path));
    }

    [Fact]
    public void RefHead_BranchNameWithSlash_ReturnsFullName()
    {
        using var repo = TempRepo.Create();
        repo.WriteGitDir();
        repo.WriteHead("ref: refs/heads/feature/foo\n");
        Assert.Equal("feature/foo", GitBranch.Read(repo.Path));
    }

    [Fact]
    public void DetachedHead_ReturnsShortSha()
    {
        using var repo = TempRepo.Create();
        repo.WriteGitDir();
        repo.WriteHead("9adbefb09adbefb09adbefb09adbefb09adbefb0\n");
        Assert.Equal("9adbefb", GitBranch.Read(repo.Path));
    }

    [Fact]
    public void GitFile_WithGitdir_FollowsToHead()
    {
        using var repo = TempRepo.Create();
        var realGitDir = Path.Combine(repo.Path, "real-git");
        Directory.CreateDirectory(realGitDir);
        File.WriteAllText(Path.Combine(realGitDir, "HEAD"), "ref: refs/heads/worktree-branch\n");
        File.WriteAllText(Path.Combine(repo.Path, ".git"), $"gitdir: {realGitDir}\n");
        Assert.Equal("worktree-branch", GitBranch.Read(repo.Path));
    }

    [Fact]
    public void GitFile_WithRelativeGitdir_ResolvesRelativeToGitFileDir()
    {
        using var repo = TempRepo.Create();
        var sub = Path.Combine(repo.Path, "sub");
        Directory.CreateDirectory(sub);
        var realGitDir = Path.Combine(repo.Path, ".git", "worktrees", "wt");
        Directory.CreateDirectory(realGitDir);
        File.WriteAllText(Path.Combine(realGitDir, "HEAD"), "ref: refs/heads/relative-branch\n");
        File.WriteAllText(Path.Combine(sub, ".git"), "gitdir: ../.git/worktrees/wt\n");
        Assert.Equal("relative-branch", GitBranch.Read(sub));
    }

    [Fact]
    public void GitFile_GitdirTargetMissing_ReturnsNull()
    {
        using var repo = TempRepo.Create();
        File.WriteAllText(Path.Combine(repo.Path, ".git"), "gitdir: does-not-exist\n");
        Assert.Null(GitBranch.Read(repo.Path));
    }

    [Fact]
    public void NoGitAnywhere_ReturnsNull()
    {
        using var repo = TempRepo.Create();
        Assert.Null(GitBranch.Read(repo.Path));
    }

    [Fact]
    public void WalksUpFromSubdirectory_FindsAncestorGit()
    {
        using var repo = TempRepo.Create();
        repo.WriteGitDir();
        repo.WriteHead("ref: refs/heads/main\n");
        var sub = Path.Combine(repo.Path, "a", "b", "c");
        Directory.CreateDirectory(sub);
        Assert.Equal("main", GitBranch.Read(sub));
    }

    [Fact]
    public void GarbageHead_ReturnsNull()
    {
        using var repo = TempRepo.Create();
        repo.WriteGitDir();
        repo.WriteHead("not a valid head line at all\n");
        Assert.Null(GitBranch.Read(repo.Path));
    }

    [Fact]
    public void MissingHeadFile_ReturnsNull()
    {
        using var repo = TempRepo.Create();
        repo.WriteGitDir();   //the .git directory exists, but it holds no HEAD file
        Assert.Null(GitBranch.Read(repo.Path));
    }

    [Fact]
    public void DriveRoot_NoGit_ReturnsNullWithoutThrowing()
    {
        var root = Path.GetPathRoot(Path.GetTempPath())!;
        Assert.Null(GitBranch.Read(root));
    }

    private sealed class TempRepo : IDisposable
    {
        public string Path { get; }
        private TempRepo(string path) => Path = path;

        public static TempRepo Create() => new(Directory.CreateTempSubdirectory("gatto-gitbranch-").FullName);

        public void WriteGitDir() => Directory.CreateDirectory(System.IO.Path.Combine(Path, ".git"));
        public void WriteHead(string content) => File.WriteAllText(System.IO.Path.Combine(Path, ".git", "HEAD"), content);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
