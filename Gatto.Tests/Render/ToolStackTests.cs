using System.Text.Json;
using Gatto.Core.Client;
using Gatto.Core.Loop;
using Gatto.Core.Loop.Permissions;
using Gatto.Core.Session;
using Gatto.Core.Tools;
using Gatto.Repl;
using Gatto.Repl.Input;
using Gatto.Repl.Render;
using Gatto.Repl.Render.Chrome;
using Gatto.Terminal;
using Gatto.Terminal.Tests.Fakes;
using Gatto.Tests.Fakes;
using Xunit;

namespace Gatto.Tests.Render;

//the calls of one message stand in the chrome as they are written, each waiting its turn, and a stopped turn commits what the record holds
public sealed class ToolStackTests
{
    private static readonly Theme T = new(new TermCaps(true, true));
    private static readonly GlyphSet U = GlyphSet.Unicode;
    private static readonly GlyphSet A = GlyphSet.Ascii;

    private sealed record Wired(StreamRenderer R, ChromePainter P, TranscriptModel Model, Conversation Convo);

    private static Wired Wire(int width, int height, GlyphSet? glyphs = null, Theme? theme = null)
    {
        var g = glyphs ?? U;
        var th = theme ?? T;
        var s = new VtScreenSurface(width, height);
        var gate = new object();
        var model = new TranscriptModel("coder");
        var convo = new Conversation("sys");
        var painter = new ChromePainter(s, th, gate, model, glyphs: g)
        {
            Frame = new InputFrame(s, th, "coder", new StatusInfo(@"C:\proj", "qwen", "coder", new CtxState(), @"C:\Users\x"), glyphs: g),
            RoleForTint = "coder",
        };
        painter.State.Composer = new EditorView(new List<string> { "" }, 0, 0);
        var ticker = new ChromeTicker(painter, gate);
        var renderer = new StreamRenderer(painter, s, th, "coder", ticker, gate, model: model,
            convoTail: () => convo.Messages.Count > 0 ? convo.Messages[^1] : null, glyphs: g);
        painter.AltScreen.Enter();
        painter.Repaint();
        return new Wired(renderer, painter, model, convo);
    }

    private static List<ChromeRow> Chrome(ChromePainter p, int width, int height) => [.. p.ComposeChromeBlock(width, height).Rows];

    private static List<string> ToolRows(ChromePainter p, int width, int height) =>
        Chrome(p, width, height).Where(r => r.Region == ChromeRegion.Tool).Select(r => r.Visible.TrimEnd()).Where(v => v.Length > 0).ToList();

    private static List<string> Rows(TranscriptItem item, int width, GlyphSet? g = null) =>
        item.Render(width, T, g ?? U).Select(r => TermText.StripAnsiForWidth(r).TrimEnd()).ToList();

    private static List<string> ModelRows(TranscriptModel m, int width, Func<TranscriptItem, bool>? keep = null) =>
        m.Items.Where(keep ?? (_ => true)).SelectMany(i => Rows(i, width)).ToList();

    private static string Shell(string command) => JsonSerializer.Serialize(new { command });

    private static readonly string[] Four = ["git status --short", "git log --oneline -5", "dotnet build --nologo", "dotnet test --nologo"];

    private static ToolCall[] Calls(params string[] commands) =>
        [.. commands.Select((c, i) => new ToolCall("c" + (i + 1), "shell", Shell(c)))];

    //every prefix of the arguments, the way a server streams them a few characters at a time
    private static IEnumerable<string> Prefixes(string json, int step)
    {
        for (var n = step; n < json.Length + step; n += step) yield return json[..Math.Min(n, json.Length)];
    }

    private static void Stream(StreamRenderer r, ToolCall call, int index, int step = 6)
    {
        foreach (var partial in Prefixes(call.ArgumentsJson, step)) r.OnToolCallDelta(call.Name, partial, index);
    }

    //the first fragment of a call, cut inside its value, so its arguments do not parse yet
    private static string FirstFragment(ToolCall call) => call.ArgumentsJson[..(call.ArgumentsJson.IndexOf(':') + 7)];

