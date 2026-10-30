using Gatto.Core.Client;
using Gatto.Core.Loop.Permissions;
using Gatto.Repl.Commands;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Repl;

//the /permissions panel through the rich face: the grants under their header, a digit moving, r revoking, the empty answers and the read-only panel while a turn runs
[Collection("e2e")]
public sealed class PermissionsPanelReplTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-permissions-panel-").FullName;
    private readonly string _cwd;

    public PermissionsPanelReplTests()
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
    private static readonly ConsoleKeyInfo R = new('r', ConsoleKey.R, false, false, false);
    private static readonly ConsoleKeyInfo Three = new('3', ConsoleKey.D3, false, false, false);
    private static readonly ConsoleKeyInfo ShiftTab = new('\t', ConsoleKey.Tab, true, false, false);

    //short, so the row and the revoked line each fit one row of the harness's width
    private const string WriteDir = @"C:\work";

    //three grants of the three kinds, persisted, in the order the listing numbers them
    private PermissionStore Store(params string[] shells)
    {
        var store = PermissionStore.Load(_home, _cwd, out _);
        foreach (var prefix in shells.Length == 0 ? ["git status"] : shells) store.GrantShellPrefix(prefix, persist: true);
        if (shells.Length == 0)
        {
            store.GrantWriteDir(WriteDir, persist: true);
            store.GrantTool("web_search", persist: true);
        }
        return store;
    }

    private RichReplHarness Harness(PermissionStore? store, WildState? wild = null) =>
        new(_cwd, permissions: store, wild: wild ?? new WildState());

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

    private static async Task Quit(RichReplHarness h, Task<int> run)
    {
        h.Keys.Line("/quit");
        await run;
    }

    [Fact]
    public async Task PERMISSIONS_AT_REST_SHOWS_THE_COUNT_THE_HEADER_THE_ROWS_AND_R_REVOKE()
    {
        var h = Harness(Store());
        var run = h.RunAsync(60_000);

        h.Keys.Line("/permissions");
        await Shows(h, s => s.Contains("permissions · 3 grants") && s.Contains("kind   target") && s.Contains("r revoke   Esc close"),
            "the panel never opened with its title, header and keys");
        await Shows(h, s => s.Contains("❯ 1. shell  git status") && s.Contains("2. write  " + WriteDir) && s.Contains("3. tool   web_search"),
            "the grants never drew numbered, kind then target");
        Assert.DoesNotContain("wild on", h.ScreenText(), StringComparison.Ordinal);
        Assert.DoesNotContain("/permissions revoke <n>", h.ScreenText(), StringComparison.Ordinal);
        await Press(h, Esc);
        await Release(h);
        await Shows(h, s => !s.Contains("r revoke"), "Esc never closed the panel");
        Assert.DoesNotContain("revoked:", h.ScreenText(), StringComparison.Ordinal);
        await Quit(h, run);
    }

    [Fact]
    public async Task WILD_ON_SAYS_THE_SUBTITLE_AND_A_TOGGLE_UNDER_THE_PANEL_TAKES_IT_AWAY_AND_BACK()
    {
        var wild = new WildState { On = true };
        var h = Harness(Store(), wild);
        var run = h.RunAsync(60_000);

        h.Keys.Line("/permissions");
        await Shows(h, s => s.Contains("wild on · prompts off this session") && s.Contains("kind   target"), "the subtitle never said wild was on");
        await Press(h, ShiftTab);
        Assert.True(await h.UntilAsync(() => !wild.On, Waits.Bound), "the toggle never reached the wild state under the panel");
        await Shows(h, s => !s.Contains("prompts off this session") && s.Contains("r revoke"), "the subtitle stayed after wild went off");
        await Press(h, ShiftTab);
        await Shows(h, s => s.Contains("wild on · prompts off this session") && s.Contains("r revoke"), "the subtitle never came back with wild");
        await Press(h, Esc);
        await Release(h);
        await Quit(h, run);
    }

    [Fact]
    public async Task A_DIGIT_MOVES_THE_CURSOR_AND_ENTER_LEAVES_THE_PANEL_UP()
    {
        var store = Store();
        var h = Harness(store);
        var run = h.RunAsync(60_000);

        h.Keys.Line("/permissions");
        await Shows(h, s => s.Contains("r revoke   Esc close"), "the panel never opened");
        await Press(h, Three);
        await Shows(h, s => s.Contains("❯ 3. tool"), "3 never moved the cursor to the third row");
        await Press(h, EnterKey);
        await Release(h);
        await Shows(h, s => s.Contains("❯ 3. tool") && s.Contains("r revoke   Esc close"), "Enter closed or moved the panel");
        Assert.Equal(3, store.ListEntries().Count);
        await Press(h, Esc);
        await Release(h);
        await Quit(h, run);
    }

    [Fact]
    public async Task R_REVOKES_THE_CURSOR_ROW_SAYS_THE_TYPED_LINE_AND_THE_ROWS_READ_AGAIN()
    {
        var store = Store();
        var h = Harness(store);
        var run = h.RunAsync(60_000);

        h.Keys.Line("/permissions");
        await Shows(h, s => s.Contains("r revoke   Esc close"), "the panel never opened");
        await Press(h, Down);
        await Shows(h, s => s.Contains("❯ 2. write"), "the cursor never reached the write row");
        await Press(h, R);
        var line = "revoked: " + CommandText.FormatKindAndText(PermissionKind.WriteDir, WriteDir);
        await Shows(h, s => s.Contains(line) && s.Contains("permissions · 2 grants") && s.Contains("❯ 2. tool"),
            "r never said the revoked line and re-read the rows with the cursor on the row that took the place");
        Assert.Equal("revoked: write " + WriteDir, line);
        Assert.DoesNotContain(store.ListEntries(), e => e.Kind == PermissionKind.WriteDir);
        Assert.DoesNotContain(PermissionStore.Load(_home, _cwd, out _).ListEntries(), e => e.Kind == PermissionKind.WriteDir);
        await Press(h, Esc);
        await Release(h);
        await Quit(h, run);
    }

    [Fact]
    public async Task R_ON_THE_LAST_GRANT_CLOSES_THE_PANEL_AND_SAYS_THE_EMPTY_LINES()
    {
        var store = Store("git status");
        var h = Harness(store);
        var run = h.RunAsync(60_000);

        h.Keys.Line("/permissions");
        await Shows(h, s => s.Contains("permissions · 1 grant") && s.Contains("r revoke"), "the panel never opened on one grant");
        await Press(h, R);
        await Release(h);
        await Shows(h, s => s.Contains("revoked: shell git status") && s.Contains("no standing grants") && s.Contains("wild mode: off")
            && !s.Contains("r revoke"), "the last revoke never closed the panel with the empty lines");
        Assert.Empty(store.ListEntries());
        await Quit(h, run);
    }

    [Fact]
    public async Task ALL_AND_ALL_AFTER_TWO_SPACES_OPEN_THE_PANEL_AND_A_MIXED_CASE_ALL_ANSWERS_THE_USAGE()
    {
        var h = Harness(Store());
        var run = h.RunAsync(60_000);

        h.Keys.Line("/permissions all");
        await Shows(h, s => s.Contains("permissions · 3 grants"), "all never opened the panel");
        await Press(h, Esc);
        await Release(h);
        h.Keys.Line("/permissions  all");
        await Shows(h, s => s.Contains("r revoke   Esc close"), "all after two spaces never opened the panel");
        await Press(h, Esc);
        await Release(h);
        h.Keys.Line("/permissions All");
        await Shows(h, s => s.Contains("usage: /permissions"), "a mixed-case all never answered the usage line");
        Assert.DoesNotContain("… ", h.ScreenText(), StringComparison.Ordinal);
        await Quit(h, run);
    }

    [Fact]
    public async Task AN_EMPTY_STORE_SAYS_THE_TWO_LINES_AND_OPENS_NO_PANEL()
    {
        var h = Harness(PermissionStore.Load(_home, _cwd, out _));
        var run = h.RunAsync(60_000);

        h.Keys.Line("/permissions");
        await Shows(h, s => s.Contains(CommandText.PermissionsEmpty) && s.Contains("wild mode: off"), "the empty store never said its lines");
        Assert.DoesNotContain("r revoke", h.ScreenText(), StringComparison.Ordinal);
        await Quit(h, run);
    }

    [Fact]
    public async Task A_NULL_STORE_SAYS_IT_IS_UNAVAILABLE()
    {
        var h = Harness(null);
        var run = h.RunAsync(60_000);

        h.Keys.Line("/permissions");
        await Shows(h, s => s.Contains("permissions are unavailable in this session"), "a null store never said it was unavailable");
        await Quit(h, run);
    }

    [Fact]
    public async Task A_GRANT_HOLDING_ESCAPE_BYTES_PRINTS_INERT_ON_ITS_OWN_ROW()
    {
        var store = PermissionStore.Load(_home, _cwd, out _);
        store.GrantShellPrefix("git \u001b[2Jstatus", persist: true);
        store.GrantTool("web\u001b]8;;http://evil\u0007_search", persist: true);
        var h = Harness(store);
        var run = h.RunAsync(60_000);

        h.Keys.Line("/permissions");
        await Shows(h, s => s.Contains("1. shell  git [2Jstatus") && s.Contains("2. tool") && s.Contains("_search"),
            "an escape payload never printed as inert text on its own row");
        await Press(h, Esc);
        await Release(h);
        await Quit(h, run);
    }

    [Fact]
    public async Task MID_TURN_THE_PANEL_READS_ONLY_AND_THE_TURNS_END_BRINGS_R_BACK()
    {
        var store = Store();
        var h = Harness(store);
        var held = new TaskCompletionSource();
        h.Client.HoldNextTurn = ct => held.Task.WaitAsync(ct);
        h.Client.EnqueueTurn(new StreamEvent.TextDelta("the answer"), new StreamEvent.Finished("stop", null));
        var run = h.RunAsync(60_000);

        h.Keys.Line("go");
        Assert.True(await h.UntilAsync(() => h.Client.Requests.Count == 1, Waits.Bound), "the turn never started");
        h.Keys.Line("/permissions");
        await Shows(h, s => s.Contains("permissions · 3 grants") && s.Contains("Esc close") && !s.Contains("r revoke"),
            "the mid-turn panel named r");
        await Press(h, R);
        Assert.Equal(3, store.ListEntries().Count);

        held.SetResult();
        await Shows(h, s => s.Contains("r revoke   Esc close"), "r never came back once the turn ended under the panel");
        await Press(h, R);
        Assert.True(await h.UntilAsync(() => store.ListEntries().Count == 2, Waits.Bound), "r never revoked once named");
        //the re-read panel holds the keys again only once it is drawn, an Esc sent before then reaches the composer
        await Shows(h, s => s.Contains("permissions · 2 grants"), "the rows never read again after the revoke");
        await Press(h, Esc);
        await Release(h);
        await Shows(h, s => s.Contains("the answer") && s.Contains("revoked: shell git status") && !s.Contains("r revoke"),
            "the panel never closed with the turn's text and the revoke behind it");
        await Quit(h, run);
    }

    [Fact]
    public async Task A_COMMAND_WITH_AN_ARGUMENT_TYPED_MID_TURN_OPENS_NO_GRANTS()
    {
        var h = Harness(Store());
        var held = new TaskCompletionSource();
        h.Client.HoldNextTurn = ct => held.Task.WaitAsync(ct);
        h.Client.EnqueueTurn(new StreamEvent.TextDelta("the answer"), new StreamEvent.Finished("stop", null));
        h.Client.EnqueueTurn(new StreamEvent.TextDelta("second"), new StreamEvent.Finished("stop", null));
        var run = h.RunAsync(60_000);

        h.Keys.Line("go");
        Assert.True(await h.UntilAsync(() => h.Client.Requests.Count == 1, Waits.Bound), "the turn never started");
        h.Keys.Line("/tools x");
        await Release(h);
        await Task.Delay(300);
        Assert.DoesNotContain("git status", h.ScreenText(), StringComparison.Ordinal);
        Assert.DoesNotContain("web_search", h.ScreenText(), StringComparison.Ordinal);
        held.SetResult();
        await Shows(h, s => s.Contains("the answer"), "the turn never ended");
        await Quit(h, run);
    }

    [Fact]
    public void A_REVOKE_WHILE_A_TURN_IS_IN_FLIGHT_IS_SPENT_AND_LEAVES_THE_STORE_WHOLE()
    {
        var store = Store();
        var turnLock = new object();

        Assert.Null(Gatto.Repl.Repl.RevokeAtRest(turnLock, () => true, store, 0));
        Assert.Equal(3, store.ListEntries().Count);
        Assert.Equal(new PermissionEntry(PermissionKind.ShellPrefix, "git status"), Gatto.Repl.Repl.RevokeAtRest(turnLock, () => false, store, 0));
        Assert.Equal(2, PermissionStore.Load(_home, _cwd, out _).ListEntries().Count);
    }
}
