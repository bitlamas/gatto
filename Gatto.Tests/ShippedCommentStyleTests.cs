using System.Text.RegularExpressions;

namespace Gatto.Tests;

//the shipped .csx comments are public, so they are checked here rather than stripped (stripping a literal would build different bytes)
public class ShippedCommentStyleTests
{
    //only a file whose comments follow the public style leaves this list. adding a file never takes one off
    private static readonly HashSet<string> Exempt = new(StringComparer.Ordinal);

    //only the space after the slashes is checked here, the prose rules live in Census.PublicProseStyle
    private static readonly (string Name, Regex Pattern)[] SyntaxViolations =
    [
        ("a space after the slashes", new Regex(@"^\s*//\s{1}\S", RegexOptions.Compiled)),
    ];

    //the line with its leading slashes removed, which is what the prose rules read
    private static string Prose(string line)
    {
        var t = line.TrimStart();
        return t.StartsWith("//", StringComparison.Ordinal) ? t[2..] : t;
    }

    [Fact]
    public void EVERY_COMMENT_IN_A_SHIPPED_CSX_IS_A_PUBLIC_COMMENT()
    {
        var root = Path.Combine(Census.SourceTree.RepoRoot(), "Gatto", "Shipped");
        Assert.True(Directory.Exists(root), $"the shipped tree is missing at {root}");

        var failures = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*.csx", SearchOption.AllDirectories)
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            var key = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (Exempt.Contains(key)) continue;

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (!lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
                foreach (var (name, pattern) in SyntaxViolations)
                    if (pattern.IsMatch(lines[i]))
                        failures.Add($"{key}:{i + 1} — {name} — {lines[i].Trim()}");
                foreach (var name in Census.PublicProseStyle.Check(Prose(lines[i])))
                    failures.Add($"{key}:{i + 1} — {name} — {lines[i].Trim()}");
            }
        }

        Assert.True(failures.Count == 0,
            "shipped .csx comments must follow the public prose style:\n  " + string.Join("\n  ", failures));
    }

    [Fact]
    public void THE_EXEMPTION_LIST_NAMES_ONLY_FILES_THAT_EXIST()
    {
        //an exemption for a missing file would silently cover a new file that took the same path
        var root = Path.Combine(Census.SourceTree.RepoRoot(), "Gatto", "Shipped");
        foreach (var key in Exempt)
            Assert.True(File.Exists(Path.Combine(root, key.Replace('/', Path.DirectorySeparatorChar))),
                $"exempt file {key} no longer exists — remove the exemption");
    }
}
