using System.Text.RegularExpressions;

namespace Gatto.Tests.Census;

//every path in the product is joined with Path.Combine. a separator literal glued on with + or set between two holes fails here, and a site that only looks like a join is named by its text
public class PathJoinCensusTests
{
    //the shapes of a join by hand, built for one separator: as a regular string, as a verbatim string and as a char
    private static Regex[] Shapes(string regular, string verbatim, string chr) =>
    [
        new(@"""(?:[^""\\\n]|\\.)*" + regular + @"""\s*\+"),
        new(@"\+\s*\$?""" + regular),
        new(@"@""[^""\n]*" + verbatim + @"""\s*\+"),
        new(@"\+\s*(?:\$@|@\$|@)""" + verbatim),
        new(@"\}" + regular + @"[\w.{]"),
        new(@"[\w.]" + regular + @"\{"),
        new(@"(?:\$@|@\$)""[^""\n]*(?:\}" + verbatim + @"[\w{]|\w" + verbatim + @"\{)"),
        new(@"\+\s*'" + chr + @"'|'" + chr + @"'\s*\+"),
        new(@"Join\(\s*(?:""" + regular + @"""|@""" + verbatim + @"""|'" + chr + @"')"),
    ];

    private static readonly Regex[] Backslash = Shapes(@"\\\\", @"\\", @"\\\\");
    private static readonly Regex[] Slash = Shapes("/", "/", "/");

    //a slash beside a scheme, a base url, an escaped segment or the user agent joins a url, never a path
    private static readonly Regex UrlLine = new(@"://|TrimEnd\('/'\)|url|Uri\.|User-Agent", RegexOptions.IgnoreCase);

    //each site that matches a shape and builds no filesystem path, by file and its trimmed line
    private static readonly (string File, string Line)[] NotPaths =
    [
        ("Gatto.Terminal/FocusKey.cs", @"public static string MoveKeyNameOf(GlyphSet g) => $""Ctrl+{g.UpKey}/{g.DownKey}"";"),   //a key name shown
        ("Gatto/Cli/Diagnose/Doctor.cs", @"problems.Add($""default_role names '{dr}', and no roles\\{dr}.json exists"");"),   //a message shown
        ("Gatto/Cli/LlamaFetch.cs", @"var prefix = from.Length == 0 ? """" : from + ""/"";"),   //a zip entry name, which uses a slash by format
        ("Gatto/Cli/Setup/SetupFlow.ModelFetch.cs", @"private static string HubLabel(string repoId) => ""huggingface.co/"" + repoId;"),   //a label shown
        ("Gatto/Cli/Setup/Shelves/Pane.cs", @"var tail = $""{g.Ellipsis}\\"" + string.Join('\\', segs[i..]);"),   //a cut path shown in a row
        ("Gatto/Core/Acquire/HubClient.cs", @"var repoId = parts[0] + ""/"" + parts[1];"),   //a repo id
        ("Gatto/Core/Acquire/HubClient.cs", @"var named = string.Join('/', parts.Skip(4));"),   //a file inside a repo
        ("Gatto/Core/Loop/Permissions/WorkspaceBoundary.cs", @"return new Regex(@""(~|\$home|\$env:userprofile|%userprofile%)\\"" + Regex.Escape(tail) + @""(\\|$|[\s""""';)|])"", RegexOptions.IgnoreCase);"),   //a regex
        ("Gatto/Core/Tools/SearchTools.cs", @"if (!globStr.Contains('/') && !globStr.Contains('\\')) globStr = ""**/"" + globStr;"),   //a glob
        ("Gatto/Repl/Commands/CommandText.cs", @": $""{Environment.NewLine}{Environment.NewLine}/model quant <name> to use another one"";"),   //a command named
        ("Gatto/Repl/Render/Chrome/ChromeTicker.cs", @"? "" "" + g.Dot + "" "" + K(streamed.PromptProcessed) + ""/"" + K(promptTotal)"),   //a count over a total
        ("Gatto/Repl/Render/Syntax/MarkupScanner.cs", @"var end = Text.IndexOf(""</"" + name, Pos, language.NamesIgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);"),   //a closing tag
        ("Gatto/Repl/Repl.cs", @"rows.Add(theme.Paint($""/quit exits{sep}/new resets{sep}Ctrl+C twice exits{sep}{g.UpKey} history"","),   //commands named
        ("Gatto/Roles/Serve/LlamaAssetSteering.cs", @"public static string ReleasePage => $""{ReleaseBase}/tag/{PinnedRelease}"";"),   //a release page url
    ];

    //every line of the product that matches a shape, as file and trimmed text, comments stripped and literals kept
    internal static List<(string File, string Line)> Sites(Func<string, string, string>? mutate = null)
    {
        var root = SourceTree.RepoRoot();
        var found = new List<(string, string)>();
        foreach (var path in SourceTree.ProductionFiles())
        {
            var rel = Path.GetRelativePath(root, path).Replace('\\', '/');
            var text = SourceTree.Read(path);
            if (mutate is not null) text = mutate(rel, text);
            foreach (var raw in SourceTree.WithoutComments(text).Split('\n'))
            {
                var line = raw.Trim();
                if (Backslash.Any(r => r.IsMatch(line)) || (!UrlLine.IsMatch(line) && Slash.Any(r => r.IsMatch(line))))
                    found.Add((rel, line));
            }
        }
        return found;
    }

    [Fact]
    public void NO_PATH_IS_JOINED_BY_A_SEPARATOR_LITERAL()
    {
        var joins = Sites().Except(NotPaths).Select(s => $"{s.File}: {s.Line}").ToList();

        Assert.True(joins.Count == 0, "join with Path.Combine:\n" + string.Join('\n', joins));
    }

    //an entry whose line changed or left its file names nothing, so it is removed rather than kept as a hole
    [Fact]
    public void EVERY_LISTED_SITE_IS_STILL_IN_THE_TREE()
    {
        var stale = NotPaths.Except(Sites()).Select(s => $"{s.File}: {s.Line}").ToList();

        Assert.True(stale.Count == 0, "no longer in the tree:\n" + string.Join('\n', stale));
    }

    //a count is evidence only once a planted join moves it, so each shape is planted into a real file and read back through the census
    [Theory]
    [InlineData("var p = home + \"\\\\models\";")]
    [InlineData("var p = dir + \"/\" + name;")]
    [InlineData("var p = $\"{home}\\\\{name}.json\";")]
    [InlineData("var p = @$\"{home}\\{name}\";")]
    [InlineData("var p = home + '\\\\' + name;")]
    [InlineData("var p = string.Join(\"\\\\\", parts);")]
    public void A_PLANTED_JOIN_IS_FOUND(string planted)
    {
        var sites = Sites((file, text) => file == "Gatto/Core/Home/GattoHome.cs" ? text + "\nclass Planted { void M(string home, string dir, string name, string[] parts) { " + planted + " } }\n" : text);

        Assert.Contains(sites, s => s.File == "Gatto/Core/Home/GattoHome.cs" && s.Line.Contains(planted, StringComparison.Ordinal));
    }
}
