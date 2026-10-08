using Gatto.Cli.Setup;
using Gatto.Core.Acquire;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup.Tui;

//the typed door takes typing, so every test here presses keys and asserts the answer. a render can pass on a door that never takes a character.
public class DoorTypingTests
{
    private static readonly ConsoleKeyInfo Tab = new('\t', ConsoleKey.Tab, false, false, false);
    private static readonly ConsoleKeyInfo Back = new('\b', ConsoleKey.Backspace, false, false, false);

    private static IEnumerable<ConsoleKeyInfo> Type(string s) => s.Select(WizardRig.Ch);

    private static WizardProbes Probes(ConnectProbe? server = null) =>
        new() { Llama = @"C:\llama\llama-server.exe", Server = server };

    //the road's screen taken from the flow rather than composed, since the door is a property of the screen the flow emits
    private static WizardScreen.Choice Road(ConnectProbe? server)
    {
        var flow = new SetupFlow(Probes(server));
        flow.StartPastOpening();
        return Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.ForkConnect));
    }

    //nothing answered, so the user's server is somewhere gatto does not look, and the face must answer with what was typed
    [Fact]
    public void AN_ADDRESS_TYPED_INTO_THE_DOOR_IS_WHAT_THE_FACE_ANSWERS()
    {
        var answer = WalkRender.AnswerAfter(Road(null), 100,
            [Tab, .. Type("http://127.0.0.1:9999"), WizardRig.Enter]);

        Assert.Equal(ShelfControls.TypedAnswer("http://127.0.0.1:9999"), answer);
    }

    //the found screen's door is a second site, under an option list rather than beside one option, so it needs its own test
    [Fact]
    public void THE_FOUND_SCREENS_DOOR_TAKES_ANOTHER_ADDRESS()
    {
        var found = Road(new ConnectProbe("http://127.0.0.1:8080", ["a-model"], 8192));

        var answer = WalkRender.AnswerAfter(found, 100,
            [Tab, .. Type("http://elsewhere:1234"), WizardRig.Enter]);

        Assert.Equal(ShelfControls.TypedAnswer("http://elsewhere:1234"), answer);
    }

    //backspace edits the draft, since a user who mistypes one character would otherwise have to leave the screen
    [Fact]
    public void BACKSPACE_EDITS_THE_DRAFT()
    {
        var answer = WalkRender.AnswerAfter(Road(null), 100,
            [Tab, .. Type("http://x:99XY"), Back, Back, WizardRig.Enter]);

        Assert.Equal(ShelfControls.TypedAnswer("http://x:99"), answer);
    }

    //the Esc key keeps the text and returns the keys to the list. assert it through what follows, since Enter then answers the first option.
    [Fact]
    public void ESC_FROM_THE_DOOR_RETURNS_THE_KEYS_TO_THE_LIST()
    {
        var answer = WalkRender.AnswerAfter(Road(null), 100,
            [Tab, .. Type("half-typed"), WizardRig.Esc, WizardRig.Enter]);

        Assert.Equal(SetupFlow.Retry, answer);
    }

    //enter on an empty door must not answer the empty string, which downstream would read as an address. the keys return to the list.
    [Fact]
    public void ENTER_ON_AN_EMPTY_DOOR_WITH_NO_OFFER_ANSWERS_NOTHING()
    {
        var answer = WalkRender.AnswerAfter(Road(null), 100,
            [Tab, WizardRig.Enter, WizardRig.Enter]);

        Assert.Equal(SetupFlow.Retry, answer);
    }

    //the census: every door-bearing screen the flow can emit takes text

    //every screen the flow emits with a door is answerable by typing, driven with real keys. the shelf's search door is excluded, since the flow composes its draft.
    [Theory]
    [InlineData("nothing answered", null)]
    [InlineData("a server found", "http://127.0.0.1:8080")]
    public void EVERY_DOOR_BEARING_SCREEN_IS_ANSWERABLE_BY_TYPING(string _, string? url)
    {
        var screen = Road(url is null ? null : new ConnectProbe(url, ["a-model"], 8192));
        Assert.NotNull(screen.Door);                    //the fixture really is a door screen

        var answer = WalkRender.AnswerAfter(screen, 100,
            [Tab, .. Type("typed-into-the-door"), WizardRig.Enter]);

        Assert.Equal(ShelfControls.TypedAnswer("typed-into-the-door"), answer);
    }

    //a door screen built for the purpose, since two fixtures alone would not show the rule fires. a fixture that lost its door could not quietly pass.
    [Fact]
    public void THE_CENSUS_SEES_A_PLANTED_DOOR_SCREEN()
    {
        var planted = new WizardScreen.Choice("planted", "Type something?",
            [new ChoiceOption("only", "The one option")],
            Door: "type here…");

        Assert.Equal(ShelfControls.TypedAnswer("abc"),
            WalkRender.AnswerAfter(planted, 100,
                [Tab, .. Type("abc"), WizardRig.Enter]));
    }

    //a screen with no door answers no text, its characters are commands or nothing, which is what makes the rule above about doors
    [Fact]
    public void A_SCREEN_WITH_NO_DOOR_DOES_NOT_ANSWER_WITH_TEXT()
    {
        var plain = new WizardScreen.Choice("plain", "Pick one?",
            [new ChoiceOption("first", "The first"), new ChoiceOption("second", "The second")]);

        Assert.Equal("first", WalkRender.AnswerAfter(plain, 100,
            [.. Type("abc"), WizardRig.Enter]));
    }

    //the shelf's door is excluded, since / and m are its own keys and its draft comes from the flow. a shelf view is the right fixture here.
    [Fact]
    public void THE_SHELVES_SEARCH_DOOR_IS_NOT_THIS_RULES_SUBJECT()
    {
        var shelf = new WizardScreen.Choice("planted.shelf", "Which model?",
            [new ChoiceOption("only", "The one option")],
            Shelf: new ShelfView([], Gatto.Core.Hardware.MachineShape.Discrete),
            Door: "search models…");

        //m is the shelf's own key, so it must not end up in a draft and be answered as text
        var answer = WalkRender.AnswerAfter(shelf, 100, [.. Type("m"), WizardRig.Enter]);

        Assert.NotEqual("m", answer);
    }

    //the digit promise's two missing halves

    //a digit picks nothing where no digit is drawn, since a key that acts must have been drawn. assert the answer, since the rows look the same either way.
    [Fact]
    public void A_DIGIT_PICKS_NOTHING_ON_AN_UNNUMBERED_SCREEN()
    {
        var bare = new WizardScreen.Choice("planted.bare", "Which model?",
            [new ChoiceOption("first", "the first"), new ChoiceOption("second", "the second")],
            Unnumbered: true);

        //2 must do nothing at all, so Enter answers the row the cursor was on all along
        Assert.Equal("first", WalkRender.AnswerAfter(bare, 100,
            [WizardRig.Digit('2'), WizardRig.Enter]));
    }

    //the same screen numbered, so the pair cannot pass against a face that had stopped reading digits
    [Fact]
    public void A_DIGIT_STILL_PICKS_ON_A_NUMBERED_SCREEN()
    {
        var numbered = new WizardScreen.Choice("planted.numbered", "Which model?",
            [new ChoiceOption("first", "the first"), new ChoiceOption("second", "the second")]);

        Assert.Equal("second", WalkRender.AnswerAfter(numbered, 100, [WizardRig.Digit('2')]));
    }

    //a digit typed into a focused door is text, so a port number is not eaten by a pick. the rule keys on the region the keys are in.
    [Fact]
    public void A_DIGIT_IN_A_FOCUSED_DOOR_IS_TEXT()
    {
        var answer = WalkRender.AnswerAfter(Road(null), 100,
            [Tab, .. Type("http://127.0.0.1:"), WizardRig.Digit('8'), WizardRig.Digit('0'),
             WizardRig.Digit('8'), WizardRig.Digit('0'), WizardRig.Enter]);

        Assert.Equal(ShelfControls.TypedAnswer("http://127.0.0.1:8080"), answer);
    }

    //every unnumbered screen the flow emits is digit-inert, and it drives a real screen rather than a fixture. the count is asserted so the sweep cannot go empty.
    [Fact]
    public void EVERY_UNNUMBERED_SCREEN_THE_FLOW_EMITS_IS_DIGIT_INERT()
    {
        var flow = new SetupFlow(new WizardProbes
        {
            Llama = @"C:\llama\llama-server.exe",
            Server = new ConnectProbe("http://127.0.0.1:1234",
                ["first-model", "second-model", "third-model"], 8192),
        });
        flow.StartPastOpening();
        flow.Answer(SetupFlow.ForkConnect);
        flow.Answer(SetupFlow.Yes);

        var bare = flow.Emitted.OfType<WizardScreen.Choice>().Where(s => s.Unnumbered).ToList();
        Assert.True(bare.Count > 0, "the walk emitted no unnumbered screen - this census swept nothing");

        foreach (var screen in bare)
        {
            //a digit must not move the answer off the row the cursor already holds
            var withDigit = WalkRender.AnswerAfter(screen, 100, [WizardRig.Digit('3'), WizardRig.Enter]);
            var without = WalkRender.AnswerAfter(screen, 100, [WizardRig.Enter]);

            Assert.Equal(without, withDigit);
        }
    }
}
