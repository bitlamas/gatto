using Gatto.Core.Client;
using Gatto.Core.Commands;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Repl;

//a line of placeholders and a command runs the command, and the placeholders stay in the composer so the next message carries them
[Collection("e2e")]
public class PasteSurvivesCommandTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-paste-cmd-").FullName;

    public PasteSurvivesCommandTests() => Environment.SetEnvironmentVariable("GATTO_HOME", _home);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("GATTO_HOME", null);
        try { Directory.Delete(_home, recursive: true); } catch { }
    }

    private sealed class OnePng : IClipboardImageNative
    {
        public ClipboardImageKind Available() => ClipboardImageKind.Png;
        public byte[]? ReadPng() => [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0x07];
        public IReadOnlyList<string> ReadFileDrop() => [];
    }

    private static readonly ConsoleKeyInfo AltV = new('v', ConsoleKey.V, false, true, false);
    private static readonly ConsoleKeyInfo Backspace = new('\b', ConsoleKey.Backspace, false, false, false);

    private static RichReplHarness Harness(int turns)
    {
        var reg = new CommandRegistry();
        reg.Add(new CommandRow("/undo", "[n|path]", "list snapshots for this folder, or restore one",
            (arg, _) => Task.FromResult($"undo ran with [{arg}]"), "undo.csx"));
        var h = new RichReplHarness(Path.GetTempPath(), commands: reg, clipboardImages: new OnePng());
        for (var i = 0; i < turns; i++)
            h.Client.EnqueueTurn(new StreamEvent.TextDelta($"answer {i}"), new StreamEvent.Finished("stop", null));
        return h;
    }

    private static void Type(RichReplHarness h, string text)
    {
        foreach (var c in text) h.Keys.Key(new ConsoleKeyInfo(c, ConsoleKey.NoName, false, false, false));
    }

    private static ChatMessage LastUser(RichReplHarness h) => h.Convo.Messages.Last(m => m.Role == "user");

    [Theory]
    [InlineData("", " /help")]
    [InlineData("", " /undo")]
    [InlineData("/undo ", "")]
    [InlineData("", " /Undo")]
    public async Task A_command_beside_a_placeholder_runs_and_the_next_message_attaches_the_image(string before, string after)
    {
        var h = Harness(1);
        Type(h, before);
        h.Keys.Key(AltV).Line(after).Line("describe it").Line("/quit");
        await h.RunAsync();

        Assert.Single(h.Convo.Messages, m => m.Role == "user");
        Assert.Equal("[Image #1] describe it", LastUser(h).Content);
        Assert.Equal("image/png", Assert.Single(LastUser(h).Images!).MediaType);
    }

    [Fact]
    public async Task The_command_runs_without_the_placeholder_in_its_argument()
    {
        var h = Harness(0);
        h.Keys.Key(AltV).Line(" /undo").Line("/quit");
        await h.RunAsync();

        Assert.Contains("undo ran with []", h.Written(), StringComparison.Ordinal);
        Assert.Empty(h.Client.Requests);
    }

    [Fact]
    public async Task A_refused_command_beside_a_placeholder_says_so_and_keeps_the_image()
    {
        var h = Harness(1);
        h.Keys.Key(AltV).Line(" /Undo").Line("describe it").Line("/quit");
        await h.RunAsync();

        Assert.Contains("/Undo is not a command", h.Written(), StringComparison.Ordinal);
        Assert.Single(LastUser(h).Images!);
    }

    [Fact]
    public async Task A_placeholder_deleted_before_a_command_attaches_nothing_to_the_next_message()
    {
        var h = Harness(1);
        h.Keys.Key(AltV).Key(Backspace).Line("/help").Line("describe it").Line("/quit");
        await h.RunAsync();

        Assert.Equal("describe it", LastUser(h).Content);
        Assert.Null(LastUser(h).Images);
    }

    [Fact]
    public async Task A_placeholder_alone_is_a_message_with_the_image()
    {
        var h = Harness(1);
        h.Keys.Key(AltV).Line("").Line("/quit");
        await h.RunAsync();

        Assert.Equal("[Image #1]", LastUser(h).Content);
        Assert.Single(LastUser(h).Images!);
    }

    [Fact]
    public async Task An_escaped_command_beside_a_placeholder_is_text_with_the_image()
    {
        var h = Harness(1);
        Type(h, "//help ");
        h.Keys.Key(AltV).Line("").Line("/quit");
        await h.RunAsync();

        Assert.Equal("/help [Image #1]", LastUser(h).Content);
        Assert.Single(LastUser(h).Images!);
    }

    [Fact]
    public async Task A_message_sent_with_the_image_leaves_nothing_for_a_later_command()
    {
        var h = Harness(2);
        h.Keys.Key(AltV).Line(" first").Line("/help").Line("second").Line("/quit");
        await h.RunAsync();

        var users = h.Convo.Messages.Where(m => m.Role == "user").ToList();
        Assert.Single(users[0].Images!);
        Assert.Equal("second", users[^1].Content);
        Assert.Null(users[^1].Images);
    }

    //a reading command answers at once during a turn and never queues, so its own arm must seed the composer too
    [Fact]
    public async Task A_reading_command_beside_a_placeholder_during_a_turn_keeps_the_image()
    {
        var h = Harness(2);
        var handled = false;
        //the turn is held from its request until the editor handled every key after it, so /help is typed during the turn
        h.Client.HoldNextTurn = async ct =>
        {
            h.Keys.Key(AltV).Line(" /help").Line("describe it").Line("/quit");
            handled = await h.UntilAsync(() => h.Keys.Handled, Waits.Bound);
        };
        h.Keys.Line("hello");
        await h.RunAsync();
        Assert.True(handled, $"the editor left {h.Keys.Pending} keys unread while the turn was held");

        Assert.Equal("[Image #1] describe it", LastUser(h).Content);
        Assert.Single(LastUser(h).Images!);
    }

    //the beep proves the queue refused the line, so the composer text that follows is the hand-back and not the typing
    [Fact]
    public async Task A_full_queue_hands_the_line_back_with_its_placeholder()
    {
        var h = Harness(1);
        h.Client.FirstEventDelayMs = 60000;
        h.Keys.Line("go");
        for (var i = 0; i < Gatto.Repl.Repl.QueueCap; i++) h.Keys.Line($"q{i}");
        h.Keys.Key(AltV).Line(" /undo");

        await h.RunUntilAsync(() => h.RawWritten().Contains('\a') && h.ScreenText().Contains("[Image #1] /undo"), 6000);
    }
}
