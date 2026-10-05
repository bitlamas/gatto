using System.Text.Json;
using Gatto.Core.Client;
using Gatto.Core.Tools;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Repl;

//the real rich loop with a turn held open by a tool, the only place a command typed mid-turn meets the queue
public class BypassWiringTests : IDisposable
{
    private readonly string _cwd = Directory.CreateTempSubdirectory("gatto-bypass-").FullName;
    public void Dispose() { try { Directory.Delete(_cwd, recursive: true); } catch { } }

    //types /context once the turn runs, the next keys once its panel is drawn, and holds until the editor read them all, so no key outruns the screen
    private sealed class HoldTool(RichReplHarness h, Action<ScriptedKeySource> afterPanel) : ITool
    {
        public string Name => "hold";
        public string Description => "holds the turn open";
        public JsonElement ParametersSchema => JsonDocument.Parse("""{"type":"object"}""").RootElement;
        public async Task<ToolResult> ExecuteAsync(JsonElement args, IToolContext ctx, CancellationToken ct)
        {
            h.Keys.Line("/context");
            for (var i = 0; i < 2000 && !h.Saw(Gatto.Repl.Repl.ContextUnavailable); i++) await Task.Delay(5, ct);
            afterPanel(h.Keys);
            for (var i = 0; i < 2000 && h.Keys.Pending > 0; i++) await Task.Delay(5, ct);
            await Task.Delay(300, ct);
            return new ToolResult("held");
        }
    }

    //the turn's first round calls hold, its second says its last words
    private RichReplHarness Rig(Action<ScriptedKeySource> afterPanel)
    {
        var reg = new ToolRegistry();
        var h = new RichReplHarness(_cwd, tools: reg);
        reg.Register(new HoldTool(h, afterPanel));
        h.Client.EnqueueTurn(new StreamEvent.ToolCallReady(new ToolCall("c1", "hold", "{}")), new StreamEvent.Finished("tool_calls", null));
        h.Client.EnqueueTurn(new StreamEvent.TextDelta("second round"), new StreamEvent.Finished("stop", null));
        h.Keys.Line("go");
        return h;
    }

    private static readonly ConsoleKeyInfo Esc = new('\u001b', ConsoleKey.Escape, false, false, false);

    [Fact]
    public async Task Context_mid_turn_answers_in_the_panel_and_never_queues_while_effort_waits_its_turn()
    {
        var h = Rig(keys => keys.Line("/effort").Line("/quit"));

        await h.RunAsync();

        var written = h.Written();
        var turnEnd = written.IndexOf("second round", StringComparison.Ordinal);
        Assert.True(turnEnd >= 0, "the held turn never finished");
        //the answer was drawn while the turn ran, and typing /effort closed it, so a later line could only come from a queued /context run at rest
        Assert.InRange(written.IndexOf(Gatto.Repl.Repl.ContextUnavailable, StringComparison.Ordinal), 0, turnEnd);
        Assert.Equal(-1, written.IndexOf(Gatto.Repl.Repl.ContextUnavailable, turnEnd, StringComparison.Ordinal));
        Assert.True(written.IndexOf("effort: not configured", turnEnd, StringComparison.Ordinal) > turnEnd, "/effort did not wait for the turn");
    }

    //the first Esc is the panel's, so the turn it was opened over runs on to its end
    [Fact]
    public async Task Esc_closes_the_panel_and_the_turn_keeps_running()
    {
        //no key follows the Esc: the scripted source releases a key only when the editor samples, and a consumed Esc never reaches the editor
        var h = Rig(keys => keys.Key(Esc));

        await h.RunUntilAsync(() => h.Saw("second round")
            && !h.ScreenText().Contains(Gatto.Repl.Repl.ContextUnavailable, StringComparison.Ordinal));

        Assert.False(h.Saw("turn cancelled"), "the first Esc stopped the turn");
    }

    //the first character typed over the panel closes it and is the first character of the next message
    [Fact]
    public async Task Typing_a_closes_the_panel_and_the_composer_holds_a()
    {
        var h = Rig(keys => keys.Key(new ConsoleKeyInfo('a', ConsoleKey.A, false, false, false)));

        await h.RunUntilAsync(() => h.Saw("second round") && h.ScreenText().Contains("❯ a", StringComparison.Ordinal)
            && !h.ScreenText().Contains(Gatto.Repl.Repl.ContextUnavailable, StringComparison.Ordinal));
    }
}
