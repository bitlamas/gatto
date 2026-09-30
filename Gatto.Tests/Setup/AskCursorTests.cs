using Gatto.Cli.Setup;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup;

//enter must answer the screen and the back row must render last. the caller places the input, so the row 0 cursor default is right for both shapes.
public class AskCursorTests
{
    private static WizardScreen.Ask Screen(string? placeholder = "type the path below", bool back = true) =>
        new WizardScreen.Ask("k", "Where is it?", _ => null, Placeholder: placeholder) { AllowBack = back };

    [Fact]
    public void TYPING_ON_ARRIVAL_ANSWERS_THE_SCREEN()
    {
        //the widget takes characters only when the cursor is on the input row, so a cursor left on the back row swallows the typing.
        var rig = new WizardRig();
        var face = rig.Face(WizardRig.Ch('h'), WizardRig.Ch('i'), WizardRig.Ch('!'), WizardRig.Enter);

        var answer = face.Ask(Screen());

        Assert.Equal("hi!", answer);
    }

    [Fact]
    public void WITH_AN_OFFER_PRESENT_ENTER_ON_ARRIVAL_ACCEPTS_IT()
    {
        //row 0 is the offer when there is one and the input when there is none, so the cursor default needs no override.
        var rig = new WizardRig();
        var face = rig.Face(WizardRig.Enter);

        var answer = face.Ask(new WizardScreen.Ask("k", "How much context?", _ => null,
            Placeholder: "Or type a number", Offer: new AskOffer("Use 32768", "32768")));

        Assert.Equal("32768", answer);
    }

    [Fact]
    public void WITH_AN_OFFER_PRESENT_THE_INPUT_IS_STILL_ONE_ARROW_AWAY()
    {
        //one down press from the offer must reach the input under it, and the input must still take text.
        var rig = new WizardRig();
        var face = rig.Face(WizardRig.Down, WizardRig.Ch('8'), WizardRig.Ch('k'), WizardRig.Enter);

        var answer = face.Ask(new WizardScreen.Ask("k", "How much context?", _ => null,
            Placeholder: "Or type a number", Offer: new AskOffer("Use 32768", "32768")));

        Assert.Equal("8k", answer);
    }

    [Fact]
    public void BACK_IS_THE_LAST_ROW_ON_AN_ASK_SCREEN()
    {
        //assert the back row's position on the rendered screen, a presence check passes for a back row above the input.
        var rig = new WizardRig();
        var face = rig.Face(WizardRig.Ch('x'), WizardRig.Enter);

        face.Ask(Screen());

        //measure the first frame of the emitted stream, the completion echo writes the back label after it erases the block.
        var screen = rig.Painted;
        var input = screen.IndexOf("type the path below", StringComparison.Ordinal);
        var back = screen.IndexOf(SetupFace.BackLabelOf(Gatto.Terminal.GlyphSet.Unicode), StringComparison.Ordinal);

        Assert.True(input >= 0, "the placeholder never rendered, this test has lost its subject");
        Assert.True(back >= 0, "the back row never rendered, this test has lost its subject");
        Assert.True(back > input,
            $"back rendered ABOVE the input (back at {back}, input at {input}). The rule is that "
            + "the free-text row stops being terminal, so the affordance lands last on an Ask exactly "
            + "as it does on a Choice.");
    }

    [Fact]
    public void BACK_IS_THE_LAST_ROW_ON_A_CHOICE_SCREEN_TOO()
    {
        //pin both screen kinds, a rule proved on one kind says nothing about the other.
        var rig = new WizardRig();
        var face = rig.Face(WizardRig.Enter);

        face.Choose(new WizardScreen.Choice("k", "Which?",
            [new ChoiceOption("a", "The first one"), new ChoiceOption("b", "The second one")])
            { AllowBack = true });

        //measure the first frame, the echo writes the back label after the erase.
        var screen = rig.Painted;
        Assert.True(screen.IndexOf(SetupFace.BackLabelOf(Gatto.Terminal.GlyphSet.Unicode), StringComparison.Ordinal)
                  > screen.IndexOf("The second one", StringComparison.Ordinal),
            "back must render after the last real option on a Choice screen");
    }

    [Fact]
    public void A_PICKER_KEEPS_ITS_CURSOR_ON_ITS_FIRST_OPTION()
    {
        //a picker opens on option one, and this test fails first if a picker grows a free-text row.
        var rig = new WizardRig();
        var face = rig.Face(WizardRig.Enter);

        var picked = face.Choose(new WizardScreen.Choice("k", "Which model?",
            [new ChoiceOption("first", "Found one on this machine"), new ChoiceOption("other", "Somewhere else")])
            { AllowBack = true });

        Assert.Equal("first", picked);
    }

    [Theory]
    [InlineData(40)]
    [InlineData(60)]
    [InlineData(80)]
    [InlineData(100)]
    [InlineData(120)]
    public void THE_ORDER_HOLDS_AT_EVERY_WIDTH(int width)
    {
        //test every width, a wrapped label can push the placeholder below the back row at one width and not another.
        var rig = new WizardRig(width);
        var face = rig.Face(WizardRig.Ch('x'), WizardRig.Enter);

        face.Ask(Screen());

        var screen = rig.Painted;
        Assert.True(screen.IndexOf(SetupFace.BackLabelOf(Gatto.Terminal.GlyphSet.Unicode), StringComparison.Ordinal)
                  > screen.IndexOf("type the path below", StringComparison.Ordinal),
            $"at width {width}, back did not render after the input row");
    }

}
