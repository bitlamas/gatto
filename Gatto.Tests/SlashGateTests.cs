using System.Linq;
using Gatto.Core.Client;
using Gatto.Core.Loop;
using Gatto.Core.Tools;
using Gatto.Repl;
using Gatto.Tests.Fakes;
using Xunit;

namespace Gatto.Tests;

//a mistyped command is refused instead of going to the model, and // escapes a leading slash. both faces call this decision, so it is tested here
public class GateSlashTests
{
    private static Gatto.Repl.Repl.SlashGate G(string input) => Gatto.Repl.Repl.GateSlash(input);

    [Fact]
    public void An_unknown_command_is_refused_and_named()
    {
        var g = G("/foo");
        Assert.True(g.Refused);
        Assert.Contains("/foo", g.Refusal!, System.StringComparison.Ordinal);
        Assert.Contains("/help", g.Refusal!, System.StringComparison.Ordinal);
        Assert.Contains("//", g.Refusal!, System.StringComparison.Ordinal);   //the refusal points at the // escape
    }

    [Fact]
    public void Every_declared_command_passes_untouched()
    {
        foreach (var cmd in SlashCommands.All)
        {
            Assert.False(G(cmd.Name).Refused);
            Assert.False(G(cmd.Name + " banana").Refused);   //a bad argument is that command's business
        }
    }

    //each of these goes to the model unchanged (a false positive costs a retype, a false negative costs nothing)
    [Theory]
    [InlineData("/")]                              //a lone slash is not a word
    [InlineData("/usr/bin/python is failing")]     //an inner slash means the user is writing a path
    [InlineData("/etc/hosts")]
    [InlineData("/foo,")]                          //the trailing comma means the token isn't a bare command name
    [InlineData("/foo?")]
    [InlineData("/r2")]                            //a digit means the token isn't a bare command name
    [InlineData("what does /foo do")]              //only the first token is read, so a slash later in the line sends
    [InlineData("hello")]
    public void Ordinary_text_is_never_refused(string input)
    {
        var g = G(input);
        Assert.False(g.Refused);
        Assert.False(g.Literal);
        Assert.Equal(input, g.Text);
    }

    //the refusal reads the first token against the declared names, since IsSlashCommand answers false for a multi-line /help whose trim leaves a newline
    [Fact]
    public void A_multi_line_submit_beginning_with_a_real_command_is_not_refused()
    {
        Assert.False(Gatto.Repl.Repl.IsSlashCommand("/help\nmore"));   //a refusal must not be built on this predicate
        Assert.False(G("/help\nmore").Refused);             //the gate never refuses a real command
    }

    [Fact]
    public void A_line_that_only_looks_like_a_command_is_refused()
    {
        Assert.True(G("/tmp is full").Refused);             //prose the gate can't tell from a command, so the // escape exists
        Assert.True(G("/hepl").Refused);                    //the typo the refusal exists for
    }

    [Fact]
    public void The_escape_hatch_drops_one_slash_and_marks_the_line_literal()
    {
        var g = G("//tmp is full");
        Assert.True(g.Literal);
        Assert.False(g.Refused);
        Assert.Equal("/tmp is full", g.Text);
    }

    //the stripped text reads exactly like the command, so Literal is the only thing keeping //help from running it
    [Fact]
    public void The_escape_hatch_escapes_a_REAL_command()
    {
        var g = G("//help");
        Assert.True(g.Literal);
        Assert.Equal("/help", g.Text);
        Assert.True(Gatto.Repl.Repl.IsSlashCommand(g.Text));   //the text matches a real command name, which the Literal flag has to keep off the chain
    }
}

//the plain face driven end to end through RunPlainAsync with a fake client, the one site where nothing being sent is provable
[Collection("e2e")]
public class PlainGateWalkTests : System.IDisposable
{
    private readonly System.IO.TextReader _origIn = System.Console.In;
    private readonly System.IO.TextWriter _origOut = System.Console.Out;

    public void Dispose()
    {
        System.Console.SetIn(_origIn);
        System.Console.SetOut(_origOut);
    }

    private static (Gatto.Repl.Repl R, FakeChatClient C) ReplWith()
    {
        var client = new FakeChatClient();
        for (var i = 0; i < 5; i++)
            client.EnqueueTurn(new StreamEvent.TextDelta("ok"), new StreamEvent.Finished("stop", null));
        var loop = new AgentLoop(client, new ToolRegistry(), new HookBus(),
            new TestToolContext(System.IO.Path.GetTempPath()), "m");
        var repl = new Gatto.Repl.Repl(loop, new Conversation("sys"), "generalist", "gemma-4-26b", null, client, null, null,
            System.IO.Path.GetTempPath(), () => Composed.Text("sys"), () => null,
            _ => new RoleSwitchResult(false, "", null, null, "unused"));
        return (repl, client);
    }

    private static async Task<(string Out, FakeChatClient C)> Feed(string input)
    {
        var (repl, client) = ReplWith();
        var sw = new System.IO.StringWriter();
        System.Console.SetOut(sw);
        System.Console.SetIn(new System.IO.StringReader(input + "\n/quit\n"));
        await repl.RunAsync(System.Threading.CancellationToken.None);
        return (sw.ToString(), client);
    }

