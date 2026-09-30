using Gatto.Cli;
using Gatto.Cli.Setup;
using Gatto.Roles.Audition;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup.Tui;

//the install question is the done step's first question, so every test names the state and the emission site it drives
public class InstallOnDoneTests
{
    //a machine with an engine and a passing check, so a difference between two walks can only come from the install state
    private static WizardProbes Machine(
        InstallState install, string? installed = null, string? held = null,
        string? running = null, bool legacy = false) => new()
    {
        //legacy is a parameter like the install state, since the aside is about the window and needs a frame of its own
        LegacyConsole = legacy,
        Running = running ?? Gatto.Core.GattoVersion.String,
        Llama = @"C:\llama\llama-server.exe",
        ActiveFile = "gemma-4-26B-A4B-it-UD-Q4_K_M.gguf",
        Install = install,
        Installed = installed,
        //the frames name this directory in every arm, so the fixture must set it instead of the fake's default
        InstalledDir = @"C:\Users\you\AppData\Local\Programs\gatto\",
        //another model holds the server in the held arm, so nothing is asked of this one
        HeldBy = held,
        Audition = new AuditionCheck(
            AuditionOutcome.Passed,
            new AuditionFacts("Q4_K_M", "defaults", "template default", 31.4, 5, 5),
            new DateOnly(2026, 8, 24)),
    };

    //the install question must not come before the engine step, so this drives NotInstalled (Installed returns from the segment in silence)
    [Fact]
    public void THE_OPENING_NEVER_SHOWS_THE_INSTALL_QUESTION()
    {
        var flow = new SetupFlow(Machine(InstallState.NotInstalled));

        Assert.Equal(SetupFlow.FoundKey, ScreenKey.Of(flow.StartPastOpening()));
    }

    //asserted over the whole emitted list, since a flow that asked and moved on would still pass the landing check
    [Fact]
    public void AND_NO_SCREEN_BEFORE_THE_ENGINE_STEP_CARRIES_AN_INSTALL_KEY()
    {
        var flow = new SetupFlow(Machine(InstallState.NotInstalled));
        try { flow.StartPastOpening(); } catch (Xunit.Sdk.XunitException) { } //swallow the failure, since this test reads the emitted list either way

        Assert.DoesNotContain(flow.Emitted, s =>
            ScreenKey.Of(s) == SetupFlow.InstallKey || ScreenKey.Of(s) == SetupFlow.UpdateInstalledKey);
    }

    //walk the whole wizard from Start, since StartAtModelSegment sets _addRoad and that entry is the point of these tests
    private static SetupFlow ToTheCheck(WizardProbes probes)
    {
        var flow = new SetupFlow(probes);
        Assert.Equal(SetupFlow.FoundKey, ScreenKey.Of(flow.StartPastOpening()));
        flow.Answer(SetupFlow.FoundUse);
        var screen = flow.ResumeAfterWrites("gemma-4-26B-A4B-it");
        while (flow.NeedsWritesApplied) screen = flow.ResumeAfterWrites(null);
        Assert.Equal(SetupFlow.AuditionOfferKey, ScreenKey.Of(screen));

        flow.Answer(SetupFlow.Yes);
        Assert.True(SpinWait.SpinUntil(flow.PollWatch, TimeSpan.FromSeconds(10)),
            "the check never finished, so the walk never reached the done step");
        Assert.Equal(SetupFlow.AuditionPassedKey, ScreenKey.Of(flow.Answer(SetupFlow.Landed)));
        return flow;
    }

