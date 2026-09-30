using Gatto.Cli.Setup;
using Gatto.Cli.Setup.Tui;
using Gatto.Repl.Term;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup.Tui;

//the space bar stops the transfer and keeps the partial, and the pause is a screen of its own, judged by the probes' records
public class PausedDownloadTests
{
    private static ConsoleKeyInfo Space => new(' ', ConsoleKey.Spacebar, false, false, false);
    private static ConsoleKeyInfo P => new('p', ConsoleKey.P, false, false, false);
    private static ConsoleKeyInfo Esc => new('\0', ConsoleKey.Escape, false, false, false);

    //keys go through the real face, so what reaches the flow is the key itself
    private static string? Press(WizardScreen.Choice c, params ConsoleKeyInfo[] keys) =>
        WalkRender.WatchAfterKeys(c, 100, ModelFetchTests.FetchingTick, keys);

    private static IReadOnlyList<string> Painted(WizardScreen.Choice c, int width) =>
        WalkRender.Watching(c, width, ModelFetchTests.FetchingTick).Rows;

    //the fetching screen paused by Space, with each step asserted so a helper that fired regardless can't pass
    private static (SetupFlow Flow, WizardProbes Probes, WizardScreen.Choice Paused) Paused()
    {
        var (flow, probes, screen) = ModelFetchTests.LiveFetch();

        Assert.Equal(SetupFlow.ModelFetchPause, Press(screen, Space));
        var next = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.ModelFetchPause));
        Assert.Equal(SetupFlow.ModelFetchingPausedKey, next.Key);
        return (flow, probes, next);
    }

    //the keys reach the fetch

    //the space bar answers the pause on the live fetch
    [Fact]
    public void SPACE_ON_A_LIVE_FETCH_ANSWERS_THE_PAUSE() =>
        Assert.Equal(SetupFlow.ModelFetchPause, Press(ModelFetchTests.LiveFetch().Screen, Space));

    //the pause keeps the partial and waits for the fetch to unwind, which the probes' records show
    [Fact]
    public void THE_PAUSE_STOPS_THE_TRANSFER_AND_DELETES_NOTHING()
    {
        var (_, probes, _) = Paused();

        Assert.True(probes.ModelUnwound, "the flow did not wait for the fetch to unwind");
        Assert.Empty(probes.PartialsDeleted);
        Assert.Equal(1, probes.ModelStarts);
    }

    //resume lives on the key that paused, and the second start is read only after the fetch is joined by pausing again
    [Fact]
    public void SPACE_ON_THE_PAUSED_SCREEN_CARRIES_ON()
    {
        var (flow, probes, paused) = Paused();

        Assert.Equal(SetupFlow.ModelFetchResume, Press(paused, Space));
        var again = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.ModelFetchResume));
        Assert.Equal(SetupFlow.ModelFetchingKey, again.Key);

        //pausing again is the product's own join, so nothing about the second fetch is read before it
        Assert.Equal(SetupFlow.ModelFetchPause, Press(again, Space));
        Assert.Equal(SetupFlow.ModelFetchingPausedKey,
            Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.ModelFetchPause)).Key);

        Assert.Equal(2, probes.ModelStarts);
        Assert.Empty(probes.PartialsDeleted);
    }

    //a paused screen never polls the disk, so the watch can't fall through to the browser's scan and arrive on a download
    [Fact]
    public void A_PAUSED_SCREEN_NEVER_POLLS_THE_DISK()
    {
        var (flow, probes, _) = Paused();
        var before = probes.ScanRoots.Count;

        Assert.False(flow.PollWatch());
        Assert.False(flow.PollWatch());

        Assert.Equal(before, probes.ScanRoots.Count);
    }

    //the paused screen, drawn

    //the paused bar keeps the fill and the two figures and drops the rate and the remainder, which a pause makes untrue
    [Fact]
    public void THE_PAUSED_BAR_KEEPS_ITS_FIGURES_AND_DROPS_THE_RATE()
    {
        var bar = Assert.Single(Painted(Paused().Paused, 100),
            r => r.Contains("7.9 of 16.9 GB", StringComparison.Ordinal));

        Assert.Contains("· paused", bar, StringComparison.Ordinal);
        Assert.DoesNotContain("MB/s", bar, StringComparison.Ordinal);
        Assert.DoesNotContain("left", bar, StringComparison.Ordinal);
        Assert.Contains('━', bar);
    }

    //the paused footer keeps the ruled words, delete the partial because nothing is running to stop, and stays one row at 80
    [Theory]
    [InlineData(100)]
    [InlineData(80)]
    public void THE_PAUSED_FOOTER_IS_THE_RULED_ROW(int width) =>
        Assert.Equal("  Space carry on   Esc delete the partial",
            Painted(Paused().Paused, width)[^1]);

    //the armed row on the paused screen must not say stops the fetch, since the fetch is already over. two Escs are scripted so the last frame is armed
    [Fact]
    public void THE_PAUSED_ARMED_ROW_DOES_NOT_CLAIM_TO_STOP_A_FETCH()
    {
        var armed = WalkRender.Watching(Paused().Paused, 100, ModelFetchTests.FetchingTick,
            script: [Esc, Esc], nowMs: () => 0).Rows;

        Assert.Contains("  Esc again: deletes the 7.9 GB already here", armed);
        Assert.DoesNotContain(armed, r => r.Contains("stops the fetch", StringComparison.Ordinal));
    }

    //the Esc press on the paused screen

    //pressing Esc twice on the paused screen deletes the partial through the probe, since no transfer is running to cancel
    [Fact]
    public void ESC_TWICE_ON_THE_PAUSED_SCREEN_DELETES_THE_PARTIAL()
    {
        var (flow, probes, paused) = Paused();

        Assert.Equal(SetupFlow.ModelFetchStop, Press(paused, Esc, Esc));
        var next = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.ModelFetchStop));

        Assert.Same(probes.HubOffer, Assert.Single(probes.PartialsDeleted));
        //the consent, the same screen the live stop reaches, since the user declined the download and the partial is gone
        Assert.Equal(SetupFlow.ModelConsentKey, next.Key);
    }

    //a digit must not restart the fetch here, since the screen's first option is the resume
    [Fact]
    public void A_DIGIT_DOES_NOTHING_ON_THE_PAUSED_SCREEN()
    {
        var (_, probes, paused) = Paused();

        Assert.Equal(SetupFlow.Landed, Press(paused, WizardRig.Digit('1')));
        Assert.Equal(1, probes.ModelStarts);
    }

    //the pause arm must read the fetch's outcome, since it can finish while the face waits for a key. anything but paused behaves as if no key was pressed

    //a fetch that finished takes the arrival screen, since a pause from 7.9 GB would describe a download that is over
    [Fact]
    public void A_FETCH_THAT_FINISHED_TAKES_THE_ARRIVAL_ROAD_NOT_THE_PAUSE()
    {
        var (flow, _, _) = ModelFetchTests.LiveFetch(blockingFetches: 0);

        var next = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.ModelFetchPause));

        Assert.Equal(SetupFlow.ModelArrivedKey, next.Key);
        Assert.NotEqual(SetupFlow.ModelFetchingPausedKey, next.Key);
    }

    //a mismatched fetch goes to its own screen, since that arm deletes the file it could not verify
    [Fact]
    public void A_FETCH_THAT_MISMATCHED_TAKES_ITS_OWN_ROAD_NOT_THE_PAUSE()
    {
        var (flow, _, _) = ModelFetchTests.LiveFetch(blockingFetches: 0,
            result: new(Gatto.Core.Acquire.HubFetchOutcome.Mismatch, "weights.gguf",
                new string('a', 64), new string('b', 64)));

        var next = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.ModelFetchPause));

        Assert.Equal(SetupFlow.ModelMismatchKey, next.Key);
    }

    //every way out of the paused screen clears the flag, or every watch after it polls forever

    //after a resume the watch resolves again, so the second fetch is left unblocked to tell a healthy flow from a wedged one
    [Fact]
    public void THE_RESUME_ROAD_LEAVES_THE_WATCH_ABLE_TO_RESOLVE()
    {
        var (flow, _, screen) = ModelFetchTests.LiveFetch(blockingFetches: 1);
        Assert.Equal(SetupFlow.ModelFetchPause, Press(screen, Space));
        var paused = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.ModelFetchPause));
        Assert.Equal(SetupFlow.ModelFetchingPausedKey, paused.Key);

        Assert.Equal(SetupFlow.ModelFetchResume, Press(paused, Space));
        flow.Answer(SetupFlow.ModelFetchResume);

        Assert.True(SpinWait.SpinUntil(flow.PollWatch, TimeSpan.FromSeconds(10)),
            "the watch never resolved after a resume: the paused flag is still forcing it false");
    }


    //the stop's own arm clears the flag, so the watch reaches the disk again after it
    [Fact]
    public void THE_STOP_ROAD_LEAVES_THE_WATCH_ABLE_TO_RESOLVE()
    {
        var (flow, probes, paused) = Paused();
        Assert.Equal(SetupFlow.ModelFetchStop, Press(paused, Esc, Esc));
        flow.Answer(SetupFlow.ModelFetchStop);

        var before = probes.ScanRoots.Count;
        flow.PollWatch();

        Assert.True(probes.ScanRoots.Count > before,
            "the watch never reached the disk after the stop: the paused flag is still set");
    }

    //re-picking a paused model is the ordinary pick in a fresh flow, which is what /model add builds, and HubFetch resumes off the .part

    //picking the same model again starts one more fetch, and the count is read only after the second fetch is joined by pausing it
    [Fact]
    public void PICKING_THE_SAME_MODEL_AGAIN_STARTS_THE_FETCH_ONCE_MORE()
    {
        var (flow, probes, screen) = ModelFetchTests.LiveFetch();
        Assert.Equal(SetupFlow.ModelFetchPause, Press(screen, Space));
        Assert.Equal(SetupFlow.ModelFetchingPausedKey,
            Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.ModelFetchPause)).Key);
        var pausedAfter = probes.ModelStarts;

        //a second gatto model run: a new flow over the same probes and the same offer
        var again = new SetupFlow(probes);
        again.StartAtModelSegment();
        again.Answer("0");
        var fetching = Assert.IsType<WizardScreen.Choice>(again.Answer(SetupFlow.ModelFetchNow));
        Assert.Equal(SetupFlow.ModelFetchingKey, fetching.Key);

        //nothing about the second fetch is read before the pause joins it
        Assert.Equal(SetupFlow.ModelFetchPause, Press(fetching, Space));
        again.Answer(SetupFlow.ModelFetchPause);

        Assert.Equal(pausedAfter + 1, probes.ModelStarts);
        Assert.Same(probes.HubOffer, probes.ModelFetched[^1]);
        Assert.Empty(probes.PartialsDeleted);
    }

    //the setup road

    //the space bar pauses the setup road's fetch too, since a multi-gigabyte wait is the same on both roads
    [Fact]
    public void SPACE_PAUSES_THE_SETUP_ROADS_FETCH()
    {
        var (flow, _, screen) = ModelFetchTests.LiveFetch(inSession: false);

        Assert.Equal(SetupFlow.ModelFetchPause, Press(screen, Space));
        Assert.Equal(SetupFlow.ModelFetchingPausedKey,
            Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.ModelFetchPause)).Key);
    }

    //p does nothing on either road, since there is no session to go back to. an unbound key lets the watch answer, so Landed is the oracle
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AND_P_DOES_NOTHING_ON_EITHER_ROAD(bool addRoad)
    {
        Assert.Equal(SetupFlow.Landed,
            Press(ModelFetchTests.LiveFetch(inSession: addRoad).Screen, P));
    }

    //neither road's fetch screens offer p, and both screens are checked since each had its own arm
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NEITHER_ROADS_FETCH_SCREENS_OFFER_A_WAY_BACK_TO_A_SESSION(bool addRoad)
    {
        var (flow, _, screen) = ModelFetchTests.LiveFetch(inSession: addRoad);

        Assert.DoesNotContain(screen.Options, o => o.Press == "p");
        Assert.Contains(screen.Options, o => o.Key == SetupFlow.ModelFetchPause);

        Press(screen, Space);
        var paused = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.ModelFetchPause));

        Assert.DoesNotContain(paused.Options, o => o.Press == "p");
        Assert.Contains(paused.Options, o => o.Key == SetupFlow.ModelFetchResume);
        Assert.Contains(paused.Options, o => o.Key == SetupFlow.ModelFetchStop);
    }

}
