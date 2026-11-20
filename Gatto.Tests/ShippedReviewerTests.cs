using System.Text.Json;
using Gatto.Core.Loop.Permissions;
using Gatto.Extensions;
using Gatto.Roles;
using Gatto.Roles.Agents;
using Gatto.Tests.Fakes;

namespace Gatto.Tests;

//the shipped reviewer as a home receives it: its levers, its line in run_agent's list, its prompt as a child and the tools a child holds
public sealed class ShippedReviewerTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-reviewer-").FullName;
    public void Dispose() => Directory.Delete(_home, recursive: true);

    private string RolesDir => Path.Combine(_home, "roles");

    //the overload the program calls, so the real shipped files are the ones written
    private RoleFile Reviewer()
    {
        ShippedExtensions.EnsureWritten(_home);
        return RoleFile.Load(RolesDir, "reviewer");
    }

    [Fact]
    public void THE_SHIPPED_REVIEWER_IS_RAW_WITH_THREE_READING_TOOLS_AND_TWENTY_TURNS()
    {
        var reviewer = Reviewer();

        Assert.True(reviewer.Raw);
        Assert.Equal(["read_file", "glob", "grep"], reviewer.Tools!);
        Assert.Equal(20, reviewer.Turns);
        Assert.True(reviewer.Callable);
        Assert.Empty(reviewer.Gates);
        Assert.Null(reviewer.WriteDir);
        Assert.False(string.IsNullOrWhiteSpace(reviewer.Append));
    }

    //the roles as the launch hands them to run_agent, every file of the folder in its order, and researcher is the one a call cannot reach
    [Fact]
    public void RUN_AGENT_LISTS_DEFAULT_RAW_READER_AND_REVIEWER()
    {
        var reviewer = Reviewer();
        var roles = RoleFile.ListNames(RolesDir).Select(n => RoleFile.Load(RolesDir, n)).ToList();
        var store = PermissionStore.Load(_home, _home, out _);
        var tool = new RunAgentTool(
            roles: () => roles,
            refuseRole: _ => null,
            composeChild: _ => throw new InvalidOperationException("no child is composed here"),
            subagentWiring: SubagentWiring.Build(new PermissionGate(store, null, false), _home),
            client: new FakeChatClient(),
            requestSettings: () => ("m", (JsonElement?)null, (JsonElement?)null, (string?)null),
            parentCtx: new TestToolContext(_home));

        var lines = tool.Description.Split('\n').Skip(1).ToArray();

        Assert.Equal(["- default", "- raw", "- reader", "- reviewer"], lines.Select(l => l.Split(':')[0]));
        Assert.Equal("- reviewer: " + reviewer.Description, lines[3]);
    }

    //composed as LaunchState.ComposeChild composes it: merged with a caller, a folder and a date given, one context file found
    [Fact]
    public void AS_A_CHILD_ITS_PROMPT_OPENS_WITH_ITS_APPEND_AND_NAMES_NO_FOLDER_OR_DATE()
    {
        var reviewer = Reviewer();
        var merged = RoleMerge.ForChild(reviewer, RoleFile.Default);

        var text = RoleComposition.Compose(merged, null, [("GATTO.md", "the project's own notes")], null,
            modelName: "test-model", os: "Windows", sessionId: "s-1", cwd: _home, date: DateTime.Now).SystemText;

        Assert.StartsWith(reviewer.Append!, text, StringComparison.Ordinal);
        Assert.DoesNotContain("test-model", text, StringComparison.Ordinal);   //the identity line names the model
        Assert.DoesNotContain("Today's date", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Working directory", text, StringComparison.Ordinal);
        Assert.Contains("the project's own notes", text, StringComparison.Ordinal);
    }

    //a seat with no list hands the child all three, and a child never holds a tool its caller lacks
    [Fact]
    public void A_CHILD_HOLDS_THE_THREE_ITS_CALLER_ALSO_HOLDS()
    {
        var reviewer = Reviewer();
        var store = PermissionStore.Load(_home, _home, out _);
        IReadOnlyList<string>? seat = null;
        var build = SubagentWiring.Build(new PermissionGate(store, null, false), _home, bound: () => new SeatBound(seat));

        var (all, _) = build(reviewer);
        seat = ["read_file", "glob", "run_agent"];
        var (two, _) = build(reviewer);

        Assert.Equal(["glob", "grep", "read_file"], all.Specs().Select(s => s.Name).Order(StringComparer.Ordinal));
        Assert.Equal(["glob", "read_file"], two.Specs().Select(s => s.Name).Order(StringComparer.Ordinal));
    }

    //a home that already holds its own reviewer.json keeps every byte of it, since no reviewer.json ever shipped
    [Fact]
    public void A_HOMES_OWN_REVIEWER_KEEPS_ITS_BYTES()
    {
        const string own = """{ "append": "my own reviewer" }""";
        Directory.CreateDirectory(RolesDir);
        File.WriteAllText(Path.Combine(RolesDir, "reviewer.json"), own);

        ShippedExtensions.EnsureWritten(_home);

        Assert.Equal(own, File.ReadAllText(Path.Combine(RolesDir, "reviewer.json")));
    }
}
