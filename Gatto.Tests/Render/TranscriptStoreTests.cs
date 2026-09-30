using Gatto.Core.Client;
using Gatto.Core.Home;
using Gatto.Core.Loop;
using Gatto.Core.Tools;
using Gatto.Repl.Render;
using Gatto.Terminal;
using Xunit;

namespace Gatto.Tests.Render;

public sealed class TranscriptStoreTests
{
    private static readonly Theme T = new(new TermCaps(true, true));

    private static string[] ChatLines(IEnumerable<string> lines) =>
        lines.Where(l => !l.Contains("\"event\"", System.StringComparison.Ordinal)).ToArray();

    //the full result comes from the tool record with no extra field, and a resumed session opens collapsed (collapse is a live affordance)
    [Fact]
    public void Tool_FullResult_round_trips_and_resumes_collapsed()
    {
        var convo = new Conversation("sys");
        convo.AddUser("read it");
        convo.Load(new[]
        {
            new ChatMessage("assistant", null, new[] { new ToolCall("c1", "read_file", "{\"path\":\"a.cs\"}") }),
            new ChatMessage("tool", "line-a\nline-b", ToolCallId: "c1"),
        });
        var file = TranscriptStore.BuildLines(new TranscriptModel("coder"), convo);

        var (_, rebuilt) = TranscriptStore.Rebuild(file, T, "coder", glyphs: GlyphSet.Unicode);
        var tool = Assert.Single(rebuilt.Items.OfType<ToolBlockItem>());
        Assert.Equal("line-a\nline-b", tool.FullResult);
        Assert.True(tool.Collapsed);
    }

    //a session killed mid tool run ends in a tool call with no result, skip that record so rebuild matches the balanced file
    [Fact]
    public void A_DANGLING_TOOL_CALL_REBUILDS_AS_IF_IT_WERE_NEVER_WRITTEN()
    {
        var balanced = new Conversation("sys");
        balanced.AddUser("read it");
        var torn = new Conversation("sys");
        torn.AddUser("read it");
        torn.Load(new[] { new ChatMessage("assistant", null, new[] { new ToolCall("c1", "read_file", "{\"path\":\"a.cs\"}") }) });

        var (tornConvo, tornModel) = TranscriptStore.Rebuild(
            TranscriptStore.BuildLines(new TranscriptModel("coder"), torn), T, "coder", glyphs: GlyphSet.Unicode);
        var (balancedConvo, balancedModel) = TranscriptStore.Rebuild(
            TranscriptStore.BuildLines(new TranscriptModel("coder"), balanced), T, "coder", glyphs: GlyphSet.Unicode);

        Assert.DoesNotContain(tornConvo.Messages, m => m.ToolCalls is { Count: > 0 });
        Assert.Equal(balancedConvo.Messages.Select(m => $"{m.Role}:{m.Content}"),
            tornConvo.Messages.Select(m => $"{m.Role}:{m.Content}"));
        Assert.Equal(balancedModel.Items.Select(i => i.GetType().Name), tornModel.Items.Select(i => i.GetType().Name));
    }

    //an event and a malformed line sit before the torn round, and rebuild skips the same record the torn classification names
    [Fact]
    public void A_TORN_ROUND_AFTER_AN_EVENT_AND_A_MALFORMED_LINE_IS_STILL_THE_ONE_SKIPPED()
    {
        var torn = new Conversation("sys");
        torn.AddUser("read it");
        torn.Load(new[] { new ChatMessage("assistant", null, new[] { new ToolCall("c1", "read_file", "{\"path\":\"a.cs\"}") }) });
        var model = new TranscriptModel("coder");
        model.Append(new SessionLeadItem("summary", After: null));
        var lines = TranscriptStore.BuildLines(model, torn).ToList();
        var lead = lines.Single(l => l.Contains("\"event\":\"lead\"", System.StringComparison.Ordinal));
        lines.Remove(lead);
        var assistantAt = lines.FindIndex(l => l.Contains("\"tool_calls\"", System.StringComparison.Ordinal));
        lines.InsertRange(assistantAt, new[] { lead, "{not json" });

        var (convo, rebuilt) = TranscriptStore.Rebuild(lines, T, "coder", glyphs: GlyphSet.Unicode);

        Assert.Equal(new[] { "system", "user" }, convo.Messages.Select(m => m.Role));
        Assert.Single(rebuilt.Items.OfType<SessionLeadItem>());
    }

    //the summary text is stripped, so zero banked stays silent, and only heading_found tells the two empty cases apart

