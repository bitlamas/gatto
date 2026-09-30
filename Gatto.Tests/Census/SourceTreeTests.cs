namespace Gatto.Tests.Census;

//tests for the shared source-reading home itself. consumer tests only ask questions downstream, so none of them can fail in a way that points here
public class SourceTreeTests
{
    [Fact]
    public void COMMENTS_RETURNS_EVERY_COMMENT_AND_NO_STRING_THAT_LOOKS_LIKE_ONE()
    {
        //the identity census reads this list as the published comments, so a missed one publishes unscanned
        const string src = "//own line\r\nint x = 1;   //trailing\r\nint y = /* block */ 2;\r\nvar u = \"http://not.a.comment\";\r\n";

        var comments = SourceTree.Comments(src);

        Assert.Equal(["//own line", "//trailing", "/* block */"], comments);
    }

    [Fact]
    public void A_HOLE_CONTAINING_A_STRING_WITH_A_CLOSING_BRACE_DOES_NOT_END_EARLY()
    {
        //a closing brace inside a nested string must not end the hole, or the prose after it reads as code.
        const string src = "var s = $\"before{(flag ? \"}\" : TypeAfterNestedString)}PROSE_TAIL\";";

        var code = SourceTree.CodeOnly(src);

        //everything in the hole after the nested literal is code, which an early close silently drops.
        Assert.Contains("flag", code, StringComparison.Ordinal);
        Assert.Contains("TypeAfterNestedString", code, StringComparison.Ordinal);
        //the string's own body is prose and must stay blanked. an early close makes the scanner treat the rest of the line as code.
        Assert.DoesNotContain("PROSE_TAIL", code, StringComparison.Ordinal);
        Assert.DoesNotContain("before", code, StringComparison.Ordinal);
    }

    [Fact]
    public void A_HOLE_CONTAINING_A_CHAR_LITERAL_WITH_A_CLOSING_BRACE_KEEPS_THE_CODE_AFTER_IT()
    {
        //a '}' char literal can close the hole early, so the assertion pins the code that follows it in the same hole
        const string src = "var s = $\"before{(c == '}' ? TypeAfterCharLiteral : 0)}tail\";";

        var code = SourceTree.CodeOnly(src);

        Assert.Contains("TypeAfterCharLiteral", code, StringComparison.Ordinal);
    }

    [Fact]
    public void A_NESTED_INTERPOLATED_STRING_STILL_YIELDS_ITS_OWN_HOLE_AS_CODE()
    {
        //skipping nested strings wholesale would discard the holes inside them, which are code. a type referenced only inside one would read as an orphan
        const string src = "var s = $\"a{(x is null ? \"\" : $\" '{TypeNameInsideNestedHole}'\")}b\";";

        var code = SourceTree.CodeOnly(src);

        Assert.Contains("TypeNameInsideNestedHole", code, StringComparison.Ordinal);
        Assert.DoesNotContain("a{", code, StringComparison.Ordinal);   //the outer prose stays blanked even as the nested hole survives.
    }

    [Fact]
    public void CODE_ONLY_PRESERVES_THE_LINE_COUNT()
    {
        //a detector matches on the code view and reports positions in raw lines. the fixture is concatenated, a raw string can't hold a quote run as long as its fence
        const string src =
            "var a = 1;                    // a line comment\n"
            + "/* a block\n"
            + "   comment spanning lines */\n"
            + "var b = @\"a verbatim\n"
            + "string over two lines\";\n"
            + "var c = $\"interpolated {value} with a hole\";\n"
            + "var d = \"\"\"\n"
            + "    a raw string\n"
            + "    over lines\n"
            + "    \"\"\";\n";

        Assert.Equal(src.Split('\n').Length, SourceTree.CodeOnly(src).Split('\n').Length);
        Assert.Equal(src.Split('\n').Length, SourceTree.WithoutComments(src).Split('\n').Length);
    }

    //a $$ raw string opens a hole with two braces, so one brace is text and the code after the string stays code
    [Fact]
    public void A_DOUBLE_DOLLAR_RAW_STRING_READS_ONE_BRACE_AS_TEXT()
    {
        const string src =
            "var s = $$\"\"\"\n"
            + "    #load \"{{HoleName}}\"\n"
            + "    Gatto.Register(\"kit_tool\", \"x\", \"{\\\"type\\\":\\\"object\\\"}\",\n"
            + "        (a,c,t) => Task.FromResult(new ProseResult(\"ok\")));\n"
            + "    \"\"\";\n"
            + "// COMMENT_AFTER\n"
            + "var t = CodeAfterRawString;\n";

        var code = SourceTree.CodeOnly(src);

        Assert.Contains("HoleName", code, StringComparison.Ordinal);
        Assert.Contains("CodeAfterRawString", code, StringComparison.Ordinal);
        Assert.DoesNotContain("ProseResult", code, StringComparison.Ordinal);
        Assert.DoesNotContain("kit_tool", code, StringComparison.Ordinal);
        Assert.DoesNotContain("COMMENT_AFTER", SourceTree.WithoutComments(src), StringComparison.Ordinal);
    }

    //a brace run shorter than the dollar count is text, and a longer run opens the hole with its last braces
    [Fact]
    public void A_TRIPLE_DOLLAR_RAW_STRING_READS_SHORT_BRACE_RUNS_AS_TEXT()
    {
        const string src = "var s = $$$\"\"\"{TextName} {{{{HoleName}}}}\"\"\";\nvar t = CodeAfter;\n";

        var code = SourceTree.CodeOnly(src);

        Assert.Contains("HoleName", code, StringComparison.Ordinal);
        Assert.Contains("CodeAfter", code, StringComparison.Ordinal);
        Assert.DoesNotContain("TextName", code, StringComparison.Ordinal);
    }
}
