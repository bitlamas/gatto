using System.Text.RegularExpressions;

namespace Gatto.Tests.Census;

//a namespace says where its code lives. a reader that goes by name misses any file whose root, folders or assembly disagree with it
public class NamespaceCensusTests
{
    internal sealed record SourceFile(string Project, string Assembly, string RelativePath, string Code);

    private static readonly Regex SolutionProject = new("^Project\\(\"[^\"]*\"\\) = \"[^\"]*\", \"([^\"]+)\\.csproj\"", RegexOptions.Multiline);

    private static readonly Regex AssemblyNameTag = new("<AssemblyName>([^<]+)</AssemblyName>");

    private static readonly Regex NamespaceDeclaration = new(@"^\s*namespace\s+([\w.]+)", RegexOptions.Multiline);

    private static readonly Regex TypeDeclaration = new(@"^\s*(?:(?:public|internal|private|protected|file|static|sealed|abstract|partial|readonly|ref|unsafe)\s+)*(?:class|struct|interface|enum|record|delegate)\s", RegexOptions.Multiline);

    //the solution is the list of projects the build compiles, so a project added to it joins this census without an edit here
    private static IReadOnlyList<SourceFile> Tree()
    {
        var root = SourceTree.RepoRoot();
        var files = new List<SourceFile>();
        foreach (Match m in SolutionProject.Matches(SourceTree.Read(Path.Combine(root, "Gatto.sln"))))
        {
            var csproj = m.Groups[1].Value.Replace('\\', '/');
            var project = csproj[..csproj.LastIndexOf('/')];
            var tag = AssemblyNameTag.Match(SourceTree.Read(Path.Combine(root, csproj + ".csproj")));
            var assembly = tag.Success ? tag.Groups[1].Value : Path.GetFileName(csproj);
            foreach (var path in Directory.EnumerateFiles(Path.Combine(root, project), "*.cs", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(Path.Combine(root, project), path).Replace('\\', '/');
                if (rel.Split('/').Any(s => s is "bin" or "obj")) continue;
                files.Add(new SourceFile(project, assembly, rel, SourceTree.CodeOnly(SourceTree.Read(path))));
            }
        }
        return files;
    }

    //the root compares without case (the exe project names its assembly in lowercase for the file it builds)
    internal static List<string> Violations(IEnumerable<SourceFile> files)
    {
        var violations = new List<string>();
        var homes = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var f in files)
        {
            var where = $"{f.Project}/{f.RelativePath}";
            var declared = NamespaceDeclaration.Matches(f.Code).Select(m => m.Groups[1].Value).ToList();
            if (declared.Count == 0)
            {
                if (TypeDeclaration.IsMatch(f.Code)) violations.Add($"{where} declares a type in no namespace, so its root is not {f.Assembly}");
                continue;
            }
            var folders = f.RelativePath.Split('/')[..^1];
            var rootLength = f.Assembly.Split('.').Length;
            foreach (var ns in declared)
            {
                if (!homes.TryGetValue(ns, out var set)) homes[ns] = set = new SortedSet<string>(StringComparer.Ordinal);
                set.Add(f.Project);
                var segments = ns.Split('.');
                if (!string.Equals(string.Join('.', segments.Take(rootLength)), f.Assembly, StringComparison.OrdinalIgnoreCase))
                    violations.Add($"{where} declares {ns}, whose root is not the assembly {f.Assembly}");
                else if (!segments.Skip(rootLength).SequenceEqual(folders, StringComparer.Ordinal))
                    violations.Add($"{where} declares {ns}, and its folders read {string.Join('.', folders.Prepend(f.Assembly))}");
            }
        }
        foreach (var (ns, projects) in homes.Where(h => h.Value.Count > 1).OrderBy(h => h.Key, StringComparer.Ordinal))
            violations.Add($"{ns} is declared in {string.Join(" and ", projects)}, and a namespace lives in one assembly");
        return violations;
    }

    [Fact]
    public void EVERY_NAMESPACE_IS_ITS_ASSEMBLY_THEN_ITS_FOLDERS_AND_LIVES_IN_ONE_ASSEMBLY()
    {
        var tree = Tree();

        //a reader that found no files would pass every rule
        Assert.True(tree.Count > 800, $"the census read only {tree.Count} source files, so the reader is broken");
        Assert.Contains(tree, f => f.Project == "Gatto.Terminal");
        var violations = Violations(tree);
        Assert.True(violations.Count == 0,
            $"{violations.Count} namespace rule(s) broken:\n  " + string.Join("\n  ", violations)
            + "\nmove the file to the folder its namespace names, or rename the namespace to the folder it sits in.");
    }

    [Fact]
    public void EACH_RULE_FIRES_ON_A_PLANTED_FILE_AND_A_CONFORMING_TREE_PASSES()
    {
        SourceFile File(string project, string assembly, string rel, string code) => new(project, assembly, rel, code);
        var conforming = new[]
        {
            File("Gatto", "gatto", "Cli/App.cs", "namespace Gatto.Cli;\nclass App { }\n"),
            File("Gatto", "gatto", "Program.cs", "return await Run(args);\n"),
            File("Gatto.Terminal", "Gatto.Terminal", "Ansi.cs", "namespace Gatto.Terminal;\nstatic class Ansi { }\n"),
            File("Gatto.Tests", "Gatto.Tests", "Census/Plants/High/A.cs", "namespace Gatto.Tests.Census.Plants.High\n{\n    class A { }\n}\n"),
        };
        Assert.Empty(Violations(conforming));

        Assert.Contains(Violations([.. conforming, File("Gatto.Terminal", "Gatto.Terminal", "Theme.cs", "namespace Gatto.Repl.Term;\nclass Theme { }\n")]),
            v => v.StartsWith("Gatto.Terminal/Theme.cs declares Gatto.Repl.Term, whose root is not", StringComparison.Ordinal));
        Assert.Contains(Violations([.. conforming, File("Gatto.Terminal", "Gatto.Terminal", "Repl/Term/Theme.cs", "namespace Gatto.Terminal;\nclass Theme { }\n")]),
            v => v.StartsWith("Gatto.Terminal/Repl/Term/Theme.cs declares Gatto.Terminal, and its folders read", StringComparison.Ordinal));
        Assert.Contains(Violations([.. conforming, File("Gatto.Tests", "Gatto.Tests", "LooseTests.cs", "public class LooseTests { }\n")]),
            v => v.StartsWith("Gatto.Tests/LooseTests.cs declares a type in no namespace", StringComparison.Ordinal));
        Assert.Contains(Violations([.. conforming, File("Gatto", "gatto", "Terminal/Ansi.cs", "namespace Gatto.Terminal;\nclass Other { }\n")]),
            v => v == "Gatto.Terminal is declared in Gatto and Gatto.Terminal, and a namespace lives in one assembly");
    }
}
