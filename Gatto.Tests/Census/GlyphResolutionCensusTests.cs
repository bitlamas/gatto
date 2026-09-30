using Gatto.Tests.Setup;

namespace Gatto.Tests.Census;

//each command arm must resolve the glyph set once, so two painters cannot disagree about the host. count per arm, so a moved call can't dodge a file ceiling.
public class GlyphResolutionCensusTests
{
    private const string Call = "CommandBanner.GlyphsFor(";

    //count the glyph-set resolutions per method, keyed by name. methods match only at class-member indent, and a local call counts toward the arm holding it.
    private static Dictionary<string, int> PerMethod(string source)
    {
        var masked = SourceMask.Mask(source);
        var counts = new Dictionary<string, int>();
        var current = "(before any method)";

        foreach (var line in masked.Split('\n'))
        {
            var name = MemberName(line);
            if (name is not null)
            {
                current = name;
                counts.TryAdd(current, 0);
            }

            var at = 0;
            while ((at = line.IndexOf(Call, at, StringComparison.Ordinal)) >= 0)
            {
                counts[current] = counts.GetValueOrDefault(current) + 1;
                at += Call.Length;
            }
        }

        return counts;
    }

    //return the method a class-member signature declares, or null. a tuple return gives the declared name, or its calls are filed under the method before it.
    private static string? MemberName(string line)
    {
        if (!line.StartsWith("    ", StringComparison.Ordinal)
            || line.StartsWith("     ", StringComparison.Ordinal)) return null;

        var body = line[4..];
        if (!body.StartsWith("private ", StringComparison.Ordinal)
            && !body.StartsWith("public ", StringComparison.Ordinal)
            && !body.StartsWith("internal ", StringComparison.Ordinal)
            && !body.StartsWith("protected ", StringComparison.Ordinal)) return null;

        //take the name as the identifier right before the first paren that has one. the first paren belongs to a tuple return type, and the token before it is a keyword
        for (var open = body.IndexOf('('); open >= 0; open = body.IndexOf('(', open + 1))
        {
            var head = body[..open];
            var end = head.Length;
            var start = end;
            while (start > 0 && (char.IsLetterOrDigit(head[start - 1]) || head[start - 1] == '_'))
                start--;

            var name = head[start..end];
            if (name.Length > 0 && (char.IsLetter(name[0]) || name[0] == '_')) return name;
        }

        return null;
    }

    [Fact]
    public void NO_COMMAND_ARM_RESOLVES_THE_GLYPH_SET_TWICE()
    {
        var counts = PerMethod(SourceTree.Read(
            Path.Combine(SourceTree.RepoRoot(), "Gatto", "Cli", "GattoApp.cs")));

        var twice = counts.Where(c => c.Value > 1).OrderByDescending(c => c.Value).ToList();

        Assert.True(twice.Count == 0,
            "these arms ask which host this is more than once, so two painters in one run can "
            + "disagree about it:\n"
            + string.Join("\n", twice.Select(c => $"  {c.Key}: {c.Value}")));
    }

    //the counts must be nonzero, or the rule above passes on nothing. a blanking masker, a matcher with no method, or a renamed call each pass empty.
    [Fact]
    public void AND_THE_CENSUS_CAN_SEE_THE_CALLS_IT_IS_COUNTING()
    {
        var source = SourceTree.Read(
            Path.Combine(SourceTree.RepoRoot(), "Gatto", "Cli", "GattoApp.cs"));
        var counts = PerMethod(source);

        Assert.True(counts.Count > 20,
            $"the member matcher found only {counts.Count} methods in GattoApp.cs");
        Assert.True(counts.Values.Sum() >= 6,
            $"the census counted {counts.Values.Sum()} glyph resolutions, and there are at least six");
    }

    //add cases that tell two calls in one method from the same two split across methods. without them the rule above could pass with no way to fail.
    [Theory]
    [InlineData("    private static int One()\n    {\n        var a = CommandBanner.GlyphsFor(home);\n"
              + "        var b = CommandBanner.GlyphsFor(home);\n    }\n", "One", 2)]
    [InlineData("    private static (int Code, bool Launch) Tuple()\n    {\n"
              + "        var a = CommandBanner.GlyphsFor(home);\n"
              + "        var b = CommandBanner.GlyphsFor(home);\n    }\n", "Tuple", 2)]
    [InlineData("    private static int A()\n    {\n        var a = CommandBanner.GlyphsFor(home);\n    }\n"
              + "    private static int B()\n    {\n        var b = CommandBanner.GlyphsFor(home);\n    }\n",
                "A", 1)]
    [InlineData("    private static int Once()\n    {\n"
              + "        var a = CommandBanner.GlyphsFor(home), b = CommandBanner.GlyphsFor(home);\n    }\n",
                "Once", 2)]
    public void THE_CENSUS_COUNTS_PER_METHOD(string source, string method, int expected)
    {
        Assert.Equal(expected, PerMethod(source)[method]);
    }

    //masking removes comments and strings, so text that only names the call counts zero (a looser matcher would fire on that prose)
    [Fact]
    public void PROSE_ABOUT_THE_CALL_IS_NOT_A_CALL()
    {
        const string source = "    private static int Talks()\n"
            + "    {\n"
            + "        // CommandBanner.GlyphsFor(home) is asked once per arm\n"
            + "        var why = \"CommandBanner.GlyphsFor(home)\";\n"
            + "        var real = CommandBanner.GlyphsFor(home);\n"
            + "    }\n";

        Assert.Equal(1, PerMethod(source)["Talks"]);
    }
}
