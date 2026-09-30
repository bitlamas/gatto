using Gatto.Cli.Setup;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup.Tui;

//the welcome through the real face, diffed against a byte-pinned golden. a hand-written expectation would only repeat the model that wrote the code
public class WelcomeTests
{
    //the machine the generator priced its hardware line against, read from it rather than chosen
    private static SetupFlow Flow(string? llama = null) => new(new WizardProbes
    {
        //a fresh machine by default, the welcome golden is the first-run screen and the map row reads the engine as something to fetch
        Llama = llama,
        //a 96 GiB graphics share out of 128 GB, so the OS sees 32 GiB. derived from the classifier's constants, so a change to its reserve fails here loudly
        Snapshot = new Gatto.Core.Hardware.HardwareSnapshot(
            InstalledBytes: 128UL * 1024 * 1024 * 1024,
            OsVisibleBytes: 32UL * 1024 * 1024 * 1024,
            GraphicsKind: Gatto.Core.Hardware.GpuKind.Integrated,
            GraphicsMemoryBytes: 96UL * 1024 * 1024 * 1024),
    });

    //esc returns the second option's key, because the footer is composed from the options. one press only, pressing twice to decline would be the wizard arguing
    [Fact]
    public void ESC_ON_THE_WELCOME_IS_THE_SAME_ANSWER_AS_NOT_NOW()
    {
        var welcome = Assert.IsType<WizardScreen.Choice>(Flow().Start());

        Assert.Equal(SetupFlow.WelcomeNot, WalkRender.Answer(welcome, ConsoleKey.Escape));
        Assert.Equal(SetupFlow.WelcomeGo, WalkRender.Answer(welcome, ConsoleKey.Enter));

        //the footer says what those two keys do, because it is built from them
        var footer = WalkRender.Choice(welcome, 100).Rows[^1];
        Assert.Equal("  Enter get started   Esc not now", footer);
    }

    [Fact]
    public void THE_WELCOME_SCREEN_MATCHES_S2S_GOLDEN_AT_100()
    {
        var welcome = Assert.IsType<WizardScreen.Choice>(Flow().Start());
        Golden.AssertEquals("s2", "welcome-100", 100, WalkRender.Choice(welcome, 100).Rows);
    }

    [Fact]
    public void AND_AT_80()
    {
        var welcome = Assert.IsType<WizardScreen.Choice>(Flow().Start());
        Golden.AssertEquals("s2", "welcome-80", 80, WalkRender.Choice(welcome, 80).Rows);
    }
    //the second sentence of the welcome folds to one clause, the first one is left alone
    [Fact]
    public void THE_WELCOME_SENTENCE_IS_ONE_CLAUSE()
    {
        var welcome = Assert.IsType<WizardScreen.Choice>(Flow().Start());
        var body = string.Join(" ", welcome.BodyRows!.Select(r => r.Text));

        Assert.Contains(
            "Describe a job in plain words and it does the work itself: reading, writing and "
            + "running things on your computer, showing you every step.",
            body, StringComparison.Ordinal);
        Assert.DoesNotContain("fix this error", body, StringComparison.Ordinal);
    }

}
