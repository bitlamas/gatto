using System.Collections.Concurrent;
using Gatto.Cli.Setup;
using Gatto.Repl;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup;

//drive the real RichPrompter here, a fake that returns null for a cancel hides the throw that ends the session
public class WizardCancelTests
{
    private sealed class ScriptedKeys(params ConsoleKeyInfo[] keys) : IKeySource
    {
        private readonly Queue<ConsoleKeyInfo> _q = new(keys);
        public bool KeyAvailable => _q.Count > 0;
        public ConsoleKeyInfo ReadKey() =>
            _q.Count > 0 ? _q.Dequeue() : throw new InvalidOperationException("test scripted too few keys");
    }

    private static readonly Theme T = new(new TermCaps(true, true));
    private static ConsoleKeyInfo Special(ConsoleKey k) => new('\0', k, false, false, false);
    private static ConsoleKeyInfo Esc() => Special(ConsoleKey.Escape);
    private static ConsoleKeyInfo Enter() => Special(ConsoleKey.Enter);
    private static ConsoleKeyInfo Down() => Special(ConsoleKey.DownArrow);

    private static WizardAsk Ask() => new(
        "how do you want to run the server?",
        [new SelectOption("gatto sets it up and runs it for me (llama.cpp)"),
         new SelectOption("I already have a server running")]);

    [Fact]
    public void LEAVING_A_WIZARD_QUESTION_RETURNS_NULL_INSTEAD_OF_THROWING_THROUGH_THE_SESSION()
    {
        //pressing Esc at a wizard screen must return null, no loop above /setup converts an exception here
        var p = new RichPrompter(new RecordingSurface { Width = 80 }, T, new ScriptedKeys(Esc()));

        Assert.Null(p.AskOne(Ask()));
    }

    [Fact]
    public void LEAVING_DOES_NOT_REQUEST_A_TURN_ABORT_because_no_turn_is_running()
    {
        //no abort may fire here, since /setup runs between turns and the cancel would hit the next turn
        var cts = new CancellationTokenSource();
        var abort = new TurnAbortHandle { Current = () => cts };
        var p = new RichPrompter(new RecordingSurface { Width = 80 }, T, new ScriptedKeys(Esc()),
            pump: null, chrome: null, abort: abort);

        Assert.Null(p.AskOne(Ask()));
        Assert.False(cts.IsCancellationRequested);
    }

