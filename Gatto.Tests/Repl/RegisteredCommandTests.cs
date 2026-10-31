using Gatto.Core.Commands;
using Gatto.Repl;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Repl;

public class RegisteredCommandTests
{
    private static CommandRegistry Undo(Func<string, string> answer)
    {
        var reg = new CommandRegistry();
        reg.Add(CommandRow.OfText("/undo", "[n|path]", "list snapshots for this folder, or restore one",
            (arg, _) => Task.FromResult(answer(arg)), "undo.csx"));
        return reg;
    }

    //a two-row listing whose Pick records the index it got, the second row from another process when began is set
    private static CommandRegistry Listed(List<int> picks, Func<int, string>? pick = null, bool began = false)
    {
        var reg = new CommandRegistry();
        reg.Add(new CommandRow("/undo", "[n|path]", "list snapshots for this folder, or restore one", (_, _) => Task.FromResult<CommandAnswer>(
            new Listing("snapshot", "restore",
                [
                    new("write", began ? ["12 B", "14:02", "", "a.txt"] : ["12 B", "14:02", "a.txt"], "restore puts back 12 B from 14:02"),
                    new("edit", began ? ["absent", "13:50", "10-29 08:58", "b.txt"] : ["absent", "13:50", "b.txt"], "restore deletes b.txt, which did not exist at 13:50"),
                ],
                i => { picks.Add(i); return Task.FromResult(pick is null ? $"restored row {i}" : pick(i)); },
                began ? ["tool", "size", "when", "began", "file"] : ["tool", "size", "when", "file"])), "undo.csx"));
        return reg;
    }

    private static readonly ConsoleKeyInfo Esc = new('\u001b', ConsoleKey.Escape, false, false, false);
    private static readonly ConsoleKeyInfo EnterKey = new('\r', ConsoleKey.Enter, false, false, false);
    private static readonly ConsoleKeyInfo Digit2 = new('2', ConsoleKey.D2, false, false, false);

    //a panel reads its last key without sampling, so the test samples once that key has left and the composer gets the next
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

    [Fact]
    public async Task A_LISTING_OPENS_A_PANEL_A_DIGIT_MOVES_AND_ENTER_SAYS_WHAT_PICK_RETURNED()
    {
        var picks = new List<int>();
        var h = new RichReplHarness(Path.GetTempPath(), commands: Listed(picks));
        var run = h.RunAsync(60_000);

        h.Keys.Line("/undo");
        await Shows(h, s => s.Contains("undo · 2 snapshots") && s.Contains("Enter restore   Esc close")
            && s.Contains("when") && s.Contains("file") && s.Contains("restore puts back 12 B from 14:02"),
            "the panel never showed its title, header, key row and the first row's sentence");
        await Press(h, Digit2);
        await Shows(h, s => s.Contains("restore deletes b.txt") && !s.Contains("restore puts back") && s.Contains("undo · 2 snapshots"),
            "the digit never moved the cursor to the second row with the panel kept");
        Assert.Empty(picks);
        await Press(h, EnterKey);
        await Release(h);
        Assert.True(await h.UntilAsync(() => h.Saw("restored row 1"), Waits.Bound), h.ScreenText());
        await Shows(h, s => !s.Contains("Enter restore"), "Enter never closed the panel");
        Assert.Equal([1], picks);
        h.Keys.Line("/quit");
        await run;
    }

    [Fact]
    public async Task ESC_CLOSES_THE_LISTING_WITH_NOTHING_SAID_AND_NO_PICK()
    {
        var picks = new List<int>();
        var h = new RichReplHarness(Path.GetTempPath(), commands: Listed(picks));
        var run = h.RunAsync(60_000);

        h.Keys.Line("/undo");
        await Shows(h, s => s.Contains("Enter restore   Esc close"), "the panel never opened");
        await Press(h, Esc);
        await Release(h);
        await Shows(h, s => !s.Contains("Enter restore"), "Esc never closed the panel");
        h.Keys.Line("/quit");
        await run;
        Assert.Empty(picks);
        Assert.False(h.Saw("restored row"));
    }

