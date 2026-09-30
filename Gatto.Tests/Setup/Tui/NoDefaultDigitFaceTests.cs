using Gatto.Cli.Setup;
using Gatto.Repl;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup.Tui;

//the digit rule reaches users through four faces, so each test names the site it drives, since a green here proves only that face
public class NoDefaultDigitFaceTests
{
    private sealed class ScriptedKeys(IEnumerable<ConsoleKeyInfo> keys) : IKeySource
    {
        private readonly Queue<ConsoleKeyInfo> _q = new(keys);
        public bool KeyAvailable => false;
        public ConsoleKeyInfo ReadKey() => _q.Count > 0
            ? _q.Dequeue()
            : throw new InvalidOperationException("test scripted too few keys");
    }

    private static ConsoleKeyInfo Digit(char d) =>
        new(d, (ConsoleKey)((int)ConsoleKey.D0 + (d - '0')), false, false, false);

    private static WizardScreen.Choice NoDefaultChoice() => new(
        "update", "Replace the installed binary?",
        [new ChoiceOption("yes", "Yes, replace it"), new ChoiceOption("no", "Not now")],
        NoDefault: true);

    //site 1 of 4: SetupFace, the plain wizard face that gatto setup runs
    [Fact]
    public void SetupFace_a_digit_then_the_same_digit_answers()
    {
        var face = new SetupFace(new RecordingSurface(), new Theme(new TermCaps(true, true)),
            new ScriptedKeys([Digit('1'), Digit('1')]), TextWriter.Null);

        //the script holds two keys, so a third read throws and a face that swallowed the digit fails loudly
        Assert.Equal("yes", face.Choose(NoDefaultChoice()));
    }

    //a replacement question must not be answerable by one stray keypress, the other half of the digit rule at this site
    [Fact]
    public void SetupFace_ONE_digit_does_not_answer()
    {
        //use the Esc key to terminate, since this face swallows the throw from ReadKey, so the oracle is the outcome
        var face = new SetupFace(new RecordingSurface(), new Theme(new TermCaps(true, true)),
            new ScriptedKeys([Digit('1'), new ConsoleKeyInfo('\0', ConsoleKey.Escape, false, false, false)]),
            TextWriter.Null);

        Assert.Null(face.Choose(NoDefaultChoice()));
    }

    //site 2 of 4: PrompterWizardSurface, the in-session face, driven here only for the wiring that tells the widget there is no default
    [Fact]
    public void PrompterWizardSurface_carries_NoDefault_to_the_ask()
    {
        WizardAsk? seen = null;
        //the fake must return a label, since the surface maps that label back to a key (a returned key makes it re-ask forever)
        var surface = new PrompterWizardSurface(
            new RecordingPrompter(a => { seen = a; return "Yes, replace it"; }), TextWriter.Null);

        Assert.Equal("yes", surface.Choose(NoDefaultChoice()));
        Assert.NotNull(seen);
        Assert.True(seen!.NoInitialCursor,
            "the in-session face dropped NoDefault, so the widget would paint a default to nudge with");
    }

    private sealed class RecordingPrompter(Func<WizardAsk, string?> answer) : IWizardPrompter
    {
        public WizardAnswer? AskOne(WizardAsk ask) =>
            answer(ask) is { } label ? new WizardAnswer(label) : null;
    }
}
