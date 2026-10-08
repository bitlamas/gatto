using Gatto.Cli;
using Gatto.Cli.Setup;
using Gatto.Core.Tools;
using Gatto.Core.Hardware;
using Gatto.Core.Acquire;
using Gatto.Roles.Audition;
using Gatto.Tests.Fakes;
using Gatto.Terminal;

namespace Gatto.Tests.Setup.Tui;

//the done step, rendered against the s9 goldens. a golden no test renders goes stale instead of red, and this is the last page a user reads.
public class DoneRenderTests
{
    //a machine at the done step with consent unanswered, since Installed silences the install question and consent is the first screen

    //the found-engine machine these frames draw, since the found screen shows that exe and the summary names its folder. both screens describe one machine.
    private const string Gguf = "gemma-4-26B-A4B-it-UD-Q4_K_M.gguf";
    private const string Weights = @"C:\Users\you\.gatto\weights\gemma-4-26B-A4B-it";
    private const long Bytes = 18_146_586_624L;   //16.9 GiB, the size the frame shows

    //the engine-fetch fixture, pinned release, Vulkan build for an AMD card and the fetch folder. the summary's engine row names all three.
    private static readonly string Pin = Gatto.Roles.LlamaAssetSteering.PinnedRelease;
    private static readonly string EngineZip = $"llama-{Pin}-bin-win-vulkan-x64.zip";
    private static readonly string EngineInto = $@"C:\Users\you\.gatto\llama\{Pin}\";

