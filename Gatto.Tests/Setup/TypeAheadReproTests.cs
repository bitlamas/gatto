using System.Text.RegularExpressions;
using Gatto.Core.Hardware;
using Gatto.Repl;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup;

//the probe blocks for up to five seconds, so keys typed during it are still buffered when a screen opens. they answer a question the user never saw
public class TypeAheadTests
{
    //stale keys are already buffered, so KeyAvailable is true for them, while a live key arrives after the drain and still answers
    private sealed class TypeAhead(ConsoleKeyInfo[] stale, ConsoleKeyInfo[] live) : IKeySource
    {
        private readonly Queue<ConsoleKeyInfo> _stale = new(stale);
        private readonly Queue<ConsoleKeyInfo> _live = new(live);
        public int Drained { get; private set; }

        public bool KeyAvailable => _stale.Count > 0;

        public ConsoleKeyInfo ReadKey()
        {
            if (_stale.Count > 0) { Drained++; return _stale.Dequeue(); }
            return _live.Count > 0 ? _live.Dequeue() : throw new InvalidOperationException("scripted too few keys");
        }
    }

    private static readonly Theme T = new(new TermCaps(true, true));
    private static ConsoleKeyInfo Digit(char d) => new(d, (ConsoleKey)((int)ConsoleKey.D0 + (d - '0')), false, false, false);

    private static SelectSpec Retry(bool drain) => new(
        TitleRows: [],
        Question: new PromptQuestion("no server answered, start it and try again?"),
        Options: [new SelectOption("try again"), new SelectOption("leave setup")],
        FooterHint: "Esc to leave",
        EchoOnCompletion: true,
        DrainTypeAhead: drain);

    [Fact]
    public void A_KEY_PRESSED_WHILE_THE_PROBE_RAN_DOES_NOT_ANSWER_THE_SCREEN_IT_NEVER_SAW()
    {
        //stale 2 would leave setup, the live 1 is the try again the user meant
        var keys = new TypeAhead(stale: [Digit('2')], live: [Digit('1')]);

        var outcome = new SelectPrompt(new RecordingSurface { Width = 80 }, T, keys).Show(Retry(drain: true));

        Assert.Equal(0, Assert.IsType<SelectOutcome.Chosen>(outcome).Index);
        Assert.Equal(1, keys.Drained);   //the drain ate the one stale key, so the screen never saw it
    }

    [Fact]
    public void WITHOUT_THE_DRAIN_the_stale_key_still_answers_which_is_the_defect_itself()
    {
        //the flag off makes the stale key win, which is the defect itself, so dropping the drain cannot pass unnoticed
        var keys = new TypeAhead(stale: [Digit('2')], live: [Digit('1')]);

        var outcome = new SelectPrompt(new RecordingSurface { Width = 80 }, T, keys).Show(Retry(drain: false));

        Assert.Equal(1, Assert.IsType<SelectOutcome.Chosen>(outcome).Index);   //the stale key picked leave setup, so the run ends before the user reads anything
    }
}
