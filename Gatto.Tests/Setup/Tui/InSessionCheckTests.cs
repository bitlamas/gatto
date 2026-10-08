using Gatto.Cli.Setup;
using Gatto.Cli.Setup.Tui;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup.Tui;

//in a session the check is asked for before anything starts, since a new model cannot be checked beside the session's own server
public class InSessionCheckTests
{
    //the ask renders before any start is asked for, and Esc on the done screen leaves the outgoing model serving

    private const string Incoming = "gemma-4-26B-A4B-it";
    //this id is never the incoming one, since a fixture using one string for both cannot tell which one a sentence names
    private const string Serving = "qwen-qwen3.6-35b-a3b";

    //the GGUF the model runs on, as a path and a size, which ActiveModelFile answers from
    private const string ActivePath = @"C:\models\gemma-4-26B-A4B-it\gemma-4-26B-A4B-it-UD-Q4_K_M.gguf";

    //the check's verdict, defaulting to passed and passed at construction since WizardProbes has init-only members
    private static WizardProbes Probes(
        string? heldBy = Serving, (string Path, long Bytes)? activeModel = null,
        AuditionOutcome outcome = AuditionOutcome.Passed) => new()
    {
        Llama = @"C:\llama\llama-server.exe",
        ActiveFile = "gemma-4-26B-A4B-it-UD-Q4_K_M.gguf",
        HeldBy = heldBy,
        ActiveModel = activeModel,
        Audition = new AuditionCheck(outcome,
            new AuditionFacts("Q4_K_M", "greedy", "off", 42.5, outcome == AuditionOutcome.Passed ? 5 : 3, 5)),
    };

    //the step as the in-session entry reaches it, through the write pause the offer sits behind
    private static WizardScreen.Choice CheckStep(SetupFlow flow)
    {
        flow.StartAtModelSegment();
        var screen = flow.ResumeAfterWrites(Incoming);
        while (flow.NeedsWritesApplied) screen = flow.ResumeAfterWrites(null);
        return Assert.IsType<WizardScreen.Choice>(screen);
    }

    //the consent is composed from the serve record alone, since nothing on disk records an attached repl
    [Fact]
    public void THE_CONSENT_NAMES_ONLY_WHAT_THE_RECORD_HOLDS()
    {
        var ask = CheckStep(new SetupFlow(Probes(heldBy: "qwen3.5")));
        var text = string.Join(Environment.NewLine, ask.BodyRows!.Select(r => r.Text));

        //the two facts it may state: what is loaded and what the deed does to it
        Assert.Contains("qwen3.5", text, StringComparison.Ordinal);
        Assert.Contains("stops", text, StringComparison.Ordinal);

        //no line about the user's own session here (nothing records which REPL is attached to the server)
        Assert.DoesNotContain("your session", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("your conversation", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("your next message", text, StringComparison.OrdinalIgnoreCase);
    }

    //two rows, the added model then what happens to the held server (gatto can't see what talks to a server)
    [Fact]
    public void THE_ASK_SAYS_THE_MODEL_IS_ADDED_AND_WHAT_HAPPENS_TO_THE_SERVER()
    {
        var ask = CheckStep(new SetupFlow(Probes(heldBy: "qwen3.5")));
        var rows = ask.BodyRows!.Select(r => r.Text).ToList();
        var all = string.Join("\n", rows);

        Assert.Equal(2, rows.Count);
        Assert.Equal($"{Incoming} is added to gatto.", rows[0]);
        Assert.Contains("qwen3.5's server stops", rows[1], StringComparison.Ordinal);
        Assert.DoesNotContain("re-read", all, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("you", all, StringComparison.OrdinalIgnoreCase);
    }

    //one Esc on the check ask answers "Add it unchecked", through the real flow and the real face, since nothing is behind it
    [Fact]
    public void ESC_ON_THE_CHECK_ASK_ADDS_IT_UNCHECKED()
    {
        var ask = CheckStep(new SetupFlow(Probes(heldBy: "qwen3.5")));
        Assert.False(ask.AllowBack);

        var (answer, _) = WalkRender.Answered(ask, 100, [WizardRig.Esc]);

        Assert.Equal(SetupFlow.AddUnchecked, answer);
    }

    //the serving fact is read before the audition task starts (at compose time it sees whatever the run has already done)
    [Fact]
    public void THE_SERVING_FACT_IS_READ_BEFORE_THE_CHECK_RUNS()
    {
        //nothing is serving this model, the fake would claim otherwise once its run has begun
        var probes = Probes(heldBy: null);
        var flow = new SetupFlow(probes);
        flow.StartAtModelSegment();
        var screen = flow.ResumeAfterWrites(Incoming);
        while (flow.NeedsWritesApplied) screen = flow.ResumeAfterWrites(null);
        var running = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.Yes));

        Assert.Contains(running.BodyRows!, r =>
            r.Text.Contains("gatto starts the server", StringComparison.Ordinal));

        //the check runs on its own thread, so wait for its deed before reading the order
        Assert.True(SpinWait.SpinUntil(() => probes.ServerDeeds.Contains("audition"), TimeSpan.FromSeconds(30)), "the check never recorded its start");
        //the oracle is the order the deeds come in, the serving read before the audition starts
        var deeds = probes.ServerDeeds;
        Assert.Contains("serverup", deeds);
        Assert.Contains("audition", deeds);
        Assert.True(deeds.ToList().IndexOf("serverup") < deeds.ToList().IndexOf("audition"),
            "the serving fact was read after the check began: " + string.Join(" → ", deeds));
    }

    //the model is already serving and nothing else holds the server, so this walk takes the plain offer
    [Fact]
    public void THE_RUNNING_SCREEN_SAYS_THE_MODEL_IS_ALREADY_UP()
    {
        var flow = new SetupFlow(Probes(heldBy: Incoming));
        flow.StartAtModelSegment();
        var screen = flow.ResumeAfterWrites(Incoming);
        while (flow.NeedsWritesApplied) screen = flow.ResumeAfterWrites(null);

        //the plain audition offer, nothing else holds the server so there is no swap to consent to
        Assert.Equal(SetupFlow.AuditionOfferKey, Assert.IsType<WizardScreen.Choice>(screen).Key);
        var running = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.Yes));