    //the fixture fetches its model, since the frame says verified, and each parameter reaches one of the other frames
    internal static WizardProbes Machine(bool? consent, HardwareSnapshot? snapshot = null,
        bool fetchEngine = false, Gatto.Cli.InstallState install = Gatto.Cli.InstallState.Installed,
        AuditionCheck? audition = null, bool block = false, string? heldBy = null,
        ProveOutcome? prove = null)
    {
        var probes = new WizardProbes
        {
            Prove = prove ?? new WizardProbes().Prove,
            Snapshot = snapshot ?? new WizardProbes().Snapshot,
            //an engine on the machine sends the run down the found path, so the fetch path must have none
            Llama = fetchEngine ? null : @"C:\llama\llama-b11071-bin-win-vulkan-x64\llama-server.exe",
            Asset = fetchEngine
                ? new Gatto.Roles.LlamaAsset(EngineZip, null, "an AMD discrete card", "Vulkan")
                : new WizardProbes().Asset,
            Offer = fetchEngine
                ? new EngineFetchOffer(EngineInto, new EnginePair(
                    new EngineAsset(EngineZip, "https://example.invalid/" + EngineZip,
                        new string('a', 64), 214L * 1024 * 1024), null))
                : null,
            Fetch = fetchEngine
                ? _ => new Gatto.Cli.EngineFetch(EngineInto + "llama-server.exe", null)
                : null,
            Verify = fetchEngine
                ? _ => new ProbeResult(ProbeShape.ClassicServer, "d", Build: Pin)
                : null,
            ActiveFile = Gguf,
            Config = @"C:\Users\you\.gatto\gatto.json",
            Install = install,
            //another model owns the server, so the fake derives the prove outcome from it, which is why one fixture value is enough
            HeldBy = heldBy,
            InstalledDir = @"C:\Users\you\AppData\Local\Programs\gatto\",
            AuditionBlockUntilCancelled = block,
            UpdateAnswered = consent,
            //the model row's file, size and folder all come from one probe read, so its two lines cannot describe different files
            ActiveModel = (Weights + @"\" + Gguf, Bytes),
            HubOffer = new ModelFetchOffer("google/gemma-4-26B-A4B-it-GGUF", "gemma-4-26B-A4B-it",
                Weights, new HubQuant(Gguf, Bytes, "a1b2c3"), null),
            ModelResult = new HubFetchResult(HubFetchOutcome.Arrived),
            Rows = [ShelfRows.Of("google/gemma-4-26B-A4B-it-GGUF", "google",
                new HubQuant(Gguf, Bytes, "a1b2c3"), Gatto.Core.Models.FitRegime.FitsGpu,
                32768, false, null, 100, false)],
            Audition = audition ?? new AuditionCheck(
                AuditionOutcome.Passed,
                new AuditionFacts("Q4_K_M", "defaults", "template default", 31.4, 5, 5),
                new DateOnly(2026, 8, 24)),
        };

        //the fetched file appears only after the fetch, set it earlier and the run takes the local path. unset, the rescan offers the download a second time.
        var scans = 0;
        probes.OnScan = _ =>
        {
            if (++scans > 1)
                probes.Found = [new FoundModel(Weights + @"\" + Gguf, Bytes, null)];
        };
        return probes;
    }

    //drives a run to the done step and returns the first screen it shows. only the combinations the frames use are driven, the check answers were printed from a run.
    internal static (SetupFlow Flow, WizardScreen Screen) DoneStep(
        bool? consent, HardwareSnapshot? snapshot = null, bool fetchEngine = false,
        Gatto.Cli.InstallState install = Gatto.Cli.InstallState.Installed,
        AuditionCheck? audition = null, bool block = false, string[]? check = null,
        string? heldBy = null, string? installAnswer = null, bool inSession = false,
        ProveOutcome? prove = null)
    {
        var flow = new SetupFlow(
            Machine(consent, snapshot, fetchEngine, install, audition, block, heldBy, prove));
        //the in-session entry skips the engine segment, so fetchEngine is not reachable from here. the model fetch that sets verified is inside the model segment.
        if (inSession)
        {
            Assert.False(fetchEngine, "the in-session entry skips the engine segment");
            flow.StartAtModelSegment();
        }
        else if (fetchEngine)
        {
            //assert every screen before answering it, as FlowStart does, a helper that fired blindly would pass while driving a run nobody meant
            Assert.Equal(SetupFlow.ConsentKey, ScreenKey.Of(flow.StartPastOpening()));
            Assert.Equal(SetupFlow.FetchingKey, ScreenKey.Of(flow.Answer(SetupFlow.ConsentFetch)));
            Assert.Equal(SetupFlow.FetchedKey, ScreenKey.Of(flow.Answer(SetupFlow.Landed)));
            flow.Answer(SetupFlow.FetchedNext);
        }
        else flow.StartPastEngine();
        flow.Answer("0");
        flow.Answer(SetupFlow.ModelFetchNow);
        Assert.Equal(SetupFlow.ModelArrivedKey, ScreenKey.Of(flow.Answer(SetupFlow.Landed)));
        var s = flow.Answer(SetupFlow.ModelArrivedNext);
        while (flow.NeedsWritesApplied) s = flow.ResumeAfterWrites("gemma-4-26B-A4B-it");

        //a held server is never offered the check, and in a session that state is an ask rather than a refusal
        Assert.Equal(
            heldBy is null ? SetupFlow.AuditionOfferKey
            : inSession ? SetupFlow.InSessionCheckKey
            : SetupFlow.AuditionHeldKey,
            ScreenKey.Of(s));

        foreach (var answer in check ?? Passes)
        {
            //wait only before Landed, the answer that means the watch settled, since the stop case answers a check built never to finish
            if (answer == SetupFlow.Landed)
                Assert.True(SpinWait.SpinUntil(flow.PollWatch, TimeSpan.FromSeconds(10)),
                    "the check never finished");
            s = flow.Answer(answer);
            while (flow.NeedsWritesApplied) s = flow.ResumeAfterWrites(null);
        }

        //a run whose gatto row says not installed has one more screen than the rest. the row is computed from the answer, so install cannot be skipped.
        if (ScreenKey.Of(s) == SetupFlow.InstallKey)
        {
            //the answer is the subject on the two closing-sentence frames, and CompletionNextStep reports what the run said here
            s = flow.Answer(installAnswer ?? SetupFlow.InstallNo);
            while (flow.NeedsWritesApplied) s = flow.ResumeAfterWrites(null);
        }

        return (flow, s);
    }

    //the passing check's answers from the audition offer onwards, printed from a probe rather than derived from the flow
    internal static readonly string[] Passes =
        [SetupFlow.Yes, SetupFlow.Landed, SetupFlow.AuditionPassedNext];

    internal static readonly string[] Struggles =
        [SetupFlow.Yes, SetupFlow.Landed, SetupFlow.Anyway];

    internal static readonly string[] Skips =
        [SetupFlow.Skip, SetupFlow.Landed, SetupFlow.AnswersNext];

    //the check is stopped, the machine holds it open and the user leaves it. a fixture with no audition would show that row from a state no run produces.
    internal static readonly string[] Stops = [SetupFlow.Yes, SetupFlow.AuditionStop];

    //another model owns the server, so the check is never offered and the held screen stands instead. one answer is the evidence nothing was asked of this model.
    internal static readonly string[] Holds = [SetupFlow.AuditionHeldNext];

    private static readonly AuditionCheck Struggled = new(
        AuditionOutcome.Failed,
        new AuditionFacts("Q4_K_M", "defaults", "template default", 31.4, 1, 5),
        new DateOnly(2026, 8, 24));

    //one frame per verdict state and install form, the check row differs on every path. a row-level assertion would pass while the screen drifted.
    [Theory]
    [InlineData("summary-stopped", null, false)]
    [InlineData("summary-skipped", null, false)]
    [InlineData("summary-failed", null, true)]
    [InlineData("summary-dev", "DevBuild", false)]
    [InlineData("summary-elsewhere", "AlreadyInstalledElsewhere", false)]
    [InlineData("summary-held", null, false)]
    public void EACH_VERDICT_AND_INSTALL_STATE_RENDERS_ITS_OWN_FRAME(
        string frame, string? installName, bool struggled)
    {
        var install = installName is null
            ? (struggled ? Gatto.Cli.InstallState.NotInstalled : Gatto.Cli.InstallState.Installed)
            : Enum.Parse<Gatto.Cli.InstallState>(installName);

        var road = frame switch
        {
            "summary-held" => Holds,
            "summary-stopped" => Stops,
            "summary-skipped" => Skips,
            "summary-failed" => Struggles,
            _ => Passes,
        };

        //these frames draw the fetched engine, the pinned release, the backend and the folder gatto chose. the found case says two of the three.
        var (flow, _) = DoneStep(consent: true, fetchEngine: true, install: install,
            audition: struggled ? Struggled : null,
            block: frame == "summary-stopped", check: road,
            heldBy: frame == "summary-held" ? "minimax-m2.7-REAP-139B-A10B-Q4_K_S" : null);

        var summary = Assert.IsType<WizardScreen.Choice>(flow.Emitted[^1]);
        Assert.Equal(SetupFlow.SummaryKey, summary.Key);

        Golden.AssertEquals("s9", frame, 100,
            WalkRender.SettledFrame(summary, 100, SetupFlow.SummaryKey).Rows);
    }

    //the consent's shape is the subject, no cursor on either row, so the frame is the oracle rather than a flag off the record
    [Fact]
    public void THE_CONSENT_RENDERS_AT_100()
    {
        var (_, screen) = DoneStep(consent: null);
        var choice = Assert.IsType<WizardScreen.Choice>(screen);
        Assert.Equal(SetupFlow.UpdateKey, choice.Key);

        Golden.AssertEquals("s9", "updates", 100,
            WalkRender.SettledFrame(choice, 100, SetupFlow.UpdateKey).Rows);
    }

    //the done step's last screen, and the strip stamps the section of the screen's key, so the summary must name its own key
    [Fact]
    public void THE_SUMMARY_RENDERS_AT_100()
    {
        var (flow, _) = DoneStep(consent: true);
        var summary = Assert.IsType<WizardScreen.Choice>(flow.Emitted[^1]);
        Assert.Equal(SetupFlow.SummaryKey, summary.Key);

        //this fixture sets Llama, so the engine row draws the found form. a frame is a claim about one path, so a test must drive the path its frame draws.
        Golden.AssertEquals("s9", "summary-found", 100,
            WalkRender.SettledFrame(summary, 100, SetupFlow.SummaryKey).Rows);
    }

    //the summary on the run that fetched the engine, which names the pinned release, the backend and the folder gatto chose
    [Fact]
    public void THE_SUMMARY_RENDERS_THE_FETCHED_ENGINE_AT_100()
    {
        var (flow, _) = DoneStep(consent: true, fetchEngine: true);
        var summary = Assert.IsType<WizardScreen.Choice>(flow.Emitted[^1]);
        Assert.Equal(SetupFlow.SummaryKey, summary.Key);

        Golden.AssertEquals("s9", "summary", 100,
            WalkRender.SettledFrame(summary, 100, SetupFlow.SummaryKey).Rows);
    }

    //an unmanaged server that reports models and no context window, its own fixture since the summary draws three rows the llama path lacks
    private static SetupFlow ConnectWalk(int? reported = null, string? already = null)
    {
        var flow = new SetupFlow(new WizardProbes
        {
            //an endpoint the config already points at this address, so the fence writes a second one rather than reuse it
            ExistingEndpoint = already,
            Llama = @"C:\llama\llama-b11071-bin-win-vulkan-x64\llama-server.exe",
            Server = new Gatto.Core.Acquire.ConnectProbe(
                "http://127.0.0.1:1234", ["qwen/qwen3-30b-a3b-2507"], reported),
            Prove = new Gatto.Core.Acquire.ProveOutcome(true, "OK", TimeSpan.FromSeconds(6)),
            Install = Gatto.Cli.InstallState.Installed,
            Config = @"C:\Users\you\.gatto\gatto.json",
            UpdateAnswered = true,
        });

        flow.StartPastOpening();
        flow.Answer(SetupFlow.ForkConnect);
        flow.Answer(SetupFlow.Yes);

        //a server that reports its window is never asked for one, so this run answers no context ask. one model means no pick screen either.
        var s = reported is null
            ? flow.Answer(SetupFlow.OfferedServerContext.ToString())
            : flow.Emitted[^1];
        while (flow.NeedsWritesApplied) s = flow.ResumeAfterWrites(null);

        //the check is a watching screen, the first answer joins it and reports the verdict
        Assert.True(s is WizardScreen.Choice { Watching: true }, "the check never started");
        Assert.Equal(SetupFlow.ProveKey, ScreenKey.Of(flow.Answer(SetupFlow.OpenRepl)));

        s = flow.Answer(SetupFlow.OpenRepl);
        while (flow.NeedsWritesApplied) s = flow.ResumeAfterWrites(null);
        return flow;
    }

    //the connect summary draws three rows the llama path has none of, the address, the loaded model and its context, then the verdict
    [Fact]
    public void THE_CONNECT_SUMMARY_RENDERS_AT_100()
    {
        var flow = ConnectWalk();
        var summary = Assert.IsType<WizardScreen.Choice>(flow.Emitted[^1]);
        Assert.Equal(SetupFlow.SummaryKey, summary.Key);

        Golden.AssertEquals("s9", "summary-connect", 100,
            WalkRender.SettledFrame(summary, 100, SetupFlow.SummaryKey).Rows);
    }

    //both screens must say where the context number came from. compare the two rendered rows rather than the constant, on both arms of the ternary.
    [Theory]
    [InlineData(null, "your input")]
    [InlineData(65536, "reported by the server")]
    public void THE_CONTEXT_NOTE_IS_THE_SAME_ON_THE_CHECK_AND_ON_THE_SUMMARY(
        int? reported, string expected)
    {
        var flow = ConnectWalk(reported);

        var check = flow.Emitted
            .OfType<WizardScreen.Choice>()
            .Where(c => c.Key == SetupFlow.ProveKey && c.BodyRows is not null)
            .SelectMany(c => c.BodyRows!)
            .Last(r => r.Text.StartsWith("context", StringComparison.Ordinal));

        var summary = Assert.IsType<WizardScreen.Choice>(flow.Emitted[^1]).BodyRows!
            .Single(r => r.Text.StartsWith("context", StringComparison.Ordinal));

        //the note is what follows the number and its separator on each rendered row
        var mark = "\u00b7 ";
        var onCheck = check.Text[(check.Text.IndexOf(mark, StringComparison.Ordinal) + 2)..];
        var onSummary = summary.Text[(summary.Text.IndexOf(mark, StringComparison.Ordinal) + 2)..];

        Assert.Equal(onCheck, onSummary);
        Assert.StartsWith(expected, onCheck, StringComparison.Ordinal);
    }

    //when the config already names this address the fence writes a second endpoint as the default. the row says so in the user's words rather than in config keys.
    [Fact]
    public void A_SECOND_ENDPOINT_BESIDE_ONE_YOU_HAD_IS_SAID_ON_THE_SUMMARY()
    {
        var flow = ConnectWalk(already: "local");
        var summary = Assert.IsType<WizardScreen.Choice>(flow.Emitted[^1]);
        Assert.Equal(SetupFlow.SummaryKey, summary.Key);

        Golden.AssertEquals("s9", "summary-connect-beside-local", 100,
            WalkRender.SettledFrame(summary, 100, SetupFlow.SummaryKey).Rows);
    }

    //with no earlier endpoint the row is the address alone. both cases are tested, since a note that always rendered would match its own frame.
    [Fact]
    public void A_FIRST_ENDPOINT_SAYS_NOTHING_ABOUT_AN_EARLIER_SETUP()
    {
        var flow = ConnectWalk();
        var row = Assert.IsType<WizardScreen.Choice>(flow.Emitted[^1]).BodyRows!
            .Single(r => r.Text.StartsWith("server", StringComparison.Ordinal));

        Assert.DoesNotContain("earlier setup", row.Text, StringComparison.Ordinal);
    }

    //the record has no beside note, since its server row holds the address, the model and the context. test it only where an earlier endpoint exists.
    [Fact]
    public void THE_RECORD_LEAVES_THE_BESIDE_NOTE_TO_THE_SUMMARY()
    {
        var flow = ConnectWalk(already: "local");

        var summary = Assert.IsType<WizardScreen.Choice>(flow.Emitted[^1]).BodyRows!
            .Single(r => r.Text.StartsWith("server", StringComparison.Ordinal));
        var record = flow.RecordRows()
            .Single(r => r.Text.StartsWith("server", StringComparison.Ordinal));

        Assert.Contains("earlier setup", summary.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("earlier setup", record.Text, StringComparison.Ordinal);
    }

    //the record folds the server into one row where the summary spends three. the frame is diffed without its last line, the live countdown.
    [Fact]
    public void THE_CONNECT_RECORD_FOLDS_THE_SERVER_ROW()
    {
        var lines = Gatto.Cli.Setup.Tui.Epilogue.Lines(
            ConnectWalk().RecordRows(), "0.5.0", "1a2b3c4", new DateOnly(2026, 8, 25),
            closing: null, width: 100, glyphs: GlyphSet.Unicode);

        var drawn = File.ReadAllLines(Path.Combine(Golden.Dir, "s9-epilogue-connect-100.txt"));
        Assert.Equal(drawn[..^1], lines);
    }

    //the summary at eighty columns, on the run that fits and the run that wraps. the stopped verdict's command cannot break, so its row folds under the indent.
    [Theory]
    [InlineData("summary-80", null)]
    [InlineData("summary-stopped-80", "stopped")]
    public void THE_SUMMARY_FOLDS_AT_EIGHTY_COLUMNS(string frame, string? road)
    {
        var (flow, _) = DoneStep(consent: true, fetchEngine: true,
            block: road == "stopped", check: road == "stopped" ? Stops : Passes);

        var summary = Assert.IsType<WizardScreen.Choice>(flow.Emitted[^1]);
        Assert.Equal(SetupFlow.SummaryKey, summary.Key);

        Golden.AssertEquals("s9", frame, 80,
            WalkRender.SettledFrame(summary, 80, SetupFlow.SummaryKey).Rows);
    }

    //assert the gatto row against the drawn golden rather than a literal here, since a literal is the code agreeing with itself
    [Fact]
    public void THE_INSTALLED_GATTO_ROW_MATCHES_THE_ONE_THE_CORPUS_DRAWS()
    {
        var (flow, _) = DoneStep(consent: true);
        var summary = Assert.IsType<WizardScreen.Choice>(flow.Emitted[^1]);

        var drawn = File.ReadAllLines(Path.Combine(Golden.Dir, "s9-summary-100.txt"))
            .Single(l => l.TrimStart().StartsWith("gatto  ", StringComparison.Ordinal));
        var rendered = summary.BodyRows!
            .Single(r => r.Text.StartsWith("gatto ", StringComparison.Ordinal));

        Assert.Equal(drawn.Trim(), rendered.Text.Trim());
    }

    //a model this run did not verify draws no verified mark. the row must be present and unadorned, since absence alone would pass with no model row.
    [Fact]
    public void A_MODEL_THIS_WALK_DID_NOT_VERIFY_DOES_NOT_SAY_VERIFIED()
    {
        var probes = new WizardProbes
        {
            Llama = @"C:\llama\llama-b11071-bin-win-vulkan-x64\llama-server.exe",
            ActiveFile = Gguf,
            Install = Gatto.Cli.InstallState.Installed,
            UpdateAnswered = true,
            ActiveModel = (Weights + @"\" + Gguf, Bytes),
            Audition = new AuditionCheck(
                AuditionOutcome.Passed,
                new AuditionFacts("Q4_K_M", "defaults", "template default", 31.4, 5, 5),
                new DateOnly(2026, 8, 24)),
        };

        //the model was already on this machine, so nothing fetched it and nothing verified it
        var flow = new SetupFlow(probes);
        Assert.Equal(SetupFlow.FoundKey, ScreenKey.Of(flow.StartPastOpening()));
        flow.Answer(SetupFlow.FoundUse);
        var s = flow.ResumeAfterWrites("gemma-4-26B-A4B-it");
        while (flow.NeedsWritesApplied) s = flow.ResumeAfterWrites(null);
        Assert.Equal(SetupFlow.AuditionOfferKey, ScreenKey.Of(s));
        flow.Answer(SetupFlow.Yes);
        Assert.True(SpinWait.SpinUntil(flow.PollWatch, TimeSpan.FromSeconds(10)), "the check never finished");
        flow.Answer(SetupFlow.Landed);
        s = flow.Answer(SetupFlow.AuditionPassedNext);
        while (flow.NeedsWritesApplied) s = flow.ResumeAfterWrites(null);

        var summary = Assert.IsType<WizardScreen.Choice>(flow.Emitted[^1]);
        Assert.Equal(SetupFlow.SummaryKey, summary.Key);
        var model = summary.BodyRows!.Single(r => r.Text.StartsWith("model ", StringComparison.Ordinal));

        Assert.Contains(Gguf, model.Text, StringComparison.Ordinal);      //the row must be present, or the assertion below would pass on a run with no model row
        Assert.DoesNotContain("verified", model.Text, StringComparison.Ordinal);   //unadorned, the row ends after the size
    }

    //an answer the home already recorded is never asked again. assert the run reaches the done step, since one that fell over earlier would also lack the screen.
    [Fact]
    public void A_RECORDED_CONSENT_IS_NEVER_ASKED_AGAIN()
    {
        var (flow, screen) = DoneStep(consent: true);

        Assert.NotEqual(SetupFlow.UpdateKey, ScreenKey.Of(screen));
        Assert.DoesNotContain(flow.Emitted, s => ScreenKey.Of(s) == SetupFlow.UpdateKey);
        //the run did arrive, the summary is the done step's last screen
        Assert.Equal(SetupFlow.SummaryKey, ScreenKey.Of(screen));
    }

    //gatto.json and update_check stay dim, since an accented key invites an action the screen is not asking for
    [Fact]
    public void THE_CONFIG_KEY_IS_MENTIONED_AND_NEVER_ACCENTED()
    {
        var (_, screen) = DoneStep(consent: null);
        var choice = Assert.IsType<WizardScreen.Choice>(screen);
        var body = string.Join(" ", choice.BodyRows!.Select(r => r.Text));

        Assert.Contains("update_check", body, StringComparison.Ordinal);
        Assert.Contains("gatto.json", body, StringComparison.Ordinal);
        Assert.DoesNotContain(choice.BodyRows!, r =>
            r.Highlight is { } h && h.Any(x => x.Contains("update_check", StringComparison.Ordinal)
                                            || x.Contains("gatto.json", StringComparison.Ordinal)));
    }

    //the install question comes first, asserted at the one state where both screens appear, so a change swapping them fails here
    [Fact]
    public void THE_CONSENT_FOLLOWS_THE_INSTALL_QUESTION()
    {
        var flow = new SetupFlow(new WizardProbes
        {
            Llama = @"C:\llama\llama-b11071-bin-win-vulkan-x64\llama-server.exe",
            ActiveFile = "gemma-4-26B-A4B-it-UD-Q4_K_M.gguf",
            Install = Gatto.Cli.InstallState.NotInstalled,
            UpdateAnswered = null,
            Audition = new AuditionCheck(
                AuditionOutcome.Passed,
                new AuditionFacts("Q4_K_M", "defaults", "template default", 31.4, 5, 5),
                new DateOnly(2026, 8, 24)),
        });
        Assert.Equal(SetupFlow.FoundKey, ScreenKey.Of(flow.StartPastOpening()));
        flow.Answer(SetupFlow.FoundUse);
        var s = flow.ResumeAfterWrites("gemma-4-26B-A4B-it");
        while (flow.NeedsWritesApplied) s = flow.ResumeAfterWrites(null);
        flow.Answer(SetupFlow.Yes);
        Assert.True(SpinWait.SpinUntil(flow.PollWatch, TimeSpan.FromSeconds(10)), "the check never finished");
        flow.Answer(SetupFlow.Landed);
        s = flow.Answer(SetupFlow.AuditionPassedNext);
        while (flow.NeedsWritesApplied) s = flow.ResumeAfterWrites(null);

        Assert.Equal(SetupFlow.InstallKey, ScreenKey.Of(s));
        var next = flow.Answer(SetupFlow.InstallNo);
        while (flow.NeedsWritesApplied) next = flow.ResumeAfterWrites(null);

        Assert.Equal(SetupFlow.UpdateKey, ScreenKey.Of(next));
    }

    //the in-session done frames are gone, their s10 mocks stay on disk as a design record. a mock a test still renders is an oracle and follows the product copy.

}
