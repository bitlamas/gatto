using System.Text.RegularExpressions;
using Gatto.Core.Home;
using Gatto.Core.Loop;
using Gatto.Tests.Fakes;
using Gatto.Terminal;
using Xunit;

namespace Gatto.Tests;

public sealed class ResumeLineTests
{
    private static ResumeDelta Delta(string? date = null, int added = 0, int removed = 0, string[]? files = null,
        bool policy = false, bool role = false, bool model = false) =>
        new(date, Enumerable.Range(0, added).Select(i => $"- added {i}").ToList(), Enumerable.Range(0, removed).Select(i => $"- removed {i}").ToList(),
            (files ?? Array.Empty<string>()).ToList(), new List<string>(), new List<string>(),
            policy ? new List<PolicyMark> { new("web_search", "x") } : new List<PolicyMark>(), new List<string>(), role, model);

    private static string? Line(ResumeReset? reset, ResumeDelta? delta = null, params string[] tools) =>
        ResumeUpdate.Line(new ResumeDecision(reset, tools), delta, @"C:\gone", @"C:\here");

    [Fact]
    public void Each_decision_names_its_reason()
    {
        const string again = ": the conversation is sent again in full";
        Assert.Equal("the tool list changed (added web_fetch)" + again, Line(ResumeReset.Tools, null, "added web_fetch"));
        Assert.Equal("the model changed" + again, Line(ResumeReset.Model));
        Assert.Equal("the role changed" + again, Line(ResumeReset.Role));
        Assert.Equal("the effort changed" + again, Line(ResumeReset.Effort));
        Assert.Equal("the reasoning history setting changed" + again, Line(ResumeReset.ReasoningHistory));
        Assert.Equal("this session predates the resume baseline: the conversation is sent again in full, once", Line(ResumeReset.OlderSession));
        Assert.Equal(@"the session's folder C:\gone no longer exists: staying in C:\here, the conversation is sent again in full", Line(ResumeReset.Folder));
        Assert.Equal(NoFolder, ResumeUpdate.Line(new ResumeDecision(ResumeReset.Folder, Array.Empty<string>()), null, null, @"C:\here"));
    }

    private const string NoFolder = @"this session does not record its folder: staying in C:\here, the conversation is sent again in full";

    [Fact]
    public void A_carried_delta_names_what_it_carried_in_order()
    {
        Assert.Equal("told the model what changed: the date, 2 memory notes, GATTO.md",
            Line(null, Delta(date: "2026-09-29", added: 1, removed: 1, files: new[] { @"C:\proj\GATTO.md" })));
        Assert.Equal("told the model what changed: 1 memory note, the extension policy, the role instructions, the model instructions",
            Line(null, Delta(added: 1, policy: true, role: true, model: true)));
    }

    [Fact]
    public void An_exact_replay_prints_no_line()
    {
        Assert.Null(Line(null, null));
        Assert.Null(Line(null, Delta()));
    }

    private static readonly Regex Duration = new(@"\d+\s*(ms|s|min|seconds)\b", RegexOptions.Compiled);

    [Fact]
    public void No_line_names_a_duration()
    {
        Assert.Matches(Duration, "resumed after 12 s");
        Assert.DoesNotMatch(Duration, "told the model what changed: 2 memory notes");
        var lines = new List<string?> { Line(null, Delta(date: "2026-09-29", added: 3, removed: 2, files: new[] { "a.md" }, policy: true, role: true, model: true)) };
        lines.AddRange(Enum.GetValues<ResumeReset>().Select(r => Line(r, null, "added web_fetch", "removed grep")));
        lines.Add(ResumeUpdate.Line(new ResumeDecision(ResumeReset.Folder, Array.Empty<string>()), null, null, @"C:\here"));
        Assert.All(lines, l => Assert.DoesNotMatch(Duration, l!));
    }

    private const string Carried = "told the model what changed: the date, 2 memory notes, GATTO.md";

    //the line starts under the divider, no row is wider than the surface, and the words of its end survive the wrap
    private static async Task AssertRich(string resumeLine, string head, string tail)
    {
        foreach (var width in new[] { 60, 100, 160 })
        {
            var h = new RichReplHarness(Path.GetTempPath(), width: width, resumedFrom: "a session", resumeLine: resumeLine);
            await h.RunUntilAsync(() => h.Saw(head));
            var frame = h.FinalFrame();
            var divider = frame.ToList().FindIndex(r => r.Contains("resumed from a session", StringComparison.Ordinal));
            var line = frame.ToList().FindIndex(r => r.Contains(head, StringComparison.Ordinal));
            Assert.True(divider >= 0 && line > divider, $"{width} cols:\n{string.Join("\n", frame)}");
            Assert.All(frame, r => Assert.True(UnicodeWidth.Of(r) <= width, $"{width} cols, a row of {UnicodeWidth.Of(r)}: {r}"));
            var text = string.Join(" ", frame.Skip(line).Take(4).Select(r => r.Trim()));
            Assert.Contains(tail, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public Task The_rich_path_shows_the_line_under_the_divider_at_every_width() =>
        AssertRich(Carried, "told the model what changed", "GATTO.md");

    [Fact]
    public Task The_rich_path_shows_the_no_folder_line_at_every_width() =>
        AssertRich(NoFolder, "this session does not record its folder", "in full");

    [Fact]
    public async Task The_rich_path_shows_no_line_on_an_exact_replay()
    {
        var h = new RichReplHarness(Path.GetTempPath(), resumedFrom: "a session");
        await h.RunUntilAsync(() => h.Saw("resumed from a session"));
        Assert.DoesNotContain("told the model", h.ScreenText(), StringComparison.Ordinal);
        Assert.DoesNotContain("sent again", h.ScreenText(), StringComparison.Ordinal);
    }
}

//the plain loop reads stdin and writes stdout, which the process shares, so it runs serialized with the other console tests
[Collection("e2e")]
public sealed class ResumeLinePlainTests : IDisposable
{
    private readonly TextReader _origIn = Console.In;
    private readonly TextWriter _origOut = Console.Out;

    public void Dispose()
    {
        Console.SetIn(_origIn);
        Console.SetOut(_origOut);
    }

    private string Run(string? resumeLine)
    {
        var client = new FakeChatClient();
        var loop = new AgentLoop(client, new Gatto.Core.Tools.ToolRegistry(), new HookBus(), new TestToolContext(Path.GetTempPath()), "m");
        var repl = new Gatto.Repl.Repl(loop, new Conversation("sys"), "generalist", "m", null, client, null, null, Path.GetTempPath(),
            () => Composed.Text("sys"), () => null, _ => new Gatto.Repl.RoleSwitchResult(false, "", null, null, "unused"),
            resumedFrom: "a session", resumeLine: resumeLine);
        var sw = new StringWriter();
        Console.SetOut(sw);
        Console.SetIn(new StringReader("/quit\n"));
        repl.RunAsync(CancellationToken.None).GetAwaiter().GetResult();
        Console.SetOut(_origOut);
        return sw.ToString().Replace("\r\n", "\n");
    }

    [Fact]
    public void The_plain_path_prints_the_line_after_the_marker()
    {
        Assert.Contains("(resumed from a session)\ntold the model what changed: GATTO.md\n", Run("told the model what changed: GATTO.md"), StringComparison.Ordinal);
        Assert.DoesNotContain("told the model", Run(null), StringComparison.Ordinal);
        const string noFolder = @"this session does not record its folder: staying in C:\here, the conversation is sent again in full";
        Assert.Contains("(resumed from a session)\n" + noFolder + "\n", Run(noFolder), StringComparison.Ordinal);
    }
}
