using Gatto.Core.Commands;
using Gatto.Repl;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Repl;

public class RegisteredCommandTests
{
    private static CommandRegistry Undo(Func<string, string> answer)
    {
        var reg = new CommandRegistry();
        reg.Add(new CommandRow("/undo", "[n|path]", "list snapshots for this folder, or restore one",
            (arg, _) => Task.FromResult(answer(arg)), "undo.csx"));
        return reg;
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
