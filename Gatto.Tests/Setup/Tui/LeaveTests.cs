using Gatto.Cli.Setup;
using Gatto.Cli.Setup.Tui;

namespace Gatto.Tests.Setup.Tui;

using L = Gatto.Cli.Setup.Tui.Epilogue.LeaveLead;
using Q = Gatto.Cli.Setup.Tui.Epilogue.LeaveQuestion;

//the leave sentence is computed from the one list of unreached questions, so each test reads it apart
public class LeaveTests
{
    private const string Model = "gemma-4-26B-A4B-it";

    private static string Say(L lead, string? model, params Q[] unreached) =>
        Epilogue.LeaveSentence(lead, model, unreached).Text;

    private static string Left(params Q[] unreached) => Say(L.PointedAt, Model, unreached);

    //the frame's own sentence verbatim, the one test that pins the whole string (the tests below take it apart)
    [Fact]
    public void THE_DRAWN_SENTENCE_word_for_word()
    {
        Assert.Equal(
            "gatto is set up and pointed at gemma-4-26B-A4B-it. Two questions weren't reached, "
            + "installing gatto, and the weekly update check, so gatto setup asks them next time. "
            + "Until then, run it from the file above.",
            Left(Q.Install, Q.Updates));
    }

    //one unreached question moves the count, the noun and the pronoun together
    [Fact]
    public void ONE_UNREACHED_QUESTION_moves_the_count_the_noun_and_the_pronoun()
    {
        var one = Left(Q.Updates);

        Assert.Contains("One question wasn't reached, the weekly update check, so gatto setup "
            + "asks it next time.", one, StringComparison.Ordinal);
        Assert.DoesNotContain("Two questions", one, StringComparison.Ordinal);
        Assert.DoesNotContain("installing gatto", one, StringComparison.Ordinal);
        Assert.DoesNotContain("asks them", one, StringComparison.Ordinal);
    }

    //the closing clause belongs to the install question, so a leave with only the update check unreached must not have it
    [Fact]
    public void THE_CLOSING_CLAUSE_RIDES_THE_INSTALL_QUESTION_and_nothing_else()
    {
        Assert.Contains("Until then", Left(Q.Install), StringComparison.Ordinal);
        Assert.Contains("Until then", Left(Q.Install, Q.Updates), StringComparison.Ordinal);

        Assert.DoesNotContain("Until then", Left(Q.Updates), StringComparison.Ordinal);
        Assert.DoesNotContain("Until then", Left(), StringComparison.Ordinal);
    }

    //a leave with no unreached questions is the lead alone, which is a real answer from the summary screen
    [Fact]
    public void NO_UNREACHED_QUESTIONS_says_only_what_happened()
    {
        Assert.Equal("gatto is set up and pointed at gemma-4-26B-A4B-it.", Left());
    }

    //before the write pause the sentence says nothing was written, and the next step is folded into it so one voice speaks
    [Fact]
    public void BEFORE_ANYTHING_LANDS_the_sentence_says_so_and_still_says_what_to_do()
    {
        var nothing = Epilogue.LeaveSentence(L.NothingWritten, null, []);

        Assert.Equal("Nothing has been written, run gatto setup again whenever you're ready.",
            nothing.Text);
        Assert.Contains("gatto setup", nothing.Highlight!);
        Assert.DoesNotContain("pointed at", nothing.Text, StringComparison.Ordinal);
    }

    //a leave that wrote nothing lists no questions and names no model, since it reached none of them
    [Fact]
    public void NOTHING_WRITTEN_CARRIES_NO_QUESTION_LIST_and_no_model()
    {
        Assert.Equal("Nothing has been written, run gatto setup again whenever you're ready.",
            Say(L.NothingWritten, Model, Q.Install, Q.Updates));
    }

    //the connect setup names no model, since the server row above already says what it points at
    [Fact]
    public void THE_CONNECT_ROAD_IS_SET_UP_without_naming_a_model()
    {
        var connect = Say(L.SetUp, null, Q.Updates);

        Assert.StartsWith("gatto is set up. One question wasn't reached", connect,
            StringComparison.Ordinal);
        Assert.DoesNotContain("pointed at", connect, StringComparison.Ordinal);
    }

    //the sentence says which half landed, since the model and the llama.cpp path are on disk but no default is
    [Fact]
    public void WRITTEN_WITHOUT_A_DEFAULT_says_which_half_landed()
    {
        var half = Say(L.SavedNotPointed, Model, Q.Updates);

        Assert.StartsWith("The model and the llama.cpp path are saved, gatto is not pointed at a "
            + "model yet.", half, StringComparison.Ordinal);
        Assert.DoesNotContain("Nothing has been written", half, StringComparison.Ordinal);
        Assert.DoesNotContain("gatto is set up", half, StringComparison.Ordinal);
    }

    //the head names the section the walk last showed, since the leave screen is deliberately off the strip
    [Fact]
    public void LEAVING_BY_OPTION_COMPOSES_A_HEAD_FROM_WHERE_THE_WALK_WAS()
    {
        var flow = new SetupFlow(DoneRenderTests.Machine(consent: true));
        flow.StartPastEngine();
        flow.Answer("0");
        flow.Answer(SetupFlow.ModelFetchNow);
        flow.Answer(SetupFlow.Landed);

        //every leave option sits before the write pause, so this site can only compose the nothing-written sentence
        var end = Assert.IsType<WizardScreen.Terminal>(flow.Answer(SetupFlow.ModelArrivedLeave));

        Assert.Equal("left", end.Key);
        Assert.Equal("left at the model", flow.LeftAt);
        Assert.Empty(flow.RecordRows());
        Assert.Equal("Nothing has been written, run gatto setup again whenever you're ready.",
            flow.LeaveStep!.Text);
    }

    //a walk that threw has no record and no stop segment, since MarkLeaving alone tells a crash from a leave
    [Fact]
    public void A_WALK_THAT_NEVER_LEFT_HAS_NO_RECORD_AND_NO_STOP_SEGMENT()
    {
        var flow = new SetupFlow(DoneRenderTests.Machine(consent: true));
        flow.StartPastEngine();
        flow.Answer("0");
        flow.Answer(SetupFlow.ModelFetchNow);
        flow.Answer(SetupFlow.Landed);
        flow.Answer(SetupFlow.ModelArrivedNext);
        while (flow.NeedsWritesApplied) flow.ResumeAfterWrites("gemma-4-26B-A4B-it");

        //writes have landed and the check was never answered, the state a leave and a throw share
        Assert.Empty(flow.RecordRows());
        Assert.Null(flow.LeftAt);
    }

    //the sentence names the model it was handed, so the fixture must use a different one than the drawn frame
    [Fact]
    public void THE_MODEL_NAME_COMES_FROM_THE_WALK()
    {
        Assert.Contains("pointed at qwen3-8b-instruct.",
            Say(L.PointedAt, "qwen3-8b-instruct", Q.Updates), StringComparison.Ordinal);
    }

    //no arm may hold an em dash, and every lead is crossed with every question set since one arm is composed separately
    [Theory]
    [InlineData(L.NothingWritten)]
    [InlineData(L.PointedAt)]
    [InlineData(L.SetUp)]
    [InlineData(L.SavedNotPointed)]
    internal void NO_EM_DASH_ON_ANY_ARM(L lead)
    {
        foreach (var unreached in new Q[][]
                 { [], [Q.Install], [Q.Updates], [Q.Install, Q.Updates] })
            Assert.DoesNotContain('—', Epilogue.LeaveSentence(lead, Model, unreached).Text);
    }
}
