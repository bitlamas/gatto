using Gatto.Core.Client;
using Gatto.Core.Home;
using Gatto.Core.Loop;
using Gatto.Core.Memory;
using Gatto.Core.Tools;
using Gatto.Repl;
using Gatto.Tests.Fakes;

namespace Gatto.Tests;

//these tests fix which summary seams bank candidates and which only strip them. a user-chosen boundary banks, auto-compaction strips and writes nothing
public sealed class MemoryPiggybackWiringTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-piggyback-home-").FullName;
    private readonly string _cwd = Directory.CreateTempSubdirectory("gatto-piggyback-cwd-").FullName;

    public void Dispose()
    {
        Directory.Delete(_home, true);
        Directory.Delete(_cwd, true);
    }


    //build a summary the summarizer might really produce: real headings, then the piggyback section whose body is the given candidate lines.
    private static string SummaryWith(params string[] candidateLines) =>
        "Task / goal: wire the piggyback.\n\nImmediate next step: run the suite.\n\n"
        + MemoryPiggyback.Heading + ":\n" + string.Join('\n', candidateLines);

    //the check reads the composed index the model would see, rather than a filename, so it holds whatever slugs the minting rule chooses
    private string ComposedIndex() => MemoryIndex.ComposeUncapped(_cwd);

    private bool NothingBanked() => MemoryDir.FactFiles(_cwd).Count == 0;

    private sealed record Fixture(
        Gatto.Repl.Repl Repl, SessionStore Sessions, string OldSessionPath,
        List<SummaryInfo> SessionSummaries, List<IReadOnlyList<string>> BankCalls,
        FakeChatClient Client, Func<int> ComposeCalls);

    //the prompt of the last request the fixture's client received, which is the assembled summarize prompt in the suppression tests
    private static string LastPrompt(Fixture fx) => fx.Client.Requests[^1].Messages[^1].Content!;

    //the memory block is the last thing recomposeSystem emits, which the ordering test asserts. the heading must mirror RoleComposition's real heading verbatim
    private const string RecomposedWithMemory =
        "freshly recomposed system prompt\n\n"
        + "## Memory (project notes gatto keeps between sessions):\n- nginx binds 127.0.0.1";

    //every bank call is recorded, which proves auto-compaction never reaches the closure rather than merely writing nothing
    private Fixture NewFixture(string? scriptedSummary, bool memoryOn = true,
        string recomposed = "freshly recomposed system prompt", int indexBudget = 1000)
    {
        var sessions = new SessionStore(_home, _cwd);
        var convo = new Conversation("original system prompt");
        convo.AddUser("earlier turn");
        convo.AddAssistant("earlier reply");
        sessions.Save(convo);
        var oldPath = sessions.CurrentPath!;

        var client = new FakeChatClient();
        if (scriptedSummary is not null)
            client.EnqueueTurn(new StreamEvent.TextDelta(scriptedSummary), new StreamEvent.Finished("stop", null));
        else
            client.EnqueueTurn(new StreamEvent.Finished("stop", null));   //with no text the summarize call returns null.

        var hooks = new HookBus();
        var summaries = new List<SummaryInfo>();
        hooks.On(HookEvent.SessionSummary, p => { summaries.Add(p.Summary!); return Task.CompletedTask; });

        var bankCalls = new List<IReadOnlyList<string>>();
        var composeCalls = 0;
        //one nullable object holds both halves of what memory.enabled consents to. a memory-off session therefore cannot bank and cannot read, by construction
        PiggybackSeam? seam = memoryOn
            ? new(Bank: cands => { bankCalls.Add(cands); return MemoryPiggyback.BankCandidates(_cwd, cands); },
                  ComposeIndex: () => { composeCalls++; return MemoryIndex.Compose(_cwd, indexBudget); })
            : null;

        var loop = new AgentLoop(client, new ToolRegistry(), hooks, new TestToolContext(_cwd), "m");
        var repl = new Gatto.Repl.Repl(
            loop, convo, "generalist", "m", sessions, client, null, 1000, _cwd,
            () => Composed.Text(recomposed),
            () => (bool?)null,
            _ => new RoleSwitchResult(false, "", null, null, "unused"),
            hooks: hooks, piggyback: seam);

        return new Fixture(repl, sessions, oldPath, summaries, bankCalls, client, () => composeCalls);
    }

    [Fact]
    public async Task ManualCompact_FeedsTheFreshIndex_ToTheSummarizePrompt()
    {
        //a fact written mid-session is invisible to the live system message. compaction reads the index fresh from disk, so the summarizer does not duplicate it
        MemoryDir.Write(_cwd, "nginx-binding", "- nginx binds 127.0.0.1");
        var fx = NewFixture(SummaryWith("- something genuinely new"));

        await fx.Repl.HandleCompactAsync(new RecordingObserver(), CancellationToken.None);

        var prompt = LastPrompt(fx);
        Assert.Contains(Compactor.SuppressionPreamble, prompt);
        Assert.Contains("- nginx binds 127.0.0.1", prompt);   //the fact is fed fresh from disk rather than from the prefix
        Assert.Equal(1, fx.ComposeCalls());
    }

    [Fact]
    public async Task MemoryOff_ComposesNothing_AndFeedsNothing()
    {
        MemoryDir.Write(_cwd, "nginx-binding", "- nginx binds 127.0.0.1");
        var fx = NewFixture(SummaryWith("- something"), memoryOn: false);

        await fx.Repl.HandleCompactAsync(new RecordingObserver(), CancellationToken.None);

        //the consent property is that the disk was never read, which the compose count shows. a gate on the always-non-null bank lambda would fail this
        Assert.Equal(0, fx.ComposeCalls());
        Assert.DoesNotContain(Compactor.SuppressionPreamble, LastPrompt(fx));
        Assert.DoesNotContain("nginx binds", LastPrompt(fx));
    }

    [Fact]
    public async Task ParrotedFedLine_IsCaughtByTheExistingExactDedup()
    {
        //a small model that ignores the instruction repeats the line it was shown. a repeated line is an exact line-1 match, so the existing dedup removes it
        MemoryDir.Write(_cwd, "nginx-binding", "- nginx binds 127.0.0.1");
        var fx = NewFixture(SummaryWith("- nginx binds 127.0.0.1"));

        await fx.Repl.HandleCompactAsync(new RecordingObserver(), CancellationToken.None);

        Assert.Single(MemoryDir.FactFiles(_cwd));   //no c- twin file was created
    }

    [Fact]
    public async Task PartialSuppression_IsReportedOnTheCompactedLine()
    {
        //whole-line truncation at the cap means the unfed facts are exactly the ones still able to duplicate. that report must never be silent.
        MemoryDir.Write(_cwd, "fact-a", "- " + new string('a', 200));
        MemoryDir.Write(_cwd, "fact-b", "- " + new string('b', 200));
        var fx = NewFixture(SummaryWith("none"), indexBudget: 30);

        await fx.Repl.HandleCompactAsync(new RecordingObserver(), CancellationToken.None);

        Assert.Contains("not fed to the summarizer", fx.Repl.CompactedLine());
    }

    [Fact]
    public async Task ManualCompact_Banks_StripsFromRebuiltPrompt_AndReportsCounts()
    {
        //twelve candidates: ten bank, two drop over the cap, and both counts must reach the confirmation.
        var fx = NewFixture(SummaryWith(Enumerable.Range(1, 12).Select(i => $"- fact {i}").ToArray()));

        var ok = await fx.Repl.HandleCompactAsync(new RecordingObserver(), CancellationToken.None);

        Assert.True(ok);
        //ten c- fact files bank, and their line 1s compose into the index the model sees. provenance is the c- prefix plus the file's mtime, so no tag line exists
        var index = ComposedIndex();
        Assert.DoesNotContain("from compact", index);
        Assert.Contains("- fact 1", index);
        Assert.Contains("- fact 10", index);
        Assert.DoesNotContain("- fact 11", index);

        //nothing about the candidate section survives into the rebuilt prefix.
        var system = fx.Repl.ConversationForTest.Messages[0].Content!;
        Assert.Contains("Immediate next step: run the suite.", system);
        Assert.DoesNotContain(MemoryPiggyback.Heading, system, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("- fact 1", system);
        //the section also must not survive into the session_summary the journal listener receives
        var emitted = Assert.Single(fx.SessionSummaries);
        Assert.DoesNotContain(MemoryPiggyback.Heading, emitted.Text, StringComparison.OrdinalIgnoreCase);

        //both counts reach the user, in the existing compacted-line style.
        var line = fx.Repl.CompactedLine();
        Assert.Contains("10 memory candidates banked", line);
        Assert.Contains("2 dropped over cap", line);
    }

    //the line must report a transition as well as an after-figure, since a correct destination alone reads as broken when the drop is large

    [Fact]
    public async Task CompactedLine_reports_before_and_after_not_just_after()
    {
        var fx = NewFixture(SummaryWith("none"));
        var ctx = new Gatto.Repl.Term.CtxState { ContextWindow = 262_144 };
        ctx.RecordUsedTokens(35_750);                       //35,750 tokens of 262,144 is the 14% the footer showed before compaction.

        Assert.True(await fx.Repl.HandleCompactAsync(new RecordingObserver(), CancellationToken.None, ctx));

        var line = fx.Repl.CompactedLine(ctx);

        Assert.Contains("14%", line, StringComparison.Ordinal);   //the line still names where the usage started.
        Assert.Contains("→", line, StringComparison.Ordinal);    //the arrow proves the line reads as a transition.
        //the destination is still reported, now beside where the usage started
        Assert.Matches(@"ctx 14% → \d+%", line);
    }

    //manual /compact is a boundary between turns, so it drops every image. auto-compact fires mid-turn, so it keeps that turn's images

    [Fact]
    public async Task Auto_compaction_carries_the_current_turns_images_into_the_continuation()
    {
        var fx = NewFixture(SummaryWith("none"));
        var img = new ImageRef(@"C:\shots.png", 1234, "abc123", "image/png");
        fx.Repl.ConversationForTest.AddUser("what is wrong in this screenshot", new[] { img });

        var result = await fx.Repl.AutoCompactAsync(
            CompactionReason.Proactive, "what is wrong in this screenshot",
            new RecordingObserver(), CancellationToken.None);

        Assert.NotNull(result);
        var continuation = Assert.Single(result!.Messages);
        Assert.Equal("abc123", Assert.Single(continuation.Images!).Sha256);
    }

    [Fact]
    public async Task The_auto_compaction_line_never_claims_it_dropped_what_it_carried()
    {
        //a user-visible line counts what happened, so the dropped count is the total minus what is kept
        var fx = NewFixture(SummaryWith("none"));
        var old = new ImageRef(@"C:\shots\old.png", 10, "oldsha", "image/png");
        var current = new ImageRef(@"C:\shots
ow.png", 10, "nowsha", "image/png");
        fx.Repl.ConversationForTest.AddUser("earlier picture", new[] { old });
        fx.Repl.ConversationForTest.AddAssistant("noted");
        fx.Repl.ConversationForTest.AddUser("and this one", new[] { current });

        var observer = new RecordingObserver();
        await fx.Repl.AutoCompactAsync(
            CompactionReason.Proactive, "and this one", observer, CancellationToken.None);

        var line = Assert.Single(observer.Warnings, w => w.Contains("image", StringComparison.Ordinal));
        Assert.Contains("1 image dropped", line, StringComparison.Ordinal);    //the dropped one is the older image.
        Assert.Contains("1 carried forward", line, StringComparison.Ordinal);  //the kept one is the image of the turn in flight.
        Assert.DoesNotContain("2 images dropped", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Compaction_drops_images_and_the_line_counts_them()
    {
        //a compacted history is text, since the rebuild is a fresh message with no images. that is the one mechanism that bounds image growth
        var fx = NewFixture(SummaryWith("none"));
        var img = new ImageRef(@"C:\shots\a.png", 1234, "abc123", "image/png");
        fx.Repl.ConversationForTest.AddUser("look at this", new[] { img });

        Assert.True(await fx.Repl.HandleCompactAsync(new RecordingObserver(), CancellationToken.None));

        Assert.All(fx.Repl.ConversationForTest.Messages, m => Assert.Null(m.Images));
        Assert.Contains("1 image dropped", fx.Repl.CompactedLine(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompactedLine_without_a_ctx_state_still_reports_a_single_figure()
    {
        //the plain path has no ctx state, so it must not print a half-formed transition.
        var fx = NewFixture(SummaryWith("none"));

        Assert.True(await fx.Repl.HandleCompactAsync(new RecordingObserver(), CancellationToken.None));

        var line = fx.Repl.CompactedLine();
        Assert.DoesNotContain("→", line, StringComparison.Ordinal);
        Assert.Contains("compacted", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManualCompact_NoneSection_BanksNothing_ConfirmationUnchanged()
    {
        var fx = NewFixture(SummaryWith("none"));

        Assert.True(await fx.Repl.HandleCompactAsync(new RecordingObserver(), CancellationToken.None));

        Assert.True(NothingBanked());
        Assert.DoesNotContain("memory candidates banked", fx.Repl.CompactedLine());
        Assert.DoesNotContain(MemoryPiggyback.Heading,
            fx.Repl.ConversationForTest.Messages[0].Content!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AutoCompact_Strips_ButNeverBanks()
    {
        //an auto-compaction is an ambient boundary, so it must never reach the bank closure. the fixture records the call, so a permissive wiring fails here
        var fx = NewFixture(SummaryWith("- ambient fact one", "- ambient fact two"));

        var result = await fx.Repl.AutoCompactAsync(
            CompactionReason.Proactive, "keep going", new RecordingObserver(), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Empty(fx.BankCalls);                 //the closure was never invoked.
        Assert.True(NothingBanked());               //nothing was written by any other route either.
        //an ambient boundary must not read the operator's memory either, so a seam threaded through fails on the compose count
        Assert.Equal(0, fx.ComposeCalls());
        //stripping still happened: the scaffolding never reaches the rebuilt prefix.
        Assert.Contains("Immediate next step: run the suite.", result!.SystemText);
        Assert.DoesNotContain(MemoryPiggyback.Heading, result.SystemText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ambient fact", result.SystemText);
    }

    [Fact]
    public async Task ManualCompact_TheCompactBlockIsAppendedAfterTheMemoryBlock()
    {
        //resuming with --continue slices from the last Compactor.ContextMarker. a block appended after that marker is swallowed, so the compact block goes last
        var fx = NewFixture(SummaryWith("- a fact"), recomposed: RecomposedWithMemory);

        Assert.True(await fx.Repl.HandleCompactAsync(new RecordingObserver(), CancellationToken.None));

        var sys = fx.Repl.ConversationForTest.Messages[0].Content!;
        var memoryAt = sys.IndexOf("## Memory (", StringComparison.Ordinal);
        var markerAt = sys.LastIndexOf(Compactor.ContextMarker, StringComparison.Ordinal);
        Assert.True(memoryAt >= 0 && markerAt >= 0, "both blocks must be present for the ordering to mean anything");
        Assert.True(markerAt > memoryAt, "the compact block must be the FINAL append (--continue slices from it to end)");
    }

    [Fact]
    public async Task AutoCompact_TheCompactBlockIsAppendedAfterTheMemoryBlock()
    {
        //the same coupling on the auto-compaction append, whose rebuilt prefix a later --continue may graft from
        var fx = NewFixture(SummaryWith("- a fact"), recomposed: RecomposedWithMemory);

        var result = await fx.Repl.AutoCompactAsync(
            CompactionReason.Proactive, "keep going", new RecordingObserver(), CancellationToken.None);

        Assert.NotNull(result);
        var memoryAt = result!.SystemText.IndexOf("## Memory (", StringComparison.Ordinal);
        var markerAt = result.SystemText.LastIndexOf(Compactor.ContextMarker, StringComparison.Ordinal);
        Assert.True(memoryAt >= 0 && markerAt >= 0, "both blocks must be present for the ordering to mean anything");
        Assert.True(markerAt > memoryAt, "the compact block must be the FINAL append (--continue slices from it to end)");
    }

    [Fact]
    public async Task FailedSummary_LeavesIndexUntouched()
    {
        //a null summary leaves loop and Repl state untouched, memory included. a failed compact must not half-write
        var fx = NewFixture(scriptedSummary: null);

        Assert.False(await fx.Repl.HandleCompactAsync(new RecordingObserver(), CancellationToken.None));

        Assert.Empty(fx.BankCalls);
        Assert.True(NothingBanked());
        Assert.Equal(fx.OldSessionPath, fx.Sessions.CurrentPath);   //the session path did not rotate either.
    }

    [Fact]
    public async Task MemoryOff_StillStrips_WritesNothing_DoesNotThrow()
    {
        //memory off gives a null bank seam. stripping is mechanical and unconditional, since the section is scaffolding
        var fx = NewFixture(SummaryWith("- would-be fact"), memoryOn: false);

        Assert.True(await fx.Repl.HandleCompactAsync(new RecordingObserver(), CancellationToken.None));

        Assert.True(NothingBanked());
        var system = fx.Repl.ConversationForTest.Messages[0].Content!;
        Assert.DoesNotContain(MemoryPiggyback.Heading, system, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("would-be fact", system);
        Assert.DoesNotContain("memory candidates banked", fx.Repl.CompactedLine());
    }
}