    private static string Waiting(string row, GlyphSet? g = null) => $"{row} {(g ?? U).Dot} waiting";

    [Fact]
    public void TWO_WRITTEN_CALLS_WAIT_ABOVE_THE_ONE_BEING_WRITTEN()
    {
        var w = Wire(120, 30);
        var calls = Calls(Four);
        w.R.BeginTurn();
        Stream(w.R, calls[0], 0);
        Stream(w.R, calls[1], 1);
        w.R.OnToolCallDelta("shell", FirstFragment(calls[2]), 2);

        Assert.Equal(
            [Waiting("○ shell git status --short"), Waiting("○ shell git log --oneline -5"), "● shell dotne"],
            ToolRows(w.P, 120, 30));
    }

    [Fact]
    public void A_ROW_TAKES_ITS_OWN_CALLS_FRAGMENTS_IN_ANY_ORDER()
    {
        var w = Wire(120, 30);
        var calls = Calls(Four);
        w.R.BeginTurn();
        w.R.OnToolCallDelta("shell", FirstFragment(calls[0]), 0);
        w.R.OnToolCallDelta("shell", FirstFragment(calls[1]), 1);
        w.R.OnToolCallDelta("shell", calls[0].ArgumentsJson, 0);   //the server goes back to the earlier call

        Assert.Equal([Waiting("○ shell git status --short"), "● shell git l"], ToolRows(w.P, 120, 30));
    }

    [Fact]
    public void AT_THE_STREAMS_END_NO_WAITING_ROW_CHANGES_THE_LAST_WRITTEN_CALL_TAKES_THE_WORD_AND_THE_FIRST_RING_BLINKS()
    {
        var w = Wire(120, 30);
        var calls = Calls("git status --short", "git log --oneline -5", "dotnet build --nologo\ndotnet test --nologo");
        w.R.BeginTurn();
        for (var i = 0; i < calls.Length; i++) Stream(w.R, calls[i], i);
        var written = ToolRows(w.P, 120, 30);
        Assert.Equal(["● shell · 2 lines", "  dotnet build --nologo", "  dotnet test --nologo"], written[2..]);   //the last call live, its command rows under its head

        ((ITurnObserver)w.R).OnToolCallsRecorded(calls);
        var recorded = ToolRows(w.P, 120, 30);

        Assert.Equal(["● shell git status --short", written[1], Waiting("○ shell · 2 lines")], recorded);
        w.P.State.BlinkOn = false;
        var dark = Chrome(w.P, 120, 30).Where(r => r.Region == ChromeRegion.Tool && r.Visible.Trim().Length > 0).Select(r => TermText.StripAnsiForWidth(r.Rendered)).ToList();
        Assert.StartsWith("  shell git status", dark[0]);   //the running ring is off in this phase
        Assert.StartsWith("○ shell git log", dark[1]);       //a waiting ring is steady
    }

    [Fact]
    public void A_RESULT_COMMITS_THE_TOP_CALL_AND_THE_REST_STAY()
    {
        var w = Wire(120, 30);
        var calls = Calls(Four);
        w.R.BeginTurn();
        for (var i = 0; i < calls.Length; i++) Stream(w.R, calls[i], i);
        ((ITurnObserver)w.R).OnToolCallsRecorded(calls);
        w.R.OnToolCallStart(calls[0]);
        w.R.OnToolResult(calls[0], new ToolResult("M  a.cs", Gloss: "exit 0"));

        Assert.Equal([.. Four[1..].Select(c => Waiting("○ shell " + c))], ToolRows(w.P, 120, 30));
        Assert.Equal("○ shell git status --short", Rows(w.Model.Items.OfType<ToolBlockItem>().Single(), 120)[0]);

        w.R.OnToolCallStart(calls[1]);
        Assert.Equal(["● shell git log --oneline -5", Waiting("○ shell dotnet build --nologo"), Waiting("○ shell dotnet test --nologo")], ToolRows(w.P, 120, 30));
    }

