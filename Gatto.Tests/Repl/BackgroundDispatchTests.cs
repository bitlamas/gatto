using System.Text.Json;
using Gatto.Core.Client;
using Gatto.Core.Tools;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Repl;

//the rich face with a real shell and real children, since the end arrives on the task's own thread and the dispatcher is the subject
[Collection("e2e")]
public sealed class BackgroundDispatchTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-bg-dispatch-").FullName;
    private readonly string _cwd;

    public BackgroundDispatchTests()
    {
        Environment.SetEnvironmentVariable("GATTO_HOME", _home);
        _cwd = Directory.CreateDirectory(Path.Combine(_home, "project")).FullName;
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("GATTO_HOME", null);
        try { Directory.Delete(_home, recursive: true); }
        catch (Exception) { }   //a killed child may still hold the folder for a moment
    }

    private sealed class OnePng : IClipboardImageNative
    {
        public ClipboardImageKind Available() => ClipboardImageKind.Png;
        public byte[]? ReadPng() => [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0x07];
        public IReadOnlyList<string> ReadFileDrop() => [];
    }

    private static readonly ConsoleKeyInfo AltV = new('v', ConsoleKey.V, false, true, false);
    private static readonly ConsoleKeyInfo Esc = new('\u001b', ConsoleKey.Escape, false, false, false);

    private RichReplHarness Harness(IClipboardImageNative? clipboard = null, Gatto.Repl.Render.Chrome.ChromeHandle? chrome = null)
    {
        var tools = new ToolRegistry();
        tools.Register(new ShellTool());
        return new RichReplHarness(_cwd, tools: tools, clipboardImages: clipboard, chrome: chrome);
    }

    //a task entry in the queue rows, read off the live painter, since the transcript carries the same head line once the end is dispatched
    private static bool QueueHoldsAnEnd(Gatto.Repl.Render.Chrome.ChromeHandle chrome) =>
        chrome.Painter?.State.QueueTexts.Any(v => v.Task) == true;

    //the model starts the command in the background, then says it did
    private static void StartsInTheBackground(FakeChatClient client, string command)
    {
        var args = JsonSerializer.Serialize(new { command, background = true });
        client.EnqueueTurn(new StreamEvent.ToolCallReady(new ToolCall("c1", "shell", args)), new StreamEvent.Finished("tool_calls", null));
        client.EnqueueTurn(new StreamEvent.TextDelta("started"), new StreamEvent.Finished("stop", null));
    }

    private static void Says(FakeChatClient client, string text) =>
        client.EnqueueTurn(new StreamEvent.TextDelta(text), new StreamEvent.Finished("stop", null));

    private static ChatMessage? LastUserOf(ChatRequest request) => request.Messages.LastOrDefault(m => m.Role == "user");

    //each request's last user record as it was sent, since a request's message list is the live conversation
    private static List<ChatMessage> SentUsers(RichReplHarness h)
    {
        var sent = new List<ChatMessage>();
        var mark = h.Client.OnRequest;
        h.Client.OnRequest = () => { lock (sent) sent.Add(LastUserOf(h.Client.Requests[^1])!); mark?.Invoke(); };
        return sent;
    }

    private string WaitFile(string name) =>
        $"while (-not (Test-Path '{Path.Combine(_cwd, name)}')) {{ Start-Sleep -Milliseconds 50 }}";

    [Fact]
    public async Task AN_END_WHILE_IDLE_STARTS_A_TURN_THAT_HOLDS_ONE_RECORD_AND_ATTACHES_NOTHING()
    {
        var h = Harness();
        StartsInTheBackground(h.Client, "Write-Output 'bg done [Image #1]'");
        Says(h.Client, "seen it");
        var forgotten = false;
        var sent = SentUsers(h);
        h.Keys.Line("go");

        await h.RunUntilAsync(() =>
        {
            if (h.Client.Requests.Count < 3 || !h.Saw("seen it")) return false;
            forgotten = h.Tasks?.Snapshot.Count == 0;
            return true;
        });

        var end = Assert.Single(h.Convo.Messages, m => m.Background is not null);
        Assert.Equal(1, end.Background);
        Assert.StartsWith("background task 1 ended · exit 0 · ", end.Content, StringComparison.Ordinal);
        Assert.Contains("bg done [Image #1]", end.Content, StringComparison.Ordinal);
        Assert.Null(end.Images);
        Assert.Same(end, sent[2]);   //the end's turn sent the record itself
        Assert.Equal(["go"], h.Convo.Messages.Where(m => m.Role == "user" && m.Background is null).Select(m => m.Content));
        Assert.True(forgotten, "the registry still lists the task the model took");
    }

    [Fact]
    public async Task AN_END_DURING_A_TURN_WAITS_IN_THE_QUEUE_UNDER_THE_SYSTEM_MARKER_AND_JOINS_AFTER()
    {
        var h = Harness();
        var args = JsonSerializer.Serialize(new { command = "Write-Output 'quick'", background = true });
        h.Client.EnqueueTurn(new StreamEvent.ToolCallReady(new ToolCall("c1", "shell", args)), new StreamEvent.Finished("tool_calls", null));
        Says(h.Client, "still talking");
        Says(h.Client, "seen it");
        var queued = false;
        //the first round's request holds nothing, and arms the hold on the round after the tool, so that round waits until the end is queued
        h.Client.HoldNextTurn = _ =>
        {
            h.Client.HoldNextTurn = async ct => queued = await h.UntilAsync(() => h.ScreenText().Contains("♯ background task 1 ended"), Waits.Bound);
            return Task.CompletedTask;
        };
        h.Keys.Line("go");

        await h.RunUntilAsync(() => h.Client.Requests.Count >= 3 && h.Saw("seen it"));

        Assert.True(queued, "the end never showed in the queue rows while the turn ran");
        var users = h.Convo.Messages.Select((m, i) => (m, i)).Where(p => p.m.Role == "user").ToList();
        Assert.Equal(2, users.Count);
        Assert.Equal(1, users[1].m.Background);
        Assert.Equal("still talking", h.Convo.Messages[users[1].i - 1].Content);   //it joined after the turn's last reply
    }

    [Fact]
    public async Task A_PASTE_PENDING_WHEN_AN_END_ARRIVES_STAYS_FOR_THE_NEXT_TYPED_MESSAGE()
    {
        var h = Harness(new OnePng());
        StartsInTheBackground(h.Client, WaitFile("go.txt") + "; Write-Output 'done'");
        Says(h.Client, "seen it");
        Says(h.Client, "a picture");
        h.Keys.Line("go");

        var run = h.RunAsync(30_000);
        Assert.True(await h.UntilAsync(() => h.Saw("started"), Waits.Bound), "the first turn never ended");
        h.Keys.Key(AltV);
        Assert.True(await h.UntilAsync(() => h.Keys.Handled && h.ScreenText().Contains("[Image #1]"), Waits.Bound), "the paste never reached the composer");
        File.WriteAllText(Path.Combine(_cwd, "go.txt"), "");
        Assert.True(await h.UntilAsync(() => h.Saw("seen it"), Waits.Bound), "the end's turn never ran");
        h.Keys.Line(" describe it").Line("/quit");
        await run;

        var users = h.Convo.Messages.Where(m => m.Role == "user").ToList();
        Assert.Equal(3, users.Count);
        Assert.Null(users[1].Images);
        Assert.Equal("[Image #1] describe it", users[2].Content);
        Assert.Single(users[2].Images!);
    }

    //a turn that fails before its first reply rolls its user record back, and an end is owed to the model, so it waits at the head of the queue
    [Fact]
    public async Task AN_END_WHOSE_TURN_ROLLS_BACK_IS_PARKED_AND_TAKEN_BEFORE_THE_NEXT_MESSAGE()
    {
        var chrome = new Gatto.Repl.Render.Chrome.ChromeHandle();
        var h = Harness(chrome: chrome);
        StartsInTheBackground(h.Client, "Write-Output 'bg done'");
        h.Client.EnqueueThrow(new GattoConnectionException("the server went away"));
        Says(h.Client, "seen it");
        Says(h.Client, "next answered");
        var listed = false;
        var sent = SentUsers(h);

        var run = h.RunAsync(30_000);
        h.Keys.Line("go");
        Assert.True(await h.UntilAsync(() => h.Saw("the server went away") && QueueHoldsAnEnd(chrome)
            && h.ScreenText().Contains("♯ background task 1 ended"), Waits.Bound), "the rolled-back end did not wait in the queue rows");
        listed = h.Tasks?.Snapshot.Count == 1;
        h.Keys.Line("next");
        Assert.True(await h.UntilAsync(() => h.Saw("next answered"), Waits.Bound), "the typed message never ran");
        h.Keys.Line("/quit");
        await run;

        Assert.True(listed, "the registry forgot the task while its end was owed");
        Assert.Equal(1, sent[3].Background);   //the parked end went first
        Assert.Equal("next", sent[4].Content);
        Assert.Single(h.Convo.Messages, m => m.Background is not null);
    }

    //the block drawn before the failed request stands for the parked end, so the retake draws none
    [Fact]
    public async Task AN_END_WHOSE_TURN_ROLLS_BACK_DRAWS_ITS_BLOCK_ONCE()
    {
        var chrome = new Gatto.Repl.Render.Chrome.ChromeHandle();
        var h = Harness(chrome: chrome);
        StartsInTheBackground(h.Client, "Write-Output 'bg done'");
        h.Client.EnqueueThrow(new GattoConnectionException("the server went away"));
        Says(h.Client, "seen it");
        Says(h.Client, "next answered");
        var blocks = -1;

        var run = h.RunAsync(30_000);
        h.Keys.Line("go");
        Assert.True(await h.UntilAsync(() => h.Saw("the server went away") && QueueHoldsAnEnd(chrome), Waits.Bound), "the rolled-back end did not wait in the queue rows");
        h.Keys.Line("next");
        Assert.True(await h.UntilAsync(() => h.Saw("next answered"), Waits.Bound), "the typed message never ran");
        blocks = chrome.Painter!.Model.Items.Count(i => i is Gatto.Repl.Render.BackgroundEndItem);
        h.Keys.Line("/quit");
        await run;

        Assert.Equal(1, blocks);
        Assert.Single(h.Convo.Messages, m => m.Background is not null);
    }

    //the end's turn has its own token, so an Esc stops it, and the loop records the cancelled reply, so the record stays and the model reads it next turn
    [Fact]
    public async Task ESC_STOPS_AN_END_S_TURN_AND_THE_RECORD_STAYS_IN_THE_CONVERSATION()
    {
        var h = Harness();
        StartsInTheBackground(h.Client, "Write-Output 'bg done'");
        Says(h.Client, "unreached");
        //the third request, the end's turn, presses Esc and holds until the Esc cancels it. a consumed Esc releases no later key, so the run ends on its condition
        h.Client.HoldNextTurn = _ =>
        {
            h.Client.HoldNextTurn = _ =>
            {
                h.Client.HoldNextTurn = ct =>
                {
                    h.Keys.Key(Esc);
                    return Task.Delay(Timeout.Infinite, ct);
                };
                return Task.CompletedTask;
            };
            return Task.CompletedTask;
        };
        h.Keys.Line("go");

        await h.RunUntilAsync(() => h.Saw("turn cancelled") && h.Tasks?.Snapshot.Count == 0);

        var end = h.Convo.Messages.Select((m, i) => (m, i)).Single(p => p.m.Background is not null);
        Assert.Contains("[truncated]", h.Convo.Messages[end.i + 1].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NEW_ENDS_THE_RUNNING_TASKS_SAYS_SO_AND_A_LATE_END_NEVER_REACHES_THE_NEW_CONVERSATION()
    {
        var h = Harness();
        StartsInTheBackground(h.Client, "Start-Sleep -Seconds 120");
        var late = new TaskCompletionSource<BackgroundEnd>(TaskCreationOptions.RunContinuationsAsynchronously);

        var run = h.RunAsync(30_000);
        Assert.True(await h.UntilAsync(() => h.Tasks is not null, Waits.Bound));
        var sink = h.Tasks!.Ended;
        h.Tasks.Ended = e => { sink?.Invoke(e); late.TrySetResult(e); };
        h.Keys.Line("go");
        Assert.True(await h.UntilAsync(() => h.Saw("started"), Waits.Bound), "the first turn never ended");
        h.Keys.Line("/new");
        var end = await late.Task.WaitAsync(Waits.Bound);
        Assert.True(await h.UntilAsync(() => h.ScreenText().Contains("♯ ended 1 background task"), Waits.Bound), "/new said nothing about the task");
        h.Keys.Line("/quit");
        await run;

        Assert.Equal(Gatto.Core.Tools.BackgroundTasks.AtNew, end.Cause);
        Assert.Equal(2, h.Client.Requests.Count);   //the late end started no turn
        Assert.DoesNotContain(h.Convo.Messages, m => m.Background is not null);
        Assert.Equal(0, h.Ended!.EndedTasks);
    }

    [Fact]
    public async Task QUIT_ENDS_THE_RUNNING_TASKS_AND_THE_SESSION_END_COUNTS_THEM()
    {
        var h = Harness();
        StartsInTheBackground(h.Client, "Start-Sleep -Seconds 120");
        h.Keys.Line("go");

        var run = h.RunAsync(30_000);
        Assert.True(await h.UntilAsync(() => h.Saw("started"), Waits.Bound), "the first turn never ended");
        //the row the idle ticker keeps after the turn, the count, the number and the command's head
        Assert.True(await h.UntilAsync(() => h.ScreenText().Contains("1 background  \u00b7  1 Start-Sleep -Seconds 120"), Waits.Bound),
            "the tasks row never showed after the turn ended");
        h.Keys.Line("/quit");
        await run;

        Assert.Equal(1, h.Ended!.EndedTasks);
        Assert.Null(h.Tasks);   //the teardown disarmed the context
    }

    private static string Sharp => GlyphSet.Unicode.Sharp;

    [Fact]
    public async Task TASKS_AT_REST_WITH_NONE_SAYS_SO_AND_REACHES_NO_MODEL()
    {
        var h = Harness();
        h.Keys.Line("/tasks").Line("/quit");

        await h.RunAsync(30_000);

        Assert.Contains($"{Sharp} {Gatto.Repl.Repl.NoBackgroundTasks}", h.Written(), StringComparison.Ordinal);
        Assert.Empty(h.Client.Requests);
    }

    //two calls in one round, a server that runs on and a quick one. the round after them is held until the quick end waits in the queue, so the panel lists it ended and undelivered
    [Fact]
    public async Task TASKS_DURING_A_TURN_LISTS_A_RUNNING_TASK_AND_AN_ENDED_ONE_WAITING_FOR_THE_MODEL()
    {
        var chrome = new Gatto.Repl.Render.Chrome.ChromeHandle();
        var h = Harness(chrome: chrome);
        var sleeper = JsonSerializer.Serialize(new { command = "Start-Sleep -Seconds 120", background = true });
        var quick = JsonSerializer.Serialize(new { command = "Write-Output 'quick'", background = true });
        h.Client.EnqueueTurn(new StreamEvent.ToolCallReady(new ToolCall("c1", "shell", sleeper)), new StreamEvent.ToolCallReady(new ToolCall("c2", "shell", quick)),
            new StreamEvent.Finished("tool_calls", null));
        Says(h.Client, "started");
        Says(h.Client, "seen quick");
        var listed = false;
        h.Client.HoldNextTurn = _ =>
        {
            h.Client.HoldNextTurn = async ct =>
            {
                await h.UntilAsync(() => QueueHoldsAnEnd(chrome), Waits.Bound);
                h.Keys.Line("/tasks");
                listed = await h.UntilAsync(() => h.ScreenText().Contains($"2  ended {GlyphSet.Unicode.Dot} exit 0, waiting for the model  Write-Output 'quick'")
                    && h.ScreenText().Contains("1  running "), Waits.Bound);
            };
            return Task.CompletedTask;
        };

        var run = h.RunAsync(30_000);
        h.Keys.Line("go");
        Assert.True(await h.UntilAsync(() => h.Saw("seen quick"), Waits.Bound), "the quick end never reached the model");
        h.Keys.Line("/quit");
        await run;

        Assert.True(listed, "the panel never listed the running task and the ended one");
        Assert.Equal(3, h.Client.Requests.Count);   //the listing reached no model
    }

    //the turn is held open on its request, so the command is typed while a turn is in flight and answers in the panel
    [Fact]
    public async Task TASKS_DURING_A_TURN_ANSWERS_IN_THE_PANEL_AND_KILL_ENDS_A_TASK_AT_ONCE()
    {
        var h = Harness();
        StartsInTheBackground(h.Client, "Start-Sleep -Seconds 120");
        Says(h.Client, "second turn");
        Says(h.Client, "seen the kill");
        var listed = false;
        var killed = false;
        //the second message's turn is held: it lists the tasks, then kills the one running, while the turn waits
        h.Client.HoldNextTurn = _ =>
        {
            h.Client.HoldNextTurn = _ =>
            {
                h.Client.HoldNextTurn = async ct =>
                {
                    h.Keys.Line("/tasks");
                    listed = await h.UntilAsync(() => h.ScreenText().Contains("1  running "), Waits.Bound);
                    h.Keys.Line("/tasks kill 1");
                    killed = await h.UntilAsync(() => h.Tasks?.Running.Count == 0, Waits.Bound);
                };
                return Task.CompletedTask;
            };
            return Task.CompletedTask;
        };

        var run = h.RunAsync(30_000);
        h.Keys.Line("go");
        Assert.True(await h.UntilAsync(() => h.Saw("started"), Waits.Bound), "the first turn never ended");
        h.Keys.Line("next");
        Assert.True(await h.UntilAsync(() => h.Saw("seen the kill"), Waits.Bound), "the end of the killed task never reached the model");
        h.Keys.Line("/quit");
        await run;

        Assert.True(listed, "the panel never listed the running task during the turn");
        Assert.True(killed, "the kill did not end the task while the turn ran");
        Assert.DoesNotContain(h.Convo.Messages, m => m.Role == "user" && m.Background is null && m.Content?.StartsWith("/tasks", StringComparison.Ordinal) == true);   //neither command was sent as a message
        var end = Assert.Single(h.Convo.Messages, m => m.Background is not null);
        Assert.StartsWith("background task 1 killed by /tasks · ", end.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("ended background task", h.Written(), StringComparison.Ordinal);   //a successful kill prints no row of its own
    }

    [Fact]
    public async Task KILL_AT_REST_ENDS_THE_TASK_AND_AN_UNKNOWN_NUMBER_SAYS_SO()
    {
        var h = Harness();
        StartsInTheBackground(h.Client, "Start-Sleep -Seconds 120");
        Says(h.Client, "seen the kill");

        var run = h.RunAsync(30_000);
        h.Keys.Line("go");
        Assert.True(await h.UntilAsync(() => h.Saw("started"), Waits.Bound), "the first turn never ended");
        h.Keys.Line("/tasks kill 9");
        Assert.True(await h.UntilAsync(() => h.Saw($"{Sharp} no background task 9"), Waits.Bound), "the unknown number said nothing");
        h.Keys.Line("/tasks kill 1");
        Assert.True(await h.UntilAsync(() => h.Saw("seen the kill"), Waits.Bound), "the killed task's end never reached the model");
        h.Keys.Line("/quit");
        await run;

        Assert.StartsWith("background task 1 killed by /tasks · ", Assert.Single(h.Convo.Messages, m => m.Background is not null).Content, StringComparison.Ordinal);
        Assert.Equal(0, h.Ended!.EndedTasks);
    }
}

//the plain face never arms a registry, so it says there is nothing to list or end
public sealed class BackgroundTasksPlainFaceTests : IDisposable
{
    private readonly TextReader _in = Console.In;
    private readonly TextWriter _out = Console.Out;

    public void Dispose()
    {
        Console.SetIn(_in);
        Console.SetOut(_out);
    }

    [Fact]
    public async Task THE_PLAIN_FACE_PRINTS_THE_EMPTY_AND_THE_UNKNOWN_ROW_AND_SENDS_NOTHING()
    {
        var client = new FakeChatClient();
        var loop = new Gatto.Core.Loop.AgentLoop(client, new ToolRegistry(), new Gatto.Core.Loop.HookBus(), new TestToolContext(Path.GetTempPath()), "m");
        var repl = new Gatto.Repl.Repl(loop, new Gatto.Core.Session.Conversation("sys"), "generalist", "m", null, client, null, null, Path.GetTempPath(),
            () => Composed.Text("sys"), _ => new Gatto.Repl.RoleSwitchResult(false, "", null, null, "unused"));
        var written = new StringWriter();
        Console.SetOut(written);
        Console.SetIn(new StringReader("/tasks\n/tasks kill 2\n/quit\n"));

        await repl.RunAsync(CancellationToken.None);

        var sharp = GlyphSet.Unicode.Sharp;
        Assert.Contains($"{sharp} {Gatto.Repl.Repl.NoBackgroundTasks}", written.ToString(), StringComparison.Ordinal);
        Assert.Contains($"{sharp} no background task 2", written.ToString(), StringComparison.Ordinal);
        Assert.Empty(client.Requests);
    }
}
