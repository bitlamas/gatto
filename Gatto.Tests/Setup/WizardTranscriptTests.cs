using Gatto.Repl;
using Gatto.Cli.Setup;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup;

//every assertion reads the replayed screen, the bytes written show nothing about what survived
public class WizardTranscriptTests
{
    private static readonly Theme T = WizardRig.T;

    //the rig lives in WizardRig so every file that scripts a face models the console the same way

    private static WizardScreen.Choice Retry(string key = "retry") => new(
        key,
        "No server answered, start it and try again?",
        [new ChoiceOption("retry", "Try again"), new ChoiceOption("no", "Leave setup")]);

    //the answer goes below the question

    [Fact]
    public void THE_COMMITTED_ANSWER_RENDERS_ON_ITS_OWN_ROW_BELOW_THE_QUESTION()
    {
        //answers go under their question rather than beside it, so assert both rows and their order
        var rig = new WizardRig();
        rig.Face(WizardRig.Digit('1')).Choose(Retry());

        var lines = rig.Plain.Split('\n').Select(l => l.Trim()).ToList();
        var q = lines.FindIndex(l => l.StartsWith("No server answered", StringComparison.Ordinal));
        var a = lines.FindIndex(l => l == "Try again");

        Assert.True(q >= 0, "the question must survive as history");
        Assert.True(a >= 0, "the answer must be a row of its own, not a suffix");
        Assert.Equal(q + 1, a);
    }

    [Fact]
    public void A_GREY_RULE_CLOSES_THE_SEGMENT()
    {
        //every answer or segment is separated by a grey rule of dashes
        var rig = new WizardRig();
        rig.Face(WizardRig.Digit('1')).Choose(Retry());

        var lines = rig.Plain.Split('\n').Select(l => l.TrimEnd()).ToList();
        var rule = lines.FindIndex(l => l.StartsWith("-----", StringComparison.Ordinal));
        Assert.True(rule >= 0, "the segment must be closed by a rule");

        //painted dim by the widget's theme rather than by an escape from the caller
        Assert.Contains(T.Paint(new string('-', 80), Theme.Dim), rig.Painted, StringComparison.Ordinal);
    }

    [Fact]
    public void THE_ANSWER_IS_ACCENT_AND_THE_QUESTION_IS_NOT_DIM()
    {
        //the answer is accent, and the question the wizard asked must not be dim
        var rig = new WizardRig();
        rig.Face(WizardRig.Digit('1')).Choose(Retry());

        Assert.Contains(T.Paint("Try again", Theme.Accent), rig.Painted, StringComparison.Ordinal);
        Assert.DoesNotContain(T.Paint("No server answered, start it and try again?", Theme.Dim),
            rig.Painted, StringComparison.Ordinal);
    }

    //the repeat collapse

    [Fact]
    public void A_REASKED_QUESTION_COLLAPSES_ITS_REPEATS_INTO_ONE_COUNTED_ROW()
    {
        //the same question and answer repeated collapses to one row with a count
        var rig = new WizardRig();
        var face = rig.Face(WizardRig.Digit('1'), WizardRig.Digit('1'), WizardRig.Digit('1'));
        face.Choose(Retry());
        face.Choose(Retry());
        face.Choose(Retry());

        var plain = rig.Plain;
        Assert.Contains("Try again (3x)", plain, StringComparison.Ordinal);

        //the collapse replaces the earlier pairs, so the question appears once on screen
        Assert.Equal(1, rig.Occurrences("No server answered"));
        Assert.Equal(1, rig.Occurrences("Try again ("));
    }

    [Fact]
    public void A_DIFFERENT_ANSWER_TO_THE_SAME_QUESTION_STACKS_UNDER_THE_ONE_QUESTION()
    {
        //the collapse is keyed on the question, so the question appears once even when the answer changes
        var rig = new WizardRig();
        var face = rig.Face(WizardRig.Digit('1'), WizardRig.Digit('1'), WizardRig.Digit('1'), WizardRig.Digit('2'));
        face.Choose(Retry());
        face.Choose(Retry());
        face.Choose(Retry());
        face.Choose(Retry());

        var plain = rig.Plain;
        Assert.Equal(1, rig.Occurrences("No server answered"));
        Assert.Contains("Try again (3x)", plain, StringComparison.Ordinal);
        Assert.Contains("Leave setup", plain, StringComparison.Ordinal);

        //the answers appear in the order they were given, the last one ending the segment
        var lines = plain.Split('\n').Select(l => l.Trim()).ToList();
        Assert.True(lines.FindIndex(l => l.StartsWith("Try again", StringComparison.Ordinal))
            < lines.FindIndex(l => l == "Leave setup"));
    }