    //a shell stand-in: the first call warns while it runs, the second is the prompt the user stops with Esc
    private sealed class EscAtTheSecond(StreamRenderer r, CancellationTokenSource cts) : ITool
    {
        private int _runs;
        public string Name => "shell";
        public string Description => "runs";
        public JsonElement ParametersSchema => JsonDocument.Parse("{\"type\":\"object\"}").RootElement;
        public Task<ToolResult> ExecuteAsync(JsonElement args, IToolContext ctx, CancellationToken ct)
        {
            if (++_runs == 1)
            {
                r.OnWarning("warned while it ran");
                return Task.FromResult(new ToolResult("M  a.cs", Gloss: "exit 0"));
            }
            r.NotePermissionOutcome(PermissionOutcomeKind.Cancelled);
            cts.Cancel();
            return Task.FromResult(new ToolResult("blocked: " + PermissionGate.CancelMessage, IsError: true));
        }
    }

    private static StreamEvent[] Written(ToolCall[] calls, int step = 6) =>
        [.. calls.SelectMany((c, i) => Prefixes(c.ArgumentsJson, step).Select(p => (StreamEvent)new StreamEvent.ToolCallDelta(c.Name, p, i)))];

    private static async Task<Wired> EscAtTheSecondPrompt(int width, bool endTurn = true)
    {
        var w = Wire(width, 30);
        using var cts = new CancellationTokenSource();
        var client = new FakeChatClient();
        var calls = Calls(Four);
        client.EnqueueTurn([.. Written(calls), .. calls.Select(c => new StreamEvent.ToolCallReady(c)), new StreamEvent.Finished("tool_calls", null)]);
        var tools = new ToolRegistry();
        tools.Register(new EscAtTheSecond(w.R, cts));
        var loop = new AgentLoop(client, tools, new HookBus(), new TestToolContext(Path.GetTempPath()), "m");
        w.R.CommitUser(["go"]);
        w.R.BeginTurn();
        await loop.RunTurnAsync(w.Convo, "go", w.R, cts.Token);
        if (endTurn) w.R.EndTurn();
        return w;
    }

    [Fact]
    public async Task ESC_AT_THE_SECOND_PROMPT_COMMITS_3_CANCELLED_BLOCKS_IN_ORDER()
    {
        var w = await EscAtTheSecondPrompt(120);
        var heads = w.Model.Items.Where(i => i is ToolBlockItem or SystemLineItem).Select(i => Rows(i, 120).First(r => r.Length > 0)).ToList();

        Assert.Equal(
        [
            "○ shell git status --short", "♯ warned while it ran",
            "○ shell git log --oneline -5", "○ shell dotnet build --nologo", "○ shell dotnet test --nologo",
            "♯ turn cancelled · remaining tool calls cancelled, not run",
        ], heads);
        Assert.IsType<SystemLineItem>(w.Model.Items[^1]);   //the stopped round's line is the last row committed
        var blocks = w.Model.Items.OfType<ToolBlockItem>().ToList();
        Assert.All(blocks.Skip(1), b => Assert.Contains("✗ cancelled", string.Join("\n", Rows(b, 120))));
        Assert.All(blocks.Skip(1), b => Assert.Equal(Theme.Err, b.BulletTint));   //a cancelled block's ring is red
        Assert.Empty(ToolRows(w.P, 120, 30));
    }

    [Theory]
    [InlineData(80)]
    [InlineData(120)]
    public async Task THEIR_RESUME_DRAWS_THE_SAME_ROWS(int width)
    {
        var w = await EscAtTheSecondPrompt(width);
        var (_, resumed) = TranscriptStore.Rebuild(TranscriptStore.BuildLines(w.Model, w.Convo), T, "coder", U);

        Assert.Equal(ModelRows(w.Model, width), ModelRows(resumed, width));
    }