    [Fact]
    public async Task A_PICK_THAT_THROWS_SAYS_ITS_MESSAGE_ALONE()
    {
        var h = new RichReplHarness(Path.GetTempPath(), commands: Listed([], _ => throw new InvalidOperationException("the copy is gone")));
        var run = h.RunAsync(60_000);

        h.Keys.Line("/undo");
        await Shows(h, s => s.Contains("Enter restore"), "the panel never opened");
        await Press(h, EnterKey);
        await Release(h);
        Assert.True(await h.UntilAsync(() => h.Saw("the copy is gone"), Waits.Bound), h.ScreenText());
        Assert.DoesNotContain("InvalidOperationException", h.Written(), StringComparison.Ordinal);
        h.Keys.Line("/quit");
        await run;
    }

    [Fact]
    public async Task A_LISTING_TYPED_WHILE_A_TURN_RUNS_OPENS_AFTER_THE_TURN_ENDS()
    {
        var h = new RichReplHarness(Path.GetTempPath(), commands: Listed([]));
        //the turn's end runs off this thread, since the panel the queue opens after it blocks the thread it shows on
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Client.HoldNextTurn = ct => held.Task.WaitAsync(ct);
        h.Client.EnqueueTurn(new Gatto.Core.Client.StreamEvent.TextDelta("the answer"), new Gatto.Core.Client.StreamEvent.Finished("stop", null));
        var run = h.RunAsync(60_000);

        h.Keys.Line("go");
        Assert.True(await h.UntilAsync(() => h.Client.Requests.Count == 1, Waits.Bound), "the turn never started");
        h.Keys.Line("/undo");
        await Shows(h, s => s.Contains("/undo"), "the typed /undo never reached the screen");
        await Task.Delay(200);
        Assert.False(h.Saw("Enter restore"), "the listing opened while the turn ran, it should wait in the queue");
        held.SetResult();
        await Shows(h, s => s.Contains("undo · 2 snapshots") && s.Contains("Enter restore"), "the panel never opened after the turn");
        Assert.True(h.Saw("the answer"));
        await Press(h, Esc);
        await Release(h);
        h.Keys.Line("/quit");
        await run;
    }

    [Fact]
    public async Task A_FIFTH_CELL_DRAWS_A_FIFTH_HEADER_WORD()
    {
        var h = new RichReplHarness(Path.GetTempPath(), commands: Listed([], began: true));
        var run = h.RunAsync(60_000);

        h.Keys.Line("/undo");
        await Shows(h, s => s.Contains("began") && s.Contains("10-29 08:58") && s.Contains("Enter restore"),
            "the panel never drew the began column");
        await Press(h, Esc);
        await Release(h);
        h.Keys.Line("/quit");
        await run;
    }

    [Fact]
    public async Task A_NULL_ANSWER_OR_A_NULL_LISTING_PRINTS_AN_EMPTY_ROW_AND_THROWS_NOTHING()
    {
        var reg = new CommandRegistry();
        reg.Add(new CommandRow("/undo", "", "x", (_, _) => Task.FromResult<CommandAnswer>(null!), "undo.csx"));
        reg.Add(new CommandRow("/redo", "", "x", (_, _) => Task.FromResult<CommandAnswer>((Listing)null!), "undo.csx"));
        var h = new RichReplHarness(Path.GetTempPath(), commands: reg);
        h.Keys.Line("/undo").Line("/redo").Line("/quit");
        await h.RunAsync();
        Assert.Empty(h.Client.Requests);
    }

    [Fact]
    public async Task A_REGISTERED_COMMAND_DISPATCHES_WITH_ITS_ARGUMENT_AND_PRINTS_ONE_ROW_PER_LINE()
    {
        var h = new RichReplHarness(Path.GetTempPath(), commands: Undo(arg => $"first {arg}\nsecond"));
        h.Keys.Line("/undo 2").Line("/quit");
        await h.RunAsync();
        var text = h.Written();
        Assert.Contains("first 2", text, StringComparison.Ordinal);
        Assert.Contains("second", text, StringComparison.Ordinal);
        Assert.Empty(h.Client.Requests);   //a registered command never reaches the model
    }

