using System.Diagnostics;
using System.Text;

namespace Gatto.Tests.Census;

//the public tree holds the entries and kinds listed here and nothing else, read from the git index so build output and scratch never count
public class TreeCensusTests
{
    private static readonly HashSet<string> RootEntries = new(StringComparer.Ordinal)
    {
        "Gatto", "Gatto.Terminal", "Gatto.Tests", "Gatto.sln", "README.md", "LICENSE", ".editorconfig", ".gitattributes", ".gitignore",
    };

    private static readonly string[] Projects = ["Gatto", "Gatto.Terminal", "Gatto.Tests"];

    //a kind other than code is tied to the one folder that may hold it, never to a subtree, so a new folder starts with code only
    private static readonly Dictionary<string, string[]> Kinds = new(StringComparer.Ordinal)
    {
        ["Gatto/Core/Acquire"] = [".json"],
        ["Gatto/Core/Home"] = [".json"],
        ["Gatto/Shipped/agents"] = [".md"],
        ["Gatto/Shipped/extensions"] = [".csx"],
        ["Gatto/Shipped/roles"] = [".json"],
        ["Gatto.Tests/Copy/Goldens"] = [".txt"],
        ["Gatto.Tests/Fixtures"] = [".csx", ".gguf", ".html", ".json", ".jsonl", ".txt"],
        ["Gatto.Tests/Fixtures/dn4"] = [".gguf"],
        ["Gatto.Tests/Fixtures/hwprobe"] = [".txt"],
        ["Gatto.Tests/Fixtures/profiles"] = [".json"],
        ["Gatto.Tests/Render/FallbackCorpus"] = [".txt"],
        ["Gatto.Tests/Render/JavaScriptOracle"] = [".cjs", ".json", ".txt"],
        ["Gatto.Tests/Render/PowerShellOracle"] = [".ps1", ".json", ".txt"],
        ["Gatto.Tests/Render/PythonOracle"] = [".py", ".json", ".txt"],
        ["Gatto.Tests/Setup/Tui/Goldens"] = [".json", ".txt"],
    };

    //every path the list does not allow, each with the reason, empty when the tree holds only what it may
    internal static List<string> Violations(IEnumerable<string> paths)
    {
        var bad = new List<string>();
        foreach (var path in paths)
        {
            var parts = path.Split('/');
            if (!RootEntries.Contains(parts[0]))
            {
                bad.Add($"{path}: '{parts[0]}' is not one of the root entries");
                continue;
            }
            if (parts.Length == 1) continue;
            if (!Projects.Contains(parts[0]))
            {
                bad.Add($"{path}: '{parts[0]}' is a root file, and a file holds nothing below it");
                continue;
            }

            var dir = string.Join('/', parts[..^1]);
            var name = parts[^1];
            var kind = Path.GetExtension(name);
            if (kind == ".cs") continue;
            if (kind == ".csproj" && dir == parts[0] && name == parts[0] + ".csproj") continue;
            if (Kinds.TryGetValue(dir, out var allowed) && allowed.Contains(kind)) continue;
            bad.Add($"{path}: the kind '{(kind.Length > 0 ? kind : name)}' is not listed for {dir}");
        }
        return bad;
    }

    //the paths git tracks under root, staged ones included, and a failure when git cannot answer, since a census that reads nothing passes
    internal static List<string> IndexPaths(string root)
    {
        var git = Process.Start(new ProcessStartInfo
        {
            FileName = "git",
            Arguments = "-c core.quotepath=off ls-files -z",
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
        });
        Assert.NotNull(git);
        var output = git!.StandardOutput.ReadToEnd();
        var error = git.StandardError.ReadToEnd();
        git.WaitForExit();
        Assert.True(git.ExitCode == 0, $"git ls-files failed in {root}: {error}");
        return [.. output.Split('\0', StringSplitOptions.RemoveEmptyEntries)];
    }

    //folders a build or a test run writes into a tree, left out wherever they stand
    private static readonly HashSet<string> BuildOutput = new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", "tmp", "TestResults" };

    //folders an editor leaves in a tree a person opened, skipped by the disk reader only, so a tracked one still fails
    private static readonly HashSet<string> EditorState = new(StringComparer.OrdinalIgnoreCase) { ".vs", ".idea", ".vscode" };