    //forwards to the renderer, and runs an action at each delta that reaches a given call
    private sealed class StopAt(StreamRenderer r, int at, Action stop) : ITurnObserver
    {
        public void OnTextDelta(string text) => r.OnTextDelta(text);
        public void OnReasoningDelta(string text) => r.OnReasoningDelta(text);
        public void OnToolCallDelta(string? name = null, string? partialArguments = null, int index = 0)
        {
            r.OnToolCallDelta(name, partialArguments, index);
            if (index == at) stop();
        }
        public void OnToolCallsRecorded(IReadOnlyList<ToolCall> calls) => ((ITurnObserver)r).OnToolCallsRecorded(calls);
        public void OnToolCallCancelled(ToolCall call) => r.OnToolCallCancelled(call);
        public void OnToolCallStart(ToolCall call) => r.OnToolCallStart(call);
        public void OnToolResult(ToolCall call, ToolResult result) => r.OnToolResult(call, result);
        public void OnWarning(string message) => r.OnWarning(message);
        public void OnUsage(Usage usage) => r.OnUsage(usage);
    }

    private static bool BlockOrLine(TranscriptItem i) => i is ToolBlockItem or SystemLineItem;

    //two calls written and the third begun, then the stream stops: by a drop when cancel is false, by the user when it is true
    private static async Task<Wired> StoppedMidCall(bool cancel, bool endTurn = true)
    {
        var w = Wire(120, 30);
        using var cts = new CancellationTokenSource();
        var client = new FakeChatClient();
        var calls = Calls(Four);
        var events = new List<StreamEvent>([.. Written(calls[..2]), new StreamEvent.ToolCallDelta("shell", FirstFragment(calls[2]), 2)]);
        if (cancel) events.Add(new StreamEvent.ToolCallDelta("shell", calls[2].ArgumentsJson, 2));
        else events.Add(new StreamEvent.Truncated());
        client.EnqueueTurn([.. events]);
        var loop = new AgentLoop(client, new ToolRegistry(), new HookBus(), new TestToolContext(Path.GetTempPath()), "m");
        var observer = cancel ? new StopAt(w.R, 2, cts.Cancel) : (ITurnObserver)w.R;
        w.R.CommitUser(["go"]);
        w.R.BeginTurn();
        await loop.RunTurnAsync(w.Convo, "go", observer, cts.Token);
        if (endTurn) w.R.EndTurn();
        return w;
    }

    [Fact]
    public async Task A_STREAM_CUT_MID_CALL_COMMITS_NOTHING_AND_CLEARS_THE_STACK()
    {
        var w = await StoppedMidCall(cancel: false);

        Assert.Empty(w.Model.Items.OfType<ToolBlockItem>());
        Assert.Equal(["♯ turn truncated · 3 tool calls discarded, not run"], ModelRows(w.Model, 120, BlockOrLine).Where(r => r.Length > 0));
        Assert.Empty(ToolRows(w.P, 120, 30));
    }

    //the record holds the text with its truncated mark, which a resume draws as prose and the live screen never did, so the blocks and the lines are compared
    [Fact]
    public async Task A_STREAM_CUT_MID_CALL_RESUMES_TO_THE_SAME_BLOCKS_AND_LINES()
    {
        var w = await StoppedMidCall(cancel: false);
        var (_, resumed) = TranscriptStore.Rebuild(TranscriptStore.BuildLines(w.Model, w.Convo), T, "coder", U);

        Assert.Equal(ModelRows(w.Model, 120, BlockOrLine), ModelRows(resumed, 120, BlockOrLine));
    }

    [Fact]
    public async Task ESC_DURING_THE_STREAM_COMMITS_NOTHING_AND_CLEARS_THE_STACK()
    {
        var w = await StoppedMidCall(cancel: true);
        var (_, resumed) = TranscriptStore.Rebuild(TranscriptStore.BuildLines(w.Model, w.Convo), T, "coder", U);

        Assert.Empty(w.Model.Items.OfType<ToolBlockItem>());
        Assert.Equal(["♯ turn cancelled · 3 tool calls discarded, not run"], ModelRows(w.Model, 120, BlockOrLine).Where(r => r.Length > 0));
        Assert.Empty(ToolRows(w.P, 120, 30));
        Assert.Equal(ModelRows(w.Model, 120, BlockOrLine), ModelRows(resumed, 120, BlockOrLine));
    }

