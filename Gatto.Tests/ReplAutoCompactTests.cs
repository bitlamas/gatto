using Gatto.Core.Client;
using Gatto.Core.Home;
using Gatto.Core.Loop;
using Gatto.Core.Tools;
using Gatto.Repl;
using Gatto.Tests.Fakes;

namespace Gatto.Tests;

//the auto-compact trigger is armed only by the interactive entry, so AutoCompactAsync is called directly
public class ReplAutoCompactTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-autocompact-home-").FullName;
    private readonly string _cwd = Directory.CreateTempSubdirectory("gatto-autocompact-cwd-").FullName;

    public void Dispose()
    {
        Directory.Delete(_home, true);
        Directory.Delete(_cwd, true);
    }

    private sealed record Fixture(
        Gatto.Repl.Repl Repl, SessionStore Sessions, string OldSessionPath, List<SummaryInfo> SessionSummaries);

    //the fixture wires a fake client that answers the summarize turn with the scripted text, plus a saved session and a hook listener
    private Fixture NewFixture(string? scriptedSummary)
    {
        var sessions = new SessionStore(_home, _cwd);
        var convo = new Conversation("original system prompt");
        convo.AddUser("earlier turn");
        convo.AddAssistant("earlier reply");
        sessions.Save(convo);                 //the save sets CurrentPath and writes the real old session file
        var oldPath = sessions.CurrentPath!;
        Assert.True(File.Exists(oldPath));

        var client = new FakeChatClient();
        if (scriptedSummary is not null)
            client.EnqueueTurn(new StreamEvent.TextDelta(scriptedSummary), new StreamEvent.Finished("stop", null));
        else
            client.EnqueueTurn(new StreamEvent.Finished("stop", null));   //a turn with no text makes SummarizeAsync return null

        var hooks = new HookBus();
        var sessionSummaries = new List<SummaryInfo>();
        hooks.On(HookEvent.SessionSummary, p => { sessionSummaries.Add(p.Summary!); return Task.CompletedTask; });

        var loop = new AgentLoop(client, new ToolRegistry(), hooks, new TestToolContext(_cwd), "m");

        var repl = new Gatto.Repl.Repl(
            loop, convo, "generalist", "m", sessions, client, null, 1000, _cwd,
            () => Composed.Text("freshly recomposed system prompt"),
            () => (bool?)null,
            _ => new RoleSwitchResult(false, "", null, null, "unused"),
            hooks: hooks);

        return new Fixture(repl, sessions, oldPath, sessionSummaries);
    }

    [Fact]
    public void AN_ANCESTOR_SAYING_FALSE_LEAVES_AUTO_COMPACT_UNARMED()
    {
        var deep = Directory.CreateDirectory(Path.Combine(_cwd, "src", "sub")).FullName;
        File.WriteAllText(Path.Combine(_cwd, ".gatto.json"), """{"auto_compact":false}""");

        Assert.Null(Gatto.Repl.Repl.ArmedAutoCompactAt(0.8, deep));
    }

    [Fact]
    public void NO_PROJECT_FILE_LEAVES_THE_CONFIGURED_THRESHOLD_UNCHANGED()
    {
        //an unwritten veto must not quietly become one, so absence keeps the configured threshold.
        Assert.Equal(0.8, Gatto.Repl.Repl.ArmedAutoCompactAt(0.8, _cwd));
    }

    [Fact]
    public void A_PROJECT_SAYING_TRUE_CANNOT_ARM_WHAT_THE_HOME_TURNED_OFF()
    {
        //a project file can only disarm auto-compact, so another repo may give gatto less to do and not more
        File.WriteAllText(Path.Combine(_cwd, ".gatto.json"), """{"auto_compact":true}""");

        Assert.Null(Gatto.Repl.Repl.ArmedAutoCompactAt(null, _cwd));
    }

    //the arming site has no unit-test seam, so this pins source text and fails if the threshold gets inlined
    [Fact]
    public void THE_ARMING_SITE_USES_THE_PROJECT_VETO()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "Gatto", "Repl", "Repl.cs"));

        Assert.Contains("var armedAt = ArmedAutoCompactAt(autoCompact, cwd);", source, StringComparison.Ordinal);
        Assert.Contains(
            "loop.EnableAutoCompact(armedAt is null ? null : new AutoCompactionHandler(this), _usageState, armedAt);",
            source, StringComparison.Ordinal);
    }

    //search upward for the folder holding Gatto.sln and fail when none exists
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Gatto.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public async Task AutoCompactAsync_Success_RotatesSession_BuildsContinuationConvo_SkipsJournal()
    {
        var fx = NewFixture(scriptedSummary: "SUMMARY TEXT\n\nImmediate next step: go on.");

        var result = await fx.Repl.AutoCompactAsync(
            CompactionReason.Proactive, "explore the codebase", new RecordingObserver(), CancellationToken.None);

        Assert.NotNull(result);
        //the rebuilt user message keeps the prompt verbatim as background, with the directive last. that shape comes from Compactor.BuildContinuationPrompt.
        var user = Assert.Single(result!.Messages);
        Assert.Equal("user", user.Role);
        Assert.Equal(Compactor.BuildContinuationPrompt("explore the codebase"), user.Content);
        Assert.Contains("explore the codebase", user.Content);
        Assert.EndsWith(Compactor.ContinuationDirective, user.Content, StringComparison.Ordinal);

        //the system text combines a fresh recompose with the BuildContext block that holds the summary
        Assert.Contains(Compactor.ContextMarker, result.SystemText);
        Assert.Contains("SUMMARY TEXT", result.SystemText);
        Assert.Contains("freshly recomposed system prompt", result.SystemText);

        //rotation runs StartNew, so CurrentPath is null until the next save. the old transcript stays on disk.
        Assert.NotEqual(fx.OldSessionPath, fx.Sessions.CurrentPath);
        Assert.True(File.Exists(fx.OldSessionPath));

        //auto-compact deliberately emits no session_summary, unlike manual /compact
        Assert.Empty(fx.SessionSummaries);
    }

    [Fact]
    public async Task AutoCompactAsync_SummarizeFails_ReturnsNull_NothingRotated()
    {
        var fx = NewFixture(scriptedSummary: null);

        var result = await fx.Repl.AutoCompactAsync(
            CompactionReason.Overflow, "task", new RecordingObserver(), CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(fx.OldSessionPath, fx.Sessions.CurrentPath);   //rotation is atomic, so a failed summarize leaves the old session current.
        Assert.Empty(fx.SessionSummaries);
    }
}
