using System.Reflection;
using System.Text.RegularExpressions;
using Gatto.Cli.Setup;

namespace Gatto.Tests.Census;

//every screen the wizard constructs needs a row here, and classifying one doesn't prove a user can reach it
public class ScreenReachabilityCensusTests
{
    private enum Reach
    {
        //it is returned by the flow or shown directly by the runner, and the switch renders it.
        Rendered,

        //constructed but never reaching a face. each row is pinned with its reason.
        KnownHole,
    }

    //the note gives the reason for the row. the BackedBy field names a real test of face-side arrival, and the census fails when that test is renamed or deleted
    private sealed record Status(Reach Reach, string Note, string? BackedBy = null);

    private static readonly Dictionary<string, Status> Table = new(StringComparer.Ordinal)
    {
        //this is the wizard's first screen. its question must read correctly both live and in committed form.
        ["welcome"] = new(Reach.Rendered, "Choice → face.Choose"),
        //the step markers hold no question on purpose, so continues never fires on them. they reach the face by narration, since being returned ends the run
        ["done.summary"] = new(Reach.Rendered, "Choice → face.Choose; the done step's last screen"),
        ["step.model"] = new(Reach.Rendered, "Info → runner drains TakeNarration()"),
        ["step.first"] = new(Reach.Rendered, "Info → runner drains TakeNarration()"),
        //the machine section sits between the welcome and the install segment.
        ["machine"] = new(Reach.Rendered, "Choice → face.Choose (KeysOnly; Enter next, Esc leave)",
            BackedBy: "THE_UNIFIED_MACHINE_MATCHES_ITS_GOLDEN_AT_100"),
        ["install"] = new(Reach.Rendered, "Choice → face.Choose"),
        //this row shares install's path and handler, and is a separate key only for its different question
        ["install.update"] = new(Reach.Rendered, "Choice → face.Choose"),
        ["connect.retry"] = new(Reach.Rendered, "Choice → face.Choose"),
        ["connect.confirm"] = new(Reach.Rendered, "Choice → face.Choose"),
        ["connect.context"] = new(Reach.Rendered, "Ask → face.Ask",
            BackedBy: "THE_CONNECT_PATH_ASKS_FOR_A_CONTEXT_THE_SERVER_WOULD_NOT_REPORT"),
        ["connect.model"] = new(Reach.Rendered, "Choice → face.Choose"),
        ["connect.modelname"] = new(Reach.Rendered, "Ask → face.Ask"),
        //this arm shows when the machine already holds the pinned release
        ["llama.found"] = new(Reach.Rendered, "Choice → face.Choose (numbered; standing typed door)",
            BackedBy: "THE_EXACT_MATCH_ARM_MATCHES_ITS_GOLDEN"),
        //the runtime-failure arm shows when the file is right and the machine cannot load it. the sibling arm is an Ask that reuses llama.path, which has its own row
        ["llama.failed"] = new(Reach.Rendered, "Choice → face.Choose (one option; standing typed door)",
            BackedBy: "THE_RUNTIME_FAILURE_ARM_MATCHES_ITS_GOLDEN"),
        //a note rather than a screen, it goes to the run record. a consent opening with a found-problem line would invite a repair of what gatto replaces
        ["engine.skipped"] = new(Reach.Rendered, "Info → face.Show (the walk record)",
            BackedBy: "A_SWEPT_EXE_THAT_IS_NOT_THE_SERVER_REACHES_THE_CONSENT_AND_IS_NOTED"),
        ["llama.consent"] = new(Reach.Rendered, "Choice → face.Choose (two options; standing typed door)",
            BackedBy: "THE_FETCH_CONSENT_MATCHES_ITS_GOLDEN"),
        //the fetch screen watches gatto's own task and repaints on the watch's clock. esc is the only armed option.
        ["llama.fetching"] = new(Reach.Rendered,
            "Choice → face.Choose (watching; one option, Esc's; the tick rides beside the predicate)",
            BackedBy: "THE_FETCHING_SCREEN_MATCHES_ITS_GOLDEN"),
        //the offline arm. reached when the release read fails and when the fetch fails.
        ["llama.fallback"] = new(Reach.Rendered, "Choice → face.Choose (one option; standing typed door)",
            BackedBy: "THE_OFFLINE_ARM_MATCHES_ITS_GOLDEN"),
        //the fetched engine arrived and ran.
        ["llama.fetched"] = new(Reach.Rendered, "Choice → face.Choose (keys only)",
            BackedBy: "THE_FETCHED_SCREEN_MATCHES_ITS_GOLDEN"),
        ["llama.steer"] = new(Reach.Rendered, "Ask → face.Ask"),
        ["llama.path"] = new(Reach.Rendered, "Ask → face.Ask"),
        ["model.found"] = new(Reach.Rendered, "Choice → face.Choose"),
        ["model.search"] = new(Reach.Rendered, "Choice → face.Choose"),
        //the shelf while its scan and search run, a watching Choice the load's end resolves
        ["model.loading"] = new(Reach.Rendered, "Choice → face.Choose (watching)",
            BackedBy: "THE_FIRST_FRAME_IS_PAINTED_BEFORE_ANY_HUB_REQUEST_RETURNS"),
        ["model.publisher"] = new(Reach.Rendered, "Choice → face.Choose",
            BackedBy: "THE_PICKER_LISTS_THE_APPROVED_PUBLISHERS_AND_ANSWERS_A_PICK"),
        ["model.typedid"] = new(Reach.Rendered, "Ask → face.Ask"),
        ["model.scanpath"] = new(Reach.Rendered, "Ask → face.Ask"),
        //split from model.scanpath. three sites asked one key, and only this one knows the file is there, so each asking site needs its own key and row
        ["model.download"] = new(Reach.Rendered, "Choice → face.Choose",
            BackedBy: "PICKING_A_HUB_MODEL_DOES_NOT_END_THE_WIZARD"),
        //the in-app fetch screens share one path (pick, consent, watch, arrive). each needs a test that answers it, and rendering alone proves nothing
        ["model.consent"] = new(Reach.Rendered, "Choice → face.Choose",
            BackedBy: "WITH_AN_OFFER_THE_WALK_IS_THE_CONSENT"),
        ["model.fetching"] = new(Reach.Rendered, "Choice → face.Choose",
            BackedBy: "CONSENTING_FETCHES_THE_OFFER_THE_SCREEN_DESCRIBED"),
        //the paused screen is reached by pressing Space on a live fetch. all three of its keys must be answered by test input
        ["model.fetching.paused"] = new(Reach.Rendered, "Choice → face.Choose",
            BackedBy: "ESC_TWICE_ON_THE_PAUSED_SCREEN_DELETES_THE_PARTIAL"),
        ["model.arrived"] = new(Reach.Rendered, "Choice → face.Choose",
            BackedBy: "THE_ARRIVAL_RESCANS_THE_FOLDER_IT_FETCHED_INTO"),
        //the two ending screens are both answered by a test. the drop resumes the same offer, and the mismatch stays for the shelf
        ["model.dropped"] = new(Reach.Rendered, "Choice → face.Choose",
            BackedBy: "RESUMING_STARTS_THE_SAME_OFFER_AGAIN"),
        ["model.mismatch"] = new(Reach.Rendered, "Choice → face.Choose",
            BackedBy: "BOTH_ENDINGS_HAVE_A_ROAD_BACK_TO_THE_SHELF"),
        ["model.partial"] = new(Reach.Rendered, "Choice → face.Choose",
            BackedBy: "A_HALF_ARRIVED_SET_IS_REFUSED_AND_THE_SCREEN_SAYS_HOW_FAR_ALONG"),
        ["model.audition.offer"] = new(Reach.Rendered, "Choice → face.Choose"),
        //in a session a held server is an ask. a census row recording rendered cannot see that a screen is unanswerable, so answerability needs its own test
        ["model.audition.insession"] = new(Reach.Rendered, "Choice → face.Choose",
            BackedBy: "ESC_ANSWERS_THE_ASK_AND_THE_FOOTER_SAYS_WHAT_IT_DOES"),
        //the refusal screen shows when another model holds the server. both of its keys are pressed by test input.
        ["model.audition.held"] = new(Reach.Rendered, "Choice → face.Choose (keys only)",
            BackedBy: "THE_HELD_SCREEN_IS_ANSWERED_BOTH_WAYS"),
        //the filed arm is decided before any spawn. both options are numbered (both are real decisions) and both are pressed by test input
        ["model.audition.incomplete"] = new(Reach.Rendered, "Choice → face.Choose",
            BackedBy: "THE_FILED_ARM_IS_ANSWERED_BOTH_WAYS"),
        //on the engine arms only the third option advances, so the other two need their own test. the load ask watches while it asks and presses all three
        ["model.audition.stillloading"] = new(Reach.Rendered, "Choice → face.Choose (watching)",
            BackedBy: "THE_LOAD_ASK_ANSWERS_TO_ENTER_AND_TO_A_DIGIT"),
        ["model.audition.exited"] = new(Reach.Rendered, "Choice → face.Choose",
            BackedBy: "THE_ENGINE_ARMS_ARE_ANSWERED_EVERY_WAY"),
        ["model.audition.stopped"] = new(Reach.Rendered, "Choice → face.Choose",
            BackedBy: "THE_ENGINE_ARMS_ARE_ANSWERED_EVERY_WAY"),
        //the live screen renders three frames and answers two inputs, stop and a watch resolving. the surface takes input, so it needs a test that gives it input
        ["model.audition.running"] = new(Reach.Rendered, "Choice → face.Choose (keys only, watching)",
            BackedBy: "ESC_ESC_STOPS_THE_CHECK_AND_CARRIES_ON_UNCHECKED"),
        //the wait is the check's screen in another form, so it renders by the same path. the arrival is answered option by option to the wizard's end.
        ["model.audition.waiting"] = new(Reach.Rendered, "Choice → face.Choose (keys only, watching)",
            BackedBy: "STOPPING_THE_WAIT_CARRIES_ON_WITH_THE_MODEL_SET_UP"),
        ["model.audition.answers"] = new(Reach.Rendered, "Choice → face.Choose (keys only)",
            BackedBy: "THE_SKIP_ROAD_LOADS_ONCE_AND_THE_COMPLETION_REPORTS_THAT_LOAD"),
        ["model.audition.failed"] = new(Reach.Rendered, "Choice → face.Choose"),
        //this frame is a Choice with its own dwell, and both of its keys are pressed by test input
        ["model.audition.passed"] = new(Reach.Rendered, "Choice → face.Choose (keys only)",
            BackedBy: "THE_PASS_SCREENS_TWO_KEYS_GO_TWO_PLACES"),
        ["model.move"] = new(Reach.Rendered, "Choice → face.Choose"),
        ["model.move.path"] = new(Reach.Rendered, "Ask → face.Ask"),
        ["model.collision"] = new(Reach.Rendered, "Choice → face.Choose"),
        ["model.quantfromnow"] = new(Reach.Rendered, "Choice → face.Choose"),
        ["projector"] = new(Reach.Rendered, "Choice → face.Choose"),
        ["custom.build"] = new(Reach.Rendered, "Choice → face.Choose"),
        ["custom.build.path"] = new(Reach.Rendered, "Ask → face.Ask"),
        ["prove"] = new(Reach.Rendered, "Choice → face.Choose (the check's own result)"),
        ["update"] = new(Reach.Rendered, "Choice → face.Choose (the update consent)"),
        ["done"] = new(Reach.Rendered, "Terminal → face.End"),
        ["left"] = new(Reach.Rendered,
            "two sites, both rendered: SetupFlow.Leave's Terminal → face.End, and SetupRunner.Left's "
            + "Info → face.Show on the Esc path. One key, two ways out."),
        ["write.failed"] = new(Reach.Rendered, "SetupRunner shows it directly before exit 1"),
        ["move.incomplete"] = new(Reach.Rendered, "SetupRunner shows it directly"),

        //narration is for discoveries, a mark under the answer just given says one event twice. the segment.install row stays, it still emits a dev-build discovery
        ["segment.install"] = new(Reach.Rendered, "Info → runner drains TakeNarration() (dev build only)",
            BackedBy: "A_DEV_BUILDS_WALK_RENDERS_ITS_REFUSAL"),
        ["llama.ok"] = new(Reach.Rendered, "Info → runner drains TakeNarration()"),
        ["model.reused"] = new(Reach.Rendered, "Info → runner drains TakeNarration() (two sites)"),
        ["model.quantadded"] = new(Reach.Rendered, "Info → runner drains TakeNarration()"),
        ["model.none"] = new(Reach.Rendered, "Info → runner drains TakeNarration()"),

        //the returned screen is consumed by the write-pause branch before the switch can render it, so it is a return value
        ["writes.pending"] = new(Reach.KnownHole,
            "never shown BY CONSTRUCTION: `Answer` must return a screen, and the runner applies the "
            + "write pause before the switch can render one. Not narration — a return value."),

        //same class as above: returned, then consumed by the pause branch before the switch can render it.
        ["default.ready"] = new(Reach.KnownHole,
            "the Done-pause: armed so default_model reaches disk BEFORE the completion screen "
            + "claims it has. Returned and consumed by the pause branch, exactly like "
            + "model.ready, so it joins that class of return values rather than forming a new one",
            BackedBy: "THE_REUSE_WALK_LANDS_ITS_DEFAULT_MODEL"),
        ["consent.ready"] = new(Reach.KnownHole,
            "the consent flush: armed so the update consent's answer reaches disk before the terminal "
            + "renders. Same pause, same skip",
            BackedBy: "THE_LLAMA_WALK_LEAVES_A_COMPLETE_SETUP_ON_DISK"),

        ["back.refused"] = new(Reach.KnownHole,
            "belt-and-braces: reachable only if a face offers back on a screen that did not "
            + "advertise it, and the flow stamps AllowBack, so no walk arrives. It is a returned "
            + "Info, which the runner shows and then ENDS the run — correct only because nothing "
            + "reaches it; if a face ever makes it reachable, that exit is the finding"),
    };