    //the session is saved when the turn returns and before it ends, so the line must be in the model by then and a resume draws it last
    [Fact]
    public async Task THE_ESC_LINE_IS_SAVED_WITH_THE_TURN_AND_ITS_RESUME_DRAWS_IT_LAST()
    {
        var w = await EscAtTheSecondPrompt(120, endTurn: false);
        var (_, resumed) = TranscriptStore.Rebuild(TranscriptStore.BuildLines(w.Model, w.Convo), T, "coder", U);

        Assert.Equal("♯ turn cancelled · remaining tool calls cancelled, not run", Rows(resumed.Items[^1], 120).Last(r => r.Length > 0));
        Assert.Equal(ModelRows(w.Model, 120), ModelRows(resumed, 120));
    }

    [Fact]
    public async Task THE_CUT_LINE_IS_SAVED_WITH_THE_TURN_AND_ITS_RESUME_DRAWS_IT()
    {
        var w = await StoppedMidCall(cancel: false, endTurn: false);
        var (_, resumed) = TranscriptStore.Rebuild(TranscriptStore.BuildLines(w.Model, w.Convo), T, "coder", U);

        Assert.Equal(["♯ turn truncated · 3 tool calls discarded, not run"], ModelRows(resumed, 120, BlockOrLine).Where(r => r.Length > 0));
        Assert.Equal(ModelRows(w.Model, 120, BlockOrLine), ModelRows(resumed, 120, BlockOrLine));
    }

