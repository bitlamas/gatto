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

    [Fact]
    public void A_NAME_OUTSIDE_THE_ALLOW_LIST_IS_NEITHER_LISTED_NOR_FOUND()
    {
        var reg = new ToolRegistry();
        reg.Register(new StubTool("shell"));
        reg.Register(new StubTool("grep"));

        reg.Allow(["grep"]);

        Assert.Null(reg.Get("shell"));
        Assert.NotNull(reg.Get("grep"));
        Assert.Equal(["grep"], reg.Specs().Select(s => s.Name));
    }

    [Fact]
    public void ALLOW_REPLACES_THE_LIST_AND_KEEPS_THE_ORDER()
    {
        var reg = new ToolRegistry();
        foreach (var n in new[] { "a", "b", "c", "d" }) reg.Register(new StubTool(n));

        reg.Allow(["d", "b"]);
        Assert.Equal(["b", "d"], reg.Specs().Select(s => s.Name));
        reg.Allow(["c", "a", "b"]);
        Assert.Equal(["a", "b", "c"], reg.Specs().Select(s => s.Name));
        reg.Allow([]);
        Assert.Empty(reg.Specs());
        reg.Allow(null);
        Assert.Equal(["a", "b", "c", "d"], reg.Specs().Select(s => s.Name));
    }

    [Fact]
    public void A_TOOL_REGISTERED_AFTER_THE_LIST_IS_SET_OBEYS_IT()
    {
        var reg = new ToolRegistry();
        reg.Register(new StubTool("grep"));
        reg.Allow(["grep", "memory_write"]);

        reg.Register(new StubTool("memory_write"));
        reg.Register(new StubTool("run_agent"), extension: false);
        reg.Register(new StubTool("ask_user"), extension: true, origin: "ask_user");

        Assert.Equal(["grep", "memory_write"], reg.Specs().Select(s => s.Name));
        Assert.Null(reg.Get("run_agent"));
        Assert.Null(reg.Get("ask_user"));
    }

    [Fact]
    public void A_LISTED_NAME_THAT_IS_NOT_REGISTERED_ADDS_NOTHING()
    {
        var reg = new ToolRegistry();
        reg.Register(new StubTool("grep"));

        reg.Allow(["grep", "shell"]);

        Assert.Equal(["grep"], reg.Specs().Select(s => s.Name));
        Assert.Null(reg.Get("shell"));
    }

    [Fact]
    public void THE_RAW_TABLE_SEES_A_NAME_THE_SEAT_LEAVES_OUT_OR_THE_USER_TURNED_OFF()
    {
        var armed = new ArmedSources(["web_search"]);
        var reg = new ToolRegistry(armed);
        reg.Register(new StubTool("grep"));
        reg.Register(new StubTool("web_search"), extension: true, origin: "web_search");

        reg.Allow([]);

        Assert.True(reg.IsRegistered("grep"));
        Assert.True(reg.IsRegistered("web_search"));
        Assert.False(reg.IsRegistered("shell"));
        Assert.True(reg.IsExtension("web_search"));
    }

    [Fact]
    public void NAMES_FROM_AN_ORIGIN_IGNORE_THE_SEAT()
    {
        var reg = new ToolRegistry();
        reg.Register(new StubTool("grep"));
        reg.Register(new StubTool("ask_user"), extension: true, origin: "ask_user");
        reg.Register(new StubTool("web_search"), extension: true, origin: "web");
        reg.Register(new StubTool("web_fetch"), extension: true, origin: "web");

        reg.Allow(["grep"]);

        Assert.Equal(["web_search", "web_fetch"], reg.NamesFrom("web"));
        Assert.Equal(["ask_user"], reg.NamesFrom("ask_user"));
        Assert.Empty(reg.NamesFrom("none"));
    }

    [Theory]
    [InlineData("Bad-Name")]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("read\n")]
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

    [Fact]
    public void An_off_source_s_tool_leaves_Specs_and_Get_and_comes_back_on()
    {
        var armed = new ArmedSources();
        var reg = new ToolRegistry(armed);
        reg.Register(new StubTool("read_file"));
        reg.Register(new StubTool("web_search"), extension: true, origin: "web_search");

        armed.Set("web_search", on: false);
        Assert.DoesNotContain("web_search", reg.Specs().Select(s => s.Name));
        Assert.Null(reg.Get("web_search"));
        Assert.NotNull(reg.Get("read_file"));

        armed.Set("web_search", on: true);
        Assert.Contains("web_search", reg.Specs().Select(s => s.Name));
        Assert.NotNull(reg.Get("web_search"));
    }

    //a role resolve replaces the list whole, which must not bring back what the flag holds off
    [Fact]
    public void A_role_s_list_leaves_an_off_tool_off()
    {
        var armed = new ArmedSources(["web_search"]);
        var reg = new ToolRegistry(armed);
        reg.Register(new StubTool("shell"));
        reg.Register(new StubTool("web_search"), extension: true, origin: "web_search");

        reg.Allow(["web_search"]);
        reg.Allow(null);

        Assert.Null(reg.Get("web_search"));
        Assert.Equal(["shell"], reg.Specs().Select(s => s.Name));
    }
}