    [Fact]
    public void CompactionLead_CarriesTheMemoryOutcome()
    {
        var model = new TranscriptModel("coder");
        model.Append(new SessionLeadItem("previous summary", After: null)
        {
            Memory = new Gatto.Core.Memory.MemoryBankOutcome(
                HeadingFound: true, Candidates: 3, Banked: 2, Dropped: 1),
        });

        var line = Assert.Single(TranscriptStore.BuildLines(model, new Conversation("sys")),
            l => l.Contains("\"event\":\"lead\"", System.StringComparison.Ordinal));

        Assert.Contains("\"heading_found\":true", line, System.StringComparison.Ordinal);
        Assert.Contains("\"candidates\":3", line, System.StringComparison.Ordinal);
        Assert.Contains("\"banked\":2", line, System.StringComparison.Ordinal);
        Assert.Contains("\"dropped\":1", line, System.StringComparison.Ordinal);
    }

    [Fact]
    public void CompactionLead_ZeroBankedWithAHeading_IsDistinguishableFromNoHeadingAtAll()
    {
        //both cases bank nothing and leave an identical store, so only the record tells them apart.
        static string Lead(bool headingFound)
        {
            var m = new TranscriptModel("coder");
            m.Append(new SessionLeadItem("s", After: null)
            {
                Memory = new Gatto.Core.Memory.MemoryBankOutcome(headingFound, 0, 0, 0),
            });
            return TranscriptStore.BuildLines(m, new Conversation("sys"))
                .Single(l => l.Contains("\"event\":\"lead\"", System.StringComparison.Ordinal));
        }

        Assert.Contains("\"heading_found\":true", Lead(true), System.StringComparison.Ordinal);
        Assert.Contains("\"heading_found\":false", Lead(false), System.StringComparison.Ordinal);
    }

    [Fact]
    public void LeadWithoutAnOutcome_WritesNoMemoryObject_AndStillRebuilds()
    {
        //the memory field is additive, so a lead with no outcome writes nothing extra and an old record still parses
        var model = new TranscriptModel("coder");
        model.Append(new SessionLeadItem("plain summary", After: null));

        var file = TranscriptStore.BuildLines(model, new Conversation("sys"));
        var lead = file.Single(l => l.Contains("\"event\":\"lead\"", System.StringComparison.Ordinal));
        Assert.DoesNotContain("memory", lead, System.StringComparison.Ordinal);

        var (_, rebuilt) = TranscriptStore.Rebuild(file, T, "coder", glyphs: GlyphSet.Unicode);
        Assert.Equal("plain summary", Assert.Single(rebuilt.Items.OfType<SessionLeadItem>()).Text);
    }

    [Fact]
    public void LeadWithAnOutcome_RebuildsAsAnOrdinaryLead_TelemetryIsWriteOnly()
    {
        var model = new TranscriptModel("coder");
        model.Append(new SessionLeadItem("summary text", After: null)
        {
            Memory = new Gatto.Core.Memory.MemoryBankOutcome(true, 5, 5, 0),
        });

        var (_, rebuilt) = TranscriptStore.Rebuild(
            TranscriptStore.BuildLines(model, new Conversation("sys")), T, "coder", glyphs: GlyphSet.Unicode);

        var lead = Assert.Single(rebuilt.Items.OfType<SessionLeadItem>());
        Assert.Equal("summary text", lead.Text);
        Assert.Null(lead.Memory);   //the memory outcome is write-only: the ui reads it back as absent
    }

