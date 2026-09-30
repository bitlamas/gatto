using System.Text.RegularExpressions;

namespace Gatto.Tests.Setup.Tui;

//exactly one numbered option says Enter next, two or more says Enter choose, and the typed field is a region that never counts
public class FooterVerbCensusTests
{
    //a numbered option row in the two spellings the frame draws
    private static readonly Regex Numbered = new(@"^(?:❯ |  )\d+\. \S", RegexOptions.Compiled);

    //screens that disagree with the count rule and can't be fixed yet go here by name with the reason (the list is empty on purpose)
    private static readonly string[] Known = [];

    private static string Footer(IReadOnlyList<string> rows)
    {
        for (var i = rows.Count - 1; i >= 0; i--)
            if (rows[i].Trim().Length > 0 && !rows[i].Contains('─', StringComparison.Ordinal))
                return rows[i];
        return "";
    }

    //the option count and the verb a screen claims, or null when it's outside the plain numbered grammar
    internal static (int Count, string Verb)? Claim(IReadOnlyList<string> rows)
    {
        var footer = Footer(rows);
        //the shelf is identified by its m key, which every shelf footer shows at every width (the arrows are gone by 100 columns)
        if (footer.Contains("   m ", StringComparison.Ordinal)
            || footer.Contains('↑', StringComparison.Ordinal)
            || footer.Contains('·', StringComparison.Ordinal))
            return null;                                   //the shelf, and s1's own vocabulary

        var verb = footer.Contains("Enter next", StringComparison.Ordinal) ? "next"
            : footer.Contains("Enter choose", StringComparison.Ordinal) ? "choose"
            : null;
        if (verb is null) return null;                     //keys-only footers name a deed, so there is no verb to compare

        var count = rows.Count(r => Numbered.IsMatch(r));
        return count == 0 ? null : (count, verb);          //bare options are a different screen
    }

    //the two halves of the no-default grammar stay together in a golden, and this reads only goldens, so a face change leaves it green
    [Fact]
    public void THE_NO_DEFAULT_GRAMMAR_TRAVELS_AS_A_PAIR()
    {
        var wrong = new List<string>();
        var seen = 0;

        foreach (var path in Directory.EnumerateFiles(Golden.Dir, "*.txt").OrderBy(p => p))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var footer = Footer(Census.SourceTree.Read(path).TrimEnd('\n').Split('\n'));
            var chooses = footer.Contains("↑↓ choose", StringComparison.Ordinal);
            var confirms = footer.Contains("Enter confirm", StringComparison.Ordinal);
            if (!chooses && !confirms) continue;

            seen++;
            if (chooses != confirms)
                wrong.Add($"{name}: `{footer.Trim()}` carries one half of the no-default grammar");
        }

        //two screens use this grammar, so the floor is the measured 2 (a reader that stopped matching would report a clean corpus)
        Assert.True(seen >= 2, $"the no-default census read {seen} screens - the reader is broken");
        Assert.True(wrong.Count == 0,
            "the no-default footer is `↑↓ choose` AND `Enter confirm`; one without the other advertises a key "
            + "that does not do what it says:\n  " + string.Join("\n  ", wrong));
    }

    [Fact]
    public void EVERY_NUMBERED_SCREENS_ENTER_VERB_MATCHES_ITS_OPTION_COUNT()
    {
        var wrong = new List<string>();
        var inScope = 0;

        foreach (var path in Directory.EnumerateFiles(Golden.Dir, "*.txt").OrderBy(p => p))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var rows = Census.SourceTree.Read(path).TrimEnd('\n').Split('\n');
            if (Claim(rows) is not { } claim) continue;

            inScope++;
            var want = claim.Count == 1 ? "next" : "choose";
            if (want != claim.Verb && !Known.Contains(name))
                wrong.Add($"{name}: {claim.Count} numbered option(s) but says `Enter {claim.Verb}`");
        }

        //a reader that matched nothing would report a clean corpus
        Assert.True(inScope > 30, $"the census read only {inScope} screens in scope - the reader is broken");
        Assert.True(wrong.Count == 0,
            "one numbered option is `Enter next`; two or more is `Enter choose`; the door is another "
            + "region and never counts:" + Environment.NewLine + "  "
            + string.Join(Environment.NewLine + "  ", wrong));
    }

    //every name in Known must still disagree with the count rule, or a fixed screen sits there unnoticed
    [Fact]
    public void EVERY_PINNED_EXCEPTION_IS_STILL_WRONG()
    {
        foreach (var name in Known)
        {
            var rows = Golden.Load(name.Split('-')[0], string.Join('-', name.Split('-')[1..^1]),
                int.Parse(name.Split('-')[^1]));
            var claim = Claim(rows);
            Assert.True(claim is not null, $"{name}: pinned as an exception but makes no claim");
            var want = claim!.Value.Count == 1 ? "next" : "choose";
            Assert.True(want != claim.Value.Verb,
                $"{name} agrees with the count rule now - delete it from Known");
        }
    }

    //one numbered option and a typed field saying choose, the shape the corpus actually got wrong
    [Fact]
    public void THE_CENSUS_CAN_SEE_A_PLANTED_DISAGREEMENT()
    {
        var planted = new[]
        {
            "  Which server should gatto talk to?",
            "",
            "❯ 1. Look again",
            "",
            "❯ type the server's address…                                  Tab, click or ↓ to type",
            "  Tab area   Enter choose   Esc leave",
        };

        var claim = Claim(planted);

        Assert.Equal((1, "choose"), claim);               //the census reads it and reports the wrong verb for one option
    }

    //one case per reason the census can decline: a correct screen, the shelf, and prose that isn't an option row
    [Fact]
    public void THE_CENSUS_DECLINES_EVERY_SHAPE_IT_SHOULD()
    {
        Assert.Equal((1, "next"), Claim(["❯ 1. Look again", "  Tab area   Enter next   Esc leave"]));
        Assert.Equal((2, "choose"), Claim(["❯ 1. Use it", "  2. Or not", "  Enter choose   Esc leave"]));

        //the shelf names its own keys and says Enter next by its own rule
        Assert.Null(Claim(["❯ 1. gemma", "  2. qwen",
            "  Tab area   ↑↓ move   Enter next   m local   Esc leave"]));

        //prose is not an option row, so a screen with none makes no claim
        Assert.Null(Claim(["  gatto found 2. of them, which is not a row",
            "  Enter next   Esc leave"]));
    }
}