    [Fact]
    public void ONE_RULE_CLOSES_A_SEGMENT_no_matter_how_many_answers_it_took()
    {
        //the divider closes a segment rather than an answer, so several presses under one question share one rule
        var rig = new WizardRig();
        var face = rig.Face(WizardRig.Digit('1'), WizardRig.Digit('1'), WizardRig.Digit('2'));
        face.Choose(Retry());
        face.Choose(Retry());
        face.Choose(Retry());

        var rules = rig.Plain.Split('\n').Count(l => l.StartsWith("-----", StringComparison.Ordinal));
        Assert.Equal(1, rules);
    }

    [Fact]
    public void A_DIFFERENT_QUESTION_IS_NEVER_COLLAPSED_INTO_THE_ONE_ABOVE_IT()
    {
        var rig = new WizardRig();
        var face = rig.Face(WizardRig.Digit('1'), WizardRig.Digit('1'));
        face.Choose(new WizardScreen.Choice("fork", "How do you want to run the server?",
            [new ChoiceOption("llama", "Let gatto manage it using llama.cpp")]));
        face.Choose(Retry());

        var plain = rig.Plain;
        Assert.Contains("How do you want to run the server?", plain, StringComparison.Ordinal);
        Assert.Contains("Let gatto manage it using llama.cpp", plain, StringComparison.Ordinal);
        Assert.Contains("No server answered", plain, StringComparison.Ordinal);
    }

    //the reclaim may only eat rows the transcript itself wrote

    [Fact]
    public void AN_INFO_SCREEN_BETWEEN_TWO_IDENTICAL_ANSWERS_IS_NEVER_EATEN()
    {
        //the reclaim must not eat a row another writer put above the block, the Interrupt call is what stops it
        var rig = new WizardRig();
        var face = rig.Face(WizardRig.Digit('1'), WizardRig.Digit('1'));
        face.Choose(Retry());
        face.Show(new WizardScreen.Info("ok", ["✓ that one worked"]));
        face.Choose(Retry());

        Assert.Contains("that one worked", rig.Plain, StringComparison.Ordinal);
    }

    [Fact]
    public void A_VALIDATION_COMPLAINT_SURVIVES_THE_RETYPED_ANSWER()
    {
        //the rejection sentence must survive the retyped answer, the cursor sits on the offer so a digit picks a row
        var rig = new WizardRig();
        var attempts = 0;
        rig.Face(WizardRig.Digit('1'), WizardRig.Digit('1')).Ask(new WizardScreen.Ask(
            "ctx", "How much context?",
            Validate: _ => attempts++ == 0 ? "that isn't a number" : null,
            Offer: new AskOffer("Use 32768", "32768")));

        Assert.Contains("that isn't a number", rig.Plain, StringComparison.Ordinal);
    }

    //a painter change is resize-tested

    [Theory]
    [InlineData(30)]
    [InlineData(40)]
    [InlineData(80)]
    [InlineData(200)]
    public void THE_COLLAPSE_HOLDS_AT_EVERY_WIDTH(int width)
    {
        var rig = new WizardRig(width);
        var face = rig.Face(WizardRig.Digit('1'), WizardRig.Digit('1'));
        face.Choose(Retry());
        face.Choose(Retry());

        var plain = rig.Plain;
        Assert.Contains("(2x)", plain, StringComparison.Ordinal);
        //the question wraps at 30 columns, so count the opening fragment rather than the sentence
        Assert.Equal(1, rig.Occurrences("No server"));
    }

    [Fact]
    public void A_RESIZE_BETWEEN_TWO_IDENTICAL_ANSWERS_APPENDS_RATHER_THAN_ERASING_THE_WRONG_ROWS()
    {
        //a resize between the presses invalidates the row count, so the collapse repeats itself rather than eat rows that belong to something else
        var rig = new WizardRig(80);
        var face = rig.Face(WizardRig.Digit('1'), WizardRig.Digit('1'));
        face.Choose(Retry());
        rig.Surface.Width = 40;
        face.Choose(Retry());

        var plain = rig.Plain;
        Assert.Equal(2, rig.Occurrences("No server answered"));
        Assert.DoesNotContain("(2x)", plain, StringComparison.Ordinal);
    }