    [Fact]
    public void ASK_USERS_WIZARD_CHROME_IS_ABSENT_from_a_setup_screen()
    {
        //a closed setup choice must render no free-text row and no submit or review step
        var surface = new RecordingSurface { Width = 80 };
        var p = new RichPrompter(surface, T, new ScriptedKeys(Enter()));

        Assert.Equal("gatto sets it up and runs it for me (llama.cpp)", p.AskOne(Ask())?.Label);
        Assert.DoesNotContain("Type my own answer", surface.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Ready to submit", surface.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Review your answers", surface.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_CHOSEN_ANSWER_comes_back_as_the_LABEL_the_flow_can_map()
    {
        var p = new RichPrompter(new RecordingSurface { Width = 80 }, T, new ScriptedKeys(Down(), Enter()));
        Assert.Equal("I already have a server running", p.AskOne(Ask())?.Label);
    }

    //answers from a script, null means the user left
    private sealed class FakeWizardPrompter(params string?[] answers) : IWizardPrompter
    {
        private int _i;
        public List<WizardAsk> Asked { get; } = [];

        public WizardAnswer? AskOne(WizardAsk ask)
        {
            Asked.Add(ask);
            if (_i >= answers.Length) return null;
            return answers[_i++] is { } a ? new WizardAnswer(a) : null;
        }
    }

    [Fact]
    public void THE_SETUP_BINDING_TREATS_LEAVING_AS_AN_ANSWERLESS_RETURN_not_an_error()
    {
        var surface = new PrompterWizardSurface(new FakeWizardPrompter((string?)null), new StringWriter());

        Assert.Null(surface.Choose(new WizardScreen.Choice(
            "k", "pick one", [new ChoiceOption("alpha", "The Alpha")])));
    }

    [Fact]
    public void THE_SETUP_BINDING_NEVER_OFFERS_A_FREE_TEXT_ROW_ON_A_CLOSED_CHOICE()
    {
        //the binding asks for no free-text row, so a typed answer matching no label cannot arise
        var prompter = new FakeWizardPrompter("The Alpha");
        new PrompterWizardSurface(prompter, new StringWriter()).Choose(new WizardScreen.Choice(
            "k", "pick one", [new ChoiceOption("alpha", "The Alpha")]));

        Assert.Null(prompter.Asked[0].FreeTextLabel);
        Assert.Equal(PrompterWizardSurface.LeaveHint, prompter.Asked[0].FooterHint);
    }

    [Fact]
    public void AN_ASK_SCREEN_IS_THE_MIRROR_IMAGE_free_text_and_no_options()
    {
        var prompter = new FakeWizardPrompter("/models/x.gguf");
        new PrompterWizardSurface(prompter, new StringWriter())
            .Ask(new WizardScreen.Ask("k", "where is it?", Validate: _ => null));

        Assert.Empty(prompter.Asked[0].Options);
        Assert.NotNull(prompter.Asked[0].FreeTextLabel);
    }

    [Fact]
    public void BOTH_PROMPTERS_IMPLEMENT_THE_WIZARD_SEAM_which_is_what_keeps_setup_available()
    {
        //a prompter that drops IWizardPrompter still compiles, /setup just answers that it is unavailable
        Assert.True(typeof(IWizardPrompter).IsAssignableFrom(typeof(RichPrompter)));
        Assert.True(typeof(IWizardPrompter).IsAssignableFrom(typeof(ConsolePrompter)));
    }
}

//the inline path erases the option block and leaves one line behind
public class InlineEchoTests
{
    private sealed class ScriptedKeys(params ConsoleKeyInfo[] keys) : IKeySource
    {
        private readonly Queue<ConsoleKeyInfo> _q = new(keys);
        public bool KeyAvailable => _q.Count > 0;
        public ConsoleKeyInfo ReadKey() =>
            _q.Count > 0 ? _q.Dequeue() : throw new InvalidOperationException("test scripted too few keys");
    }

    private static readonly Theme T = new(new TermCaps(false, false));   //no paint, so the assertions below read plain text
    private static ConsoleKeyInfo Digit(char d) => new(d, (ConsoleKey)((int)ConsoleKey.D0 + (d - '0')), false, false, false);
    private static ConsoleKeyInfo Esc() => new('\0', ConsoleKey.Escape, false, false, false);

    private static SelectSpec Spec(bool echo) => new(
        TitleRows: [],
        Question: new PromptQuestion("how do you want to run the server?"),
        Options: [new SelectOption("gatto runs it"), new SelectOption("I already have a server running")],
        FooterHint: "Esc to leave",
        EchoOnCompletion: echo);

    [Fact]
    public void THE_ANSWERED_BLOCK_KEEPS_ITS_BODY_AND_DROPS_ONLY_THE_CHROME()
    {
        //the options, cursor and footer go once the choice is made, the body above them stays
        var spec = new SelectSpec(
            TitleRows: [],
            Question: new PromptQuestion("carry on?"),
            Options: [new SelectOption("carry on")],
            FooterHint: "Esc to leave",
            BodyRows:
            [
                new BodyRow("the server for gemma stopped while the check was running"),
                new BodyRow("`gatto doctor` checks the whole setup and says what to fix."),
            ],
            EchoOnCompletion: true);

        var screen = Run(spec, Digit('1'));

        Assert.Contains("stopped while the check was running", screen, StringComparison.Ordinal);
        Assert.Contains("gatto doctor", screen, StringComparison.Ordinal);
        Assert.DoesNotContain("1. carry on", screen, StringComparison.Ordinal);
        Assert.DoesNotContain("Esc to leave", screen, StringComparison.Ordinal);
    }

    private static string Run(SelectSpec spec, params ConsoleKeyInfo[] keys)
    {
        var surface = new RecordingSurface { Width = 80 };
        new SelectPrompt(surface, T, new ScriptedKeys(keys)).Show(spec);
        return Screen(surface.Text);
    }

    //the emulator lives in TerminalReplay, so both callers read the same escapes
    private static string Screen(string written) => Gatto.Tests.Fakes.TerminalReplay.Screen(written);

    [Fact]
    public void THE_LAST_FRAME_AGREES_WITH_WHAT_WAS_PICKED_even_when_a_digit_confirmed_it()
    {
        //a digit confirm must repaint, the last frame has to agree with the pick
        var screen = Run(Spec(echo: false), Digit('2'));

        Assert.Contains("❯ 2. I already have a server running", screen, StringComparison.Ordinal);
        Assert.DoesNotContain("❯ 1.", screen, StringComparison.Ordinal);
    }

    [Fact]
    public void ON_COMPLETION_THE_OPTION_BLOCK_IS_ERASED_and_one_compact_line_replaces_it()
    {
        var text = Run(Spec(echo: true), Digit('2'));

        Assert.DoesNotContain("❯", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Esc to leave", text, StringComparison.Ordinal);
        Assert.DoesNotContain("gatto runs it", text, StringComparison.Ordinal);
        Assert.Contains("how do you want to run the server? I already have a server running",
            text, StringComparison.Ordinal);
    }

    [Fact]
    public void LEAVING_ERASES_THE_BLOCK_AND_ECHOES_NOTHING()
    {
        //leaving echoes nothing, inventing a cancelled line would name a choice the user never made
        var text = Run(Spec(echo: true), Esc());

        Assert.DoesNotContain("❯", text, StringComparison.Ordinal);
        Assert.DoesNotContain("I already have a server running", text, StringComparison.Ordinal);
    }

    [Fact]
    public void WITHOUT_THE_FLAG_NOTHING_CHANGES_for_every_caller_that_predates_it()
    {
        //the echo flag stays opt-in, the permission prompt, the pickers and ask_user keep their own rendering
        var text = Run(Spec(echo: false), Digit('2'));

        Assert.Contains("Esc to leave", text, StringComparison.Ordinal);
        Assert.Contains("gatto runs it", text, StringComparison.Ordinal);
    }
}
