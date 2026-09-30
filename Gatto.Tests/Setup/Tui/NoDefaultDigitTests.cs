using Gatto.Repl;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup.Tui;

//a no-default prompt lights a digit's row and submits on a second key, and the render is the oracle rather than the outcome
public class NoDefaultDigitTests
{
    private sealed class ScriptedKeys(IEnumerable<ConsoleKeyInfo> keys) : IKeySource
    {
        private readonly Queue<ConsoleKeyInfo> _q = new(keys);
        public bool KeyAvailable => false;
        //reading past the script throws, so a prompt that swallows a key fails loudly instead of by a subtle outcome
        public ConsoleKeyInfo ReadKey() => _q.Count > 0
            ? _q.Dequeue()
            : throw new InvalidOperationException("test scripted too few keys");
    }

    private static readonly Theme T = new(new TermCaps(true, true));

    private static ConsoleKeyInfo Digit(char d) =>
        new(d, (ConsoleKey)((int)ConsoleKey.D0 + (d - '0')), false, false, false);
    private static ConsoleKeyInfo Special(ConsoleKey k) => new('\0', k, false, false, false);

    //two numbered rows, no free-text row and no initial cursor, the shape every consent screen uses and the one that collides the two sentinels
    private static SelectSpec NoDefaultSpec() =>
        new(["Update gatto"], "Replace the installed binary?",
            [new SelectOption("Yes, replace it"), new SelectOption("Not now")])
        { NoInitialCursor = true };

    private static (RecordingSurface Surface, SelectOutcome Outcome) Run(params ConsoleKeyInfo[] keys)
    {
        var surface = new RecordingSurface();
        return (surface, new SelectPrompt(surface, T, new ScriptedKeys(keys)).Show(NoDefaultSpec()));
    }

    private static int Chosen(SelectOutcome o) => Assert.IsType<SelectOutcome.Chosen>(o).Index;

    [Fact]
    public void A_digit_on_a_no_default_prompt_LIGHTS_its_row_and_submits_nothing()
    {
        //the Esc key is pressed only so the prompt can terminate, since the assertion is about what the digit painted
        var (surface, outcome) = Run(Digit('1'), Special(ConsoleKey.Escape));
        Assert.IsNotType<SelectOutcome.Chosen>(outcome);          //one keystroke never answers

        var screen = TerminalReplay.Plain(surface.Text);
        Assert.Contains("❯ 1.", screen, StringComparison.Ordinal); //the digit lights the row without answering
    }

    [Fact]
    public void The_same_digit_again_submits_its_row()
    {
        var (_, outcome) = Run(Digit('1'), Digit('1'));
        Assert.Equal(0, Chosen(outcome));
    }

    [Fact]
    public void Enter_after_a_digit_submits_the_lit_row()
    {
        var (_, outcome) = Run(Digit('1'), Special(ConsoleKey.Enter));
        Assert.Equal(0, Chosen(outcome));
    }

    [Fact]
    public void A_DIFFERENT_digit_submits_ITS_row_not_the_lit_one()
    {
        //a second digit names its own row and answers for itself, since it does not confirm the row the first digit lit
        var (_, outcome) = Run(Digit('1'), Digit('2'));
        Assert.Equal(1, Chosen(outcome));
    }

    [Fact]
    public void Esc_after_a_digit_still_leaves()
    {
        var (_, outcome) = Run(Digit('1'), Special(ConsoleKey.Escape));
        Assert.IsNotType<SelectOutcome.Chosen>(outcome);
    }

    //with no cursor lit, Enter must answer nothing, since a replacement question must not be answerable by one stray keypress
    [Fact]
    public void Enter_ALONE_answers_nothing_and_paints_no_cursor()
    {
        var (surface, outcome) = Run(Special(ConsoleKey.Enter), Special(ConsoleKey.Escape));
        Assert.IsNotType<SelectOutcome.Chosen>(outcome);
        Assert.DoesNotContain("❯ 1.", TerminalReplay.Plain(surface.Text), StringComparison.Ordinal);
    }
}
