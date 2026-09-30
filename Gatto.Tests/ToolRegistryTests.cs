using System.Text.Json;
using Gatto.Core.Tools;

namespace Gatto.Tests;

public sealed class StubTool(string name) : ITool
{
    public string Name => name;
    public string Description => "stub";
    public JsonElement ParametersSchema => JsonDocument.Parse("{\"type\":\"object\"}").RootElement;
    public Task<ToolResult> ExecuteAsync(JsonElement args, IToolContext ctx, CancellationToken ct)
        => Task.FromResult(new ToolResult("ok"));
}

//a stubbed gate-owned tool that tracks an external flag through IsAvailable, with no dependency on the roles project
public sealed class GatedStubTool(string name) : ITool
{
    public bool Armed { get; set; }
    public string Name => name;
    public string Description => "gated stub";
    public JsonElement ParametersSchema => JsonDocument.Parse("{\"type\":\"object\"}").RootElement;
    public bool IsAvailable => Armed;
    public Task<ToolResult> ExecuteAsync(JsonElement args, IToolContext ctx, CancellationToken ct)
        => Task.FromResult(new ToolResult("ok"));
}

public class ToolRegistryTests
{
    [Fact]
    public void Registers_and_lists_specs()
    {
        var reg = new ToolRegistry();
        reg.Register(new StubTool("read_file"));
        Assert.NotNull(reg.Get("read_file"));
        Assert.Null(reg.Get("nope"));
        Assert.Equal("read_file", reg.Specs().Single().Name);
    }

    [Fact]
    public void Rejects_duplicate_names()
    {
        var reg = new ToolRegistry();
        reg.Register(new StubTool("shell"));
        var ex = Assert.Throws<InvalidOperationException>(() => reg.Register(new StubTool("shell")));
        Assert.Contains("shell", ex.Message);
    }

    [Theory]
    [InlineData("Bad-Name")]
    [InlineData("")]
    [InlineData("has space")]
    public void Rejects_invalid_names(string name)
    {
        var reg = new ToolRegistry();
        Assert.Throws<InvalidOperationException>(() => reg.Register(new StubTool(name)));
    }

    //filtering by IsAvailable

    [Fact]
    public void Specs_omits_a_disarmed_gated_tool_and_includes_it_once_armed()
    {
        var reg = new ToolRegistry();
        var gated = new GatedStubTool("task_restate") { Armed = false };
        reg.Register(gated);

        Assert.Empty(reg.Specs());       //disarmed, so it stays out of the model's tool list
        Assert.NotNull(reg.Get("task_restate"));   //still registered, so Get stays total

        gated.Armed = true;
        Assert.Equal("task_restate", reg.Specs().Single().Name);   //armed, so it is advertised again
    }

    [Fact]
    public void Specs_always_includes_a_plain_tool_with_no_IsAvailable_override()
    {
        //a tool with no IsAvailable override is always listed, so the default interface member changes nothing
        var reg = new ToolRegistry();
        reg.Register(new StubTool("read_file"));
        var gated = new GatedStubTool("task_restate") { Armed = false };
        reg.Register(gated);

        var names = reg.Specs().Select(s => s.Name).ToList();
        Assert.Contains("read_file", names);
        Assert.DoesNotContain("task_restate", names);
    }
}
