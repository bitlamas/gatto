using Gatto.Cli.Setup;
using Gatto.Core.Acquire;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup.Tui;

//the golden corpus wins on wording, but a footer verb must follow what its key does. the retry state stays a question screen with a typed address
public class ConnectTests
{
    private static WizardProbes Probes(ConnectProbe? server = null, ConnectProbe? afterSkip = null,
        (int Port, string Model)? own = null) =>
        new()
        {
            Llama = @"C:\llama\llama-server.exe",
            Server = server,
            ServerAfterSkip = afterSkip,
            Own = own,
        };

    //reach the screen through the flow, a hand-built screen would skip the probe that runs on the way in
    private static WizardScreen.Choice Road(WizardProbes probes)
    {
        var flow = new SetupFlow(probes);
        flow.StartPastOpening();
        return Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.ForkConnect));
    }

    //no answer is a normal state. the screen names ports by owner, so the user recognizes their own setup instead of gatto's defaults
    [Fact]
    public void NOTHING_ANSWERED_RENDERS_AT_100() =>
        Golden.AssertEquals("s8", "server-none", 100,
            WalkRender.SettledFrame(Road(Probes()), 100, "server-none").Rows);

    //the corpus fixes one paragraph ending in No other server answered. the invitation changes, gatto's own server already runs
    [Fact]
    public void GATTOS_OWN_SERVER_WITH_NOTHING_BEHIND_IT_RENDERS_AT_100() =>
        Golden.AssertEquals("s8", "server-own", 100,
            WalkRender.SettledFrame(
                Road(Probes(
                    server: new ConnectProbe("http://127.0.0.1:1235", ["gemma-4-26B-A4B-it"], 8192),
                    afterSkip: null,
                    own: (1235, "gemma-4-26B-A4B-it"))),
                100, "server-own").Rows);

    //one fixture serves both screens, they show one server answer twice and two lists could drift apart
    private static readonly string[] LmsModels =
    [
        "qwen/qwen3-30b-a3b-2507", "google/gemma-3-12b", "mistralai/devstral-small-2507",
        "openai/gpt-oss-20b", "liquid/lfm2-1.2b",
    ];

    //the screen shows three facts and a cost sentence. the cost sentence states what the option is and never warns against choosing it
    [Fact]
    public void A_HAND_STARTED_LLAMA_SERVER_RENDERS_AT_100() =>
        Golden.AssertEquals("s8", "server-llama", 100,
            WalkRender.SettledFrame(
                Road(Probes(server: new ConnectProbe(
                    "http://127.0.0.1:8080", ["Qwen3-30B-A3B-Instruct-2507-Q4_K_M.gguf"], 131072))),
                100, "server-llama").Rows);

    //the server returns a list of loadable models, so the row must not claim they all run at once. the context row says what is missing instead of guessing
    [Fact]
    public void AN_LM_STUDIO_SERVER_RENDERS_AT_100() =>
        Golden.AssertEquals("s8", "server-lms", 100,
            WalkRender.SettledFrame(
                Road(Probes(server: new ConnectProbe("http://127.0.0.1:1234", LmsModels, null))),
                100, "server-lms").Rows);

    //the second width proves the fold, the cost sentence wraps and the fact rows hold their column. the footer entry drops its hint and keeps its text
    [Fact]
    public void THE_FOUND_SCREEN_FOLDS_AT_EIGHTY_COLUMNS() =>
        Golden.AssertEquals("s8", "server-80", 80,
            WalkRender.SettledFrame(
                Road(Probes(server: new ConnectProbe("http://127.0.0.1:1234", LmsModels, null))),
                80, "server-80").Rows);

    //reach the pick through the flow, the pick exists only because a list came back. a composed screen would assert a model of the flow
    private static WizardScreen.Choice Pick(WizardProbes probes)
    {
        var flow = new SetupFlow(probes);
        flow.StartPastOpening();
        flow.Answer(SetupFlow.ForkConnect);
        return Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.Yes));
    }

    //gatto names a model in every request and cannot invent one, so the rows show the server's own strings. the list is whole, with arrows and no digits
    [Fact]
    public void THE_MODEL_PICK_RENDERS_AT_100() =>
        Golden.AssertEquals("s8", "model-pick", 100,
            WalkRender.SettledFrame(
                Pick(Probes(server: new ConnectProbe("http://127.0.0.1:1234", LmsModels, 8192))),
                100, "model-pick").Rows);

    //every request sends a model field and only the server knows the names it answers to, so an empty list must produce a question
    [Fact]
    public void THE_MODEL_NAME_ASK_RENDERS_AT_100() =>
        Golden.AssertEquals("s8", "model-name", 100,
            WalkRender.Ask(
                Assert.IsType<WizardScreen.Ask>(AskFor(Probes(
                    server: new ConnectProbe("http://127.0.0.1:1234", [], 8192)))),
                100, "model-name").Rows);

    private static WizardScreen AskFor(WizardProbes probes)
    {
        var flow = new SetupFlow(probes);
        flow.StartPastOpening();
        flow.Answer(SetupFlow.ForkConnect);
        return flow.Answer(SetupFlow.Yes);
    }

    //the ask appears because the server would not report its window, and it says so. the offer is a pickable row and the typed field takes any number
    [Fact]
    public void THE_CONTEXT_ASK_RENDERS_AT_100()
    {
        var flow = new SetupFlow(Probes(
            server: new ConnectProbe("http://127.0.0.1:1234", ["one-model"], null)));
        flow.StartPastOpening();
        flow.Answer(SetupFlow.ForkConnect);
        var screen = flow.Answer(SetupFlow.Yes);

        Golden.AssertEquals("s8", "context", 100, Rendered(screen, 100, "context"));
    }

    //render whichever screen record the flow emitted, a cast to Choice would hide the record type. which record it is forms part of what the golden measures
    private static IReadOnlyList<string> Rendered(WizardScreen s, int width, string key) => s switch
    {
        WizardScreen.Choice c => WalkRender.SettledFrame(c, width, key).Rows,
        WizardScreen.Ask a => WalkRender.Ask(a, width, key).Rows,
        _ => throw new InvalidOperationException($"{key}: the flow emitted {s.GetType().Name}"),
    };

    //reach the check the way the runner does, writes apply and then the flow resumes. a hand-built screen would assert a model of the measurement
    private static WizardScreen Checked(WizardProbes probes)
    {
        var flow = new SetupFlow(probes);
        flow.StartPastOpening();
        flow.Answer(SetupFlow.ForkConnect);
        flow.Answer(SetupFlow.Yes);
        return Settled(flow);
    }

    //this helper moves a live watch screen to its verdict when PollWatch resolves. do not copy it, a missed step pins a live frame
    private static WizardScreen Settled(SetupFlow flow)
    {
        var screen = flow.ResumeAfterWrites(null);
        while (flow.NeedsWritesApplied) screen = flow.ResumeAfterWrites(null);
        return screen is WizardScreen.Choice { Watching: true }
            ? flow.Answer(SetupFlow.OpenRepl)
            : screen;
    }

    //the check asks one small question and reports four facts, where, what, context and speed. the speed band reports words and never decides anything
    [Fact]
    public void THE_CHECK_THAT_ANSWERS_RENDERS_AT_100()
    {
        var screen = Checked(new WizardProbes
        {
            Llama = @"C:\llama\llama-server.exe",
            Server = new ConnectProbe(
                "http://127.0.0.1:8080", ["Qwen3-30B-A3B-Instruct-2507-Q4_K_M.gguf"], 131072),
            Prove = new Gatto.Core.Acquire.ProveOutcome(
                true, "OK", TimeSpan.FromSeconds(3), TokensPerSecond: 38.2),
        });

        Golden.AssertEquals("s8", "answers-llama", 100, Rendered(screen, 100, "answers-llama"));
    }

    //the server listed models and reported no context, so the flow adds two more answers. the rows must read as what that flow decided
    private static WizardScreen CheckedAfterPickAndContext(int width)
    {
        var flow = new SetupFlow(new WizardProbes
        {
            Llama = @"C:\llama\llama-server.exe",
            Server = new ConnectProbe("http://127.0.0.1:1234", LmsModels, null),
            Prove = new Gatto.Core.Acquire.ProveOutcome(true, "OK", TimeSpan.FromSeconds(6)),
        });
        flow.StartPastOpening();
        flow.Answer(SetupFlow.ForkConnect);
        flow.Answer(SetupFlow.Yes);
        //with no reported window the context ask comes first and the model pick follows. reversing it would pin a screen the product cannot produce
        flow.Answer(SetupFlow.OfferedServerContext.ToString());
        flow.Answer(LmsModels[0]);
        return Settled(flow);
    }

    //no speed row here, nothing measured one, and the context row names the user. both facts come from what the run measured
    [Fact]
    public void THE_CHECK_ON_A_SERVER_THAT_REPORTED_NO_CONTEXT_RENDERS_AT_100() =>
        Golden.AssertEquals("s8", "answers-lms", 100,
            Rendered(CheckedAfterPickAndContext(100), 100, "answers-lms"));

    //at eighty columns the fact column must hold and the footer must stay one row.
    [Fact]
    public void THE_CHECK_FOLDS_AT_EIGHTY_COLUMNS() =>
        Golden.AssertEquals("s8", "answers-80", 80,
            Rendered(CheckedAfterPickAndContext(80), 80, "answers-80"));

    //reach the failed check screen through the same flow as the passing one, so the failure is what the check measured
    private static WizardScreen CheckFailed(Gatto.Core.Acquire.ProveOutcome outcome) =>
        Checked(new WizardProbes
        {
            Llama = @"C:\llama\llama-server.exe",
            Server = new ConnectProbe("http://127.0.0.1:1234", ["a-model"], 8192),
            Prove = outcome,
        });

    //the server answered during discovery, so the advice names a transport fact. try again is offered, restarting the server is the fix
    [Fact]
    public void THE_CHECK_THAT_WAS_REFUSED_RENDERS_AT_100() =>
        Golden.AssertEquals("s8", "refused", 100, Rendered(
            CheckFailed(new Gatto.Core.Acquire.ProveOutcome(
                false, "connection refused", TimeSpan.Zero)),
            100, "refused"));

    //the capability sentence appears only for this status, and try again is not offered since asking twice cannot teach a server to stream
    [Fact]
    public void THE_CHECK_THAT_DID_NOT_STREAM_RENDERS_AT_100() =>
        Golden.AssertEquals("s8", "no-stream", 100, Rendered(
            CheckFailed(new Gatto.Core.Acquire.ProveOutcome(
                false, "no frames", TimeSpan.Zero, ServerAnswered: 200)),
            100, "no-stream"));

    //a stream that closed empty is not a server that cannot stream: the screen says the reply was empty and offers to ask again
    [Fact]
    public void THE_CHECK_THAT_STREAMED_NOTHING_SAYS_SO()
    {
        var text = string.Join("\n", Rendered(
            CheckFailed(new Gatto.Core.Acquire.ProveOutcome(false, "closed", TimeSpan.Zero, ServerAnswered: 200, StreamedEmpty: true)),
            100, "streamed-empty"));

        Assert.Contains("The server answered, but its reply was empty.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("didn't stream", text, StringComparison.Ordinal);
        Assert.Contains("try again", text, StringComparison.OrdinalIgnoreCase);
    }

    //the server's own words are the actionable half, so the row shows them whole and wraps at the fact column.
    [Fact]
    public void THE_CHECK_THE_SERVER_REFUSED_RENDERS_AT_100() =>
        Golden.AssertEquals("s8", "answered-no", 100, Rendered(
            CheckFailed(new Gatto.Core.Acquire.ProveOutcome(
                false,
                "No models loaded. Please load a model in the developer page or use the "
                + "'lms load' command.",
                TimeSpan.Zero, ServerAnswered: 400)),
            100, "answered-no"));

    //one verdict covers both statuses, a 404 route is absent and a 405 refuses the verb. no try again, asking twice cannot change a property of the server
    [Theory]
    [InlineData(404, "no-chat-404")]
    [InlineData(405, "no-chat-405")]
    public void THE_CHECK_THAT_GOT_NO_CHAT_ENDPOINT_RENDERS_AT_100(int status, string screen) =>
        Golden.AssertEquals("s8", screen, 100, Rendered(
            CheckFailed(new Gatto.Core.Acquire.ProveOutcome(
                false, "not found", TimeSpan.Zero, ServerAnswered: status)),
            100, screen));

    //zero on the first call then atMs, so the wait drawn on the frame is the one the golden pins. a constant clock draws a purr that never started
    private static Func<long> Purring(long atMs)
    {
        var opened = false;
        return () => { if (!opened) { opened = true; return 0; } return atMs; };
    }

    //the facts shown are what is being asked, so a user recognizing none is on the wrong server. a settled render would pin a past-tense purr
    [Fact]
    public void THE_CHECK_WHILE_IT_RUNS_RENDERS_AT_100()
    {
        var flow = new SetupFlow(new WizardProbes
        {
            Llama = @"C:\llama\llama-server.exe",
            Server = new ConnectProbe("http://127.0.0.1:1234", [LmsModels[0]], 65536),
            Prove = new Gatto.Core.Acquire.ProveOutcome(true, "OK", TimeSpan.FromSeconds(4)),
        });
        flow.StartPastOpening();
        flow.Answer(SetupFlow.ForkConnect);
        flow.Answer(SetupFlow.Yes);
        var resumed = flow.ResumeAfterWrites(null);
        while (flow.NeedsWritesApplied) resumed = flow.ResumeAfterWrites(null);
        var watching = Assert.IsType<WizardScreen.Choice>(resumed);

        Assert.True(watching.Watching, "the check must be a watched screen while it runs");
        Golden.AssertEquals("s8", "first-reply", 100,
            WalkRender.Watching(watching, 100, tick: null, nowMs: Purring(4_000)).Rows);
    }
}
