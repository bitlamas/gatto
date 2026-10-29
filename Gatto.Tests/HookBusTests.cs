using Gatto.Core.Client;
using Gatto.Core.Loop;
using Gatto.Core.Tools;

namespace Gatto.Tests;

public class HookBusTests
{
    [Fact]
    public async Task Tool_call_hook_failure_propagates()
    {
        var bus = new HookBus();
        bus.On(HookEvent.ToolCall, _ => throw new InvalidOperationException("gate says no"));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => bus.EmitToolCallAsync(new HookPayload(Call: new ToolCall("1", "shell", "{}"))));
        Assert.Equal("gate says no", ex.Message);
    }

    [Fact]
    public async Task Tool_result_hook_failure_is_swallowed_and_reported()
    {
        var bus = new HookBus();
        var laterRan = false;
        Exception? seen = null;
        bus.OnHandlerError += (_, ex) => seen = ex;
        bus.On(HookEvent.ToolResult, _ => throw new Exception("buggy handler"));
        bus.On(HookEvent.ToolResult, _ => { laterRan = true; return Task.CompletedTask; });

        await bus.EmitToolResultAsync(new HookPayload(Result: new ToolResult("ok")));

        Assert.True(laterRan);
        Assert.Equal("buggy handler", seen!.Message);
    }

    [Fact]
    public async Task Message_end_hook_failure_is_swallowed()
    {
        var bus = new HookBus();
        bus.On(HookEvent.MessageEnd, _ => throw new Exception("boom"));
        await bus.EmitMessageEndAsync(new HookPayload(AssistantText: "hi")); //a swallowed handler failure must not reach the caller.
    }

    [Fact]
    public async Task Handler_registering_handler_mid_emit_does_not_break_emit()
    {
        var bus = new HookBus();
        var laterRan = false;
        bus.On(HookEvent.ToolResult, _ =>
        {
            bus.On(HookEvent.ToolResult, _ => Task.CompletedTask);   //a handler may register another handler while the emit runs.
            return Task.CompletedTask;
        });
        bus.On(HookEvent.ToolResult, _ => { laterRan = true; return Task.CompletedTask; });

        await bus.EmitToolResultAsync(new HookPayload(Result: new ToolResult("ok")));

        Assert.True(laterRan);
    }

    [Fact]
    public async Task Tool_result_emit_with_null_result_throws()
    {
        var bus = new HookBus();
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => bus.EmitToolResultAsync(new HookPayload()));   //a missing result is a caller bug, so the emit throws.
    }

    [Fact]
    public async Task Throwing_error_listener_does_not_break_fail_open()
    {
        var bus = new HookBus();
        var laterRan = false;
        bus.OnHandlerError += (_, _) => throw new Exception("broken logger");
        bus.On(HookEvent.MessageEnd, _ => throw new Exception("buggy handler"));
        bus.On(HookEvent.MessageEnd, _ => { laterRan = true; return Task.CompletedTask; });

        await bus.EmitMessageEndAsync(new HookPayload());   //a broken error listener must not stop the remaining handlers.

        Assert.True(laterRan);
    }

    [Fact]
    public async Task Session_summary_handler_receives_payload()
    {
        var bus = new HookBus();
        SummaryInfo? seen = null;
        var summary = new SummaryInfo("did stuff", DateTimeOffset.UtcNow, "qwen3.6-35b", "gist");
        bus.On(HookEvent.SessionSummary, p => { seen = p.Summary; return Task.CompletedTask; });

        await bus.EmitSessionSummaryAsync(new HookPayload(Summary: summary));

        Assert.Equal(summary, seen);
    }

    [Fact]
    public async Task Session_summary_hook_failure_is_swallowed_and_reported()
    {
        var bus = new HookBus();
        var laterRan = false;
        Exception? seen = null;
        bus.OnHandlerError += (_, ex) => seen = ex;
        bus.On(HookEvent.SessionSummary, _ => throw new Exception("buggy summarizer"));
        bus.On(HookEvent.SessionSummary, _ => { laterRan = true; return Task.CompletedTask; });

        await bus.EmitSessionSummaryAsync(new HookPayload()); //one failing summarizer must not stop the other handlers.

        Assert.True(laterRan);
        Assert.Equal("buggy summarizer", seen!.Message);
    }

    [Fact]
    public void HasSessionSummaryListeners_false_then_true_on_subscribe()
    {
        var bus = new HookBus();
        Assert.False(bus.HasSessionSummaryListeners);

        bus.On(HookEvent.SessionSummary, _ => Task.CompletedTask);

        Assert.True(bus.HasSessionSummaryListeners);
    }

    [Fact]
    public async Task An_off_owner_s_handlers_do_not_run_and_the_others_do()
    {
        var armed = new ArmedSources(["undo"]);
        var bus = new HookBus(armed);
        var ran = new List<string>();
        Task Mark(string what) { ran.Add(what); return Task.CompletedTask; }
        //a tool_call hook of an off owner that would block, so a run shows as a throw
        bus.On(HookEvent.ToolCall, _ => throw new InvalidOperationException("off hook ran"), "undo");
        bus.On(HookEvent.ToolResult, _ => Mark("undo result"), "undo");
        bus.On(HookEvent.MessageEnd, _ => Mark("undo end"), "undo");
        bus.On(HookEvent.ToolCall, _ => Mark("cards call"), "cards");
        bus.On(HookEvent.ToolCall, _ => Mark("gate"));
        bus.OnToolResult(_ => { ran.Add("citations"); return Task.FromResult<ToolResult?>(null); });
        bus.On(HookEvent.MessageEnd, _ => Mark("cards end"), "cards");

        await bus.EmitToolCallAsync(new HookPayload(Call: new ToolCall("1", "write_file", "{}")));
        await bus.EmitToolResultAsync(new HookPayload(Result: new ToolResult("ok")));
        await bus.EmitMessageEndAsync(new HookPayload(AssistantText: "hi"));

        Assert.Equal(["cards call", "gate", "citations", "cards end"], ran);
        Assert.Equal(2, bus.HandlerCount(HookEvent.ToolCall));

        armed.Set("undo", on: true);
        Assert.Equal(3, bus.HandlerCount(HookEvent.ToolCall));
    }

    //gatto's own handlers carry no owner, so a source that happens to be named gatto turns none of them off
    [Fact]
    public async Task Gatto_s_own_handlers_run_whatever_the_flag_holds()
    {
        var bus = new HookBus(new ArmedSources(["gatto"]));
        var ran = 0;
        bus.On(HookEvent.ToolCall, _ => { ran++; return Task.CompletedTask; });

        await bus.EmitToolCallAsync(new HookPayload(Call: new ToolCall("1", "shell", "{}")));

        Assert.Equal(1, ran);
    }
}