    //the silent arm writes nothing and still names the directory, so both halves are asserted together
    [Fact]
    public void THE_SILENT_ARM_NAMES_THE_DIRECTORY_AND_WRITES_NO_INSTALL()
    {
        //an equal installed copy asks no question, and the row still states both places
        var flow = ToTheCheck(Machine(InstallState.AlreadyInstalledElsewhere,
            installed: "0.5.0", running: "0.5.0"));
        var screen = Drive(flow, SetupFlow.AuditionPassedNext);

        Assert.NotEqual(SetupFlow.InstallKey, ScreenKey.Of(screen));
        Assert.NotEqual(SetupFlow.UpdateInstalledKey, ScreenKey.Of(screen));

        //nothing was asked, so no install intent may be recorded
        Assert.Null(flow.Writes.InstallTo);

        while (flow.NeedsWritesApplied) screen = flow.ResumeAfterWrites(null);
        var row = Assert.IsType<WizardScreen.Choice>(flow.Emitted[^1]).BodyRows!
            .Single(r => r.Text.StartsWith("gatto ", StringComparison.Ordinal));

        Assert.Contains(@"C:\Users\you\AppData\Local\Programs\gatto\", row.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("installed at  ", row.Text, StringComparison.Ordinal);
    }

    //one screen, four states, each compared against its own stored frame (a key assertion can't see the sentence the repair arms differ by)
    [Theory]
    [InlineData("install", "NotInstalled", null, false)]
    [InlineData("install-legacy", "NotInstalled", null, true)]
    [InlineData("install-repair", "InstalledButNotOnPath", "0.5.0", false)]
    [InlineData("install-repair-older", "InstalledButNotOnPath", "0.5.1", false)]
    [InlineData("install-update", "AlreadyInstalledElsewhere", "0.4.2", false)]
    public void EACH_INSTALL_STATE_RENDERS_ITS_OWN_FRAME(
        string frame, string stateName, string? installed, bool legacy)
    {
        //the version is the frame's subject, so it is injected here while production reads the build's own string
        var flow = ToTheCheck(Machine(Enum.Parse<InstallState>(stateName), installed, running: "0.5.0",
            legacy: legacy));
        var screen = Drive(flow, SetupFlow.AuditionPassedNext);

        var key = frame == "install-update" ? SetupFlow.UpdateInstalledKey : SetupFlow.InstallKey;
        Assert.Equal(key, ScreenKey.Of(screen));

        Golden.AssertEquals("s9", frame, 100,
            WalkRender.SettledFrame(Assert.IsType<WizardScreen.Choice>(screen), 100, key).Rows);
    }

    //the install question comes right after the check settles, on a machine with nothing installed
    [Fact]
    public void THE_DONE_STEPS_FIRST_QUESTION_IS_THE_INSTALL_ONE()
    {
        var flow = ToTheCheck(Machine(InstallState.NotInstalled));

        var next = Drive(flow, SetupFlow.AuditionPassedNext);

        Assert.Equal(SetupFlow.InstallKey, ScreenKey.Of(next));
    }

    //an image no newer than the installed copy may only make it findable, so assert Writes.PathOnly (the label reads the same in both arms)
    [Fact]
    public void AN_IMAGE_NO_NEWER_THAN_THE_INSTALLED_COPY_ONLY_MAKES_IT_FINDABLE()
    {
        var flow = ToTheCheck(Machine(InstallState.InstalledButNotOnPath, installed: "9.9.9"));
        Assert.Equal(SetupFlow.InstallKey, ScreenKey.Of(Drive(flow, SetupFlow.AuditionPassedNext)));

        flow.Answer(SetupFlow.InstallYes);

        Assert.True(flow.Writes.PathOnly);
        Assert.Null(flow.Writes.InstallTo);
    }

    //the update offer arrives with no default, since a replacement offer must not be pre-answered
    [Fact]
    public void A_NEWER_IMAGE_BESIDE_AN_INSTALL_OFFERS_THE_UPDATE_WITH_NO_DEFAULT()
    {
        var flow = ToTheCheck(Machine(InstallState.AlreadyInstalledElsewhere, installed: "0.0.1"));

        var offer = Assert.IsType<WizardScreen.Choice>(Drive(flow, SetupFlow.AuditionPassedNext));

        Assert.Equal(SetupFlow.UpdateInstalledKey, offer.Key);
        Assert.True(offer.NoDefault);
        Assert.DoesNotContain(offer.Options, o => o.Recommended);
    }

    //an equal image asks nothing, since a screen would be a question with one honest answer, and the fact goes on the summary
    [Fact]
    public void AN_EQUAL_IMAGE_ASKS_NOTHING_AND_THE_GATTO_ROW_NAMES_BOTH_PLACES()
    {
        var flow = ToTheCheck(Machine(InstallState.AlreadyInstalledElsewhere,
            installed: Gatto.Core.GattoVersion.String));

        var next = Drive(flow, SetupFlow.AuditionPassedNext);

        Assert.NotEqual(SetupFlow.InstallKey, ScreenKey.Of(next));
        Assert.NotEqual(SetupFlow.UpdateInstalledKey, ScreenKey.Of(next));
        var row = GattoRow(flow);
        Assert.Contains("runs from", row, StringComparison.Ordinal);
        Assert.Contains("gatto is also installed at", row, StringComparison.Ordinal);
    }

    //a development build asks nothing, since a framework-dependent image can't be installed by copying one file, and the summary says so
    [Fact]
    public void A_DEV_BUILD_ASKS_NOTHING_AND_THE_GATTO_ROW_SAYS_WHY()
    {
        var flow = ToTheCheck(Machine(InstallState.DevBuild));

        var next = Drive(flow, SetupFlow.AuditionPassedNext);

        Assert.NotEqual(SetupFlow.InstallKey, ScreenKey.Of(next));
        Assert.Contains("runs from a development build", GattoRow(flow), StringComparison.Ordinal);
    }

    //the DoneStep seam has three call sites, and each one is named here

    //site C is AnswerProve, the ordinary success, already driven by the state frames above, so it needs no test of its own

    //site A of 3, the held arm, where another model holds the server and the flow still owes the user the done step
    [Fact]
    public void SITE_A_A_HELD_SERVER_STILL_REACHES_THE_DONE_STEPS_QUESTION()
    {
        //this arm never reaches the audition offer, since a held server gets the held screen, so it cannot use ToTheCheck
        var flow = new SetupFlow(Machine(InstallState.NotInstalled, held: "another-model"));
        Assert.Equal(SetupFlow.FoundKey, ScreenKey.Of(flow.StartPastOpening()));
        flow.Answer(SetupFlow.FoundUse);
        var screen = flow.ResumeAfterWrites("gemma-4-26B-A4B-it");
        while (flow.NeedsWritesApplied) screen = flow.ResumeAfterWrites(null);
        Assert.Equal(SetupFlow.AuditionHeldKey, ScreenKey.Of(screen));

        Assert.Equal(SetupFlow.InstallKey, ScreenKey.Of(Drive(flow, SetupFlow.AuditionHeldNext)));
    }

    //the /model add command must never offer to install gatto, and it needs its own guard now that the done step calls in
    [Fact]
    public void SITE_B_MODEL_ADD_NEVER_OFFERS_TO_INSTALL_GATTO()
    {
        var flow = new SetupFlow(Machine(InstallState.NotInstalled));
        flow.StartAtModelSegment();
        var screen = flow.ResumeAfterWrites("gemma-4-26B-A4B-it");
        while (flow.NeedsWritesApplied) screen = flow.ResumeAfterWrites(null);
        Assert.Equal(SetupFlow.AuditionOfferKey, ScreenKey.Of(screen));

        flow.Answer(SetupFlow.Yes);
        Assert.True(SpinWait.SpinUntil(flow.PollWatch, TimeSpan.FromSeconds(10)), "the check never finished");
        flow.Answer(SetupFlow.Landed);
        Drive(flow, SetupFlow.AuditionPassedNext);

        //assert over the whole emitted list, since an offer made one screen later would pass a check on the next screen
        Assert.DoesNotContain(flow.Emitted, s =>
            ScreenKey.Of(s) == SetupFlow.InstallKey || ScreenKey.Of(s) == SetupFlow.UpdateInstalledKey);
    }

    //answer then drain the write pause, as SetupRunner does, or a test stands on the pause screen
    private static WizardScreen Drive(SetupFlow flow, string answer)
    {
        var screen = flow.Answer(answer);
        while (flow.NeedsWritesApplied) screen = flow.ResumeAfterWrites(null);
        return screen;
    }

    //the summary's gatto row, read from the render rather than from a probe call so the row and the deed can't part
    private static string GattoRow(SetupFlow flow)
    {
        var done = flow.Emitted.Last(s => ScreenKey.Of(s) == SetupFlow.SummaryKey);
        var rows = done switch
        {
            WizardScreen.Choice c => c.BodyRows,
            WizardScreen.Info i => i.Rows,
            _ => null,
        };
        Assert.NotNull(rows);
        return rows!.Single(r => r.Text.StartsWith("gatto", StringComparison.Ordinal)).Text;
    }
}