    //every file under root, for a tree with no repository (a source zip), with build output and editor state left out
    internal static List<string> DiskPaths(string root) =>
        [.. Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .Where(rel => !rel.Split('/')[..^1].Any(s => BuildOutput.Contains(s) || EditorState.Contains(s)))
            .OrderBy(rel => rel, StringComparer.Ordinal)];

    //a .git folder or file makes the root a repository, a linked worktree's .git is a file, and a repository whose git fails stays a failure
    internal static bool IsRepository(string root) =>
        Directory.Exists(Path.Combine(root, ".git")) || File.Exists(Path.Combine(root, ".git"));

    internal static List<string> TreePaths(string root) => IsRepository(root) ? IndexPaths(root) : DiskPaths(root);

    [Fact]
    public void THE_PUBLIC_TREE_HOLDS_ONLY_WHAT_THE_LIST_NAMES()
    {
        var bad = Violations(TreePaths(SourceTree.RepoRoot()));
        Assert.True(bad.Count == 0, "the tree holds entries the list does not name:\n  " + string.Join("\n  ", bad));
    }

    //the census must read the tree it claims to read, or a tree of one file passes
    [Fact]
    public void THE_READER_HOLDS_THE_TREE()
    {
        var paths = TreePaths(SourceTree.RepoRoot());
        Assert.Contains("Gatto.sln", paths);
        Assert.Contains("Gatto/Gatto.csproj", paths);
        Assert.Contains("Gatto.Tests/Census/SourceTree.cs", paths);
        Assert.True(paths.Count > 1000, $"the index listed {paths.Count} paths");
        Assert.DoesNotContain(paths, p => p.Contains("/bin/", StringComparison.Ordinal) || p.Contains("/obj/", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("tools/build.py")]
    [InlineData("docs/plan.md")]
    [InlineData("NOTES.md")]
    [InlineData("Gatto.Tests/Fixtures/generate.py")]
    [InlineData("Gatto.Tests/Setup/Tui/Goldens/generate.py")]
    [InlineData("Gatto/Core/Tools/notes.md")]
    [InlineData("Gatto/Core/NewFolder/data.json")]
    [InlineData("Gatto.Tests/Fixtures/newfolder/capture.json")]
    [InlineData("Gatto.Tests/Gatto.csproj")]
    [InlineData("Gatto/Core/Makefile")]
    public void AN_ENTRY_THE_LIST_DOES_NOT_NAME_FAILS(string path) => Assert.Single(Violations([path]));

    [Theory]
    [InlineData("README.md")]
    [InlineData("Gatto/Core/NewFolder/Thing.cs")]
    [InlineData("Gatto.Tests/Gatto.Tests.csproj")]
    [InlineData("Gatto.Tests/Render/PythonOracle/generate.py")]
    [InlineData("Gatto.Tests/Fixtures/new-capture.json")]
    public void AN_ENTRY_THE_LIST_NAMES_PASSES(string path) => Assert.Empty(Violations([path]));

    //a real repository in a scratch folder, so the path from the index to a failure is driven whole
    [Fact]
    public void A_PLANTED_ROOT_FOLDER_AND_A_PLANTED_KIND_FAIL_IN_A_SCRATCH_REPOSITORY()
    {
        var dir = Directory.CreateTempSubdirectory("gatto-tree-").FullName;
        try
        {
            foreach (var (rel, text) in new[]
                     {
                         ("README.md", "readme"),
                         ("Gatto/Thing.cs", "class Thing { }"),
                         ("scratch/notes.txt", "a planted folder"),
                         ("Gatto.Tests/Fixtures/planted.xml", "<planted/>"),
                     })
                Plant(dir, rel, text);
            Assert.True(Git(dir, "init -q") && Git(dir, "add -A"), "git could not make the scratch repository");

            //a file git does not track is not in the index, so the census reading it proves the index reader ran
            Plant(dir, "untracked/notes.txt", "not added");

            var bad = Violations(TreePaths(dir));
            Assert.Equal(2, bad.Count);
            Assert.Contains(bad, b => b.StartsWith("scratch/notes.txt:", StringComparison.Ordinal));
            Assert.Contains(bad, b => b.StartsWith("Gatto.Tests/Fixtures/planted.xml:", StringComparison.Ordinal));
        }
        finally { try { Directory.Delete(dir, true); } catch (Exception) { } }
    }

    //a source zip has no repository, so the disk is read, and build output at any depth is left out
    [Fact]
    public void A_TREE_WITH_NO_REPOSITORY_IS_READ_FROM_THE_DISK()
    {
        var dir = Directory.CreateTempSubdirectory("gatto-zip-").FullName;
        try
        {
            foreach (var (rel, text) in new[]
                     {
                         ("README.md", "readme"),
                         ("Gatto/Thing.cs", "class Thing { }"),
                         ("scratch/notes.txt", "a planted folder"),
                         ("Gatto.Tests/Fixtures/planted.xml", "<planted/>"),
                         ("Gatto/bin/Debug/gatto.dll", "built"),
                         ("Gatto.Tests/obj/project.assets.json", "restored"),
                         ("tmp/testbin-x/log.txt", "scratch output"),
                         ("Gatto.Tests/TestResults/run.trx", "results"),
                     })
                Plant(dir, rel, text);
            Assert.False(TreeCensusTests.IsRepository(dir));

            var bad = Violations(TreePaths(dir));
            Assert.Equal(2, bad.Count);
            Assert.Contains(bad, b => b.StartsWith("scratch/notes.txt:", StringComparison.Ordinal));
            Assert.Contains(bad, b => b.StartsWith("Gatto.Tests/Fixtures/planted.xml:", StringComparison.Ordinal));
        }
        finally { try { Directory.Delete(dir, true); } catch (Exception) { } }
    }

    //a zip opened in an editor gains a folder of the editor's state, which the disk reader leaves out
    [Fact]
    public void AN_EDITOR_FOLDER_IN_A_TREE_WITH_NO_REPOSITORY_IS_LEFT_OUT()
    {
        var dir = Directory.CreateTempSubdirectory("gatto-editor-").FullName;
        try
        {
            foreach (var (rel, text) in new[]
                     {
                         ("README.md", "readme"),
                         (".vs/gatto/v17/.suo", "editor state"),
                         (".idea/workspace.xml", "<project/>"),
                         (".vscode/settings.json", "{}"),
                         ("scratch/notes.txt", "a planted folder"),
                     })
                Plant(dir, rel, text);

            var bad = Violations(TreePaths(dir));
            Assert.Equal(["scratch/notes.txt"], bad.Select(b => b[..b.IndexOf(':')]));
        }
        finally { try { Directory.Delete(dir, true); } catch (Exception) { } }
    }

    //the index reader skips nothing, so an editor folder someone added to git still fails
    [Fact]
    public void A_TRACKED_EDITOR_FOLDER_STILL_FAILS()
    {
        var dir = Directory.CreateTempSubdirectory("gatto-tracked-editor-").FullName;
        try
        {
            Plant(dir, "README.md", "readme");
            Plant(dir, ".vs/settings.json", "{}");
            Assert.True(Git(dir, "init -q") && Git(dir, "add -A"), "git could not make the scratch repository");

            var bad = Violations(TreePaths(dir));
            Assert.Equal([".vs/settings.json"], bad.Select(b => b[..b.IndexOf(':')]));
        }
        finally { try { Directory.Delete(dir, true); } catch (Exception) { } }
    }

    //a .git file that git cannot read is a repository that failed, never a zip to read from the disk
    [Fact]
    public void A_REPOSITORY_WHOSE_GIT_FAILS_IS_A_FAILURE_AND_NOT_A_DISK_READ()
    {
        var dir = Directory.CreateTempSubdirectory("gatto-badgit-").FullName;
        try
        {
            Plant(dir, "README.md", "readme");
            Plant(dir, ".git", "gitdir: " + Path.Combine(dir, "no-such-repository"));
            Assert.True(TreeCensusTests.IsRepository(dir));
            Assert.ThrowsAny<Exception>(() => TreePaths(dir));
        }
        finally { try { Directory.Delete(dir, true); } catch (Exception) { } }
    }

    private static void Plant(string dir, string rel, string text)
    {
        var full = Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    private static bool Git(string cwd, string args)
    {
        var p = Process.Start(new ProcessStartInfo
        {
            FileName = "git",
            Arguments = args,
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        });
        if (p is null) return false;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode == 0;
    }
}
