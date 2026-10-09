using System.Collections.Concurrent;
using System.Text.Json;
using Gatto.Core.Client;
using Gatto.Core.Tools;
using Gatto.Repl.Input;
using Gatto.Repl.Render;
using Gatto.Repl.Term;
using Gatto.Terminal;
using Gatto.Tests.Fakes;
using KeyDisposition = Gatto.Terminal.KeyDisposition;

namespace Gatto.Tests.Repl;

//with the chrome scrolled away a typed key returns the view first, and a prompt that is not wholly on screen is never answered by it
public class AwaySnapTests : IDisposable
{
    private readonly string _cwd = Directory.CreateTempSubdirectory("gatto-away-").FullName;
    public void Dispose() { try { Directory.Delete(_cwd, recursive: true); } catch { } }

    private sealed class ScriptSource : IInputSource
    {
        private readonly BlockingCollection<InputEvent> _q = new();
        public void Push(InputEvent e) => _q.Add(e);
        public InputEvent Read() => _q.Take();
        public bool KeyDownAvailable => _q.Count > 0;
    }

    private static ConsoleKeyInfo Key(char c, ConsoleKey k, bool ctrl = false) => new(c, k, false, false, ctrl);
    private static readonly ConsoleKeyInfo Esc = Key('\x1b', ConsoleKey.Escape);
    private static readonly ConsoleKeyInfo PageUp = Key('\0', ConsoleKey.PageUp);

    //the rule alone: off the bottom every key snaps, a hidden prompt swallows it, and a copy with a selection leaves the view
    [Theory]
    [InlineData(false, false, false, null)]
    [InlineData(false, true, false, null)]
    [InlineData(true, false, false, KeyDisposition.PassThrough)]
    [InlineData(true, true, false, KeyDisposition.Consumed)]
    [InlineData(true, false, true, null)]
    [InlineData(true, true, true, null)]
    public void The_away_rule(bool away, bool promptHidden, bool copyWithSelection, KeyDisposition? expected) =>
        Assert.Equal(expected, Gatto.Repl.Repl.DecideAway(away, promptHidden, copyWithSelection));

    [Fact]
    public void The_away_sink_sees_Esc_before_the_cancel_sink_and_a_consumed_key_goes_nowhere()
    {
        var src = new ScriptSource();
        var pump = new InputPump(src);
        var cancelled = false;
        var seen = new BlockingCollection<ConsoleKey>();
        pump.SetAwaySink(k => { seen.Add(k.Key); return KeyDisposition.Consumed; });
        pump.SetCancelSink(_ => { cancelled = true; return KeyDisposition.Consumed; });
        pump.Start();

        src.Push(new KeyEvent(Esc));

        Assert.True(seen.TryTake(out var k, 5000));
        Assert.Equal(ConsoleKey.Escape, k);
        Assert.False(cancelled);
        Assert.False(pump.Composer.KeyAvailable);
    }

    [Fact]
    public void A_passed_key_goes_on_to_the_cancel_sink_and_a_consumed_one_never_reaches_a_modal()
    {
        var src = new ScriptSource();
        var pump = new InputPump(src);
        var cancelled = false;
        var consume = false;
        pump.SetAwaySink(_ => consume ? KeyDisposition.Consumed : KeyDisposition.PassThrough);
        pump.SetCancelSink(_ => { cancelled = true; return KeyDisposition.Consumed; });
        pump.Start();

        src.Push(new KeyEvent(Esc));
        Assert.True(SpinWait.SpinUntil(() => cancelled, 5000));

        consume = true;
        using var scope = pump.PushFocus();
        src.Push(new KeyEvent(Key('1', ConsoleKey.D1)));
        src.Push(new KeyEvent(PageUp));
        Assert.False(scope.Keys.KeyAvailable && scope.Keys.ReadKey().Key == ConsoleKey.D1);
    }

    [Fact]
    public void The_scroll_keys_never_reach_the_away_sink()
    {
        var src = new ScriptSource();
        var pump = new InputPump(src);
        var seen = new BlockingCollection<ConsoleKey>();
        var scrolled = new BlockingCollection<ScrollKey>();
        pump.SetAwaySink(k => { seen.Add(k.Key); return KeyDisposition.PassThrough; });
        pump.SetScrollSink(scrolled.Add);
        pump.Start();

        src.Push(new KeyEvent(PageUp));
        src.Push(new KeyEvent(Key('\0', ConsoleKey.PageDown)));
        src.Push(new KeyEvent(Key('\0', ConsoleKey.Home)));
        src.Push(new KeyEvent(Key('\0', ConsoleKey.End)));

        for (var i = 0; i < 4; i++) Assert.True(scrolled.TryTake(out _, 5000));
        Assert.Empty(seen);
    }

