using System.Text.RegularExpressions;

namespace Gatto.Tests.Census;

//a test that walks a product folder by a literal path reads one project, so a second product project drops out and the test still passes
public class TestWalkCensusTests
{
    private static readonly Regex Walk = new(
        @"\b(EnumerateFiles|GetFiles|EnumerateDirectories|GetDirectories|EnumerateFileSystemEntries|GetFileSystemEntries)\s*\(",
        RegexOptions.Compiled);

    //an assignment, or a member or method with an expression body. a lambda parameter has no type before it, so it is not read as one
    private static readonly Regex Declaration = new(
        @"(?<![\w.])(\w+)\s*=(?![=>])|(?<=[\w>\]?]\s+)(\w+)\s*(?:\([^()]*\))?\s*=>",
        RegexOptions.Compiled);

    private static readonly Regex Identifier = new(@"(?<!\w)[A-Za-z_]\w*", RegexOptions.Compiled);

    //the shared source home is the one file that may name a product project, every walk of product source goes through it
    private const string Home = "Census/SourceTree.cs";

    private sealed record Source(string Rel, string Text, bool[] InLiteral, IReadOnlyList<(int Start, int End)> Literals);

    private sealed record Decl(Source In, int Start, int End);

    private static Source Load(string rel, string source)
    {
        var text = SourceTree.WithoutComments(source);
        var spans = SourceTree.LiteralSpans(text);
        var mask = new bool[text.Length];
        foreach (var (start, end) in spans)
            Array.Fill(mask, true, start, end - start);
        return new Source(rel, text, mask, spans);
    }

    private static List<(string Rel, string Source)> TestTree()
    {
        var root = SourceTree.RepoRoot();
        return [.. SourceTree.TestFiles()
            .Select(f => (Path.GetRelativePath(root, f).Replace('\\', '/'), SourceTree.Read(f)))
            .OrderBy(t => t.Item1, StringComparer.Ordinal)];
    }

