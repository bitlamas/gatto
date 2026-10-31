using System.Text.Json;
using Gatto.Core.Client;
using Gatto.Core.Commands;
using Gatto.Core.Home;
using Gatto.Core.Tools;
using Gatto.Repl.Prompts;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Repl;

//the /help panel through the rich face: its three tabs, Enter compose, the toggle and what it leaves behind, the same panel while a turn runs
[Collection("e2e")]
public sealed class HelpPanelReplTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-help-panel-").FullName;
    private readonly string _cwd;

    public HelpPanelReplTests()
    {
        Environment.SetEnvironmentVariable("GATTO_HOME", _home);
        _cwd = Directory.CreateDirectory(Path.Combine(_home, "project")).FullName;
        File.WriteAllText(Path.Combine(_home, "gatto.json"), """{"endpoints":{},"default_endpoint":"local"}""");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("GATTO_HOME", null);
        try { Directory.Delete(_home, recursive: true); }
        catch (Exception) { }   //a test that failed mid-run may still hold the folder for a moment
    }

    private static readonly ConsoleKeyInfo Esc = new('\u001b', ConsoleKey.Escape, false, false, false);
    private static readonly ConsoleKeyInfo EnterKey = new('\r', ConsoleKey.Enter, false, false, false);
    private static readonly ConsoleKeyInfo Down = new('\0', ConsoleKey.DownArrow, false, false, false);
    private static readonly ConsoleKeyInfo Right = new('\0', ConsoleKey.RightArrow, false, false, false);
    private static readonly ConsoleKeyInfo Left = new('\0', ConsoleKey.LeftArrow, false, false, false);
    private static readonly ConsoleKeyInfo Space = new(' ', ConsoleKey.Spacebar, false, false, false);
    private static readonly ConsoleKeyInfo Backspace = new('\b', ConsoleKey.Backspace, false, false, false);

    //a home of four sources in discovery order: cards loaded with a command, late off at launch, lumo serving the endpoint, web_search loaded with two tools
    private sealed class Home
    {
        private readonly string _path;
        public readonly ArmedSources Armed = new(["late"]);
        public readonly ToolRegistry Tools;
        public readonly CommandRegistry Commands;
        public readonly List<(string Name, bool On)> Sets = [];
        public string? FailWith;

        public Home(string path)
        {
            _path = path;
            Tools = new ToolRegistry(Armed);
            Tools.Register(new StubTool("read_file"));
            Tools.Register(new StubTool("web_search"), extension: true, origin: "web_search");
            Tools.Register(new StubTool("web_fetch"), extension: true, origin: "web_search");
            Commands = new CommandRegistry(Armed);
            Commands.Add(CommandRow.OfText("/card", "", "draw a card", (_, _) => Task.FromResult("a card"), "cards.csx", "cards"));
        }

        public IReadOnlyList<ExtensionView> Views() =>
        [
            new("cards", Armed.IsOn("cards"), true, false, null, false, [], ["/card"], []),
            new("late", Armed.IsOn("late"), false, true, null, false, [], [], []),
            new("lumo", Armed.IsOn("lumo"), true, false, null, true, [], [], ["lumo"]),
            new("web_search", Armed.IsOn("web_search"), true, false, null, false, ["web_search", "web_fetch"], [], []),
        ];

        public string? Set(string name, bool on)
        {
            Sets.Add((name, on));
            Armed.Set(name, on);
            if (FailWith is { } line) return line;
            GattoConfigWriter.SetExtensionsOff(_path, Armed.Off);
            return null;
        }
    }

    private RichReplHarness Harness(Home home) =>
        new(_cwd, tools: home.Tools, commands: home.Commands,
            listTools: () => [.. home.Tools.Specs().Select(s => new ToolInfo(s.Name, s.Description, null))],
            listExtensions: home.Views, setExtension: home.Set);

    private string[]? OffInFile()
    {
        var root = JsonDocument.Parse(File.ReadAllText(Path.Combine(_home, "gatto.json"))).RootElement;
        return root.TryGetProperty("extensions_off", out var off) ? [.. off.EnumerateArray().Select(e => e.GetString()!)] : null;
    }

    //a panel reads its last key without sampling, as every modal does, so the test samples once that key has left and the composer gets the next
    private static async Task Release(RichReplHarness h)
    {
        await h.UntilAsync(() => h.Keys.Pending == 0, Waits.Bound);
        await Task.Delay(100);
        _ = h.Keys.KeyAvailable;
    }

    private static async Task Press(RichReplHarness h, ConsoleKeyInfo key)
    {
        _ = h.Keys.KeyAvailable;
        h.Keys.Key(key);
        await h.UntilAsync(() => h.Keys.Pending == 0, Waits.Bound);
    }

    private static async Task Shows(RichReplHarness h, Func<string, bool> holds, string what) =>
        Assert.True(await h.UntilAsync(() => holds(h.ScreenText()), Waits.Bound), $"{what}:\n{h.ScreenText()}");

    private static async Task ToExtensions(RichReplHarness h)
    {
        h.Keys.Line("/help");
        await Shows(h, s => s.Contains("Enter compose"), "the panel never opened on the commands tab");
        await Press(h, Right);
        await Press(h, Right);
        await Shows(h, s => s.Contains("web_search") && s.Contains("lumo"), "the extensions tab never showed its rows");
    }

    [Fact]
    public async Task HELP_AT_REST_SHOWS_THREE_TABS_AND_EACH_TAB_ITS_ROWS_AND_KEYS()
    {
        var home = new Home(_home);
        var h = Harness(home);
        var run = h.RunAsync(60_000);

        h.Keys.Line("/help");
        await Shows(h, s => s.Contains("help    commands    shortcuts    extensions") && s.Contains("this screen") && s.Contains("↓ 10 more"),
            "the commands tab never listed the built-ins and the registered row under the tabbed title");
        await Shows(h, s => s.Contains("↑↓ move   Enter compose   ←→ tabs   Esc close"), "the commands tab's key row");
        await Press(h, Right);
        await Shows(h, s => s.Contains(Gatto.Terminal.WildKeys.KeyName) && s.Contains("wild mode on or off") && s.Contains("←→ tabs   Esc close")
            && !s.Contains("Enter compose"), "the shortcuts tab never showed its rows and its two keys");
        await Press(h, Right);
        await Shows(h, s => s.Contains("tools web_search, web_fetch") && s.Contains("command /card"), "the extensions tab never named what each source holds");
        await Shows(h, s => s.Contains("Space on or off"), "the extensions tab's key row never named Space on a loaded source");
        await Press(h, Esc);
        await Release(h);
        await Shows(h, s => !s.Contains("Enter compose") && !s.Contains("wild mode on or off"), "the panel never closed");
        h.Keys.Line("/quit");
        await run;
    }

    [Fact]
    public async Task ENTER_ON_A_COMMAND_PUTS_ITS_TEXT_IN_THE_COMPOSER_AND_RUNS_NOTHING()
    {
        var h = Harness(new Home(_home));
        var run = h.RunAsync(60_000);

        h.Keys.Line("/help");
        await Shows(h, s => s.Contains("Enter compose"), "the panel never opened");
        for (var i = 0; i < 4; i++) await Press(h, Down);
        await Press(h, EnterKey);
        await Release(h);
        h.Keys.Key(new ConsoleKeyInfo('z', ConsoleKey.Z, false, false, false));
        await Shows(h, s => s.Contains("/model z"), "the composer never held the command with its space");
        Assert.Empty(h.Client.Requests);
        for (var i = 0; i < 8; i++) h.Keys.Key(Backspace);
        h.Keys.Line("/quit");
        await run;
    }

    [Fact]
    public async Task SPACE_TURNS_A_LOADED_SOURCE_OFF_AND_ON_AT_ONCE_AND_THE_FILE_FOLLOWS()
    {
        var home = new Home(_home);
        var h = Harness(home);
        var run = h.RunAsync(60_000);

        await ToExtensions(h);
        for (var i = 0; i < 3; i++) await Press(h, Down);
        await Press(h, Space);
        await Shows(h, s => s.Contains("off  tools web_search, web_fetch"), "the row never said off before its names");
        Assert.DoesNotContain("web_search", home.Tools.Specs().Select(t => t.Name));
        Assert.Equal(["late", "web_search"], OffInFile());

        await Press(h, Space);
        await Shows(h, s => !s.Contains("off  tools web_search"), "the row never came back on");
        Assert.Contains("web_search", home.Tools.Specs().Select(t => t.Name));
        Assert.Equal(["late"], OffInFile());
        await Press(h, Esc);
        await Release(h);
        //a fresh session's first request reads everything anyway, so no line is owed
        Assert.DoesNotContain("reads the whole conversation again", h.ScreenText(), StringComparison.Ordinal);
        h.Keys.Line("/quit");
        await run;
    }

    [Fact]
    public async Task A_SOURCE_OFF_AT_LAUNCH_TURNED_ON_WAITS_FOR_THE_NEXT_LAUNCH_AND_THE_ENDPOINT_S_FILE_NAMES_NO_SPACE()
    {
        var home = new Home(_home);
        var h = Harness(home);
        var run = h.RunAsync(60_000);

        await ToExtensions(h);
        await Press(h, Down);
        await Shows(h, s => s.Contains("late") && s.Contains("Space on or off"), "the off row never named Space");
        await Press(h, Space);
        await Shows(h, s => s.Contains("on at the next launch"), "the row off at launch never said it waits for the next launch");
        Assert.False(home.Views()[1].Loaded);

        await Press(h, Down);
        await Shows(h, s => s.Contains("serves this session's endpoint") && !s.Contains("Space on or off"),
            "the endpoint's file still named Space or never said what it serves");
        await Press(h, Space);
        Assert.Equal([("late", true)], home.Sets);
        await Press(h, Esc);
        await Release(h);
        h.Keys.Line("/quit");
        await run;
    }

    //off moves the tool list, so a talked session owes the reread line at the close, and a call written against the old list answers unknown tool
    [Fact]
    public async Task AN_OFF_IN_A_TALKED_SESSION_SAYS_THE_REREAD_LINE_AND_A_LATER_CALL_ANSWERS_UNKNOWN_TOOL()
    {
        var home = new Home(_home);
        var h = Harness(home);
        h.Client.EnqueueTurn(new StreamEvent.TextDelta("first answer"), new StreamEvent.Finished("stop", null));
        h.Client.EnqueueTurn(new StreamEvent.ToolCallReady(new ToolCall("c1", "web_search", "{}")), new StreamEvent.Finished("tool_calls", null));
        h.Client.EnqueueTurn(new StreamEvent.TextDelta("done"), new StreamEvent.Finished("stop", null));
        var run = h.RunAsync(60_000);

        h.Keys.Line("hello");
        await Shows(h, s => s.Contains("first answer"), "the first turn never answered");
        await ToExtensions(h);
        for (var i = 0; i < 3; i++) await Press(h, Down);
        await Press(h, Space);
        await Shows(h, s => s.Contains("off  tools web_search"), "the row never turned off");
        await Press(h, Esc);
        await Release(h);
        await Shows(h, s => s.Contains("the next request reads the whole conversation again"), "the close never said the reread line");

        h.Keys.Line("search");
        await Shows(h, s => s.Contains("done"), "the turn with the call never ended");
        Assert.Contains(h.Convo.Messages, m => m.Role == "tool" && m.Content == "unknown tool: web_search");
        h.Keys.Line("/quit");
        await run;
    }

    [Fact]
    public async Task A_WRITE_THAT_FAILS_LEAVES_THE_ROW_TOGGLED_WITH_ITS_LINE_UNDER_IT_AND_THE_PANEL_UP()
    {
        var home = new Home(_home) { FailWith = "could not write gatto.json: held by another program" };
        var h = Harness(home);
        var run = h.RunAsync(60_000);

        await ToExtensions(h);
        await Press(h, Space);
        await Shows(h, s => s.Contains("off  command /card") && s.Contains("could not write gatto.json: held by another program") && s.Contains("Space on or off"),
            "the row never turned off with the write's line under it");
        Assert.False(home.Armed.IsOn("cards"));
        await Press(h, Esc);
        await Release(h);
        h.Keys.Line("/quit");
        await run;
    }

    //an off command leaves the commands tab when the user goes back to it, rather than being typed and refused
    [Fact]
    public async Task AFTER_AN_OFF_THE_COMMANDS_TAB_NO_LONGER_LISTS_THE_SOURCE_S_COMMAND()
    {
        var home = new Home(_home);
        var h = Harness(home);
        var run = h.RunAsync(60_000);

        await ToExtensions(h);
        await Press(h, Space);
        await Shows(h, s => s.Contains("off  command /card"), "cards never turned off");
        await Press(h, Left);
        await Press(h, Left);
        await Shows(h, s => s.Contains("Enter compose") && !s.Contains("cards.csx") && !s.Contains("draw a card"), "the commands tab still listed /card");
        await Press(h, Esc);
        await Release(h);
        h.Keys.Line("/quit");
        await run;
    }

    //read-only while a turn runs: Space is neither named nor taken, and the row names it again once the turn ends under the open panel
    [Fact]
    public async Task MID_TURN_SPACE_IS_NOT_NAMED_OR_TAKEN_AND_COMES_BACK_WHEN_THE_TURN_ENDS()
    {
        var home = new Home(_home);
        var h = Harness(home);
        var held = new TaskCompletionSource();
        h.Client.HoldNextTurn = ct => held.Task.WaitAsync(ct);
        h.Client.EnqueueTurn(new StreamEvent.TextDelta("the answer"), new StreamEvent.Finished("stop", null));
        var run = h.RunAsync(60_000);

        h.Keys.Line("go");
        Assert.True(await h.UntilAsync(() => h.Client.Requests.Count == 1, Waits.Bound), "the turn never started");
        await ToExtensions(h);
        await Shows(h, s => s.Contains("↑↓ move   ←→ tabs   Esc close") && !s.Contains("Space on or off"), "the mid-turn row named Space");
        await Press(h, Space);
        Assert.Empty(home.Sets);

        held.SetResult();
        await Shows(h, s => s.Contains("Space on or off"), "the row never named Space once the turn ended under the panel");
        await Press(h, Space);
        Assert.True(await h.UntilAsync(() => home.Sets.Count == 1, Waits.Bound), "Space never acted once named");
        await Press(h, Esc);
        await Release(h);
        await Shows(h, s => s.Contains("the answer"), "the turn's text never landed behind the panel");
        h.Keys.Line("/quit");
        await run;
    }
}
