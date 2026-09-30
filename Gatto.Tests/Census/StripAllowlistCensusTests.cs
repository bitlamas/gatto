using System.Reflection;
using System.Text.RegularExpressions;
using Gatto.Core;

namespace Gatto.Tests.Census;

//the strip removes exact strings only. a pattern would also match the chat template tokens inside real tool results, and those must stay byte for byte
public class StripAllowlistCensusTests
{
    private static readonly string[] Four =
    [
        "<|tool_calls_section_begin|>",
        "<|tool_calls_section_end|>",
        "<|tool_call_begin|>",
        "<|tool_call_end|>",
    ];

    private static readonly Regex PatternUse = new(@"\bRegex\b|\bRegularExpressions\b|\bGeneratedRegex\b", RegexOptions.Compiled);

    private static List<(int Line, string Text)> PatternUses(string source)
    {
        var raw = source.Split('\n');
        var code = SourceTree.CodeOnly(source).Split('\n');
        Assert.True(code.Length == raw.Length, "the code view and the source differ in line count, so every reported line would be wrong");
        return [.. code.Select((l, i) => (Line: i + 1, Code: l)).Where(x => PatternUse.IsMatch(x.Code)).Select(x => (x.Line, raw[x.Line - 1].Trim()))];
    }

    //a token joins the list only after it was seen leaking from a model, and this census moves in the same commit
    [Fact]
    public void THE_STRIP_ALLOWLIST_IS_EXACTLY_THE_FOUR_OBSERVED_TOKENS()
    {
        var field = typeof(ControlTokens).GetField("Allowlist", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.True(field is not null, "`ControlTokens` no longer holds an `Allowlist` field, so this census reads nothing");
        Assert.Equal(Four, (string[])field!.GetValue(null)!);
    }

    [Fact]
    public void THE_STRIP_SOURCE_USES_NO_PATTERN()
    {
        var uses = PatternUses(SourceTree.Read(Path.Combine(SourceTree.RepoRoot(), "Gatto", "Core", "ControlTokens.cs")));
        Assert.True(uses.Count == 0,
            "`ControlTokens.cs` uses a regular expression. the strip matches exact strings only:\n  "
            + string.Join("\n  ", uses.Select(u => $"{u.Line}  {u.Text}")));
    }

    [Fact]
    public void THE_PATTERN_DETECTOR_SEES_EACH_SPELLING_AND_NOT_PROSE()
    {
        string[] positives =
        [
            "using System.Text.RegularExpressions;",
            "var r = new Regex(\"<\\\\|.*?\\\\|>\");",
            "[GeneratedRegex(\"x\")] private static partial Regex Token();",
            "var t = System.Text.RegularExpressions.Regex.Replace(s, p, \"\");",
        ];
        foreach (var p in positives) Assert.Single(PatternUses(p));

        const string negatives = "// a Regex would strip too much\nvar s = \"Regex\";\nvar regexFree = true;\n";
        Assert.Empty(PatternUses(negatives));
    }
}
