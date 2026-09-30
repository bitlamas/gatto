using Gatto.Repl.Render;
using Gatto.Terminal;
using Xunit;

namespace Gatto.Tests.Render;

public sealed class CSharpClassifierTests
{
    //every character of the occurrence-th copy of probe in code must have the expected role
    private static void AssertRole(string code, string probe, SpanRole expected, int occurrence = 0)
    {
        var roles = new SpanRole[code.Length];
        foreach (var span in CSharpClassifier.Classify(code))
            for (var i = span.Start; i < span.Start + span.Length; i++) roles[i] = span.Role;

        var at = -1;
        for (var n = 0; n <= occurrence; n++) at = code.IndexOf(probe, at + 1, StringComparison.Ordinal);
        Assert.True(at >= 0, $"the probe '{probe}' is not in the code");
        var got = roles.Skip(at).Take(probe.Length).ToArray();
        Assert.True(got.All(r => r == expected), $"'{probe}' read as [{string.Join(",", got)}], expected {expected}");
    }

    [Fact]
    public void A_declaration_colours_its_keyword_its_number_and_its_punctuation()
    {
        const string code = "var count = 42;";
        AssertRole(code, "var", SpanRole.Keyword);
        AssertRole(code, "count", SpanRole.None);
        AssertRole(code, "=", SpanRole.Punct);
        AssertRole(code, "42", SpanRole.Number);
        AssertRole(code, ";", SpanRole.Punct);
    }

    [Fact]
    public void Comments_of_every_form_are_comments()
    {
        const string code = "// line\n/* block */\n/// <summary>doc</summary>\nint x;";
        AssertRole(code, "// line", SpanRole.Comment);
        AssertRole(code, "/* block */", SpanRole.Comment);
        AssertRole(code, "/// <summary>doc</summary>", SpanRole.Comment);
        AssertRole(code, "int", SpanRole.Keyword);
    }

    [Fact]
    public void Every_string_form_is_a_string_and_an_interpolation_hole_is_code()
    {
        const string code = "var a = \"plain\"; var b = @\"verbatim\"; var c = 'c'; var d = $\"n={n + 1}\"; var e = \"\"\"raw\"\"\";";
        AssertRole(code, "\"plain\"", SpanRole.String);
        AssertRole(code, "@\"verbatim\"", SpanRole.String);
        AssertRole(code, "'c'", SpanRole.String);
        AssertRole(code, "$\"n=", SpanRole.String);
        AssertRole(code, "{", SpanRole.Punct);
        AssertRole(code, "1", SpanRole.Number);
        AssertRole(code, "\"\"\"raw\"\"\"", SpanRole.String);
    }

    [Fact]
    public void Type_names_in_type_positions_and_declarations_are_types()
    {
        const string code = "class Widget : Base<int> { List<string> items = new List<string>(); }";
        AssertRole(code, "class", SpanRole.Keyword);
        AssertRole(code, "Widget", SpanRole.Type);
        AssertRole(code, "Base", SpanRole.Type);
        AssertRole(code, "List", SpanRole.Type);
        AssertRole(code, "List", SpanRole.Type, occurrence: 1);
        AssertRole(code, "string", SpanRole.Keyword);
        AssertRole(code, "items", SpanRole.None);
    }

    [Fact]
    public void A_called_method_is_a_function_and_its_receiver_is_not_guessed()
    {
        const string code = "Console.WriteLine(Format(\"x\"));";
        AssertRole(code, "WriteLine", SpanRole.Function);
        AssertRole(code, "Format", SpanRole.Function);
        AssertRole(code, "Console", SpanRole.None);
    }

    [Fact]
    public void Contextual_keywords_read_as_keywords_where_the_parser_says_so()
    {
        const string code = "async Task Run() { await Go(); var n = nameof(Run); }\nrecord Point(int X);";
        AssertRole(code, "async", SpanRole.Keyword);
        AssertRole(code, "Task", SpanRole.Type);
        AssertRole(code, "Run", SpanRole.Function);
        AssertRole(code, "await", SpanRole.Keyword);
        AssertRole(code, "nameof", SpanRole.Keyword);
        AssertRole(code, "record", SpanRole.Keyword);
        AssertRole(code, "Point", SpanRole.Type);
    }

    [Fact]
    public void An_attribute_name_and_a_preprocessor_line_are_marked()
    {
        const string code = "#if DEBUG\n#endif\n[Fact]\nvoid T() { }";
        AssertRole(code, "#if DEBUG", SpanRole.Keyword);
        AssertRole(code, "Fact", SpanRole.Type);
    }

    [Fact]
    public void Code_inside_an_inactive_preprocessor_region_reads_as_a_comment()
    {
        //a fence is parsed with no symbols defined, so an #if region stays inactive, the way the compiler sees it without one
        const string code = "#if DEBUG\nvoid Hidden() { }\n#endif";
        AssertRole(code, "void Hidden() { }", SpanRole.Comment);
    }

    [Fact]
    public void A_half_written_block_still_classifies_what_it_can()
    {
        const string code = "var s = \"unterminated\nif (s.Length > 0) {";
        AssertRole(code, "var", SpanRole.Keyword);
        AssertRole(code, "if", SpanRole.Keyword);
    }

    [Fact]
    public void Random_text_never_throws_and_every_span_stays_inside_the_text()
    {
        var rng = new Random(7);
        const string alphabet = "abc{}()[]\"'@$#/\\*;:=<>.,0123456789 \n\tclassvoidif";
        var spans = 0;
        for (var n = 0; n < 300; n++)
        {
            var text = new string(Enumerable.Range(0, rng.Next(0, 60)).Select(_ => alphabet[rng.Next(alphabet.Length)]).ToArray());
            foreach (var s in CSharpClassifier.Classify(text))
            {
                spans++;
                Assert.True(s.Start >= 0 && s.Length > 0 && s.Start + s.Length <= text.Length, $"the span {s} falls outside `{text}`");
            }
        }
        Assert.True(spans > 0, "the classifier returned no spans at all, so the bounds were never checked");
    }
}