    [Fact]
    public void A_WRAPPED_ANSWER_STILL_COLLAPSES_CLEANLY()
    {
        //the reclaim count comes from the wrapped rows, so a long answer takes more than one
        var longAnswer = @"C:\a-very-long-directory-name\that-will-not-fit\on-one-line-at-all\llama-server.exe";
        var screen = new WizardScreen.Choice("p", "Where is llama-server.exe?",
            [new ChoiceOption("p", longAnswer)]);

        var rig = new WizardRig(40);
        var face = rig.Face(WizardRig.Digit('1'), WizardRig.Digit('1'));
        face.Choose(screen);
        face.Choose(screen);

        var plain = rig.Plain;
        Assert.Contains("(2x)", plain, StringComparison.Ordinal);
        Assert.Equal(1, rig.Occurrences("Where is llama-server.exe?"));
    }


    //the empty-tail contract

    [Fact]
    public void AN_EMPTY_TAIL_RESETS_THE_RECLAIM_STATE_SO_A_LATER_COMMIT_APPENDS()
    {
        //an empty tail must reset the reclaim state, a later commit would otherwise eat rows that are gone
        var t = new WizardTranscript(WizardRig.T, glyphs: GlyphSet.Unicode);

        Assert.Equal(0, t.Commit(new EchoInput("Nothing new here yet", "Look again", 80, false)).ReclaimAbove);
        Assert.True(t.Commit(new EchoInput("Nothing new here yet", "Look again", 80, false)).ReclaimAbove > 0,
            "the second commit of the same question must reclaim the first block, so a re-asked question shows once");

        var skipped = t.Skip();
        Assert.Empty(skipped.Rows);

        Assert.Equal(0, t.Commit(new EchoInput("Nothing new here yet", "Look again", 80, false)).ReclaimAbove);
    }

    [Fact]
    public void A_SCREEN_WITH_NO_QUESTION_OR_A_NEW_QUESTION_NEVER_CONTINUES()
    {
        //the collapse keys on the question text, so a screen with no question never continues
        var t = new WizardTranscript(WizardRig.T, glyphs: GlyphSet.Unicode);

        //a null question stops the collapse, however often the same screen commits
        t.Commit(new EchoInput(null, "answer", 80, false));
        Assert.Equal(0, t.Commit(new EchoInput(null, "answer", 80, false)).ReclaimAbove);

        //a different question never continues from the one before it
        var u = new WizardTranscript(WizardRig.T, glyphs: GlyphSet.Unicode);
        u.Commit(new EchoInput("first question", "a", 80, false));
        Assert.Equal(0, u.Commit(new EchoInput("second question", "a", 80, false)).ReclaimAbove);
    }

    //a back-step is a navigation rather than an answer

    //a back-step commits a navigation line, and the option's own label must not survive
    [Fact]
    public void A_BACK_STEP_COMMITS_A_NAVIGATION_NOT_AN_ANSWER()
    {
        var rig = new WizardRig();
        var screen = new WizardScreen.Choice(
            "fork",
            "How do you want to run the server?",
            [new ChoiceOption("a", "gatto sets one up"), new ChoiceOption("b", "I have my own")])
        { AllowBack = true };

        var chosen = rig.Face(WizardRig.Down, WizardRig.Down, WizardRig.Enter).Choose(screen);

        //the appended row is the one that was pressed
        Assert.Equal(SetupFlow.BackKey, chosen);

        var lines = rig.Plain.Split('\n').Select(l => l.Trim()).ToList();
        var q = lines.FindIndex(l => l.StartsWith("How do you want", StringComparison.Ordinal));
        var a = lines.FindIndex(l => l == "→ went back");

        Assert.True(a >= 0, "a back-step commits a navigation line");
        Assert.Equal(q + 1, a);
        Assert.DoesNotContain(SetupFace.BackLabelOf(Gatto.Terminal.GlyphSet.Unicode), rig.Plain, StringComparison.Ordinal);
    }

    //the navigation row is dim where an answer would be accent
    [Fact]
    public void THE_NAVIGATION_LINE_IS_DIM_WHERE_AN_ANSWER_WOULD_BE_ACCENT()
    {
        var rig = new WizardRig();
        var screen = new WizardScreen.Choice(
            "fork", "How do you want to run the server?",
            [new ChoiceOption("a", "gatto sets one up")])
        { AllowBack = true };

        rig.Face(WizardRig.Down, WizardRig.Enter).Choose(screen);

        Assert.Contains(T.Paint("→ went back", Theme.Dim), rig.Painted, StringComparison.Ordinal);
        Assert.DoesNotContain(T.Paint("→ went back", Theme.Accent), rig.Painted, StringComparison.Ordinal);
    }
}
