using System.Text.RegularExpressions;

namespace Gatto.Tests.Census;

//a command name must reach the user plain, since a terminal shows the quote marks. count matches inside literals, and two names on a line count two.
public class CommandNameCensusTests
{
    //match gatto, a space and then a letter, < or -. a bare gatto is the product name in ordinary sentences and not a command
    private const string Command = "gatto [a-z<-]";

    //match only the quote or backtick directly before gatto and a command word. a closing quote can sit past an interpolation hole, so the match never needs one.
    private static readonly Regex Wrapped = new(
        "[`'](" + Command + "[^`'\r\n]*)", RegexOptions.Compiled);

    [Fact]
    public void NO_COMMAND_NAME_REACHES_A_USER_QUOTED_OR_BACKTICKED()
    {
        var offenders = new List<string>();

        foreach (var file in SourceTree.ProductionFiles())
        {
            var source = File.ReadAllText(file);
            foreach (var literal in SourceTree.StringLiterals(source))
                //take every match in the literal, one line can hold two command names
                foreach (Match m in Wrapped.Matches(literal))
                    offenders.Add($"{Path.GetFileName(file)}: {m.Value}");
        }

        Assert.True(offenders.Count == 0,
            $"{offenders.Count} command names reach a user wrapped in quotes or backticks — "
            + "backticks are markup a terminal does not render, so the user reads the punctuation. "
            + "Accent it where a painter is in scope; leave it PLAIN where none is.\n\n  "
            + string.Join("\n  ", offenders));
    }

    [Fact]
    public void THE_CENSUS_CAN_SEE_A_VIOLATION_when_there_is_one()
    {
        //a matcher that matches nothing also reports zero offenders. fire the pattern at the shapes it must catch and the ones it must not, so zero is a measurement.
        foreach (var bad in new[]
        {
            "run `gatto serve stop` when you're done",
            "try 'gatto doctor' first",
            "run 'gatto serve start [model]' or 'gatto serve stop'",
            "no models — run 'gatto model new <model.gguf>' to make one",
            "override with `gatto <role>`",
        })
            Assert.NotEmpty(Wrapped.Matches(bad));

        //two command names on one line must count as two, a count by line would miss the second
        Assert.Equal(2, Wrapped.Matches("run 'gatto serve start' or 'gatto serve stop'").Count);

        foreach (var fine in new[]
        {
            "run gatto doctor first",
            "gatto isn't set up on this machine yet.",
            "the user's gatto config",
            "gatto",
        })
            Assert.Empty(Wrapped.Matches(fine));
    }
}
