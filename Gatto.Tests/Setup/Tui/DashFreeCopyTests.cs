namespace Gatto.Tests.Setup.Tui;

//no wizard copy holds an em dash. source spells it four ways: the literal and three escapes. so the check needs one known match per spelling and a known miss
public class DashFreeCopyTests
{
    private const char Literal = '—';
    //verbatim strings keep the backslashes, since an ordinary escape resolves at compile time to one character and the check would see that
    private static readonly string[] Escapes = [@"\u2014", @"\x2014", @"\U00002014"];

    //matches every spelling of the dash, and the one shared list keeps the matcher and the known cases on the same patterns
    private static bool SpellsAnEmDash(string text) =>
        text.Contains(Literal) || Escapes.Any(e => text.Contains(e, StringComparison.OrdinalIgnoreCase));

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "Gatto.sln"))) d = d.Parent;
        Assert.True(d is not null, "could not find the repo root");
        return d!.FullName;
    }

    private static string WizardSource() => Path.Combine(RepoRoot(), "Gatto", "Cli", "Setup");

    //the scope runs by channel, so a new site joins the census when it is written. a root would miss copy elsewhere and a whole file would sweep in diagnostics
    private enum Channel { WizardRoot, SayDelegate, SwitchResult, AutoServeAskFile, SwapConfirmFile }

    //comments are skipped on purpose, since a comment may hold the character and scanning them would enforce a rule nobody made
    private static bool LineHasDashedLiteral(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith('*')) return false;
        var parts = line.Split('"');
        for (var j = 1; j < parts.Length; j += 2)
            if (SpellsAnEmDash(parts[j])) return true;
        return false;
    }

    private static IEnumerable<string> ProductionFiles() =>
        Gatto.Tests.Census.SourceTree.ProductionFiles();

    //fires only when the literal sits on the say( line, since a variable's own literal is counted where it is written
    private static bool IsSayLiteral(string line) =>
        line.Contains("say(\"", StringComparison.Ordinal)
        || line.Contains("say($\"", StringComparison.Ordinal);

    //finds a result message by the type's name, and the match spans the whole construction since the message often sits on a continuation line
    private static IEnumerable<(int Line, string Text)> SwitchResultLiterals(string[] lines)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            var at = lines[i].IndexOf("new ModelSwitchResult(", StringComparison.Ordinal);
            if (at < 0) continue;
            var depth = 0;
            for (var j = i; j < lines.Length && j < i + 8; j++)
            {
                var from = j == i ? at : 0;
                if (LineHasDashedLiteral(lines[j])) yield return (j + 1, lines[j].Trim());
                foreach (var c in lines[j][from..])
                {
                    if (c == '(') depth++;
                    else if (c == ')') depth--;
                }
                if (depth <= 0) break;
            }
        }
    }

    private static IEnumerable<(string File, int Line, string Text, Channel Ch)> DashedLiterals()
    {
        foreach (var path in ProductionFiles())
        {
            var name = Path.GetFileName(path);
            var lines = File.ReadAllLines(path);
            //a file in the terminal library holds copy, since the wizard and the repl both paint with it
            var inWizardRoot = Gatto.Tests.Census.SourceTree.IsUnder(path, "Cli", "Setup")
                || Gatto.Tests.Census.SourceTree.IsInProject(path, "Gatto.Terminal");
            var byFile = name is "AutoServeAsk.cs" or "SwapConfirm.cs";

            for (var i = 0; i < lines.Length; i++)
            {
                if (!LineHasDashedLiteral(lines[i])) continue;

                if (inWizardRoot) yield return (name, i + 1, lines[i].Trim(), Channel.WizardRoot);
                else if (byFile)
                    yield return (name, i + 1, lines[i].Trim(),
                        name == "AutoServeAsk.cs" ? Channel.AutoServeAskFile : Channel.SwapConfirmFile);
                else if (IsSayLiteral(lines[i]))
                    yield return (name, i + 1, lines[i].Trim(), Channel.SayDelegate);
            }

            foreach (var (line, text) in SwitchResultLiterals(lines))
                if (!inWizardRoot && !byFile)
                    yield return (name, line, text, Channel.SwitchResult);
        }
    }

    [Fact]
    public void NO_wizard_string_literal_carries_an_em_dash()
    {
        var offenders = DashedLiterals()
            .Select(o => $"[{o.Ch}] {o.File}:{o.Line}  {o.Text[..Math.Min(90, o.Text.Length)]}")
            .Distinct()
            .ToArray();

        Assert.True(offenders.Length == 0,
            "an em dash in wizard copy:\n  " + string.Join("\n  ", offenders));
    }

    //each spelling goes into a real file on disk, read back the way the census reads. the compiler resolves an inline literal, so every escape becomes one character
    [Theory]
    [InlineData("literal")]
    [InlineData("u2014")]
    [InlineData("x2014")]
    [InlineData("U00002014")]
    public void The_census_sees_every_spelling_IN_FILE_TEXT(string spelling)
    {
        var backslash = ((char)92).ToString();
        var body = spelling == "literal" ? Literal.ToString() : backslash + spelling;
        var path = Path.Combine(Path.GetTempPath(), "dash-probe-" + Guid.NewGuid().ToString("N") + ".cs");
        try
        {
            File.WriteAllText(path, "class P { const string S = \"a tail " + body + " planted\"; }");
            var raw = File.ReadAllText(path);
            Assert.Contains(body, raw, StringComparison.Ordinal);      //the text must really be on disk before the census match is judged
            Assert.True(SpellsAnEmDash(raw), "the census cannot see this spelling in FILE TEXT: " + spelling);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    //hyphens and en dashes are legal, and the census fires only on the em dash
    [Theory]
    [InlineData("a plain - hyphen")]
    [InlineData("a range 1–10")]
    [InlineData("no dash at all")]
    public void The_census_does_NOT_fire_on_ordinary_text(string ordinary) =>
        Assert.False(SpellsAnEmDash(ordinary), "the census fired on: " + ordinary);

    //a wrong root would pass by reading no files at all, so this counts the wizard files
    [Fact]
    public void The_census_actually_reads_the_wizards_source()
    {
        var files = Directory.EnumerateFiles(WizardSource(), "*.cs", SearchOption.AllDirectories).Count();
        Assert.True(files > 10, $"only {files} wizard source files found: the census is looking in the wrong place");
    }

    //each of the five channels needs its own known match, since a broken matcher would report a clean tree forever
    [Theory]
    [InlineData("say(\"a tail — planted\");", true)]
    [InlineData("say($\"a tail — planted {id}\");", true)]
    [InlineData("say(someVariable);", false)]
    [InlineData("Say(\"not the delegate — planted\");", false)]
    public void The_say_channel_fires_on_a_literal_and_not_on_a_variable(string line, bool expected) =>
        Assert.Equal(expected, IsSayLiteral(line) && LineHasDashedLiteral(line));

    //the construction spans two lines on purpose, since that is the shape this channel exists for
    [Fact]
    public void The_switch_result_channel_reaches_a_message_on_a_continuation_line()
    {
        string[] spans =
        [
            "return new ModelSwitchResult(false, id, null, null,",
            "    $\"stayed on {x} — see above\", null, null, null);",
        ];
        Assert.NotEmpty(SwitchResultLiterals(spans));

        string[] clean =
        [
            "return new ModelSwitchResult(false, id, null, null,",
            "    $\"stayed on {x}, see above\", null, null, null);",
        ];
        Assert.Empty(SwitchResultLiterals(clean));
    }

    //the two by-file channels must really find their files, since a rename would leave them matching nothing and passing
    [Theory]
    [InlineData("AutoServeAsk.cs")]
    [InlineData("SwapConfirm.cs")]
    public void The_by_file_channels_have_a_file_to_read(string name)
    {
        var hits = ProductionFiles().Where(p => Path.GetFileName(p) == name).ToList();
        Assert.True(hits.Count == 1, $"{name}: expected exactly one production copy, found {hits.Count}");
        Assert.True(File.ReadAllLines(hits[0]).Length > 20, $"{name} is suspiciously short");
    }

}
