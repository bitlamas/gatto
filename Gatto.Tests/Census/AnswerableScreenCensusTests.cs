using System.Text.RegularExpressions;
using Gatto.Cli.Setup;

namespace Gatto.Tests.Census;

//every waiting screen must have an arm in the SetupFlow.Answer dispatch, or an answer throws and exits the wizard
public class AnswerableScreenCensusTests
{
    private static string Source() =>
        File.ReadAllText(Path.Combine(SourceTree.RepoRoot(), "Gatto", "Cli", "Setup", "SetupFlow.cs"));

    //map the name of each string constant in the flow source to its value.
    private static Dictionary<string, string> Constants(string src) =>
        Regex.Matches(src, @"const\s+string\s+(\w+)\s*=\s*""([^""]+)""")
             .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value, StringComparer.Ordinal);

    //count an arm with a when guard as armed, since a waiting screen needs a handler. an impossible guard passes here, so another census drives those screens.
    private static HashSet<string> ArmedKeys(string src, Dictionary<string, string> consts)
    {
        var start = src.IndexOf("return _awaiting switch", StringComparison.Ordinal);
        var end = src.IndexOf("has no handler for the answer", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "the dispatch switch was not found — this census is reading the wrong shape");

        var armed = new HashSet<string>(StringComparer.Ordinal);
        //match only the key list before when or =>. a guard pattern such as [^=]* stops at the first =, so RetryKey when answer == Retry would read as unarmed
        foreach (Match m in Regex.Matches(src[start..end],
                     @"^\s*\(?(\w+(?:\s+or\s+\w+)*)\)?\s*(?:when\b|=>)", RegexOptions.Multiline))
            foreach (var name in Regex.Split(m.Groups[1].Value, @"\s+or\s+"))
                if (consts.TryGetValue(name, out var value)) armed.Add(value);
        return armed;
    }

    //list each constructed Choice or Ask, the two screen kinds that wait for an answer, by the constant that names its key
    private static List<(string Kind, string Name, string Value)> WaitingScreens(
        string src, Dictionary<string, string> consts)
    {
        var found = new List<(string, string, string)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(src, @"new WizardScreen\.(Choice|Ask)\(\s*\r?\n?\s*([A-Za-z_][\w.]*)"))
        {
            var name = m.Groups[2].Value.Split('.')[^1];
            if (consts.TryGetValue(name, out var value) && seen.Add(value))
                found.Add((m.Groups[1].Value, name, value));
        }
        return found;
    }

    //a screen that waits and cannot be answered is a dead end that the user reaches by following the screen itself.
    [Fact]
    public void EVERY_SCREEN_THAT_WAITS_FOR_AN_ANSWER_HAS_A_DISPATCH_ARM()
    {
        var src = Source();
        var consts = Constants(src);
        var waiting = WaitingScreens(src, consts);
        var armed = ArmedKeys(src, consts);

        //a census that finds nothing passes forever after a rename or a new shape. the floor sits below the measured count, so a large drop means the reader broke.
        Assert.True(waiting.Count >= 25,
            $"only {waiting.Count} waiting screens found — the census is reading the wrong shape");
        Assert.True(armed.Count >= 25,
            $"only {armed.Count} dispatch arms found — the census is reading the wrong shape");

        var unanswerable = waiting.Where(w => !armed.Contains(w.Value)).ToList();

        Assert.True(unanswerable.Count == 0,
            "these screens wait for an answer that SetupFlow.Answer cannot dispatch, so choosing an "
            + "option on them throws and drops the user out of the wizard:"
            + string.Concat(unanswerable.Select(u => $"\n  {u.Kind} {u.Name} ('{u.Value}')")));
    }

    //the census proves an absence only once its pattern matched a known missing arm. without it, a pattern that stops matching reports a clean census forever.
    [Fact]
    public void THE_CENSUS_CAN_SEE_A_MISSING_ARM()
    {
        var src = Source();
        var consts = Constants(src);

        //remove the arm from the source text in memory only, so no file on disk changes.
        var withoutArm = src.Replace(
            "            UpdateInstalledKey => AnswerUpdateInstalled(answer),\r\n", "")
            .Replace("            UpdateInstalledKey => AnswerUpdateInstalled(answer),\n", "");
        Assert.NotEqual(src, withoutArm);   //the removal must change the text, or the test proves nothing.

        var armed = ArmedKeys(withoutArm, consts);
        var waiting = WaitingScreens(withoutArm, consts);

        Assert.DoesNotContain(SetupFlow.UpdateInstalledKey, armed);
        Assert.Contains(waiting, w => w.Value == SetupFlow.UpdateInstalledKey);
    }
}