    [Fact]
    public void Convergence_round_trip_context_fidelity_and_event_interleaving()
    {
        //the fixture mirrors a live session: a tool turn with live-only event items interleaved.
        var convo = new Conversation("you are a cat");
        convo.AddUser("hi");
        convo.AddAssistant("hello there", new[] { new ToolCall("call_1", "read_file", "{\"path\":\"a\"}") });
        convo.AddToolResult("call_1", new ToolResult("file body"));
        convo.AddAssistant("done");
        var toolMsg = convo.Messages[3];
        var lastAsst = convo.Messages[4];

        var model = new TranscriptModel("generalist");
        model.Append(new UserEchoItem(new[] { "hi" }));
        model.Append(new ReasoningItem(new[] { "let me think" }));                        //a reasoning item is not persisted and must drop out on replay.
        model.Append(new AssistantBlockItem(new[] { "hello there" }, "generalist"));
        model.Append(new ToolBlockItem("read_file", "a", ItemRender.ToolGloss(new ToolResult("file body"), T, glyphs: GlyphSet.Unicode), true, "generalist"));
        model.Append(new SystemLineItem("a warning", toolMsg));                           //this event anchors after the tool record.
        model.Append(new CommandEchoItem(new[] { "  allowed read_file" }, toolMsg));       //this echo shares the anchor of the tool record.
        model.Append(new AssistantBlockItem(new[] { "done" }, "generalist"));
        model.Append(new CompletionItem("purred for 3s", lastAsst));

        var file1 = TranscriptStore.BuildLines(model, convo);

        //the chat-record lines must be byte-identical to the lines a plain conversation save writes.
        Assert.Equal(convo.Messages.Select(m => SessionStore.ChatJson(m)).ToArray(), ChatLines(file1));

        //two events share one anchor between the tool record and the next assistant record, and their order comes from the anchor
        int toolIdx = System.Array.FindIndex(file1.ToArray(), l => l.Contains("\"tool_call_id\""));
        int sysIdx = System.Array.FindIndex(file1.ToArray(), l => l.Contains("\"event\":\"system\""));
        int cmdIdx = System.Array.FindIndex(file1.ToArray(), l => l.Contains("\"event\":\"command\""));
        int doneIdx = System.Array.FindIndex(file1.ToArray(), l => l.Contains("\"content\":\"done\""));
        Assert.True(toolIdx < sysIdx && sysIdx < cmdIdx && cmdIdx < doneIdx, "events must sit after the tool record, before the next block");

        var (convo2, model2) = TranscriptStore.Rebuild(file1, T, "generalist", glyphs: GlyphSet.Unicode);

        //the rebuilt conversation must equal the original records, so the context is restored.
        Assert.Equal(
            convo.Messages.Select(m => SessionStore.ChatJson(m)).ToArray(),
            convo2.Messages.Select(m => SessionStore.ChatJson(m)).ToArray());

        //rebuild derives the item types from the conversation and restores the events in order, with no reasoning item
        Assert.Collection(model2.Items,
            i => Assert.IsType<UserEchoItem>(i),
            i => Assert.IsType<AssistantBlockItem>(i),
            i => Assert.IsType<ToolBlockItem>(i),
            i => Assert.IsType<SystemLineItem>(i),
            i => Assert.IsType<CommandEchoItem>(i),
            i => Assert.IsType<AssistantBlockItem>(i),
            i => Assert.IsType<CompletionItem>(i));
        Assert.DoesNotContain(model2.Items, i => i is ReasoningItem);

        //serializing the rebuilt model and conversation must reproduce the same file.
        Assert.Equal(file1, TranscriptStore.BuildLines(model2, convo2));
    }

    //resume must re-seed the live conversation with the rebuilt message instances, or every non-leading event drops on the next save
    [Fact]
    public void A_resumed_session_saves_old_AND_new_events_because_the_convo_shares_the_rebuilt_instances()
    {
        var c1 = new Conversation("you are a cat");
        c1.AddUser("hi");
        c1.AddAssistant("hello");
        var m1 = new TranscriptModel("generalist");
        m1.Append(new CommandEchoItem(new[] { "banner" }, null));      //a leading echo has no anchor.
        m1.Append(new UserEchoItem(new[] { "hi" }));
        m1.Append(new AssistantBlockItem(new[] { "hello" }, "generalist"));
        m1.Append(new SystemLineItem("journal updated", c1.Messages[^1]));   //this warning anchors to the assistant record.
        var file1 = TranscriptStore.BuildLines(m1, c1);

        var (rebuiltConvo, rebuiltModel) = TranscriptStore.Rebuild(file1, T, "generalist", glyphs: GlyphSet.Unicode);
        var live = new Conversation("you are a cat (recomposed)");        //the resumed conversation keeps its own fresh system message.
        live.Load(rebuiltConvo.Messages.Where(m => m.Role != "system"));  //the re-seeded conversation must hold the very rebuilt message instances that the model's anchors point to.
        var liveModel = new TranscriptModel("generalist");
        liveModel.Append(new CommandEchoItem(new[] { "banner" }, null));
        liveModel.Append(new CommandEchoItem(new[] { "— resumed —" }, null));
        foreach (var it in rebuiltModel.Items) liveModel.Append(it);

        live.AddUser("again");
        live.AddAssistant("still here");
        liveModel.Append(new UserEchoItem(new[] { "again" }));
        liveModel.Append(new AssistantBlockItem(new[] { "still here" }, "generalist"));
        liveModel.Append(new SystemLineItem("second note", live.Messages[^1]));

        var file2 = TranscriptStore.BuildLines(liveModel, live);
        var joined = string.Join("\n", file2);

        //both event rows must persist, the older one only when the re-seed makes its anchor resolvable
        Assert.Contains("journal updated", joined, System.StringComparison.Ordinal);
        Assert.Contains("second note", joined, System.StringComparison.Ordinal);
        AssertRoundTripStable(liveModel, live);
    }