    //the end of an expression: the closer of its call, or a semicolon or comma at its own depth
    private static int Extent(Source s, int from, bool wholeCall)
    {
        var depth = 0;
        for (var i = from; i < s.Text.Length; i++)
        {
            if (s.InLiteral[i]) continue;
            var c = s.Text[i];
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}')
            {
                if (depth == 0) return i;
                depth--;
            }
            else if (depth == 0 && (c == ';' || (c == ',' && !wholeCall))) return i;
        }
        return s.Text.Length;
    }

    //a literal names a product project when any of its path segments is that project's folder
    private static bool NamesAProject(string literal)
    {
        var value = literal.TrimStart('$', '@').Trim('"');
        return value.Split('/', '\\').Any(seg => SourceTree.ProductProjects.Contains(seg, StringComparer.OrdinalIgnoreCase));
    }

    private sealed class Index
    {
        public readonly Dictionary<string, Dictionary<string, List<Decl>>> ByFile = new(StringComparer.Ordinal);
        public readonly Dictionary<string, List<Decl>> Anywhere = new(StringComparer.Ordinal);

        public Index(IEnumerable<Source> tree)
        {
            foreach (var s in tree)
            {
                var local = ByFile[s.Rel] = new(StringComparer.Ordinal);
                foreach (Match m in Declaration.Matches(s.Text))
                {
                    if (s.InLiteral[m.Index]) continue;
                    var name = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                    var start = m.Index + m.Length;
                    var decl = new Decl(s, start, Extent(s, start, wholeCall: false));
                    Add(local, name, decl);
                    Add(Anywhere, name, decl);
                }
            }
        }

        private static void Add(Dictionary<string, List<Decl>> into, string name, Decl decl)
        {
            if (!into.TryGetValue(name, out var list)) into[name] = list = [];
            list.Add(decl);
        }
    }

    //true when the span holds a product-project literal, or names a variable or member whose value does, followed a few steps back
    private static bool Tainted(Index index, Source s, int start, int end, int depth, HashSet<(string, int)> seen)
    {
        if (s.Literals.Any(l => l.Start >= start && l.End <= end && NamesAProject(s.Text[l.Start..l.End]))) return true;
        if (depth == 0) return false;

        foreach (Match id in Identifier.Matches(s.Text[start..end]))
        {
            var at = start + id.Index;
            if (s.InLiteral[at]) continue;
            var qualified = Before(s.Text, at) is var b && b >= 0 && s.Text[b] == '.';
            var found = qualified
                ? index.Anywhere.GetValueOrDefault(id.Value)
                : index.ByFile[s.Rel].GetValueOrDefault(id.Value);
            foreach (var d in found ?? [])
                if (seen.Add((d.In.Rel, d.Start)) && Tainted(index, d.In, d.Start, d.End, depth - 1, seen)) return true;
        }
        return false;
    }

    //the index of the last character before at that is not whitespace, or -1
    private static int Before(string text, int at)
    {
        var i = at - 1;
        while (i >= 0 && char.IsWhiteSpace(text[i])) i--;
        return i;
    }

    //the start of the name a call is made on, d in d.EnumerateFiles, so an instance walk reads its receiver too
    private static int? Receiver(string text, int call)
    {
        var dot = Before(text, call);
        if (dot < 0 || text[dot] != '.') return null;
        var end = Before(text, dot);
        var i = end;
        while (i >= 0 && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i--;
        return i == end ? null : i + 1;
    }

    //the one matcher, so the checks below run the code the census runs. the second count is every walk read, so an empty read shows
    private static (List<string> Offences, int Walks) Scan(IEnumerable<(string Rel, string Source)> files)
    {
        var tree = files.Where(f => !f.Rel.EndsWith(Home, StringComparison.Ordinal)).Select(f => Load(f.Rel, f.Source)).ToList();
        var index = new Index(tree);
        var offences = new List<string>();
        var walks = 0;

        foreach (var s in tree)
            foreach (Match m in Walk.Matches(s.Text))
            {
                if (s.InLiteral[m.Index]) continue;
                walks++;
                var start = Receiver(s.Text, m.Index) ?? m.Index + m.Length;
                if (!Tainted(index, s, start, Extent(s, m.Index + m.Length, wholeCall: true), depth: 4, [])) continue;
                var line = 1 + s.Text.AsSpan(0, m.Index).Count('\n');
                offences.Add($"{s.Rel}:{line}  {s.Text.Split('\n')[line - 1].Trim()}");
            }
        return (offences, walks);
    }

    [Fact]
    public void NO_TEST_WALKS_A_PRODUCT_PROJECT_BY_A_LITERAL_PATH()
    {
        var (offences, walks) = Scan(TestTree());
        Assert.True(walks > 50, $"the census read {walks} walks, so it is reading the wrong tree");
        Assert.True(offences.Count == 0,
            "walk SourceTree.ProductionFiles, ProductionFilesUnder, ProductionFilesIn or ScriptsUnder instead:\n  " + string.Join("\n  ", offences));
    }

    [Theory]
    [InlineData("var f = Directory.EnumerateFiles(Path.Combine(root, \"Gatto\"), \"*.cs\");")]
    [InlineData("var f = Directory\n        .GetFiles(Path.Combine(root,\n            \"Gatto.Terminal\"), \"*.cs\");")]
    [InlineData("var src = Path.Combine(root, \"Gatto\", \"Cli\");\n    foreach (var f in Directory.EnumerateFiles(src, \"*.cs\")) { }")]
    [InlineData("var a = Path.Combine(root, \"Gatto\");\n    var b = Path.Combine(a, \"Cli\");\n    var f = Directory.GetFiles(b);")]
    [InlineData("var d = Directory.GetDirectories(Path.Combine(root, @\"Gatto\\Core\"));")]
    [InlineData("var d = new DirectoryInfo(Path.Combine(root, \"Gatto\"));\n    var f = d.EnumerateFiles(\"*.cs\");")]
    public void A_WALK_OF_A_LITERAL_PRODUCT_PATH_IS_AN_OFFENCE(string body)
    {
        var (offences, _) = Scan([("Gatto.Tests/Planted.cs", "class Planted\n{\n    void M()\n    {\n    " + body + "\n    }\n}\n")]);
        Assert.Single(offences);
        Assert.StartsWith("Gatto.Tests/Planted.cs:", offences[0], StringComparison.Ordinal);
    }

    //a member declared in another file is followed by its qualified name
    [Fact]
    public void A_WALK_OF_A_MEMBER_DECLARED_IN_ANOTHER_FILE_IS_AN_OFFENCE()
    {
        var (offences, _) = Scan([
            ("Gatto.Tests/Helper.cs", "static class Helper\n{\n    public static string Src => Path.Combine(Root, \"Gatto\");\n}\n"),
            ("Gatto.Tests/Planted.cs", "class Planted\n{\n    void M() => Directory.EnumerateFiles(Helper.Src, \"*.cs\");\n}\n")]);
        Assert.Equal(["Gatto.Tests/Planted.cs:3  void M() => Directory.EnumerateFiles(Helper.Src, \"*.cs\");"], offences);
    }

    //a temp folder, a comment, a literal holding the call, one file read by path, a name that only starts like a project, and the home itself
    [Theory]
    [InlineData("Gatto.Tests/Planted.cs", "var f = Directory.GetFiles(Path.Combine(_home, \"sessions\"), \"*.jsonl\");")]
    [InlineData("Gatto.Tests/Planted.cs", "//Directory.GetFiles(Path.Combine(root, \"Gatto\"));")]
    [InlineData("Gatto.Tests/Planted.cs", "var s = \"Directory.GetFiles(Path.Combine(root, \\\"Gatto\\\"))\";")]
    [InlineData("Gatto.Tests/Planted.cs", "var t = File.ReadAllText(Path.Combine(root, \"Gatto\", \"Cli\", \"GattoApp.cs\"));")]
    [InlineData("Gatto.Tests/Planted.cs", "var f = Directory.GetFiles(Path.Combine(root, \"GattoExtras\"));")]
    [InlineData("Gatto.Tests/Census/SourceTree.cs", "var f = Directory.EnumerateFiles(Path.Combine(root, \"Gatto\"));")]
    public void A_WALK_THAT_NAMES_NO_PRODUCT_PROJECT_IS_NOT_AN_OFFENCE(string rel, string body)
    {
        var (offences, _) = Scan([(rel, "class Planted\n{\n    void M()\n    {\n    " + body + "\n    }\n}\n")]);
        Assert.Empty(offences);
    }
}