    [Fact]
    public async Task A_HANDLER_THAT_THROWS_PRINTS_ITS_MESSAGE_ONLY()
    {
        var h = new RichReplHarness(Path.GetTempPath(), commands: Undo(_ => throw new InvalidOperationException("boom here")));
        h.Keys.Line("/undo").Line("/quit");
        await h.RunAsync();
        Assert.Contains("boom here", h.Written(), StringComparison.Ordinal);
        Assert.DoesNotContain("InvalidOperationException", h.Written(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_HANDLER_LINE_WITH_AN_ESCAPE_IS_SANITISED()
    {
        var h = new RichReplHarness(Path.GetTempPath(), commands: Undo(_ => "safe\u001b[31mred"));
        h.Keys.Line("/undo").Line("/quit");
        await h.RunAsync();
        //the sanitiser drops the ESC byte alone and keeps the letters after it, so the row reads safe[31mred
        Assert.DoesNotContain("\u001b[31mred", h.RawWritten(), StringComparison.Ordinal);
        Assert.Contains("safe[31mred", h.Written(), StringComparison.Ordinal);
    }

    //the panel reads keys without sampling, so the test samples before each key and once the last has left
    [Fact]
    public async Task HELP_LISTS_THE_REGISTERED_ROW_AFTER_THE_BUILT_INS()
    {
        var h = new RichReplHarness(Path.GetTempPath(), commands: Undo(_ => ""));
        var run = h.RunAsync(60_000);
        h.Keys.Line("/help");
        Assert.True(await h.UntilAsync(() => h.ScreenText().Contains("Enter compose"), Waits.Bound), h.ScreenText());
        _ = h.Keys.KeyAvailable;
        h.Keys.Key(new ConsoleKeyInfo('\0', ConsoleKey.End, false, false, false));
        Assert.True(await h.UntilAsync(() => h.ScreenText().Contains("❯ /undo [n|path]") && h.ScreenText().Contains("undo.csx"), Waits.Bound),
            $"the last row was not the registered one:\n{h.ScreenText()}");
        Assert.Contains("/quit", h.ScreenText(), StringComparison.Ordinal);
        _ = h.Keys.KeyAvailable;
        h.Keys.Key(new ConsoleKeyInfo('\u001b', ConsoleKey.Escape, false, false, false));
        await h.UntilAsync(() => h.Keys.Pending == 0, Waits.Bound);
        await Task.Delay(100);
        _ = h.Keys.KeyAvailable;
        h.Keys.Line("/quit");
        await run;
    }

    [Fact]
    public async Task TAB_COMPLETES_A_REGISTERED_NAME_IN_THE_LIVE_COMPOSER()
    {
        var h = new RichReplHarness(Path.GetTempPath(), commands: Undo(arg => $"ran with [{arg}]"));
        h.Keys.Key(new ConsoleKeyInfo('/', ConsoleKey.NoName, false, false, false))
            .Key(new ConsoleKeyInfo('u', ConsoleKey.U, false, false, false))
            .Key(new ConsoleKeyInfo('\t', ConsoleKey.Tab, false, false, false))
            .Line("")
            .Line("/quit");
        await h.RunAsync();
        Assert.Contains("ran with []", h.Written(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task THE_LIVE_COMPOSER_GHOSTS_A_REGISTERED_NAME()
    {
        var h = new RichReplHarness(Path.GetTempPath(), commands: Undo(_ => ""));
        h.Keys.Key(new ConsoleKeyInfo('/', ConsoleKey.NoName, false, false, false))
            .Key(new ConsoleKeyInfo('u', ConsoleKey.U, false, false, false));
        await h.RunUntilAsync(() => h.Written().Contains("ndo [n|path]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_DOUBLE_SLASH_UNDO_REACHES_THE_MODEL_AS_TEXT()
    {
        var h = new RichReplHarness(Path.GetTempPath(), commands: Undo(_ => "never"));
        h.Client.EnqueueTurn(new Gatto.Core.Client.StreamEvent.TextDelta("ok"), new Gatto.Core.Client.StreamEvent.Finished("stop", null));
        h.Keys.Line("//undo").Line("/quit");
        await h.RunAsync();
        Assert.Single(h.Client.Requests);
        Assert.DoesNotContain("never", h.Written(), StringComparison.Ordinal);
    }
}