    //a transient command echo is session-local and must never serialize, or it piles up on every resume cycle
    [Fact]
    public void A_transient_command_echo_is_never_serialized()
    {
        var c = new Conversation("sys");
        c.AddUser("hi");
        var m = new TranscriptModel("generalist");
        m.Append(new CommandEchoItem(new[] { "the banner" }, null) { Transient = true });
        m.Append(new UserEchoItem(new[] { "hi" }));
        m.Append(new CommandEchoItem(new[] { "help table" }, null));   //this echo is not transient, so it must persist.

        var joined = string.Join("\n", TranscriptStore.BuildLines(m, c));
        Assert.DoesNotContain("the banner", joined, System.StringComparison.Ordinal);
        Assert.Contains("help table", joined, System.StringComparison.Ordinal);
    }

    //after compaction only the fresh lead serializes: the pre-compact transcript belongs to the previous file
    [Fact]
    public void After_reset_the_store_serializes_only_the_fresh_lead_not_the_old_transcript()
    {
        var old = new Conversation("sys");
        old.AddUser("old q");
        old.AddAssistant("old reply");
        var m = new TranscriptModel("generalist");
        m.Append(new UserEchoItem(new[] { "old q" }));
        m.Append(new AssistantBlockItem(new[] { "old reply" }, "generalist"));
        m.Append(new SystemLineItem("old note", old.Messages[^1]));

        m.Reset();
        Assert.Empty(m.Items);
        var fresh = new Conversation("fresh sys");
        m.Append(new SessionLeadItem("summary of the old session", After: null));

        var joined = string.Join("\n", TranscriptStore.BuildLines(m, fresh));
        Assert.Contains("summary of the old session", joined, System.StringComparison.Ordinal);
        Assert.DoesNotContain("old note", joined, System.StringComparison.Ordinal);   //the old event row must not survive into the new file.
        Assert.DoesNotContain("old reply", joined, System.StringComparison.Ordinal);  //the old chat belongs to the previous file, so it must not appear here.
    }

    //a compacted session has no chat turns, only the lead, and rebuild must still restore it so a resumed session can show the summary
    [Fact]
    public void A_compacted_session_with_no_chat_turns_rebuilds_its_SessionLead()
    {
        var convo = new Conversation("compacted system carrying the summary");
        var model = new TranscriptModel("generalist");
        model.Append(new SessionLeadItem("the previous session summary", After: null));
        var file = TranscriptStore.BuildLines(model, convo);

        var (_, rebuilt) = TranscriptStore.Rebuild(file, T, "generalist", glyphs: GlyphSet.Unicode);
        var lead = Assert.Single(rebuilt.Items.OfType<SessionLeadItem>());
        Assert.Equal("the previous session summary", lead.Text);
    }

    //the model must reset on a new-session request like compaction does, or the screen and the next save diverge
    [Fact]
    public void After_new_reset_the_store_keeps_only_the_post_reset_turn()
    {
        var old = new Conversation("sys");
        old.AddUser("old q");
        old.AddAssistant("old a");
        var m = new TranscriptModel("generalist");
        m.Append(new CommandEchoItem(new[] { "BANNER" }, null) { Transient = true });
        m.Append(new UserEchoItem(new[] { "old q" }));
        m.Append(new AssistantBlockItem(new[] { "old a" }, "generalist"));
        m.Append(new SystemLineItem("old note", old.Messages[^1]));

        m.Reset();
        var fresh = new Conversation("sys");
        m.Append(new CommandEchoItem(new[] { "BANNER" }, null) { Transient = true });
        fresh.AddUser("new q");
        fresh.AddAssistant("new a");
        m.Append(new UserEchoItem(new[] { "new q" }));
        m.Append(new AssistantBlockItem(new[] { "new a" }, "generalist"));
        m.Append(new SystemLineItem("new note", fresh.Messages[^1]));

        var joined = string.Join("\n", TranscriptStore.BuildLines(m, fresh));
        Assert.DoesNotContain("old q", joined, System.StringComparison.Ordinal);
        Assert.DoesNotContain("old note", joined, System.StringComparison.Ordinal);
        Assert.DoesNotContain("BANNER", joined, System.StringComparison.Ordinal);
        Assert.Contains("new q", joined, System.StringComparison.Ordinal);
        Assert.Contains("new note", joined, System.StringComparison.Ordinal);
    }

