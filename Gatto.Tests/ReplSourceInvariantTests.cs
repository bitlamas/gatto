//this guards Repl.cs source text, since RunRichAsync has no headless driver, and the display reset keeps one call site
using System.Text.RegularExpressions;

namespace Gatto.Tests;

public class ReplSourceInvariantTests
{
    //search upward from the test binary for Repl.cs and hard-fail when it is missing
    private static string ReplSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "Gatto", "Repl", "Repl.cs");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            dir = dir.Parent;
        }
        throw new FileNotFoundException(
            $"could not find Gatto/Repl/Repl.cs above {AppContext.BaseDirectory} — this test reads the repo source");
    }

    //every dispatched command must have a help row or a written exemption, matched by dispatch shape in source text
    [Fact]
    public void EveryDispatchedSlashCommand_HasAHelpRow_OrIsAWrittenDownExemption()
    {
        var src = ReplSource();

        var dispatched = Regex.Matches(src, @"trimmed(?:Line)?\s*==\s*""(/[a-z][a-z0-9-]*)""")
            .Concat(Regex.Matches(src, @"trimmed(?:Line)?\.StartsWith\(""(/[a-z][a-z0-9-]*) """))
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        //the match must find real commands, or the guard can never fail.
        Assert.True(dispatched.Count >= 5,
            $"found only {dispatched.Count} dispatched slash commands in Repl.cs — the regex has "
            + "stopped matching the dispatch shape, so this guard is vacuous. Fix the pattern.");

        var known = Gatto.Repl.SlashCommands.All.Select(c => c.Name)
            .Concat(Gatto.Repl.SlashCommands.ExemptCommands)
            .ToHashSet(StringComparer.Ordinal);

        var orphans = dispatched.Where(c => !known.Contains(c)).ToList();
        Assert.True(orphans.Count == 0,
            $"dispatched but absent from /help and from ExemptCommands: {string.Join(", ", orphans)}. "
            + "A command a user can type and cannot discover is the drift this guard exists to kill — "
            + "give it a row in SlashCommands.All, or write it into ExemptCommands with a reason.");
    }

    [Fact]
    public void TheSetupDispatchIsGone_and_nothing_is_exempt_any_more()
    {
        //the exemption set stays empty, adding one has to be a deliberate decision
        Assert.DoesNotContain("\"/setup\"", ReplSource(), StringComparison.Ordinal);
        Assert.Empty(Gatto.Repl.SlashCommands.ExemptCommands);
    }

    [Fact]
    public void DisplayModelReset_HasExactlyOneCallSite_InsideRotateDisplay()
    {
        var src = ReplSource();

        //match both spellings, a rename can't hide a second reset
        var resets = Regex.Matches(src, @"\bModel\.Reset\(\)|\bmodel\.Reset\(\)");
        Assert.True(resets.Count == 1,
            $"expected exactly ONE display-model reset in Repl.cs, found {resets.Count}. A rotation that "
            + "resets the model outside RotateDisplay loses the recompose's warnings and the handler's "
            + "carried rows — the live-gate F-A defect, reintroduced. Route it through RotateDisplay.");

        //the single reset must sit between the RotateDisplay signature and the next member
        var seam = src.IndexOf("internal static void RotateDisplay(", StringComparison.Ordinal);
        Assert.True(seam >= 0, "RotateDisplay is gone from Repl.cs — the rotation seam this invariant guards no longer exists");
        var nextMember = src.IndexOf("\n    internal static void RotateForNew(", seam, StringComparison.Ordinal);
        Assert.True(nextMember > seam, "RotateForNew no longer follows RotateDisplay — re-anchor this invariant");
        Assert.InRange(resets[0].Index, seam, nextMember);
    }

    [Fact]
    public void RotateForNew_DoesNotOpenItsOwnHold_ItReceivesTheHandlers()
    {
        //the handler opens the hold and passes it in, RotateForNew must not call Hold() itself (it would start after warnings already raised)
        var src = ReplSource();
        var start = src.IndexOf("internal static void RotateForNew(", StringComparison.Ordinal);
        Assert.True(start >= 0, "RotateForNew is gone from Repl.cs — re-anchor this invariant");
        var end = src.IndexOf("\n    internal static StreamRenderer BuildSessionRenderer(", start, StringComparison.Ordinal);
        Assert.True(end > start, "BuildSessionRenderer no longer follows RotateForNew — re-anchor this invariant");

        Assert.DoesNotContain(".Hold()", src[start..end], StringComparison.Ordinal);
    }

    //the /tools renders have no in-process driver, so the source text is what gets checked. both renderers must take their policy rows from PolicyRows()
    [Fact]
    public void BothToolsRendersReadThePolicyRowsFromOneHelper()
    {
        var src = ReplSource();

        //the count is three, one declaration and two calls, and the needle matches the signature too (a narrower pattern can be silently wrong)
        Assert.Equal(3, Occurrences(src, "PolicyRows()"));
        //neither site may pass its own empty list instead of the session's rows.
        Assert.DoesNotContain("ToolsSpec(tools, Array.Empty", src, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePolicyRowsCensusCanSeeAThirdCallSite()
    {
        var planted = ReplSource() + "\nclass PlantedThirdToolsRender { void M() { var x = PolicyRows(); } }\n";

        Assert.Equal(4, Occurrences(planted, "PolicyRows()"));
    }

    private static int Occurrences(string haystack, string needle)
    {
        var n = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    //two call sites commit the switch's own message, the ModelLine assert makes a zero count mean the sites changed
    [Fact]
    public void BOTH_MODEL_DISPATCH_SITES_COMMIT_THE_SWITCHS_OWN_MESSAGE()
    {
        var source = ReplSource();

        //assert the member exists so a zero count means the call sites changed (a broken needle would look the same)
        Assert.Contains("internal static string ModelLine(", source, StringComparison.Ordinal);

        Assert.Equal(2, source.Split("notice ?? ModelLine(modelName)").Length - 1);
    }
}