    [Fact]
    public void EVERY_EMITTED_SCREEN_DECLARES_ITS_REACHABILITY()
    {
        var emitted = EmittedKeys();

        //an extraction that matched nothing would report no undeclared screens and pass. this floor makes a broken pattern loud.
        Assert.True(emitted.Count >= 30,
            $"extraction found only {emitted.Count} screen keys — the construction-site pattern is broken");

        var undeclared = emitted.Keys.Where(k => !Table.ContainsKey(k))
            .OrderBy(k => k, StringComparer.Ordinal).ToList();

        Assert.True(undeclared.Count == 0,
            $"extracted {emitted.Count} screen keys.\n"
            + "These screens are constructed but declare no reachability status. Add a row saying "
            + "whether a real walk reaches it — Rendered with the render path, or KnownHole with the "
            + "reason:\n  " + string.Join("\n  ", undeclared));

        var stale = Table.Keys.Where(k => !emitted.ContainsKey(k))
            .OrderBy(k => k, StringComparer.Ordinal).ToList();

        Assert.True(stale.Count == 0,
            "these rows name screens the code no longer constructs — delete the row with the "
            + "screen:\n  " + string.Join("\n  ", stale));
    }

    //the narration-only set must stay empty, such a key never leaves the flow. the floor is proved on the narration channel, an empty subject can't prove the pattern
    [Fact]
    public void NO_SCREEN_IS_NARRATION_ONLY_ANY_MORE()
    {
        //the extractor is proved against the channel that has keys, and an empty subject cannot prove it
        var narrated = ScreenKeys.Extract(ScreenKeys.NarrationChannel);
        Assert.True(narrated.Count >= 4,
            $"the narration-channel pattern matched only {narrated.Count} keys — the extractor is "
            + "broken, so the emptiness asserted below would be meaningless.");

        var orphaned = NarrationOnlyKeys();
        Assert.True(orphaned.Count == 0,
            "these keys are constructed ONLY into `_emitted`, which nothing drains, so they reach "
            + "no face however they are classified. That re-opens the hole of keys no face shows:\n  "
            + string.Join("\n  ", orphaned)
            + "\n\nPut them on the narration channel (`_narrate`, drained by SetupRunner) or delete "
            + "them. The rule decides which: gatto narrates its DISCOVERIES, never its CONFIRMATIONS.");
    }

