using Gatto.Core.Home;
using Gatto.Core.Loop;
using Xunit;

namespace Gatto.Tests;

public sealed class ResumePlanTests
{
    private static BaselineSources Sources() => new("2026-09-29", null, new List<ContextFileMark>(), new List<PolicyMark>(), "r", "m");

    private static SessionBaseline B(string model = "test-model", string role = "generalist", string history = "all",
        string? body = "{\"a\":1}", string level = "medium", params (string Name, string Sha)[] tools) =>
        new(1, role, model, history, new ThinkingMark(level, body),
            (tools.Length == 0 ? new[] { ("read_file", "1"), ("shell", "2") } : tools).Select(t => new ToolMark(t.Item1, t.Item2)).ToList(), Sources());

    [Fact]
    public void Every_value_that_agrees_is_a_replay()
    {
        Assert.Equal(ResumeDecision.Replay, ResumePlan.Decide(B(), B(), folderExists: true, effortFlagGiven: true));
    }

    [Fact]
    public void Each_row_resets_when_its_value_alone_disagrees()
    {
        Assert.Equal(ResumeReset.Folder, ResumePlan.Decide(B(), B(), folderExists: false, effortFlagGiven: false).Reset);
        Assert.Equal(ResumeReset.OlderSession, ResumePlan.Decide(null, B(), true, false).Reset);
        Assert.Equal(ResumeReset.Model, ResumePlan.Decide(B(model: "other"), B(), true, false).Reset);
        Assert.Equal(ResumeReset.Role, ResumePlan.Decide(B(role: "coder"), B(), true, false).Reset);
        Assert.Equal(ResumeReset.Tools, ResumePlan.Decide(B(tools: new[] { ("read_file", "1") }), B(), true, false).Reset);
        Assert.Equal(ResumeReset.ReasoningHistory, ResumePlan.Decide(B(history: "none"), B(), true, false).Reset);
        Assert.Equal(ResumeReset.Effort, ResumePlan.Decide(B(body: "{\"a\":2}"), B(), true, effortFlagGiven: true).Reset);
    }

    [Fact]
    public void The_first_row_that_holds_wins()
    {
        Assert.Equal(ResumeReset.Model, ResumePlan.Decide(B(model: "other", tools: new[] { ("x", "9") }), B(), true, false).Reset);
        Assert.Equal(ResumeReset.Folder, ResumePlan.Decide(null, B(), false, false).Reset);
    }

    [Fact]
    public void No_effort_flag_and_another_level_is_a_replay()
    {
        Assert.Equal(ResumeDecision.Replay, ResumePlan.Decide(B(body: "{\"a\":2}", level: "high"), B(), true, effortFlagGiven: false));
    }

    [Fact]
    public void A_tool_change_names_what_moved()
    {
        var stored = B(tools: new[] { ("read_file", "1"), ("shell", "2"), ("grep", "3") });
        var launch = B(tools: new[] { ("read_file", "1"), ("shell", "9"), ("web_fetch", "4") });
        Assert.Equal(new[] { "added web_fetch", "removed grep", "changed shell" }, ResumePlan.Decide(stored, launch, true, false).ToolChanges);
        var reordered = ResumePlan.Decide(B(tools: new[] { ("shell", "2"), ("read_file", "1") }), B(), true, false);
        Assert.Equal(ResumeReset.Tools, reordered.Reset);
        Assert.Equal(new[] { "reordered" }, reordered.ToolChanges);
    }

    //a stored baseline is data from disk, so a repeated key in any of its lists must not end the resume, and the last entry wins
    [Fact]
    public void A_repeated_key_in_a_stored_list_does_not_throw()
    {
        var stored = B(tools: new[] { ("read_file", "1"), ("read_file", "1"), ("shell", "2") });
        Assert.Equal(ResumeReset.Tools, ResumePlan.Decide(stored, B(), true, false).Reset);
        var files = new BaselineSources(null, null, new List<ContextFileMark> { new("a.md", "1"), new("a.md", "2") }, new List<PolicyMark>(), "r", "m");
        var launchFiles = files with { ContextFiles = new List<ContextFileMark> { new("a.md", "2") } };
        Assert.True(ResumePlan.Delta(files, launchFiles).IsEmpty);
        var policy = files with { Policy = new List<PolicyMark> { new("web_search", "old"), new("web_search", "new") }, ContextFiles = new List<ContextFileMark>() };
        var launchPolicy = policy with { Policy = new List<PolicyMark> { new("web_search", "new") } };
        Assert.True(ResumePlan.Delta(policy, launchPolicy).IsEmpty);
        Assert.Null(Record.Exception(() => ResumePlan.Delta(launchFiles, files)));
    }
}
