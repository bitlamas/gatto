using Gatto.Cli.Setup;
using Gatto.Tests.Fakes;
using Xunit;

namespace Gatto.Tests.Setup;

//the three columns of WalkOpening must agree on one walk, and the face's answer proves it rather than a count
public class WalkOpeningTests
{
    //the real face answers the keys here, a table mapping keys to options in this test would only agree with itself
    [Fact]
    public void EACH_OPENING_KEYSTROKE_PRODUCES_THE_ANSWER_ITS_ROW_NAMES()
    {
        var flow = new SetupFlow(new WizardProbes());
        var face = new WizardRig(100).Face(WalkOpening.Keys);

        var screen = flow.Start();

        for (var i = 0; i < WalkOpening.Screens.Count; i++)
        {
            var choice = Assert.IsType<WizardScreen.Choice>(screen);
            Assert.Equal(WalkOpening.Screens[i], choice.Key);

            var answered = face.Choose(choice);
            Assert.Equal(WalkOpening.Answers[i], answered);

            screen = flow.Answer(answered!);
        }
    }

    //drive TuiWizardSurface here, the face that ships, SetupFace has no production caller
    [Fact]
    public void EACH_OPENING_KEYSTROKE_IS_THE_KEY_THE_SHIPPED_FACE_ACCEPTS()
    {
        var flow = new SetupFlow(new WizardProbes());
        var screen = flow.Start();

        for (var i = 0; i < WalkOpening.Screens.Count; i++)
        {
            var choice = Assert.IsType<WizardScreen.Choice>(screen);
            Assert.Equal(WalkOpening.Screens[i], choice.Key);
            Assert.Equal(WalkOpening.Answers[i], Tui.WalkRender.Answer(choice, WalkOpening.Keys[i]));

            screen = flow.Answer(WalkOpening.Answers[i]!);
        }
    }

    //a keys-only screen draws no numbered rows yet still answers a digit, so this test pins that
    [Fact]
    public void A_KEYS_ONLY_SCREEN_STILL_ANSWERS_A_DIGIT()
    {
        var flow = new SetupFlow(new WizardProbes());
        var welcome = Assert.IsType<WizardScreen.Choice>(flow.Start());

        Assert.Equal(SetupFlow.WelcomeGo, Tui.WalkRender.Answer(welcome, WizardRig.Digit('1')));
        Assert.Equal(SetupFlow.WelcomeGo, Tui.WalkRender.Answer(welcome, WizardRig.Enter));

        //pressing Esc answers differently here, so the digit check above is not something any key would pass
        Assert.Equal(SetupFlow.WelcomeNot, Tui.WalkRender.Answer(welcome, ConsoleKey.Escape));
    }

    //the two entry points must end on the same screen, or the table's columns have drifted apart
    [Fact]
    public void THE_ANSWER_WALK_AND_THE_KEYSTROKE_WALK_LAND_ON_THE_SAME_SCREEN()
    {
        var keyed = new SetupFlow(new WizardProbes());
        var face = new WizardRig(100).Face(WalkOpening.Keys);
        var screen = keyed.Start();
        foreach (var _ in WalkOpening.Keys)
            screen = keyed.Answer(face.Choose((WizardScreen.Choice)screen)!);

        var answered = new SetupFlow(new WizardProbes()).StartPastOpening();

        Assert.Equal(ScreenKey.Of(answered), ScreenKey.Of(screen));
    }

    //the opening must end on the found screen of the engine step, a negative check would pass for a walk that stopped short
    [Fact]
    public void THE_OPENING_ENDS_AT_THE_ENGINE_STEP()
    {
        //the probes' machine has an engine that answers, so the walk stops on the found screen
        Assert.Equal(SetupFlow.FoundKey, ScreenKey.Of(new SetupFlow(new WizardProbes()).StartPastOpening()));
    }

    //the refusal must name both the screen it wanted and the one it found
    [Fact]
    public void A_WALK_THAT_IS_NOT_AT_ITS_OPENING_IS_REFUSED_BY_NAME()
    {
        var flow = new SetupFlow(new WizardProbes());
        var elsewhere = flow.StartAtModelSegment();

        var ex = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => flow.PastOpeningFrom(elsewhere));

        Assert.Contains(SetupFlow.WelcomeKey, ex.Message, StringComparison.Ordinal);
        Assert.Contains(ScreenKey.Of(elsewhere), ex.Message, StringComparison.Ordinal);
    }
}