    private static ChromePainter Painter()
    {
        var s = new VtScreenSurface(60, 30);
        var model = new TranscriptModel("coder");
        model.Append(new AssistantBlockItem([.. Enumerable.Range(0, 200).Select(i => $"t{i:000}")], "coder"));
        var p = new ChromePainter(s, new Theme(new TermCaps(true, true)), new object(), model)
        {
            Frame = new InputFrame(s, new Theme(new TermCaps(true, true)), "coder", new StatusInfo(@"C:\proj", "qwen", "coder", new CtxState(), @"C:\Users\x"), glyphs: GlyphSet.Unicode),
            RoleForTint = "coder",
            ChromeScrolls = true,
        };
        p.State.Composer = new EditorView(new List<string> { "" }, 0, 0);
        p.AltScreen.Enter();
        p.Repaint();
        return p;
    }

    //a prompt is hidden once a row of it is off the window, and the status line and rule below it go first
    [Fact]
    public void A_prompt_is_hidden_once_one_of_its_rows_leaves_the_window()
    {
        var p = Painter();
        p.SetPanel(["  Allow write_file?", "  1. Yes", "  2. No"]);
        Assert.False(p.Away);
        Assert.False(p.PromptHidden());

        p.Scroll.ScrollBy(2, 60, p.ViewportRows());
        Assert.True(p.Away);
        Assert.False(p.PromptHidden());

        p.Scroll.ScrollBy(1, 60, p.ViewportRows());
        Assert.True(p.PromptHidden());
    }

    [Fact]
    public void An_info_panel_is_never_a_hidden_prompt()
    {
        var p = Painter();
        p.SetInfoPanel((_, _) => ["  context", "  rows"]);
        p.Scroll.ScrollBy(10, 60, p.ViewportRows());

        Assert.True(p.Away);
        Assert.False(p.PromptHidden());
    }

    //the turn's reply is long enough to scroll, and the keys given here are typed in stages as the screen reaches each one
    private RichReplHarness Rig(ToolRegistry? tools = null)
    {
        var h = new RichReplHarness(_cwd, width: 60, height: 30, mouseEnabled: true, modeControl: new FakeConsoleModeControl(), tools: tools);
        h.Keys.Line("go");
        return h;
    }

    //the scroll sink took PgUp and the editor never sampled after it, so the script's sample gate is released here, as the editor's next sample would
    private static void Release(RichReplHarness h) => _ = h.Keys.KeyAvailable;

    private static string Reply => string.Join("\n", Enumerable.Range(0, 100).Select(i => $"line {i}"));

    [Fact]
    public async Task A_letter_typed_while_away_returns_the_view_and_lands_in_the_composer()
    {
        var h = Rig();
        h.Client.EnqueueTurn(new StreamEvent.TextDelta(Reply), new StreamEvent.Finished("stop", null));
        var stage = 0;

        await h.RunUntilAsync(() =>
        {
            if (stage == 0 && h.Saw("line 99") && h.Keys.Pending == 0 && h.Surface.CursorVisible) { h.Keys.Key(PageUp); stage = 1; }
            else if (stage == 1 && !h.Surface.CursorVisible) { Release(h); h.Keys.Key(Key('a', ConsoleKey.A)); stage = 2; }
            return stage == 2 && h.Surface.CursorVisible && h.ScreenText().Contains("❯ a", StringComparison.Ordinal);
        });
    }

    //holds the turn open until it is cancelled
    private sealed class HoldTool : ITool
    {
        public string Name => "hold";
        public string Description => "holds the turn open";
        public JsonElement ParametersSchema => JsonDocument.Parse("""{"type":"object"}""").RootElement;
        public async Task<ToolResult> ExecuteAsync(JsonElement args, IToolContext ctx, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new ToolResult("held");
        }
    }

    //no prompt waits, so Esc does what it does today and stops the turn, and the view returns as for any key
    [Fact]
    public async Task Esc_while_away_with_no_prompt_stops_the_turn_and_returns_the_view()
    {
        var reg = new ToolRegistry();
        reg.Register(new HoldTool());
        var h = Rig(reg);
        h.Client.EnqueueTurn(new StreamEvent.TextDelta(Reply), new StreamEvent.ToolCallReady(new ToolCall("c1", "hold", "{}")), new StreamEvent.Finished("tool_calls", null));
        var stage = 0;

        await h.RunUntilAsync(() =>
        {
            if (stage == 0 && h.Saw("line 99") && h.Saw("hold") && h.Keys.Pending == 0) { h.Keys.Key(PageUp); stage = 1; }
            else if (stage == 1 && !h.Surface.CursorVisible) { Release(h); h.Keys.Key(Esc); stage = 2; }
            return stage == 2 && h.Saw("turn cancelled") && h.Surface.CursorVisible;
        });
    }

