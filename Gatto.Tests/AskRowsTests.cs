using System.Text.Json;
using Gatto.Core.Tools;
using Xunit;

namespace Gatto.Tests;

//the ask_user call and its result read back into questions and what was chosen, built with the extension's own result shape
public sealed class AskRowsTests
{
    private static string Args(params (string Question, string Header, object[] Options)[] qs) =>
        JsonSerializer.Serialize(new { questions = qs.Select(q => new { question = q.Question, header = q.Header, options = q.Options }) });

    //the shape ask_user.csx returns, a header and what was selected per question
    private static string Result(params (string Header, string[] Selected)[] answers) =>
        JsonSerializer.Serialize(answers.Select(a => new { header = a.Header, selected = a.Selected }));

    private static readonly object[] Two = { "yes", "no" };

    private static readonly string FourArgs = Args(
        ("Which colour theme should the site default to?", "Theme", new object[] { "dark", "light", "follow the system" }),
        ("What should the package be called on the shelf?", "Name", new object[] { "gatto", "gatto-lite" }),
        ("Which platforms should the first build target?", "Targets", new object[] { "Windows x64", "Windows ARM64", "Linux x64" }),
        ("Should the release notes thank the people who walked the build?", "Notes", Two));

    private static readonly string FourResult = Result(
        ("Theme", new[] { "dark" }),
        ("Name", new[] { "gatto-lite, since the binary already carries that name in the installer, the docs and the site's download page" }),
        ("Targets", new[] { "Windows x64", "Windows ARM64" }),
        ("Notes", Array.Empty<string>()));

    [Fact]
    public void THE_FOUR_QUESTIONS_OF_THE_FRAMES_PAIR_IN_ORDER()
    {
        var pairs = AskRows.Parse(FourArgs, FourResult);
        Assert.NotNull(pairs);
        Assert.Equal(new[] { "Which colour theme should the site default to?", "What should the package be called on the shelf?",
            "Which platforms should the first build target?", "Should the release notes thank the people who walked the build?" }, pairs!.Select(p => p.Question));
        Assert.Equal(new[] { "dark" }, pairs[0].Chosen);
    }

    [Fact]
    public void A_TYPED_ANSWER_IS_ONE_CHOICE()
    {
        var pairs = AskRows.Parse(FourArgs, FourResult)!;
        Assert.Single(pairs[1].Chosen);
        Assert.StartsWith("gatto-lite, since", pairs[1].Chosen[0], StringComparison.Ordinal);
    }

    [Fact]
    public void A_MULTI_SELECT_OF_TWO_IS_TWO_CHOICES() =>
        Assert.Equal(new[] { "Windows x64", "Windows ARM64" }, AskRows.Parse(FourArgs, FourResult)![2].Chosen);

    [Fact]
    public void A_SKIP_IS_NO_CHOICE() => Assert.Empty(AskRows.Parse(FourArgs, FourResult)![3].Chosen);

    [Fact]
    public void A_REPEATED_HEADER_PAIRS_BY_POSITION()
    {
        var args = Args(("First question?", "Pick", Two), ("Second question?", "Pick", Two));
        var pairs = AskRows.Parse(args, Result(("Pick", new[] { "yes" }), ("Pick", new[] { "no" })))!;
        Assert.Equal(("First question?", "yes"), (pairs[0].Question, pairs[0].Chosen[0]));
        Assert.Equal(("Second question?", "no"), (pairs[1].Question, pairs[1].Chosen[0]));
    }

    [Fact]
    public void A_COUNT_THAT_DIFFERS_IS_NO_PARSE() =>
        Assert.Null(AskRows.Parse(FourArgs, Result(("Theme", new[] { "dark" }))));

    [Fact]
    public void A_RESULT_THAT_IS_NOT_JSON_IS_NO_PARSE()
    {
        Assert.Null(AskRows.Parse(FourArgs, "the user chose dark"));
        Assert.Null(AskRows.Parse(FourArgs, ""));
        Assert.Null(AskRows.Parse(FourArgs, "{\"header\":\"Theme\"}"));
    }

    [Fact]
    public void A_HEADER_OUT_OF_PLACE_IS_NO_PARSE() =>
        Assert.Null(AskRows.Parse(FourArgs, Result(("Name", new[] { "dark" }), ("Theme", new[] { "gatto" }), ("Targets", new[] { "Linux x64" }), ("Notes", new[] { "yes" }))));

    [Fact]
    public void OPTIONS_AS_OBJECTS_AND_AS_STRINGS_BOTH_PARSE()
    {
        var args = Args(("Objects?", "A", new object[] { new { label = "one", description = "the first", recommended = true }, new { label = "two" } }),
            ("Strings?", "B", Two));
        var pairs = AskRows.Parse(args, Result(("A", new[] { "one" }), ("B", new[] { "no" })))!;
        Assert.Equal(new[] { "one" }, pairs[0].Chosen);
        Assert.Equal(new[] { "no" }, pairs[1].Chosen);
    }

    [Fact]
    public void QUESTIONS_OF_COUNTS_THE_ARRAY_AND_REFUSES_OTHER_SHAPES()
    {
        Assert.Equal(4, AskRows.QuestionsOf(FourArgs));
        Assert.Equal(1, AskRows.QuestionsOf(Args(("One?", "A", Two))));
        Assert.Null(AskRows.QuestionsOf("{\"questions\":[]}"));
        Assert.Null(AskRows.QuestionsOf("{\"questions\":\"none\"}"));
        Assert.Null(AskRows.QuestionsOf("{\"path\":\"a.cs\"}"));
        Assert.Null(AskRows.QuestionsOf("{\"questions\":[{\"header\":\"no text\"}]}"));
        Assert.Null(AskRows.QuestionsOf("not json"));
        Assert.Null(AskRows.QuestionsOf(null));
    }

    [Fact]
    public void THE_ROWS_ARE_A_HEADING_PER_QUESTION_A_ROW_PER_CHOICE_AND_SKIPPED_IN_THE_NOTE_INK()
    {
        var rows = ToolBody.Ask(AskRows.Parse(FourArgs, FourResult)!);
        Assert.Equal(9, rows.Count);
        Assert.Equal(new[] { BodyKind.Heading, BodyKind.Plain, BodyKind.Heading, BodyKind.Plain, BodyKind.Heading, BodyKind.Plain, BodyKind.Plain, BodyKind.Heading, BodyKind.Plain },
            rows.Select(r => r.Kind));
        Assert.Equal(("skipped", BodyInk.Note), (rows[8].Text, rows[8].Ink));
        Assert.All(rows.Where(r => r.Kind == BodyKind.Heading), r => Assert.Equal(BodyInk.Heading, r.Ink));
        Assert.DoesNotContain(rows, r => r.Text is "light" or "follow the system" or "Linux x64" or "gatto");
    }
}
