using Gatto.Core.Client;
using Gatto.Repl.Prompts;
using Gatto.Repl.Render.Chrome;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Repl;

//the /tools panel through the rich face: its rows and detail at rest, the same panel while a turn runs, its two lines and the slot a prompt takes
[Collection("e2e")]
public sealed class ToolsPanelReplTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-tools-panel-").FullName;
    private readonly string _cwd;

    public ToolsPanelReplTests()
    {
        Environment.SetEnvironmentVariable("GATTO_HOME", _home);
        _cwd = Directory.CreateDirectory(Path.Combine(_home, "project")).FullName;
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("GATTO_HOME", null);
        try { Directory.Delete(_home, recursive: true); }
        catch (Exception) { }   //a test that failed mid-run may still hold the folder for a moment
    }

    private static readonly ConsoleKeyInfo Esc = new('\u001b', ConsoleKey.Escape, false, false, false);
    private static readonly ConsoleKeyInfo EnterKey = new('\r', ConsoleKey.Enter, false, false, false);

    private static readonly IReadOnlyList<ToolInfo> Two =
    [
        new("ask_user", "Ask the user a question.\nThe answer comes back as text.", "ask_user"),
        new("read_file", "Read a file from disk.\nThe path is relative to the project.", null),
    ];

    private static readonly IReadOnlyList<(string Extension, string Line)> AskPolicy = [("ask_user", "ask before guessing a choice that is the user's")];

    private RichReplHarness Harness(Func<IReadOnlyList<ToolInfo>>? tools, ChromeHandle? chrome = null) =>
        new(_cwd, chrome: chrome, listTools: tools, listPolicy: () => AskPolicy);

    //a panel reads its last key without sampling, as every modal does, so the test samples once that key has left and the composer gets the next
    private static async Task Release(RichReplHarness h)
    {
        await h.UntilAsync(() => h.Keys.Pending == 0, Waits.Bound);
        await Task.Delay(100);
        _ = h.Keys.KeyAvailable;
    }

    //the panel has no poll to sample for it, so the test releases each key itself and waits for it to leave
    private static async Task Press(RichReplHarness h, ConsoleKeyInfo key)
    {
        _ = h.Keys.KeyAvailable;
        h.Keys.Key(key);
        await h.UntilAsync(() => h.Keys.Pending == 0, Waits.Bound);
    }

    private static async Task Shows(RichReplHarness h, Func<string, bool> holds, string what) =>
        Assert.True(await h.UntilAsync(() => holds(h.ScreenText()), Waits.Bound), $"{what}:\n{h.ScreenText()}");

    [Fact]
    public async Task TOOLS_AT_REST_LISTS_UNDER_ITS_TITLE_OPENS_THE_TEXT_AND_CLOSES_WITH_NO_LISTING_LEFT()
    {
        var h = Harness(() => Two);
        var run = h.RunAsync(60_000);

        h.Keys.Line("/tools");
        await Shows(h, s => s.Contains("tools · 2 armed") && s.Contains("read a file") && s.Contains("ask_user.csx"),
            "the panel never listed the two tools under its title");
        await Shows(h, s => s.Contains("Enter detail   Esc close"), "the key row never named the list's acts");

        await Press(h, EnterKey);
        await Shows(h, s => s.Contains("Read a file from disk.") && s.Contains("The path is relative to the project.") && s.Contains("Esc back"),
            "the first row's detail never showed the model's text and its key");
        Assert.DoesNotContain("ask before guessing", h.ScreenText(), StringComparison.Ordinal);   //a built-in has no policy row
        await Press(h, Esc);
        await Shows(h, s => s.Contains("ask_user.csx") && !s.Contains("Esc back"), "Esc in the detail never returned to the list");
        await Press(h, Esc);
        await Release(h);
        await Shows(h, s => !s.Contains("tools · 2 armed"), "the panel never closed back to the composer");
        Assert.DoesNotContain("tools armed this session", h.ScreenText(), StringComparison.Ordinal);   //no transcript listing
        h.Keys.Line("/quit");
        await run;
    }

    [Fact]
    public async Task AN_EXTENSIONS_DETAIL_ENDS_WITH_ITS_POLICY_LINE()
    {
        var h = Harness(() => Two);
        var run = h.RunAsync(60_000);

        h.Keys.Line("/tools");
        await Shows(h, s => s.Contains("ask_user.csx"), "the panel never opened");
        await Press(h, new ConsoleKeyInfo('\0', ConsoleKey.DownArrow, false, false, false));
        await Press(h, EnterKey);
        await Shows(h, s => s.Contains("Ask the user a question.") && s.Contains("ask before guessing a choice"),
            "the extension's detail never showed its text and its policy line");
        await Press(h, Esc);
        await Press(h, Esc);
        await Release(h);
        h.Keys.Line("/quit");
        await run;
    }

    //the same title and key row while a turn runs, read-only, and the turn's text lands once the panel closes
    [Fact]
    public async Task TOOLS_WHILE_A_TURN_RUNS_SHOWS_THE_SAME_PANEL_AND_THE_TURN_ENDS_BEHIND_IT()
    {
        var h = Harness(() => Two);
        var held = new TaskCompletionSource();
        h.Client.HoldNextTurn = ct => held.Task.WaitAsync(ct);
        h.Client.EnqueueTurn(new StreamEvent.TextDelta("the answer"), new StreamEvent.Finished("stop", null));
        var run = h.RunAsync(60_000);

        h.Keys.Line("go");
        Assert.True(await h.UntilAsync(() => h.Client.Requests.Count == 1, Waits.Bound), "the turn never started");
        h.Keys.Line("/tools");
        await Shows(h, s => s.Contains("tools · 2 armed") && s.Contains("Enter detail   Esc close"),
            "the mid-turn panel never showed the title and the key row it shows at rest");
        await Press(h, EnterKey);
        await Shows(h, s => s.Contains("Read a file from disk."), "the mid-turn detail never opened");

        held.SetResult();
        Assert.True(await h.UntilAsync(() => h.Saw("the answer"), Waits.Bound), "the turn's text never landed behind the panel");
        await Press(h, Esc);
        await Press(h, Esc);
        await Release(h);
        await Shows(h, s => !s.Contains("tools · 2 armed"), "the panel never closed");
        h.Keys.Line("/quit");
        await run;
    }

    [Fact]
    public async Task NO_TOOL_LIST_SAYS_IT_IS_UNAVAILABLE()
    {
        var h = Harness(null);
        var run = h.RunAsync(60_000);

        h.Keys.Line("/tools");
        await Shows(h, s => s.Contains("the tool list is unavailable in this session"), "the unavailable line never printed");
        Assert.DoesNotContain("tools ·", h.ScreenText(), StringComparison.Ordinal);
        h.Keys.Line("/quit");
        await run;
    }

    [Fact]
    public async Task NO_TOOLS_AT_REST_SAYS_SO_AND_OPENS_NOTHING()
    {
        var h = Harness(() => []);
        var run = h.RunAsync(60_000);

        h.Keys.Line("/tools");
        await Shows(h, s => s.Contains("no tools are armed this session"), "the empty line never printed");
        Assert.DoesNotContain("Esc close", h.ScreenText(), StringComparison.Ordinal);
        h.Keys.Line("/quit");
        await run;
    }

    //a line typed between two streamed pieces of one reply waits for the block, so the reply reads as one piece
    [Fact]
    public async Task THE_EMPTY_LINE_MID_REPLY_WAITS_FOR_THE_BLOCK_AND_THE_REPLY_STAYS_WHOLE()
    {
        var h = Harness(() => []);
        await SaysBetweenBlocks(h, "no tools are armed this session");
    }

    [Fact]
    public async Task A_THROWING_TOOL_LIST_SAYS_ITS_OWN_MESSAGE_AND_THE_REPLY_STAYS_WHOLE()
    {
        var h = Harness(() => throw new InvalidOperationException("the registry went away"));
        await SaysBetweenBlocks(h, "the registry went away");
    }

    private static async Task SaysBetweenBlocks(RichReplHarness h, string line)
    {
        var held = new TaskCompletionSource();
        h.Client.HoldAfterFirstEvent = ct => held.Task.WaitAsync(ct);
        h.Client.EnqueueTurn(new StreamEvent.TextDelta("first half "), new StreamEvent.TextDelta("second half"), new StreamEvent.Finished("stop", null));
        var run = h.RunAsync(60_000);

        h.Keys.Line("go");
        Assert.True(await h.UntilAsync(() => h.Saw("first half"), Waits.Bound), "the reply's first piece never streamed");
        h.Keys.Line("/tools");
        await h.UntilAsync(() => h.Keys.Handled, Waits.Bound);
        await Task.Delay(300);
        Assert.False(h.Saw(line), "the line cut into the open block");

        held.SetResult();
        Assert.True(await h.UntilAsync(() => h.Saw(line), Waits.Bound), "the line never printed after the block closed");
        await Shows(h, s => s.Contains("first half second half"), "the reply was cut");
        h.Keys.Line("/quit");
        await run;
    }

    //a turn-thread prompt holding the slot is the answer on screen, so /tools opens nothing, and a prompt arriving closes an open panel
    [Fact]
    public async Task A_PROMPT_HOLDING_THE_SLOT_KEEPS_THE_PANEL_SHUT_AND_A_NEW_ONE_CLOSES_IT()
    {
        var chrome = new ChromeHandle();
        var h = Harness(() => Two, chrome);
        var run = h.RunAsync(60_000);
        await h.UntilAsync(() => chrome.HoldModalSlot is not null, Waits.Bound);

        var slot = await Task.Run(() => chrome.HoldModalSlot!.Invoke()).WaitAsync(Waits.Bound);
        h.Keys.Line("/tools");
        await h.UntilAsync(() => h.Keys.Handled, Waits.Bound);
        await Task.Delay(300);
        Assert.DoesNotContain("tools · 2 armed", h.ScreenText(), StringComparison.Ordinal);   //the slot is held, so the open did nothing
        slot!.Dispose();

        _ = h.Keys.KeyAvailable;
        h.Keys.Line("/tools");
        await Shows(h, s => s.Contains("tools · 2 armed"), "the panel never opened once the slot was free");
        IDisposable? second = null;
        var prompt = Task.Run(() =>
        {
            second = chrome.HoldModalSlot!.Invoke();
            chrome.Painter!.SetPanelFactory((_, _) => new PanelContent(["  Allow shell?"]));
        });
        await prompt.WaitAsync(Waits.Bound);
        await Shows(h, s => s.Contains("Allow shell?") && !s.Contains("tools · 2 armed"), "the prompt never closed the panel");
        chrome.Painter!.SetPanel(null);
        second!.Dispose();
        h.Keys.Line("/quit");
        await run;
    }
}