    //the refusal must send nothing, and a screen-only check would pass while the message also went to the model
    [Fact]
    public async Task An_unknown_command_is_refused_and_never_reaches_the_model()
    {
        var (text, client) = await Feed("/foo");

        Assert.Contains("/foo is not a command", text, System.StringComparison.Ordinal);
        Assert.Empty(client.Requests);
    }

    //a line that opens with a path still sends untouched, the false-positive rule proving itself on the real loop
    [Fact]
    public async Task A_line_opening_with_a_path_still_reaches_the_model()
    {
        var (_, client) = await Feed("/usr/bin/python is failing");

        //the recorded ChatRequest points at the live conversation, so [^1] is the assistant reply by now. assert on the user message
        var sent = Assert.Single(client.Requests);
        Assert.Contains(sent.Messages, m => m.Role == "user" && m.Content == "/usr/bin/python is failing");
    }

    [Fact]
    public async Task A_real_command_still_runs_and_sends_nothing()
    {
        var (text, client) = await Feed("/help");

        Assert.Contains("show this table", text, System.StringComparison.Ordinal);
        Assert.Empty(client.Requests);
    }

    //a source-text pin: the refusal must sit in DispatchLinearAsync above DispatchBracketAsync, or the echo, the turn and the purr come first
    [Fact]
    public void THE_RICH_REFUSAL_SITS_ABOVE_THE_TURN_BRACKET()
    {
        //normalize CRLF to LF first, or the search passes on this checkout and fails on one with autocrlf
        var source = System.IO.File.ReadAllText(
            System.IO.Path.Combine(RepoRoot(), "Gatto", "Repl", "Repl.cs")).Replace("\r\n", "\n");

        var gateCall = source.IndexOf("var gate = GateSlash(input);", System.StringComparison.Ordinal);
        var refusal = source.IndexOf(
            "            s.Renderer.CommitSystem(gate.Refusal!);\n            return false;",
            System.StringComparison.Ordinal);
        var bracket = source.IndexOf("return await DispatchBracketAsync(", System.StringComparison.Ordinal);

        Assert.True(gateCall >= 0, "the rich dispatch no longer calls GateSlash");
        Assert.True(bracket >= 0, "DispatchBracketAsync is no longer called where this test expects");
        Assert.True(gateCall < bracket,
            "the refusal moved BELOW the turn bracket: the echo, the turn and the purr all happen first");

        //the refusal commits the row and returns, since a restore would insert into the composer between reads and race its blank repaint
        Assert.True(refusal >= 0 && refusal < bracket,
            "the refusal is no longer 'commit the row, return' above the bracket");

        //the body skips its chain on the gate's Literal flag alone, whatever the stripped text now reads as
        Assert.Contains("var trimmed = literal ? \"\" : input.Trim();", source, System.StringComparison.Ordinal);
        Assert.Contains("DispatchLinearBodyAsync(input, s, attach, appCt, gate.Literal)", source, System.StringComparison.Ordinal);
    }

    //the rich face as behaviour: RunUntilAsync, because a refused message goes back to the composer and a scripted /quit races the restore
    [Fact]
    public async Task An_unknown_command_is_refused_in_the_rich_face_and_never_sent()
    {
        var h = new RichReplHarness(System.IO.Path.GetTempPath());
        h.Keys.Line("/foo");

        await h.RunUntilAsync(() => h.Saw("/foo is not a command"));

        Assert.Empty(h.Client.Requests);
    }

    //a bare //help skips the command chain and still opens a live turn, so the committed "purred for" line is the oracle
    [Fact]
    public async Task A_bare_escaped_command_is_sent_as_text_and_still_purrs()
    {
        var h = new RichReplHarness(System.IO.Path.GetTempPath());
        h.Client.EnqueueTurn(new StreamEvent.TextDelta("ok"), new StreamEvent.Finished("stop", null));
        h.Client.FirstEventDelayMs = 1400;   //past CompletionRow's one-second floor, so the purr line is printed

        h.Keys.Line("//help");
        h.Keys.Line("/quit");
        await h.RunAsync(timeoutMs: 30000);

        var user = h.Convo.Messages.Last(m => m.Role == "user");
        Assert.Equal("/help", user.Content);              //one slash off and sent as text
        Assert.False(h.Saw("show this table"));           //the help table never ran
        Assert.True(h.Saw("purred for"),
            "the turn ran with no purr: the bracket classified the stripped text");
    }

    //go up from the test binary to the folder with Gatto.sln, and fail if it's not there (reading nothing would look like a pass)
    private static string RepoRoot()
    {
        var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir is not null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "Gatto.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    //the plain face end to end: //help reaches the model as the text /help, and no help table renders
    [Fact]
    public async Task The_escape_hatch_sends_a_real_command_as_text()
    {
        var (text, client) = await Feed("//help");

        Assert.DoesNotContain("show this table", text, System.StringComparison.Ordinal);
        var sent = Assert.Single(client.Requests);
        Assert.Contains(sent.Messages, m => m.Role == "user" && m.Content == "/help");
    }
}
