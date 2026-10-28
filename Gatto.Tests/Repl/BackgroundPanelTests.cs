using System.Text.Json;
using Gatto.Core.Client;
using Gatto.Core.Tools;
using Gatto.Repl.Render.Chrome;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Repl;

//the background panel through the rich face with real children: the list, its detail, its kill, the slot a prompt takes from it, and the cell it opens from
[Collection("e2e")]
public sealed class BackgroundPanelTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-bg-panel-").FullName;
    private readonly string _cwd;

    public BackgroundPanelTests()
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

    private static readonly ConsoleKeyInfo Esc = new('\u001b', ConsoleKey.Escape, false, false, false);
    private static readonly ConsoleKeyInfo EnterKey = new('\r', ConsoleKey.Enter, false, false, false);
    private static readonly ConsoleKeyInfo DownKey = new('\0', ConsoleKey.DownArrow, false, false, false);
    private static readonly ConsoleKeyInfo X = new('x', ConsoleKey.X, false, false, false);

    private RichReplHarness Harness(ChromeHandle? chrome = null)
    {
        var tools = new ToolRegistry();
        tools.Register(new ShellTool());
        return new RichReplHarness(_cwd, tools: tools, chrome: chrome);
    }

    //two shells in one round, one silent and one that writes a line, then the model says it started them
    private static void StartsTwo(FakeChatClient client)
    {
        var silent = JsonSerializer.Serialize(new { command = "Start-Sleep -Seconds 120", background = true });
        var talker = JsonSerializer.Serialize(new { command = "Write-Output 'warming up'; Start-Sleep -Seconds 120", background = true });
        client.EnqueueTurn(new StreamEvent.ToolCallReady(new ToolCall("c1", "shell", silent)), new StreamEvent.ToolCallReady(new ToolCall("c2", "shell", talker)),
            new StreamEvent.Finished("tool_calls", null));
        client.EnqueueTurn(new StreamEvent.TextDelta("started"), new StreamEvent.Finished("stop", null));
    }

    //a panel reads its last key without sampling, as every modal does, so the test samples once that key has left and the composer gets the next
    private static async Task Release(RichReplHarness h)
    {
        await h.UntilAsync(() => h.Keys.Pending == 0, Waits.Bound);
        await Task.Delay(100);
        _ = h.Keys.KeyAvailable;
    }

    private static async Task Shows(RichReplHarness h, Func<string, bool> holds, string what) =>
        Assert.True(await h.UntilAsync(() => holds(h.ScreenText()), Waits.Bound), $"{what}:\n{h.ScreenText()}");

    [Fact]
    public async Task TASKS_OPENS_THE_LIST_ITS_DETAIL_AND_X_KILLS_AND_ESC_GIVES_BACK_THE_DRAFT()
    {
        var h = Harness();
        StartsTwo(h.Client);
        h.Client.EnqueueTurn(new StreamEvent.TextDelta("seen the end"), new StreamEvent.Finished("stop", null));   //the killed task's end starts a turn at rest
        var run = h.RunAsync(60_000);
        h.Keys.Line("go");
        await Shows(h, s => s.Contains("2 shells"), "the cell never counted the two shells");

        h.Keys.Line("/tasks");
        await Shows(h, s => s.Contains("1  running ") && s.Contains("2  running "), "the panel never listed the two shells");
        Assert.DoesNotContain("1. ", h.ScreenText(), StringComparison.Ordinal);   //the rows carry their task numbers, no digits beside them

        h.Keys.Key(EnterKey);
        await Shows(h, s => s.Contains("no output yet"), "the silent task's detail never said it has no output");
        h.Keys.Key(EnterKey);
        h.Keys.Key(DownKey);
        h.Keys.Key(EnterKey);
        await Shows(h, s => s.Contains("⎿ warming up"), "the second task's detail never showed its line");

        h.Keys.Key(X);
        Assert.True(await h.UntilAsync(() => h.Tasks!.Running.Count == 1, Waits.Bound), "x did not kill the task under the cursor");
        //the end reaches the model in a turn of its own while the panel stays, and the delivered row leaves at the next re-entry
        Assert.True(await h.UntilAsync(() => h.Saw("seen the end"), Waits.Bound), "the end's turn never ran");
        await Shows(h, s => s.Contains("1  running ") && !s.Contains("2  "), "the delivered row never left the panel");

        h.Keys.Key(Esc);
        await Release(h);
        await Shows(h, s => !s.Contains("1  running ") && s.Contains("1 shell"), "the panel never closed back to the composer");
        h.Keys.Line("/quit");
        await run;
    }

    //from the composer Down selects the cell and Enter opens it, and the effort command and the panel at rest leave the cell painted
    [Fact]
    public async Task DOWN_SELECTS_THE_CELL_ENTER_OPENS_AND_THE_CELL_OUTLIVES_EFFORT_AND_TASKS()
    {
        var h = Harness();
        StartsTwo(h.Client);
        var run = h.RunAsync(60_000);
        h.Keys.Line("go");
        await Shows(h, s => s.Contains("2 shells"), "the cell never showed");

        h.Keys.Line("/effort");
        await Shows(h, s => s.Contains("2 shells"), "the cell went away after /effort");

        h.Keys.Key(DownKey);
        h.Keys.Key(EnterKey);
        await Shows(h, s => s.Contains("1  running "), "Enter on the selected cell never opened the panel");
        h.Keys.Key(Esc);
        await Release(h);
        await Shows(h, s => s.Contains("2 shells") && !s.Contains("1  running "), "the cell went away after the panel");
        h.Keys.Line("/quit");
        await run;
    }

    //a turn-thread prompt takes the slot: the open panel closes, the prompt's rows are what stays painted, and a /tasks while it holds opens nothing
    [Fact]
    public async Task A_PROMPT_TAKING_THE_SLOT_CLOSES_THE_PANEL_AND_HOLDS_IT_SHUT()
    {
        var chrome = new ChromeHandle();
        var h = Harness(chrome);
        StartsTwo(h.Client);
        var run = h.RunAsync(60_000);
        h.Keys.Line("go");
        await Shows(h, s => s.Contains("2 shells"), "the cell never counted the two shells");
        h.Keys.Line("/tasks");
        await Shows(h, s => s.Contains("1  running "), "the panel never opened");

        IDisposable? slot = null;
        var prompt = Task.Run(() =>
        {
            slot = chrome.HoldModalSlot!.Invoke();
            chrome.Painter!.SetPanelFactory((_, _) => new PanelContent(["  Allow shell?"]));
        });
        await prompt.WaitAsync(Waits.Bound);
        await Shows(h, s => s.Contains("Allow shell?") && !s.Contains("1  running "), "the prompt's rows were not the ones painted");
        await Task.Delay(300);
        Assert.Contains("Allow shell?", h.ScreenText(), StringComparison.Ordinal);   //the panel's own clear did not blank the prompt

        _ = h.Keys.KeyAvailable;
        chrome.Painter!.SetPanel(null);
        h.Keys.Line("/tasks");
        await h.UntilAsync(() => h.Keys.Handled, Waits.Bound);
        await Task.Delay(300);
        Assert.DoesNotContain("1  running ", h.ScreenText(), StringComparison.Ordinal);   //the slot is held, so the open did nothing

        slot!.Dispose();
        h.Keys.Line("/quit");
        await run;
    }
}
