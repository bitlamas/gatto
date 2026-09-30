using System.Text.RegularExpressions;

namespace Gatto.Tests.Census;

//a fixture key in a namespace the flow owns must be a key the flow emits
public class FixtureScreenKeyCensusTests
{
    //a file name has the same lowercase dotted shape, so shape alone never decides, the literal must be the argument of a screen construction
    private static readonly Regex KeyShaped = new(@"^[a-z][a-z0-9]*(\.[a-z0-9]+)+$", RegexOptions.Compiled);

    //the named form and the target-typed new( both count
    private static readonly Regex ScreenKeyArgument = new(
        @"new(\s+WizardScreen\.\w+)?\(\s*""([^""]+)""",
        RegexOptions.Compiled);

    private static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "Gatto.sln"))) d = d.Parent;
        Assert.True(d is not null, "could not find the repo root");
        return d!.FullName;
    }

    //the keys the flow can emit are its public const string values in SetupFlow.cs
    private static HashSet<string> DeclaredKeys()
    {
        var src = File.ReadAllText(Path.Combine(Root(), "Gatto", "Cli", "Setup", "SetupFlow.cs"));
        return [.. Regex.Matches(src, @"public const string \w+ = ""([^""]+)"";")
            .Select(m => m.Groups[1].Value)];
    }

    //derive the owned namespaces from the flow's own keys, a hand-written list goes stale when a segment is added
    private static HashSet<string> OwnedPrefixes() =>
        [.. DeclaredKeys().Where(k => k.Contains('.')).Select(k => k[..k.IndexOf('.')])];

    private static IEnumerable<string> TestSources()
    {
        var root = Path.Combine(Root(), "Gatto.Tests");
        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));
    }

    [Fact]
    public void NO_fixture_invents_a_screen_key_in_the_flows_namespace()
    {
        var declared = DeclaredKeys();
        var owned = OwnedPrefixes();
        var offenders = new List<string>();

        foreach (var path in TestSources())
        {
            var lines = File.ReadAllLines(path);
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith('*')) continue;

                foreach (Match m in ScreenKeyArgument.Matches(lines[i]))
                {
                    var lit = m.Groups[2].Value;
                    if (!KeyShaped.IsMatch(lit)) continue;
                    if (!owned.Contains(lit[..lit.IndexOf('.')])) continue;
                    if (declared.Contains(lit)) continue;
                    offenders.Add($"{Path.GetFileName(path)}:{i + 1}  \"{lit}\"");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "a fixture invents a screen key the flow never emits — use the SetupFlow constant:\n  "
            + string.Join("\n  ", offenders));
    }

    //the invented key must go through the same matcher the census uses (a stand-in would prove nothing). a declared key and a foreign namespace must not fire
    [Theory]
    [InlineData("model.shelf", true)]            //this is the known match, an owned prefix the flow never declares
    [InlineData("model.search", false)]          //an owned prefix that is declared, so it must not fire.
    [InlineData("planted.bare", false)]          //a namespace the flow does not own stays legal.
    [InlineData("Which model should gatto?", false)]  //prose is not key-shaped, so it cannot match.
    public void The_census_fires_on_an_invented_key_and_not_on_the_others(string literal, bool offends)
    {
        var declared = DeclaredKeys();
        var owned = OwnedPrefixes();

        var fires = KeyShaped.IsMatch(literal)
            && owned.Contains(literal[..Math.Max(literal.IndexOf('.'), 0)])
            && !declared.Contains(literal);

        Assert.Equal(offends, fires);
    }

    //the scan must reach real files, a wrong root makes the census pass by reading nothing
    [Fact]
    public void The_census_reads_real_sources()
    {
        Assert.True(DeclaredKeys().Count > 30, "the flow's key constants were not found");
        Assert.True(OwnedPrefixes().Contains("model"), "the flow's own namespace was not derived");
        Assert.True(TestSources().Count() > 50, "the test tree was not found");
    }
}
