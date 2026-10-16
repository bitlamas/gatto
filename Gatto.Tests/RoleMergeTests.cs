using Gatto.Roles;
using Gatto.Roles.Agents;

namespace Gatto.Tests;

public class RoleMergeTests
{
    private static RoleFile Role(
        string name,
        string? model = null,
        string? endpoint = null,
        IReadOnlyList<string>? gates = null,
        bool raw = false,
        string? append = null,
        ThinkingLevel? thinking = null,
        string? description = null,
        IReadOnlyList<string>? tools = null,
        int turns = RoleFile.DefaultTurns,
        bool contextFiles = true)
        => new(name, model, endpoint, gates ?? Array.Empty<string>(), raw, append, thinking, description, tools, turns, contextFiles, null, null, null, null);

    private static readonly RoleFile Caller = Role("caller", model: "caller-model", endpoint: "caller-endpoint",
        gates: new[] { "citations" }, append: "CALLER APPEND", thinking: ThinkingLevel.High,
        description: "caller words", tools: new[] { "shell" }, turns: 4, contextFiles: false);

    [Fact]
    public void MODEL_ENDPOINT_AND_THINKING_INHERIT_WHEN_UNSET()
    {
        var child = RoleMerge.ForChild(Role("callee"), Caller);

        Assert.Equal("caller-model", child.Model);
        Assert.Equal("caller-endpoint", child.Endpoint);
        Assert.Equal(ThinkingLevel.High, child.ThinkingRequested);
    }

    [Fact]
    public void A_SET_MODEL_ENDPOINT_AND_THINKING_WIN()
    {
        var child = RoleMerge.ForChild(Role("callee", model: "own-model", endpoint: "own-endpoint", thinking: ThinkingLevel.Low), Caller);

        Assert.Equal("own-model", child.Model);
        Assert.Equal("own-endpoint", child.Endpoint);
        Assert.Equal(ThinkingLevel.Low, child.ThinkingRequested);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void RAW_IS_THE_OR_OF_BOTH(bool called, bool caller, bool expected)
    {
        var child = RoleMerge.ForChild(Role("callee", raw: called), Role("caller", raw: caller));

        Assert.Equal(expected, child.Raw);
    }

    [Fact]
    public void APPEND_GATES_TOOLS_TURNS_AND_CONTEXT_FILES_ARE_THE_CALLED_ROLES()
    {
        var bare = RoleMerge.ForChild(Role("callee"), Caller);

        Assert.Null(bare.Append);
        Assert.Empty(bare.Gates);
        Assert.Null(bare.Description);
        Assert.Null(bare.Tools);
        Assert.Equal(RoleFile.DefaultTurns, bare.Turns);
        Assert.True(bare.ContextFiles);

        var set = RoleMerge.ForChild(Role("callee", gates: new[] { "grounding" }, append: "CALLEE APPEND",
            description: "callee words", tools: Array.Empty<string>(), turns: 2, contextFiles: false), Caller);

        Assert.Equal("CALLEE APPEND", set.Append);
        Assert.Equal(new[] { "grounding" }, set.Gates);
        Assert.Equal("callee words", set.Description);
        Assert.Empty(set.Tools!);
        Assert.Equal(2, set.Turns);
        Assert.False(set.ContextFiles);
    }

    [Fact]
    public void THE_NAME_IS_THE_CALLED_ROLES()
    {
        Assert.Equal("callee", RoleMerge.ForChild(Role("callee"), Caller).Name);
    }
}
