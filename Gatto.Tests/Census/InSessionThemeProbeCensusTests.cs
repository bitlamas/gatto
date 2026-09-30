using System.Text.RegularExpressions;
using Gatto.Cli;
using Gatto.Terminal;

namespace Gatto.Tests.Census;

//never ask the terminal anything inside a live session, the reply arrives as keystrokes in the input pump and no test can see it
public class InSessionThemeProbeCensusTests
{
    //the probing entry points (ForSession is out, it takes a resolved mode and can't ask)
    private static readonly Regex Probing =
        new(@"CommandBanner\.Chrome\s*\(|QueryBackgroundColor", RegexOptions.Compiled);

    //each exemption names one exact source line and how many times it may appear, so a defect elsewhere in the file still fails
    private static readonly (string File, string Text, int Count, string Why)[] Exempt =
    [
        ("Gatto/Cli/CommandContext.cs", "CommandBanner.Chrome(home));", 1,
            "Production() builds the command surface — no REPL, no pump"),
        ("Gatto/Cli/GattoApp.cs", "var theme = CommandBanner.Chrome(home);", 2,
            "RunAuditionAsync and RunSetup — command entry points, identical text"),
        ("Gatto/Cli/GattoApp.cs", "CommandBanner.Write(Console.Out, CommandBanner.Chrome(home),", 1,
            "RunModelAsync — a command entry point"),
        ("Gatto/Cli/GattoApp.cs",
            "themeMode = Theme.ResolveMode(config.Theme, rich, TermCaps.QueryBackgroundColor);", 1,
            "RunInnerAsync — ⬥ THE launch probe, and the one that must exist: it runs BEFORE the pump "
          + "is started, which is the ordering Repl.RunRichAsync documents"),
        ("Gatto/Cli/GattoApp.cs",
            "new Theme(TermCaps.Detect(), Theme.ResolveMode(config.Theme, rich, TermCaps.QueryBackgroundColor)),", 1,
            "RunModelAsync — a command entry point"),
    ];

    //pass SourceTree.Read here so line endings are handled the same way (the parameter exists so a test can inject a fake corpus)
    private static IEnumerable<(string File, int Line, string Text)> Sites(Func<string, string> read)
    {
        var root = SourceTree.RepoRoot();
        foreach (var file in SourceTree.ProductionFiles())
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            //these two files hold the declarations (a census that counts a definition as a use reports its own subject as a violation)
            if (rel.EndsWith("Cli/CommandBanner.cs", StringComparison.Ordinal)) continue;
            if (rel.EndsWith("Gatto.Terminal/TermCaps.cs", StringComparison.Ordinal)) continue;
            var code = SourceTree.CodeOnly(read(file));
            var lines = code.Split('\n');
            for (var i = 0; i < lines.Length; i++)
                if (Probing.IsMatch(lines[i])) yield return (rel, i + 1, lines[i].Trim());
        }
    }

    //the absence claim in the census means nothing until this test proves the scanner can see the call
    [Fact]
    public void THE_SCANNER_CAN_SEE_A_PLANTED_CALL()
    {
        var planted = "var t = CommandBanner.Chrome(home);";
        Assert.Matches(Probing, planted);
        Assert.Matches(Probing, "TermCaps.QueryBackgroundColor");
        //the pattern must not match the probe-less replacement, or the census would forbid the fix.
        Assert.DoesNotMatch(Probing, "CommandBanner.ForSession(TermCaps.DetectForOutput(), themeMode)");
    }

    [Fact]
    public void NO_PROBING_CALL_SITE_OUTSIDE_THE_NAMED_COMMAND_PATHS()
    {
        var all = Sites(SourceTree.Read).ToList();

        //an exemption covers exactly its count, each site uses one up and a leftover is unexpected
        var remaining = Exempt.ToDictionary(e => (e.File, e.Text), e => e.Count);
        var unexpected = new List<(string File, int Line, string Text)>();
        foreach (var s in all)
        {
            var key = remaining.Keys.FirstOrDefault(
                k => s.File.EndsWith(k.File, StringComparison.Ordinal) && s.Text == k.Text);
            if (key.Text is not null && remaining[key] > 0) remaining[key]--;
            else unexpected.Add(s);
        }

        Assert.True(unexpected.Count == 0,
            "A theme PROBE is reachable from a path this census does not exempt. If it runs while an "
            + "InputPump owns stdin, the terminal's reply lands in the composer. Use "
            + "CommandBanner.ForSession(caps, mode) there, or add the site to Exempt WITH its reason:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, unexpected.Select(s => $"  {s.File}:{s.Line}  {s.Text}")));
    }

    //the probe-less path must use the mode it was handed, the real Chrome returns null on a test process's Plain caps
    [Theory]
    [InlineData(ThemeMode.Dark)]
    [InlineData(ThemeMode.Light)]
    public void FOR_SESSION_CARRIES_THE_MODE_THE_SESSION_ALREADY_RESOLVED(ThemeMode mode)
    {
        var theme = CommandBanner.ForSession(new TermCaps(true, true), mode);

        Assert.NotNull(theme);
        Assert.Equal(mode, theme!.Mode);
    }

    //a non-rich terminal gets no theme here, the in-session path degrades the same way the command path does
    [Fact]
    public void FOR_SESSION_RETURNS_NO_THEME_ON_A_PLAIN_TERMINAL()
        => Assert.Null(CommandBanner.ForSession(new TermCaps(false, false), ThemeMode.Dark));

    //a broken reader and a clean tree give the same result, so assert the scanner still sees the exempt sites
    [Fact]
    public void THE_CENSUS_STILL_SEES_THE_EXEMPT_SITES()
        => Assert.True(Sites(SourceTree.Read).Any(),
            "The scanner found no probing call site anywhere, including the exempt command paths. "
            + "That is a broken reader, not a clean tree.");
}