    //two generations of resume close the save and rebuild loop, and the transient banner must not pile up across cycles
    [Fact]
    public void Two_generation_resume_keeps_exactly_one_banner_and_all_real_events()
    {
        //one resume the way production does it: rebuild the file, then load a fresh conversation with the rebuilt message instances
        static (Conversation Convo, TranscriptModel Model) Resume(IReadOnlyList<string> file)
        {
            var (rebuiltConvo, rebuiltModel) = TranscriptStore.Rebuild(file, T, "generalist", glyphs: GlyphSet.Unicode);
            var live = new Conversation("fresh sys");
            live.Load(rebuiltConvo.Messages.Where(m => m.Role != "system"));
            var model = new TranscriptModel("generalist");
            model.Append(new CommandEchoItem(new[] { "BANNER" }, null) { Transient = true });
            model.Append(new CommandEchoItem(new[] { "— resumed —" }, null) { Transient = true });
            foreach (var it in rebuiltModel.Items) model.Append(it);
            return (live, model);
        }

        var c1 = new Conversation("sys");
        c1.AddUser("q1");
        c1.AddAssistant("a1");
        var m1 = new TranscriptModel("generalist");
        m1.Append(new CommandEchoItem(new[] { "BANNER" }, null) { Transient = true });
        m1.Append(new UserEchoItem(new[] { "q1" }));
        m1.Append(new AssistantBlockItem(new[] { "a1" }, "generalist"));
        m1.Append(new SystemLineItem("note one", c1.Messages[^1]));
        m1.Append(new CompletionItem("purred for 1s", c1.Messages[^1]));
        var file1 = TranscriptStore.BuildLines(m1, c1);

        var (c2, m2) = Resume(file1);
        c2.AddUser("q2");
        c2.AddAssistant("a2");
        m2.Append(new UserEchoItem(new[] { "q2" }));
        m2.Append(new AssistantBlockItem(new[] { "a2" }, "generalist"));
        m2.Append(new CommandEchoItem(new[] { "  allowed shell" }, c2.Messages[^1]));
        var file2 = TranscriptStore.BuildLines(m2, c2);

        var (_, m3) = Resume(file2);

        //exactly one banner survives, the fresh transient one, and no file persisted it
        Assert.Equal(1, m3.Items.Count(i => i is CommandEchoItem c && c.LogicalRows.Contains("BANNER")));
        //every real event from both generations must be present after the second resume.
        var text = string.Join("\n", m3.Items.SelectMany(i => i.Render(80, T, glyphs: GlyphSet.Unicode)));
        Assert.Contains("note one", text, System.StringComparison.Ordinal);
        Assert.Contains("purred for 1s", text, System.StringComparison.Ordinal);
        Assert.Contains("allowed shell", text, System.StringComparison.Ordinal);
        Assert.Contains("q1", text, System.StringComparison.Ordinal);
        Assert.Contains("q2", text, System.StringComparison.Ordinal);
        //the saved file must never grow a persisted banner across resumes.
        Assert.DoesNotContain("BANNER", string.Join("\n", file2), System.StringComparison.Ordinal);
    }

    //a mid-session role switch and a failed tool result must survive rebuild under another fallback role, so the stamped fields win
    [Fact]
    public void Rebuild_restores_the_stamped_role_and_tool_outcome_not_the_fallback_role()
    {
        var convo = new Conversation("sys");
        convo.SetGattoRole("generalist");
        convo.AddUser("hi");
        convo.AddAssistant("hello there");

        convo.SetGattoRole("coder");
        convo.AddAssistant("fixing it", new[] { new ToolCall("c1", "shell", "{}") });
        convo.AddToolResult("c1", new ToolResult("boom", IsError: true, Gloss: "shell failed"));

        var model = new TranscriptModel("generalist");
        model.Append(new UserEchoItem(new[] { "hi" }));
        model.Append(new AssistantBlockItem(new[] { "hello there" }, "generalist"));
        model.Append(new AssistantBlockItem(new[] { "fixing it" }, "coder"));
        model.Append(new ToolBlockItem("shell", "", ItemRender.ToolGloss(new ToolResult("boom", IsError: true, Gloss: "shell failed"), T, glyphs: GlyphSet.Unicode), true, "coder"));

        var file = TranscriptStore.BuildLines(model, convo);

        //rebuild uses a different fallback role, so only stamped roles can win.
        var (_, rebuilt) = TranscriptStore.Rebuild(file, T, "mind", glyphs: GlyphSet.Unicode);

        var blocks = rebuilt.Items.OfType<AssistantBlockItem>().ToArray();
        Assert.Equal(2, blocks.Length);
        Assert.Equal("generalist", blocks[0].Role);
        Assert.Equal("coder", blocks[1].Role);

        var tool = Assert.Single(rebuilt.Items.OfType<ToolBlockItem>());
        Assert.Equal("coder", tool.Role);
        Assert.Contains("✗", tool.Gloss, System.StringComparison.Ordinal);
        Assert.DoesNotContain("mind", string.Join("\n", rebuilt.Items.OfType<AssistantBlockItem>().Select(b => b.Role)), System.StringComparison.Ordinal);
    }