        Assert.Contains(running.BodyRows!, r =>
            r.Text.Contains($"{Incoming} is on the server now", StringComparison.Ordinal));
    }

    [Fact]
    public void THE_CHECK_IS_ASKED_BEFORE_ANY_START_IS_ASKED_FOR()
    {
        var probes = Probes();
        var ask = CheckStep(new SetupFlow(probes));

        //the ask's own key, so a tree that skipped the ask is already on the running screen
        Assert.Equal(SetupFlow.InSessionCheckKey, ask.Key);

        //nothing may start behind the ask, and the wait has to time out (an immediate read races the task)
        Assert.False(
            SpinWait.SpinUntil(() => probes.AuditionStarts > 0, TimeSpan.FromMilliseconds(500)),
            "the check was started without asking, and a swap of the served model needs a confirm");
    }

    //a check that could not run must not say the swap already happened (that claim sits with the completion's check row)

    private static WizardProbes CheckReturning(AuditionCheck check) => new()
    {
        Llama = @"C:\llama\llama-server.exe",
        ActiveFile = "gemma-4-26B-A4B-it-UD-Q4_K_M.gguf",
        HeldBy = Serving,
        Audition = check,
    };

    //answering yes releases the held server before the check starts (a release after it leaves the same defect on screen)
    [Fact]
    public void YES_RELEASES_THE_HELD_SERVER_BEFORE_THE_CHECK_STARTS()
    {
        var probes = Probes();
        var flow = new SetupFlow(probes);
        CheckStep(flow);

        flow.Answer(SetupFlow.Yes);

        Assert.True(
            SpinWait.SpinUntil(() => probes.ServerDeeds.Contains("audition"), TimeSpan.FromSeconds(30)),
            "the check never started, so this test cannot say anything about the order");
        //the serving read comes after the release and before the check starts, so it sees the machine and can't race the task
        Assert.Equal(["release", "serverup", "audition"], probes.ServerDeeds);
    }

    //the unchecked answer must release nothing (this arm keeps the fix from becoming "always release")
    [Fact]
    public void ADDING_IT_UNCHECKED_LEAVES_THE_SERVER_ALONE()
    {
        var probes = Probes();
        var flow = new SetupFlow(probes);
        CheckStep(flow);

        flow.Answer(SetupFlow.AddUnchecked);

        Assert.Empty(probes.ServerDeeds);
        Assert.Equal(Serving, probes.ServerHeldByAnother(Incoming));
    }

    //press both answers, a dispatch with no arm for a key throws on the answer while the screen renders fine
    [Fact]
    public void BOTH_ANSWERS_ARE_REACHABLE_AND_ONLY_YES_STARTS_THE_CHECK()
    {
        var yes = new SetupFlow(Probes());
        var checkStep = CheckStep(yes);
        Assert.Equal(SetupFlow.Yes, checkStep.Options[0].Key);
        Assert.Equal(SetupFlow.AuditionRunningKey,
            Assert.IsType<WizardScreen.Choice>(yes.Answer(SetupFlow.Yes)).Key);

        var unchecked_ = Probes();
        var flow = new SetupFlow(unchecked_);
        var step = CheckStep(flow);
        Assert.Equal(SetupFlow.AddUnchecked, step.Options[1].Key);
        flow.Answer(SetupFlow.AddUnchecked);
        Assert.False(
            SpinWait.SpinUntil(() => unchecked_.AuditionStarts > 0, TimeSpan.FromMilliseconds(500)),
            "the unchecked answer ran the check anyway");
    }

    //a held server is still the refusal on the setup flow, nothing there has consented to stopping anything
    [Fact]
    public void ON_THE_SETUP_ROAD_A_HELD_SERVER_IS_STILL_THE_REFUSAL()
    {
        var flow = new SetupFlow(Probes());
        flow.StartPastEngine();
        var screen = flow.ResumeAfterWrites(Incoming);
        while (flow.NeedsWritesApplied) screen = flow.ResumeAfterWrites(null);

        Assert.Equal(SetupFlow.AuditionHeldKey, Assert.IsType<WizardScreen.Choice>(screen).Key);
    }

    //nothing is serving here, so the ordinary offer stands (this is the fixture that keeps the condition load-bearing)
    [Fact]
    public void IN_A_SESSION_WITH_NOTHING_SERVING_THE_ORDINARY_OFFER_STANDS()
    {
        var flow = new SetupFlow(Probes(heldBy: null));
        Assert.Equal(SetupFlow.AuditionOfferKey, CheckStep(flow).Key);
    }

    //the key, pressed

    private sealed class Keys(IEnumerable<ConsoleKeyInfo> k) : Gatto.Terminal.IKeySource
    {
        private readonly Queue<ConsoleKeyInfo> _q = new(k);
        public bool KeyAvailable => false;
        public ConsoleKeyInfo ReadKey() => _q.Count > 0
            ? _q.Dequeue()
            : throw new InvalidOperationException("the face asked for a key the script does not have");
    }

    //write control keys as escapes, a raw byte in source is invisible to a reviewer and to a diff
    private static readonly ConsoleKeyInfo Enter = new('\r', ConsoleKey.Enter, false, false, false);
    private static readonly ConsoleKeyInfo Tab = new('\t', ConsoleKey.Tab, false, false, false);
    private static readonly ConsoleKeyInfo Esc = new('\u001b', ConsoleKey.Escape, false, false, false);

    private static (TuiWizardSurface F, Func<IReadOnlyList<string>> Painted) Face(params ConsoleKeyInfo[] keys)
    {
        var f = new TuiWizardSurface(new RecordingSurface { Width = 100 }, new Keys(keys),
            new Gatto.Terminal.Theme(new Gatto.Terminal.TermCaps(true, true)),
            "0.5.0", "1a2b3c4", () => 0);
        return (f, () => f.LastPainted);
    }

    //pressing Esc answers the ask unchecked, and the footer must say so (the screen must come from the flow)
    [Fact]
    public void ESC_ANSWERS_THE_ASK_AND_THE_FOOTER_SAYS_WHAT_IT_DOES()
    {
        var ask = CheckStep(new SetupFlow(Probes()));
        var (f, painted) = Face(Esc);

        Assert.Equal(SetupFlow.AddUnchecked, f.Choose(ask));
        Assert.Contains(painted(), r => r.Contains("Esc add it unchecked", StringComparison.Ordinal));
        Assert.DoesNotContain(painted(), r => r.Contains("Esc again", StringComparison.Ordinal));
    }

    //the in-session ending shows no install, no consent and no launch row (the launch row is still owed a guard)

    //the in-session strip, and what it does to the frame

    //the in-session strip has three sections and the setup strip five (asserting three alone would pass a tree that gives every strip three)
    [Fact]
    public void THE_IN_SESSION_ROAD_IS_THREE_SECTIONS_AND_THE_SETUP_ROAD_IS_STILL_FIVE()
    {
        var inSession = CheckStep(new SetupFlow(Probes()));
        Assert.Equal(["model", "check", "done"], inSession.Strip.Select(s => s.Name));

        var setup = new SetupFlow(Probes());
        setup.StartPastEngine();
        var screen = setup.ResumeAfterWrites(Incoming);
        while (setup.NeedsWritesApplied) screen = setup.ResumeAfterWrites(null);
        Assert.Equal(["machine", "engine", "model", "check", "done"],
            Assert.IsType<WizardScreen.Choice>(screen).Strip.Select(s => s.Name));
    }

    //the footer names no Tab in a session and the key moves nothing, either half alone lets the screen lie about its keys
    [Fact]
    public void IN_SESSION_THE_STRIP_IS_NOT_A_TAB_STOP()
    {
        var ask = CheckStep(new SetupFlow(Probes()));
        var (f, painted) = Face(Tab, Tab, Esc);

        Assert.Equal(SetupFlow.AddUnchecked, f.Choose(ask));

        var footer = painted().LastOrDefault(r => r.Contains("Esc ", StringComparison.Ordinal)) ?? "";
        Assert.DoesNotContain("Tab", footer, StringComparison.Ordinal);
        //the Tab presses moved no focus, Esc still answered the screen
        Assert.Contains("Esc add it unchecked", footer, StringComparison.Ordinal);
    }

    //the strip is a Tab stop on neither road, and this step draws no second area so its footer names no Tab
    [Fact]
    public void ON_THE_SETUP_ROAD_THE_STRIP_IS_NOT_A_TAB_STOP_EITHER()
    {
        var flow = new SetupFlow(Probes(heldBy: null));
        flow.StartPastEngine();
        var screen = flow.ResumeAfterWrites(Incoming);
        while (flow.NeedsWritesApplied) screen = flow.ResumeAfterWrites(null);

        //press Enter here so the ordinary footer is the last frame painted, the first Esc arms the leave chord and repaints
        var (f, painted) = Face(Enter);
        f.Choose(Assert.IsType<WizardScreen.Choice>(screen));
        Assert.DoesNotContain(painted(), r => r.Contains("Tab ", StringComparison.Ordinal));
    }

    //the legend reads the road (adding is not setting up) and the body reads whether this model's server is up
    [Fact]
    public void THE_LIVE_CHECK_READS_THE_SERVER_IN_ITS_BODY_AND_THE_ROAD_IN_ITS_LEGEND()
    {
        var inSession = new SetupFlow(Probes());
        CheckStep(inSession);
        var running = Assert.IsType<WizardScreen.Choice>(inSession.Answer(SetupFlow.Yes));
        Assert.Equal(SetupFlow.AuditionRunningKey, running.Key);
        Assert.Contains(running.BodyRows!, r =>
            r.Text.Contains("gatto starts the server", StringComparison.Ordinal));
        //the legend still reads the road, which adds here rather than setting up
        Assert.Equal("stopping keeps it added, unchecked", running.Legend);

        var setup = new SetupFlow(Probes(heldBy: null));
        setup.StartPastEngine();
        var screen = setup.ResumeAfterWrites(Incoming);
        while (setup.NeedsWritesApplied) screen = setup.ResumeAfterWrites(null);
        var onTheRoad = Assert.IsType<WizardScreen.Choice>(setup.Answer(SetupFlow.Yes));
        Assert.Contains(onTheRoad.BodyRows!, r =>
            r.Text.Contains("gatto starts the server", StringComparison.Ordinal));
        Assert.Equal("stopping keeps it set up, unchecked", onTheRoad.Legend);
    }

    //two options claiming Esc throw from the paint, before a key is read, so the screen is never drawn
    [Fact]
    public void TWO_OPTIONS_CLAIMING_ESC_IS_A_SCREEN_DEFECT_AND_IT_FAILS_LOUD()
    {
        var twoClaims = new WizardScreen.Choice(
            SetupFlow.InSessionCheckKey, "Check it now?",
            [
                new ChoiceOption(SetupFlow.Yes, "Check it now", EscVerb: "check it"),
                new ChoiceOption(SetupFlow.AddUnchecked, "Add it unchecked", EscVerb: "add it"),
            ]);

        var (f, _) = Face(Esc);
        var ex = Assert.Throws<InvalidOperationException>(() => f.Choose(twoClaims));
        Assert.Contains("exactly one option", ex.Message, StringComparison.Ordinal);
        Assert.Contains(SetupFlow.InSessionCheckKey, ex.Message, StringComparison.Ordinal);
    }

    //one declaring option is the shipped shape and must not throw, otherwise the other test passes for a guard that throws on everything
    [Fact]
    public void ONE_OPTION_CLAIMING_ESC_IS_THE_ORDINARY_SHAPE()
    {
        var oneClaim = new WizardScreen.Choice(
            SetupFlow.InSessionCheckKey, "Check it now?",
            [
                new ChoiceOption(SetupFlow.Yes, "Check it now"),
                new ChoiceOption(SetupFlow.AddUnchecked, "Add it unchecked", EscVerb: "add it"),
            ]);

        var (f, _) = Face(Esc);
        Assert.Equal(SetupFlow.AddUnchecked, f.Choose(oneClaim));
    }
}
