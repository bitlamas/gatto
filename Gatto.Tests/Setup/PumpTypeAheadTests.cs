using System.Collections.Concurrent;
using Gatto.Repl;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup;

//a focus scope reads a fresh, empty channel, so a key typed before the push cannot reach it, and every wait is on a signal

//xUnit1031 stays suppressed, since blocking is the subject of these tests and an await would change what they check
#pragma warning disable xUnit1031
public class PumpTypeAheadTests
{
    //a read blocks until a key arrives, so the pump waits the way it does on a real console.
    private sealed class FedSource : IInputSource
    {
        private readonly BlockingCollection<ConsoleKeyInfo> _q = new();
        public void Press(ConsoleKeyInfo k) => _q.Add(k);
        public InputEvent Read() => new KeyEvent(_q.Take());
        public bool KeyDownAvailable => _q.Count > 0;
    }

    private static readonly Theme T = new(new TermCaps(true, true));
    private static ConsoleKeyInfo Digit(char d) => new(d, (ConsoleKey)((int)ConsoleKey.D0 + (d - '0')), false, false, false);

    //return only after the pump routes the key, so the test never depends on thread scheduling.
    private static void PressAndSettle(FedSource source, InputPump pump, ConsoleKeyInfo k)
    {
        using var routed = new ManualResetEventSlim();
        pump.SetKeySink(_ => routed.Set());
        try
        {
            source.Press(k);
            Assert.True(routed.Wait(TimeSpan.FromSeconds(30)), "the pump never routed the type-ahead key");
            //the sink fires before the pump routes the key, so wait until the key sits in the composer
            Assert.True(SpinWait.SpinUntil(() => pump.ComposerPending > 0, TimeSpan.FromSeconds(30)), "the pump never put the type-ahead key in the composer");
        }
        finally { pump.SetKeySink(null); }
    }

    [Fact]
    public void A_KEY_TYPED_BEFORE_THE_PROMPT_OPENED_IS_NOT_IN_THE_PROMPTS_CHANNEL()
    {
        //assert on the channel a prompt reads, since every other claim about the guarantee follows from it.
        var source = new FedSource();
        var pump = new InputPump(source);
        pump.Start();

        PressAndSettle(source, pump, Digit('2'));

        using var scope = pump.PushFocus();
        Assert.False(scope.Keys.KeyAvailable, "type-ahead landed in the prompt's own channel");

        //press a key afterwards to prove the channel is reachable. an unreachable scope would pass the empty check for the wrong reason.
        source.Press(Digit('1'));
        Assert.Equal(ConsoleKey.D1, scope.Keys.ReadKey().Key);
    }

    //return only after the prompt pushes its focus scope, or a press sent during startup goes to the composer
    private static Task<WizardAnswer?> OpenPrompt(InputPump pump, RichPrompter prompter)
    {
        using var pushed = new ManualResetEventSlim();
        pump.SetModalPushSink(pushed.Set);
        try
        {
            var task = Task.Run(() => prompter.AskOne(new WizardAsk(
                "no server answered, start it and try again?",
                [new SelectOption("try again"), new SelectOption("leave setup")])));
            Assert.True(pushed.Wait(TimeSpan.FromSeconds(30)), "the prompt never took modal focus");
            return task;
        }
        finally { pump.SetModalPushSink(null); }
    }

    [Fact]
    public void THE_WHOLE_SETUP_PROMPT_RESOLVES_TO_WHAT_THE_USER_PRESSED_not_to_the_stale_key()
    {
        //the outcome decides, so no timing window is needed
        var source = new FedSource();
        var pump = new InputPump(source);
        pump.Start();

        PressAndSettle(source, pump, Digit('2'));

        var answer = OpenPrompt(pump, new RichPrompter(new RecordingSurface { Width = 80 }, T, new NeverKeys(), pump));

        source.Press(Digit('1'));

        Assert.True(answer.Wait(TimeSpan.FromSeconds(30)), "the prompt never resolved");
        Assert.Equal("try again", answer.Result?.Label);
    }

    [Fact]
    public void THE_SAME_KEY_DOES_RESOLVE_IT_once_the_screen_is_actually_open()
    {
        //the control case, since without it the two tests above only prove that nothing happened
        var source = new FedSource();
        var pump = new InputPump(source);
        pump.Start();

        var answer = OpenPrompt(pump, new RichPrompter(new RecordingSurface { Width = 80 }, T, new NeverKeys(), pump));

        source.Press(Digit('2'));

        Assert.True(answer.Wait(TimeSpan.FromSeconds(30)), "the prompt never resolved");
        Assert.Equal("leave setup", answer.Result?.Label);
    }

    //reading throws, since the pump path must never touch the fallback key source. two readers on one console is the defect this makes visible.
    private sealed class NeverKeys : IKeySource
    {
        public bool KeyAvailable => false;
        public ConsoleKeyInfo ReadKey() =>
            throw new InvalidOperationException("the pump path read the FALLBACK key source, that is a second reader on stdin");
    }
}