    //a legacy session without the role, error and gloss fields takes the fallback role, and its tool block renders the success gloss
    [Fact]
    public void Rebuild_falls_back_to_the_role_parameter_for_a_legacy_session_without_the_new_fields()
    {
        var path = System.IO.Path.Combine(
            System.AppContext.BaseDirectory, "Fixtures", "legacy-session-7d209d82.jsonl");
        var lines = System.IO.File.ReadAllLines(path);

        var (_, rebuilt) = TranscriptStore.Rebuild(lines, T, "coder", glyphs: GlyphSet.Unicode);

        //the legacy assistant record has empty content, so no prose block is appended and the tool block is the only item with a role
        Assert.Empty(rebuilt.Items.OfType<AssistantBlockItem>());
        var tool = Assert.Single(rebuilt.Items.OfType<ToolBlockItem>());
        Assert.Equal("coder", tool.Role);
        Assert.Contains("✓", tool.Gloss, System.StringComparison.Ordinal);
        Assert.DoesNotContain("✗", tool.Gloss, System.StringComparison.Ordinal);
    }

    //a cancelled tool is an error, so it rebuilds into the failure gloss branch.
    [Fact]
    public void A_cancelled_tool_rebuilds_to_the_error_gloss()
    {
        var convo = new Conversation("sys");
        convo.AddUser("go");
        convo.AddAssistant("running it", new[] { new ToolCall("c1", "shell", "{}") });
        convo.AddToolResult("c1", new ToolResult("cancelled", IsError: true));

        var model = new TranscriptModel("generalist");
        model.Append(new UserEchoItem(new[] { "go" }));
        model.Append(new AssistantBlockItem(new[] { "running it" }, "generalist"));
        model.Append(new ToolBlockItem("shell", "", ItemRender.ToolGloss(new ToolResult("cancelled", IsError: true), T, glyphs: GlyphSet.Unicode), true, "generalist"));

        var file = TranscriptStore.BuildLines(model, convo);
        var (_, rebuilt) = TranscriptStore.Rebuild(file, T, "generalist", glyphs: GlyphSet.Unicode);

        var tool = Assert.Single(rebuilt.Items.OfType<ToolBlockItem>());
        Assert.Contains("✗", tool.Gloss, System.StringComparison.Ordinal);
    }

    //rebuild then serializing must reproduce the same file, which every edge-case test here leans on
    private static void AssertRoundTripStable(TranscriptModel model, Conversation convo)
    {
        var f1 = TranscriptStore.BuildLines(model, convo);
        var (c2, m2) = TranscriptStore.Rebuild(f1, T, "generalist", glyphs: GlyphSet.Unicode);
        Assert.Equal(f1, TranscriptStore.BuildLines(m2, c2));
    }

    [Fact]
    public void Round_trip_is_stable_across_a_multi_tool_turn_and_multiline_events()
    {
        var c1 = new Conversation("sys");
        c1.AddUser("go");
        c1.AddAssistant("", new[] { new ToolCall("t1", "shell", "{}"), new ToolCall("t2", "shell", "{}") });
        c1.AddToolResult("t1", new ToolResult("r1"));
        c1.AddToolResult("t2", new ToolResult("r2"));
        var m1 = new TranscriptModel("generalist");
        m1.Append(new UserEchoItem(new[] { "go" }));
        m1.Append(new ToolBlockItem("shell", "", ItemRender.ToolGloss(new ToolResult("r1"), T, glyphs: GlyphSet.Unicode), true, "generalist"));
        m1.Append(new ToolBlockItem("shell", "", ItemRender.ToolGloss(new ToolResult("r2"), T, glyphs: GlyphSet.Unicode), true, "generalist"));
        m1.Append(new CompletionItem("purred for 4s", c1.Messages[^1]));
        AssertRoundTripStable(m1, c1);

        var c2 = new Conversation("sys");
        c2.AddUser("hi");
        var m2 = new TranscriptModel("generalist");
        m2.Append(new SessionLeadItem("previous session\nsummary line", c2.Messages[0]));   //an item anchored to the system message becomes leading.
        m2.Append(new UserEchoItem(new[] { "hi" }));
        m2.Append(new CommandEchoItem(new[] { "help row 1", "help row 2" }, c2.Messages[^1]));
        m2.Append(new SystemLineItem("warn\nwith a newline", c2.Messages[^1]));
        AssertRoundTripStable(m2, c2);
    }