    //a citation naming a test that no longer exists reads as evidence, which is worse than no citation.
    [Fact]
    public void EVERY_NAMED_BACKING_TEST_EXISTS()
    {
        //the check must match a declaration, a name in a comment is not a backing test. the pattern also accepts Task return types, async tests are legitimate citations
        var corpus = string.Join("\n", Directory
            .EnumerateFiles(Path.Combine(SourceTree.RepoRoot(), "Gatto.Tests"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !Path.GetFileName(f).Equals("ScreenReachabilityCensusTests.cs", StringComparison.Ordinal))
            .Select(f => SourceTree.WithoutComments(File.ReadAllText(f))));

        static bool Declared(string corpus, string name) =>
            System.Text.RegularExpressions.Regex.IsMatch(
                corpus, @"(?:void|Task(?:<[^>]+>)?)\s+" + System.Text.RegularExpressions.Regex.Escape(name) + @"\s*\(");

        var missing = Table.Values
            .Select(s => s.BackedBy)
            .Where(n => n is { Length: > 0 })
            .Distinct(StringComparer.Ordinal)
            .Where(n => !Declared(corpus, n!))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.True(missing.Count == 0,
            "the table cites backing tests that do not exist as DECLARED METHODS — a mention in a "
            + "comment is not a backing test:\n  "
            + string.Join("\n  ", missing!));
    }

    //the extraction lives in ScreenKeys, shared by both censuses. two copies of one regex let two censuses disagree about which screens exist
    private static IReadOnlyDictionary<string, int> EmittedKeys() => ScreenKeys.Extract(ScreenKeys.Construction);

    private static IReadOnlySet<string> NarrationOnlyKeys()
    {
        var narration = ScreenKeys.Extract(ScreenKeys.Narration);
        var all = ScreenKeys.Extract(ScreenKeys.Construction);

        //only keys whose every construction site is a narration one. a key with a returned site is reachable, and the returned site is what counts.
        return narration.Where(n => all[n.Key] == n.Value)
            .Select(n => n.Key).ToHashSet(StringComparer.Ordinal);
    }
}