    //a shell stand-in the user stops while it runs, after it warned
    private sealed class StoppedWhileRunning(StreamRenderer r, CancellationTokenSource cts) : ITool
    {
        public string Name => "shell";
        public string Description => "runs";
        public JsonElement ParametersSchema => JsonDocument.Parse("{\"type\":\"object\"}").RootElement;
        public Task<ToolResult> ExecuteAsync(JsonElement args, IToolContext ctx, CancellationToken ct)
        {
            r.OnWarning("warned while it ran");
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new ToolResult("unreached"));
        }
    }

    [Fact]
    public async Task A_LINE_WARNED_BY_A_CALL_STOPPED_MID_RUN_FOLLOWS_ITS_BLOCK()
    {
        var w = Wire(120, 30);
        using var cts = new CancellationTokenSource();
        var client = new FakeChatClient();
        var calls = Calls(Four);
        client.EnqueueTurn([.. Written(calls), .. calls.Select(c => new StreamEvent.ToolCallReady(c)), new StreamEvent.Finished("tool_calls", null)]);
        var tools = new ToolRegistry();
        tools.Register(new StoppedWhileRunning(w.R, cts));
        var loop = new AgentLoop(client, tools, new HookBus(), new TestToolContext(Path.GetTempPath()), "m");
        w.R.CommitUser(["go"]);
        w.R.BeginTurn();
        await loop.RunTurnAsync(w.Convo, "go", w.R, cts.Token);   //no EndTurn: the save at the turn's return sees this model
        var heads = w.Model.Items.Where(BlockOrLine).Select(i => Rows(i, 120).First(r => r.Length > 0)).ToList();
        var (_, resumed) = TranscriptStore.Rebuild(TranscriptStore.BuildLines(w.Model, w.Convo), T, "coder", U);

        Assert.Equal(
        [
            "○ shell git status --short", "♯ warned while it ran",
            "○ shell git log --oneline -5", "○ shell dotnet build --nologo", "○ shell dotnet test --nologo",
            "♯ turn cancelled · remaining tool calls cancelled, not run",
        ], heads);
        Assert.Equal(ModelRows(w.Model, 120), ModelRows(resumed, 120));
    }

    //rows captured from the renderer before the stack existed, for one read_file call
    [Fact]
    public void A_MESSAGE_WITH_ONE_CALL_WHOSE_STREAM_FINISHES_DRAWS_WHAT_IT_DREW_AT_RC()
    {
        var w = Wire(120, 30);
        var json = JsonSerializer.Serialize(new { path = @"C:\proj\notes.md" });
        var call = new ToolCall("c1", "read_file", json);
        var seen = new List<string>();
        w.R.BeginTurn();
        Stream(w.R, call, 0);
        seen.Add("written: " + string.Join(" | ", ToolRows(w.P, 120, 30)));
        w.Convo.AddAssistant("", new[] { call });
        ((ITurnObserver)w.R).OnToolCallsRecorded(new[] { call });
        seen.Add("recorded: " + string.Join(" | ", ToolRows(w.P, 120, 30)));
        w.R.OnToolCallStart(call);
        seen.Add("started: " + string.Join(" | ", ToolRows(w.P, 120, 30)));
        var result = new ToolResult("line one\nline two", Gloss: "2 lines");
        w.R.OnToolResult(call, result);
        w.Convo.AddToolResult(call.Id, result);
        w.R.EndTurn();
        seen.Add("chrome: " + string.Join(" | ", ToolRows(w.P, 120, 30)));
        seen.AddRange(ModelRows(w.Model, 120).Select(r => "model: " + r));

        Assert.Equal(
        [
            @"written: ● read_file C:\proj\notes.md",
            @"recorded: ● read_file C:\proj\notes.md",
            @"started: ● read_file C:\proj\notes.md",
            "chrome: ",
            @"model: ○ read_file C:\proj\notes.md",
            "model:   ⎿ ✓ 2 lines · click to expand · ~4 tok",
        ], seen);
    }

    private static readonly string[] EditPrompt =
    [
        @"Edit C:\proj\a.cs?",
        "  - old line",
        "  + new line",
        "❯ 1. Yes",
        "  2. No",
    ];

    //which of the stack's forms a frame shows, and whether the running call and the whole prompt are there
    private static (string Stack, bool Live, bool Prompt) Shape(ChromePainter p, int height)
    {
        var rows = Chrome(p, 120, height).Select(r => r.Visible.TrimEnd()).ToList();
        var waiting = rows.Count(r => r.StartsWith("○ shell", StringComparison.Ordinal) && r.EndsWith(" waiting", StringComparison.Ordinal));
        var stack = waiting == 3 ? "whole" : waiting > 0 ? "torn" : rows.Contains("  3 calls waiting") ? "folded" : "gone";
        return (stack, rows.Any(r => r.StartsWith("● shell git status", StringComparison.Ordinal)), EditPrompt.All(rows.Contains));
    }

    [Fact]
    public void THE_CLAMP_DROPS_THE_STACK_THEN_THE_FOLDED_ROW_BEFORE_THE_RUNNING_CALL()
    {
        var w = Wire(120, 30);
        var calls = Calls(Four);
        w.R.BeginTurn();
        for (var i = 0; i < calls.Length; i++) Stream(w.R, calls[i], i);
        ((ITurnObserver)w.R).OnToolCallsRecorded(calls);
        w.R.OnToolCallStart(calls[0]);
        w.P.SetPanel(EditPrompt);

        Assert.True(Shape(w.P, 30).Prompt);
        Assert.True(Shape(w.P, 20).Prompt);
        var order = new[] { "whole", "folded", "gone" };
        var last = 0;
        var seen = new HashSet<string>();
        for (var h = 40; h >= 12; h--)
        {
            var (stack, live, _) = Shape(w.P, h);
            Assert.NotEqual("torn", stack);
            var at = Array.IndexOf(order, stack);
            Assert.True(at >= last, $"at height {h} the stack came back as {stack}");
            if (stack != "gone") Assert.True(live, $"at height {h} the running call left before the stack");
            last = at;
            seen.Add(stack);
        }
        Assert.Equal(order, order.Where(seen.Contains));   //every form shows at some height
    }

    private static Wired OneWaiting(GlyphSet g, Theme? theme = null)
    {
        var w = Wire(120, 30, g, theme);
        var read = new ToolCall("c1", "read_file", JsonSerializer.Serialize(new { path = @"C:\proj\a.md" }));
        w.R.BeginTurn();
        Stream(w.R, read, 0);
        w.R.OnToolCallDelta("shell", FirstFragment(Calls("git status")[0]), 1);
        return w;
    }

    [Fact]
    public void THE_WAITING_ROW_IN_BOTH_SETS_AND_BELOW_TRUECOLOUR()
    {
        Assert.Equal(@"○ read_file C:\proj\a.md · waiting", ToolRows(OneWaiting(U).P, 120, 30)[0]);
        Assert.Equal(@"o read_file C:\proj\a.md . waiting", ToolRows(OneWaiting(A).P, 120, 30)[0]);
        var flat = new Theme(new TermCaps(true, false));
        var row = Chrome(OneWaiting(U, flat).P, 120, 30).First(r => r.Visible.Contains("waiting", StringComparison.Ordinal));
        Assert.Equal(@"○ read_file C:\proj\a.md · waiting", TermText.StripAnsiForWidth(row.Rendered).TrimEnd());
    }

    [Fact]
    public void A_WAITING_SHELL_CALL_IS_ITS_HEAD_ROW()
    {
        var w = Wire(120, 30);
        var calls = Calls("dotnet build --nologo\ndotnet test --nologo\ndotnet pack", "git status");
        w.R.BeginTurn();
        Stream(w.R, calls[0], 0);
        Stream(w.R, calls[1], 1);
        var rows = ToolRows(w.P, 120, 30);

        w.R.OnToolCallStart(calls[0]);
        w.R.OnToolResult(calls[0], new ToolResult("ok", Gloss: "exit 0"));
        var head = Rows(w.Model.Items.OfType<ToolBlockItem>().Single(), 120)[0];
        Assert.Equal([Waiting(head), "● shell git status"], rows);
    }

    //the stack's rows are reserved like the live row's, so a tall composer windows itself before the clamp folds the stack
    [Theory]
    [InlineData(14)]
    [InlineData(18)]
    [InlineData(24)]
    public void A_TALL_COMPOSER_LEAVES_THE_STACK_ITS_ROWS(int height)
    {
        var w = Wire(120, height);
        var calls = Calls(Four);
        w.R.BeginTurn();
        for (var i = 0; i < calls.Length; i++) Stream(w.R, calls[i], i);
        w.P.State.Composer = new EditorView([.. Enumerable.Range(1, 20).Select(n => $"line {n}")], 19, 7);

        var rows = Chrome(w.P, 120, height).Select(r => r.Visible.TrimEnd()).ToList();
        Assert.Equal(3, rows.Count(r => r.EndsWith($"{U.Dot} waiting", StringComparison.Ordinal)));
    }

    [Fact]
    public void NO_ROW_IS_WIDER_THAN_THE_WINDOW()
    {
        foreach (var g in new[] { U, A })
            for (var width = 30; width <= 160; width++)
            {
                var w = Wire(width, 30, g);
                var read = new ToolCall("r1", "read_file", JsonSerializer.Serialize(new { path = @"C:\proj\src\Gatto\Repl\Render\Chrome\ChromePainter.cs" }));
                ToolCall[] calls = [read, .. Calls([.. Four.Select(c => c + " --verbosity detailed --configuration Release")])];
                w.R.BeginTurn();
                for (var i = 0; i < calls.Length; i++) Stream(w.R, calls[i], i, step: 40);
                var rows = Chrome(w.P, width, 30);
                foreach (var row in rows)
                {
                    Assert.True(UnicodeWidth.Of(row.Visible) <= width, $"{width}: {row.Visible}");
                    Assert.True(UnicodeWidth.Of(TermText.StripAnsiForWidth(row.Rendered)) <= width, $"{width}: {row.Rendered}");
                }
                var waiting = rows.Where(r => r.Region == ChromeRegion.Tool && r.Visible.Trim().Length > 0).Take(4).ToList();   //every waiting call stands above the live row while the stream runs
                Assert.All(waiting, r => Assert.EndsWith($"{g.Dot} waiting", r.Visible.TrimEnd()));   //the word wins the room at every width
            }
    }
}