    [Fact]
    public void Event_anchored_to_the_system_message_survives_ReplaceSystem_as_a_leading_record()
    {
        //an echo before the first turn anchors to the system message, and a later role or compaction swap must not drop the event
        var c = new Conversation("orig system");
        var sysMsg = c.Messages[0];
        var m = new TranscriptModel("generalist");
        m.Append(new CommandEchoItem(new[] { "help text" }, sysMsg));
        c.ReplaceSystem("new system", null);   //the role or compaction change replaces the system message instance.

        var lines = TranscriptStore.BuildLines(m, c).ToArray();
        var cmdIdx = System.Array.FindIndex(lines, l => l.Contains("\"event\":\"command\""));
        var sysIdx = System.Array.FindIndex(lines, l => l.Contains("\"role\":\"system\""));
        Assert.True(cmdIdx >= 0, "the system-anchored event must survive ReplaceSystem, not be dropped");
        Assert.True(cmdIdx < sysIdx, "it emits leading, before the system chat record");
        AssertRoundTripStable(m, c);
    }

    [Fact]
    public void Reference_identity_anchoring_places_an_event_after_the_second_of_two_identical_messages()
    {
        //chat messages compare equal by value, so an anchor on the second of two identical messages must match by reference
        var convo = new Conversation(null);
        convo.AddUser("yes");
        convo.AddUser("yes");
        var secondYes = convo.Messages[1];

        var model = new TranscriptModel("generalist");
        model.Append(new UserEchoItem(new[] { "yes" }));
        model.Append(new UserEchoItem(new[] { "yes" }));
        model.Append(new SystemLineItem("after the second", secondYes));

        var lines = TranscriptStore.BuildLines(model, convo).ToArray();
        int firstYes = System.Array.FindIndex(lines, l => l.Contains("\"content\":\"yes\""));
        int lastYes = System.Array.FindLastIndex(lines, l => l.Contains("\"content\":\"yes\""));
        int sys = System.Array.FindIndex(lines, l => l.Contains("\"system\""));
        Assert.True(sys == lastYes + 1, "the event sits after the SECOND identical message, not the first");
        Assert.True(sys > firstYes + 1);
    }

    [Fact]
    public void An_event_anchored_to_a_trimmed_message_is_dropped_not_misfiled()
    {
        //trimming a torn-turn suffix drops a message, and its anchored event must drop with it
        var convo = new Conversation(null);
        convo.AddUser("kept");
        var trimmed = new ChatMessage("user", "trimmed away");   //this message is not part of the conversation.

        var model = new TranscriptModel("generalist");
        model.Append(new UserEchoItem(new[] { "kept" }));
        model.Append(new SystemLineItem("orphaned", trimmed));

        var lines = TranscriptStore.BuildLines(model, convo);
        Assert.DoesNotContain(lines, l => l.Contains("\"system\""));   //the orphaned event drops, and saving must not throw.
    }

    //both save paths must stamp the same normalized working directory, or memory recall misses sessions saved by the other path
    [Fact]
    public void Save_stamps_the_normalized_cwd()
    {
        var home = Directory.CreateTempSubdirectory("gatto-home-").FullName;
        var cwd = Directory.CreateTempSubdirectory("gatto-cwd-").FullName;
        try
        {
            var store = new SessionStore(home, cwd);
            var convo = new Conversation("sys");
            convo.AddUser("hi");
            var model = new TranscriptModel("generalist");
            model.Append(new UserEchoItem(new[] { "hi" }));

            TranscriptStore.Save(model, convo, store);

            var first = File.ReadLines(store.CurrentPath!).First();
            using var doc = System.Text.Json.JsonDocument.Parse(first);
            Assert.Equal(Path.GetFullPath(cwd), doc.RootElement.GetProperty("cwd").GetString());
        }
        finally
        {
            Directory.Delete(home, true);
            Directory.Delete(cwd, true);
        }
    }

    [Fact]
    public void Rebuild_derives_clean_assistant_display_from_a_poisoned_record()
    {
        //the display derives from the chat record, so healing the record heals replay and no display-side strip is needed
        var lines = new[]
        {
            "{\"role\":\"system\",\"content\":\"s\",\"schema_version\":1}",
            "{\"role\":\"assistant\",\"content\":\"<|tool_calls_section_end|>clean prose\"}",
        };
        var (convo, model) = TranscriptStore.Rebuild(lines, T, "generalist", glyphs: GlyphSet.Unicode);
        Assert.Equal("clean prose", convo.Messages[^1].Content);
        var block = Assert.Single(model.Items.OfType<AssistantBlockItem>());
        Assert.Equal("clean prose", block.RawLines.Single());
    }