    //holds the turn until the test lets it go, so the view can be scrolled away before the next round asks
    private sealed class GateTool(Func<bool> go) : ITool
    {
        public string Name => "wait";
        public string Description => "waits for the test";
        public JsonElement ParametersSchema => JsonDocument.Parse("""{"type":"object"}""").RootElement;
        public async Task<ToolResult> ExecuteAsync(JsonElement args, IToolContext ctx, CancellationToken ct)
        {
            while (!go()) await Task.Delay(5, ct);
            return new ToolResult("waited");
        }
    }

    //the tool the prompt guards, counting its runs
    private sealed class TouchTool : ITool
    {
        public int Runs;
        public string Name => "touch";
        public string Description => "changes something";
        public JsonElement ParametersSchema => JsonDocument.Parse("""{"type":"object"}""").RootElement;
        public Task<ToolResult> ExecuteAsync(JsonElement args, IToolContext ctx, CancellationToken ct)
        {
            Interlocked.Increment(ref Runs);
            return Task.FromResult(new ToolResult("touched"));
        }
    }

    //the one rule that guards consent, on the whole path: a key that returns the view from a pinned prompt answers nothing, the next one answers
    [Fact]
    public async Task A_key_that_returns_to_a_pinned_prompt_answers_nothing_and_the_next_one_approves()
    {
        var released = false;
        var touch = new TouchTool();
        var reg = new ToolRegistry();
        reg.Register(new GateTool(() => Volatile.Read(ref released)));
        reg.Register(touch);
        var hooks = new Gatto.Core.Loop.HookBus();
        var chrome = new ChromeHandle();
        var h = new RichReplHarness(_cwd, width: 60, height: 30, mouseEnabled: true, modeControl: new FakeConsoleModeControl(), tools: reg, hooks: hooks, chrome: chrome);
        var prompter = new Gatto.Repl.RichPermissionPrompter(h.Surface, new Theme(new TermCaps(true, true)), h.Keys, h.Pump, chrome);
        var gate = new Gatto.Core.Loop.Permissions.PermissionGate(Gatto.Core.Loop.Permissions.PermissionStore.InMemory(_cwd), prompter, autoYes: false);
        gate.AllowReadClass("wait");   //the gate asks for every other tool, and only the guarded one may open the prompt
        hooks.On(Gatto.Core.Loop.HookEvent.ToolCall, gate.CheckAsync);
        h.Client.EnqueueTurn(new StreamEvent.TextDelta(Reply), new StreamEvent.ToolCallReady(new ToolCall("c1", "wait", "{}")), new StreamEvent.Finished("tool_calls", null));
        h.Client.EnqueueTurn(new StreamEvent.ToolCallReady(new ToolCall("c2", "touch", "{}")), new StreamEvent.Finished("tool_calls", null));
        h.Client.EnqueueTurn(new StreamEvent.TextDelta("all done"), new StreamEvent.Finished("stop", null));
        h.Keys.Line("go");
        var stage = 0;
        var top = "";
        string Pinned() => h.FinalFrame()[^1].TrimEnd();

        var run = h.RunUntilAsync(() =>
        {
            switch (stage)
            {
                case 0 when h.Saw("line 99") && h.Saw("wait") && h.Keys.Pending == 0:
                    h.Keys.Key(PageUp); stage = 1; break;
                case 1 when !h.Surface.CursorVisible:
                    top = h.FinalFrame()[0]; Volatile.Write(ref released, true); stage = 2; break;
                case 2 when Pinned().StartsWith("↓ a prompt is waiting for your answer", StringComparison.Ordinal):
                    //the view held while the prompt opened
                    Assert.Equal(top, h.FinalFrame()[0]);
                    Release(h); h.Keys.Key(Key('1', ConsoleKey.D1)); stage = 3; break;
                case 3 when h.ScreenText().Contains("1. Yes", StringComparison.Ordinal) && !Pinned().StartsWith("↓ a prompt", StringComparison.Ordinal):
                    //back at the bottom with the prompt whole, open and unanswered, the tool not run
                    Assert.Equal(0, Volatile.Read(ref touch.Runs));
                    Release(h); h.Keys.Key(Key('1', ConsoleKey.D1)); stage = 4; break;
            }
            return stage == 4 && h.Saw("all done");
        });
        //a stalled stage leaves the prompt reading a key the loop's cancel never reaches, so the test bounds its own wait
        Assert.True(await Task.WhenAny(run, Task.Delay(30000)) == run, $"stalled at stage {stage}");
        await run;

        Assert.Equal(1, touch.Runs);
    }
}
