using Gatto.Cli.Setup;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup;

//the wizard reads the mouse setting itself, and the console it borrowed is the shell's again on every exit
public class WizardInputTests
{
    private const uint PI = ConsoleInputMode.EnableProcessedInput;

    private sealed class OneKeySource(char c) : IKeySource
    {
        private bool _read;
        public bool KeyAvailable => !_read;
        public ConsoleKeyInfo ReadKey() { _read = true; return new(c, ConsoleKey.A, false, false, false); }
    }

    //take and giveBack model TreatControlCAsInput: they read the bit at take and write that reading back
    private static (Func<bool?> take, Action<bool?> give) ControlC(FakeConsoleModeControl ctl) =>
        (() => { var was = (ctl.Get() & PI) == 0; ctl.Set(ctl.Get() & ~PI); return was; },
         was => ctl.Set(was is true ? ctl.Get() & ~PI : ctl.Get() | PI));

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public void THE_SHELLS_MODE_COMES_BACK_ON_EVERY_EXIT(bool throws, bool mouse)
    {
        var ctl = new FakeConsoleModeControl(initial: 0x01F7);
        var (take, give) = ControlC(ctl);
        Action run = () => WizardSession.Run(new AltScreen(new RecordingSurface()),
            () => throws ? throw new InvalidOperationException("walk") : 0,
            () => [], TextWriter.Null, registerHooks: false, take: take, giveBack: give,
            inputMode: mouse ? new ConsoleInputMode(ctl) : null);
        if (throws) Assert.Throws<InvalidOperationException>(run); else run();
        Assert.Equal(0x01F7u, ctl.Get());
        Assert.Equal(0x01F7u, ctl.Sets[^1]);   //the restore writes last
    }

    [Theory]
    [InlineData("{ not json", true)]
    [InlineData("""{ "mouse": false }""", false)]
    [InlineData("""{ "mouse": true }""", true)]
    [InlineData("""{ "mouse": "yes" }""", true)]
    [InlineData("""[ 1, 2 ]""", true)]
    [InlineData(null, true)]
    public void THE_MOUSE_SETTING_IS_READ_WITHOUT_THE_CONFIG_LOADER(string? json, bool expected)
    {
        using var home = json is null ? TempHome.Empty() : TempHome.With("gatto.json", json);
        Assert.Equal(expected, WizardInput.MouseOf(home.Path));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void THE_MOUSE_SETTING_CHOOSES_SOURCE_MODE_AND_WHEEL(bool mouse)
    {
        var w = WizardInput.For(mouse, () => new ScriptedInputSource(), () => new KeyInputSource(new OneKeySource('x')),
            () => new ConsoleInputMode(new FakeConsoleModeControl(0)), () => 500);
        Assert.Equal(mouse, w.Mode is not null);
        Assert.Equal(!mouse, w.QuietWheel);
        Assert.Equal(mouse, w.Source is ScriptedInputSource);
        Assert.Equal(500, w.DoubleClickMs);
    }
}