    //the write preview derives from the tool call arguments the record already holds, so replay rebuilds it with no new field
    [Fact]
    public void A_write_preview_survives_replay_without_being_persisted()
    {
        var content = string.Join("\n", Enumerable.Range(1, 12).Select(n => $"line {n}"));
        var argsJson = System.Text.Json.JsonSerializer.Serialize(new { path = "a.md", content });

        var convo = new Conversation("sys");
        convo.AddUser("write it");
        convo.Load(new[]
        {
            new ChatMessage("assistant", null, new[] { new ToolCall("c1", "write_file", argsJson) }),
            new ChatMessage("tool", "wrote 12 lines", ToolCallId: "c1"),
        });
        var file = TranscriptStore.BuildLines(new TranscriptModel("coder"), convo);

        //the file must hold no preview, only the arguments the preview derives from.
        Assert.DoesNotContain(file, l => l.Contains("\"preview\"", StringComparison.Ordinal));

        var (_, model) = TranscriptStore.Rebuild(file, T, "coder", glyphs: GlyphSet.Unicode);
        var tool = Assert.Single(model.Items.OfType<ToolBlockItem>());
        var rows = tool.Render(80, T, glyphs: GlyphSet.Unicode);

        //assert on the content: the block leads with a blank row, so a fixed offset would pin that blank
        var body = string.Join("\n", rows);
        for (var n = 1; n <= 5; n++)
            Assert.Contains($"line {n}", body, StringComparison.Ordinal);
        Assert.DoesNotContain("line 6", body, StringComparison.Ordinal);        //the window shows five rows, and the write opens on replay as it did live
        Assert.Contains(" 7 more", rows[^1], StringComparison.Ordinal);         //the footer counts twelve minus the five shown
        Assert.Equal(5, rows.Count(r => r.Contains("line ", StringComparison.Ordinal)));
    }

    //a failed write shows no preview on the live path or on replay, so a preview cannot appear only after resume
    [Fact]
    public void A_failed_write_shows_no_preview_on_replay_either()
    {
        var content = string.Join("\n", Enumerable.Range(1, 12).Select(n => $"line {n}"));
        var argsJson = System.Text.Json.JsonSerializer.Serialize(new { path = "a.md", content });

        var convo = new Conversation("sys");
        convo.AddUser("write it");
        convo.Load(new[]
        {
            new ChatMessage("assistant", null, new[] { new ToolCall("c1", "write_file", argsJson) }),
            new ChatMessage("tool", "denied: path outside project", ToolCallId: "c1", IsError: true),
        });

        var (_, model) = TranscriptStore.Rebuild(TranscriptStore.BuildLines(new TranscriptModel("coder"), convo), T, "coder", glyphs: GlyphSet.Unicode);
        var tool = Assert.Single(model.Items.OfType<ToolBlockItem>());
        var body = string.Join("\n", tool.Render(80, T, glyphs: GlyphSet.Unicode));

        Assert.DoesNotContain("line 1", body, StringComparison.Ordinal);
        Assert.DoesNotContain("+7 lines", body, StringComparison.Ordinal);
    }

    //a legacy prompt event record must be skipped, and an unknown event kind must not disturb the records around it
    [Fact]
    public void A_legacy_prompt_event_record_is_skipped_without_disturbing_the_session()
    {
        var convo = new Conversation("you are a cat");
        convo.AddUser("run it");
        convo.AddAssistant("done");

        //replay the exact record shape a legacy session holds, in its own file position
        var lines = new List<string>
        {
            SessionStore.ChatJson(convo.Messages[0]),
            SessionStore.ChatJson(convo.Messages[1]),
            SessionStore.ChatJson(convo.Messages[2]),
            "{\"event\":\"prompt\",\"rows\":[\"  allowed shell\",\"  ✓ allowed once\"]}",
        };

        var (rebuiltConvo, model) = TranscriptStore.Rebuild(lines, T, "generalist", glyphs: GlyphSet.Unicode);   //rebuild must not throw on the unknown record.

        //the record must produce no item of any kind, and no silently mistyped one.
        Assert.Equal(2, model.Items.Count);   //only the user echo and the assistant block remain.
        Assert.Contains(model.Items, i => i is UserEchoItem);
        Assert.Contains(model.Items, i => i is AssistantBlockItem);
        var rendered = string.Join("\n", model.Items.SelectMany(i => i.Render(80, T, glyphs: GlyphSet.Unicode)));
        Assert.DoesNotContain("allowed shell", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("allowed once", rendered, StringComparison.Ordinal);

        //the surrounding chat records stay intact and in order.
        Assert.Equal(new[] { "system", "user", "assistant" }, rebuiltConvo.Messages.Select(m => m.Role).ToArray());
        Assert.Equal("run it", rebuiltConvo.Messages[1].Content);
        Assert.Equal("done", rebuiltConvo.Messages[2].Content);
    }
}
