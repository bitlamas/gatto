using Gatto.Cli.Setup;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup;

//the aside shows on a legacy console only, and that answer comes from the glyph set's own rule
public class TerminalNudgeTests
{
    private const string Sentence =
        "gatto looks better in Windows Terminal (free, in the Microsoft Store); this console works too.";

    private static IReadOnlyDictionary<string, string?> Env(params (string Key, string? Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);

    //set one variable per case, a check reading only one of them would still pass a case that set both
    [Theory]
    [InlineData("WT_SESSION", "abc-123")]
    [InlineData("TERM_PROGRAM", "vscode")]
    public void A_MODERN_HOST_IS_NOT_A_LEGACY_CONSOLE(string key, string value) =>
        Assert.False(GlyphSet.IsLegacyConsole(Env((key, value))));

    //the case where the nudge appears
    [Fact]
    public void A_HOST_THAT_SETS_NEITHER_IS_A_LEGACY_CONSOLE() =>
        Assert.True(GlyphSet.IsLegacyConsole(Env(("PATH", "C:\\"))));

    //an empty variable counts as unset, the same rule the glyph resolver follows
    [Fact]
    public void AN_EMPTY_VARIABLE_IS_NOT_A_MODERN_HOST() =>
        Assert.True(GlyphSet.IsLegacyConsole(Env(("WT_SESSION", ""), ("TERM_PROGRAM", null))));

    //the glyph set and the nudge read the same host, so they can't disagree about which console this is
    [Theory]
    [InlineData("WT_SESSION", "abc-123")]
    [InlineData("TERM_PROGRAM", "vscode")]
    public void THE_NUDGES_HOST_AND_THE_ASCII_HOST_ARE_THE_SAME_HOST(string key, string value)
    {
        Assert.Equal(GlyphSet.Ascii, GlyphSet.Resolve(GlyphMode.Auto, Env(("PATH", "C:\\"))));
        Assert.Equal(GlyphSet.Unicode, GlyphSet.Resolve(GlyphMode.Auto, Env((key, value))));
    }

    //drive the flow to reach the screen, a screen built by hand proves nothing about arriving
    private static WizardScreen.Choice Install(
        bool legacy, Gatto.Cli.InstallState state = Gatto.Cli.InstallState.NotInstalled)
    {
        var flow = new SetupFlow(new WizardProbes
        {
            LegacyConsole = legacy,
            Llama = @"C:\llama\llama-server.exe",
            ActiveFile = "gemma-4-26B-A4B-it-UD-Q4_K_M.gguf",
            Install = state,
            Audition = new Gatto.Cli.Setup.AuditionCheck(
                Gatto.Cli.Setup.AuditionOutcome.Passed,
                new Gatto.Cli.Setup.AuditionFacts("Q4_K_M", "defaults", "template default", 31.4, 5, 5),
                new DateOnly(2026, 8, 24)),
        });

        Assert.Equal(SetupFlow.FoundKey, ScreenKey.Of(flow.StartPastOpening()));
        flow.Answer(SetupFlow.FoundUse);
        var screen = flow.ResumeAfterWrites("gemma-4-26B-A4B-it");
        while (flow.NeedsWritesApplied) screen = flow.ResumeAfterWrites(null);
        Assert.Equal(SetupFlow.AuditionOfferKey, ScreenKey.Of(screen));

        flow.Answer(SetupFlow.Yes);
        Assert.True(System.Threading.SpinWait.SpinUntil(flow.PollWatch, TimeSpan.FromSeconds(10)),
            "the check never finished, so the walk never reached the install question");
        Assert.Equal(SetupFlow.AuditionPassedKey, ScreenKey.Of(flow.Answer(SetupFlow.Landed)));

        var install = flow.Answer(SetupFlow.AuditionPassedNext);
        while (flow.NeedsWritesApplied) install = flow.ResumeAfterWrites(null);

        Assert.Equal(SetupFlow.InstallKey, ScreenKey.Of(install));
        return Assert.IsType<WizardScreen.Choice>(install);
    }

    [Fact]
    public void THE_INSTALL_SCREEN_CARRIES_THE_ASIDE_ON_A_LEGACY_CONSOLE() =>
        Assert.Contains(Install(legacy: true).BodyRows ?? [],
            r => r.Text.Contains(Sentence, StringComparison.Ordinal));

    //a one-sided test would pass on a screen that always shows the aside
    [Fact]
    public void AND_NOT_ON_A_MODERN_ONE() =>
        Assert.DoesNotContain(Install(legacy: false).BodyRows ?? [],
            r => r.Text.Contains("Windows Terminal", StringComparison.Ordinal));

    //the aside is appended, so assert the screen's own rows too
    [Fact]
    public void THE_SCREENS_OWN_ROWS_SURVIVE_THE_ASIDE()
    {
        var rows = Install(legacy: true).BodyRows ?? [];

        Assert.Contains(rows, r => r.Text.Contains("gatto is running from the folder", StringComparison.Ordinal));
        Assert.Contains(rows, r => r.Text.Contains("Your account only", StringComparison.Ordinal));
    }

    //the aside is added from one place, so both install branches need their own test (the repair branch arrives by a different path)
    [Fact]
    public void THE_REPAIR_BRANCH_CARRIES_THE_SAME_ASIDE()
    {
        var screen = Install(legacy: true, Gatto.Cli.InstallState.InstalledButNotOnPath);

        Assert.Contains(screen.BodyRows ?? [],
            r => r.Text.Contains(Sentence, StringComparison.Ordinal));
        //assert the repair row too, or the screen could be the ordinary one in another state
        Assert.Contains(screen.BodyRows ?? [],
            r => r.Text.Contains("A copy is already at", StringComparison.Ordinal));
    }

    //the repair branch on a modern host, where no aside shows
    [Fact]
    public void AND_THE_REPAIR_BRANCH_STAYS_QUIET_ON_A_MODERN_HOST() =>
        Assert.DoesNotContain(
            Install(legacy: false, Gatto.Cli.InstallState.InstalledButNotOnPath).BodyRows ?? [],
            r => r.Text.Contains("Windows Terminal", StringComparison.Ordinal));

    //every other guard drives the fake, so this reads the live probe's source (a known match and a known miss come first)
    private static bool ReadsTheGlyphSetsRule(string source) =>
        source.Contains("GlyphSet.IsLegacyConsole(", StringComparison.Ordinal)
        && source.Contains("GlyphSet.HostEnv()", StringComparison.Ordinal);

    [Fact]
    public void THE_MATCHER_FIRES_ON_A_PLANTED_POSITIVE_AND_REFUSES_A_PLANTED_NEGATIVE()
    {
        Assert.True(ReadsTheGlyphSetsRule(
            "public bool IsLegacyConsole() => "
            + "Gatto.Terminal.GlyphSet.IsLegacyConsole(Gatto.Terminal.GlyphSet.HostEnv());"));
        //the matcher must reject a probe that answers from its own dictionary
        Assert.False(ReadsTheGlyphSetsRule(
            "public bool IsLegacyConsole() => "
            + "Gatto.Terminal.GlyphSet.IsLegacyConsole(new Dictionary<string, string?>());"));
    }

    [Fact]
    public void THE_LIVE_PROBE_ANSWERS_FROM_THE_GLYPH_SETS_OWN_RULE()
    {
        var src = File.ReadAllText(Path.Combine(
            Gatto.Tests.Census.SourceTree.RepoRoot(), "Gatto", "Cli", "Setup", "LiveSetupProbes.cs"));
        var at = src.IndexOf("public bool IsLegacyConsole()", StringComparison.Ordinal);

        Assert.True(at >= 0, "the live probe no longer declares IsLegacyConsole");
        Assert.True(ReadsTheGlyphSetsRule(src[at..(at + 200)]),
            "the live probe stopped reading GlyphSet.IsLegacyConsole over GlyphSet.HostEnv()");
    }

    //a case-sensitive read would drop the run to ASCII in silence, so the probe name stays unique to this test
    [Fact]
    public void THE_HOSTS_ENVIRONMENT_IS_READ_CASE_INSENSITIVELY()
    {
        const string Name = "GATTO_A11_CASE_PROBE";
        Environment.SetEnvironmentVariable(Name, "yes");
        try
        {
            var env = GlyphSet.HostEnv();
            Assert.True(env.TryGetValue(Name.ToLowerInvariant(), out var lower) && lower == "yes");
            Assert.True(env.TryGetValue(Name, out var exact) && exact == "yes");
        }
        finally { Environment.SetEnvironmentVariable(Name, null); }
    }

    //the screen marks the aside droppable, so assert the row's own flag (the drop order has its own guards)
    [Fact]
    public void THE_ASIDE_IS_THE_LAST_ROW_A_SHORT_TERMINAL_KEEPS()
    {
        var row = Assert.Single(Install(legacy: true).BodyRows ?? [],
            r => r.Text.Contains(Sentence, StringComparison.Ordinal));

        Assert.True(row.Optional, "the aside must be droppable before a fact row is");
        //a fact row must not be optional, or a short terminal drops it before the aside
        Assert.DoesNotContain(Install(legacy: true).BodyRows ?? [],
            r => r.Optional && r.Text.Contains("Your account only", StringComparison.Ordinal));
    }
}
