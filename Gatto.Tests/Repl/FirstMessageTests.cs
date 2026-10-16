using Gatto.Core.Client;
using Gatto.Repl;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Repl;

//a role's first message is the opening assistant turn of every conversation it starts, committed as history and never requested
[Collection("e2e")]
public class FirstMessageTests
{
    private const string Hello = "Hello. What are we building?";
    private const string Sera = "I am Sera.";

    private static StreamEvent[] Reply(string text) =>
        [new StreamEvent.TextDelta(text), new StreamEvent.Finished("stop", null)];

    private static Func<string, RoleSwitchResult> SwitchToSera() => _ =>
        new RoleSwitchResult(true, "sera", "test-model", "sys2", null, FirstMessage: Sera);

    private static int Count(string haystack, string needle)
    {
        var n = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    //the request holds the live list, so a later turn's records follow and the opening is its first three
    private static IReadOnlyList<string> Opening(RichReplHarness h) => h.Client.Requests[0].Messages.Take(3).Select(m => m.Role).ToList();

    //the composer and the footer, the rows a fresh session draws at the bottom of the frame
    private static IReadOnlyList<string> Bottom(RichReplHarness h) => h.FinalFrame().TakeLast(2).ToList();

    [Fact]
    public async Task THE_FIRST_MESSAGE_IS_THE_FIRST_ASSISTANT_BLOCK_AND_NO_REQUEST_IS_MADE_FOR_IT()
    {
        var fresh = new RichReplHarness(Path.GetTempPath());
        fresh.Keys.Line("/quit");
        await fresh.RunAsync();

        var h = new RichReplHarness(Path.GetTempPath(), firstMessage: Hello);
        h.Keys.Line("/quit");
        await h.RunAsync();

        Assert.True(h.Saw(Hello), h.ScreenText());
        Assert.Empty(h.Client.Requests);
        //no usage moved, so the footer reads as a fresh session's
        Assert.Equal(Bottom(fresh), Bottom(h));
    }

    [Fact]
    public async Task THE_FIRST_REQUEST_CARRIES_IT_AS_HISTORY()
    {
        var h = new RichReplHarness(Path.GetTempPath(), firstMessage: Hello);
        h.Client.EnqueueTurn(Reply("ok"));
        h.Keys.Line("hi").Line("/quit");
        await h.RunAsync();

        Assert.Equal(["system", "assistant", "user"], Opening(h));
        Assert.Equal(Hello, h.Client.Requests[0].Messages[1].Content);
    }

    [Fact]
    public async Task A_NEW_CONVERSATION_OPENS_WITH_IT_AGAIN()
    {
        var h = new RichReplHarness(Path.GetTempPath(), firstMessage: Hello);
        h.Client.EnqueueTurn(Reply("ok"));
        h.Keys.Line("/new").Line("hi").Line("/quit");
        await h.RunAsync();

        //the rotation clears the screen, so the block under the new-conversation line was painted after it
        var screen = h.ScreenText();
        var line = screen.IndexOf(Gatto.Repl.Repl.NewConversationLine, StringComparison.Ordinal);
        Assert.True(line >= 0 && screen.IndexOf(Hello, line, StringComparison.Ordinal) > line, screen);
        var messages = h.Client.Requests[0].Messages;
        Assert.Equal("assistant", messages[1].Role);
        Assert.Equal(Hello, messages[1].Content);
    }

    [Fact]
    public async Task A_ROLE_SWITCH_INTO_AN_EMPTY_CONVERSATION_OPENS_WITH_THE_ROLES_FIRST_MESSAGE()
    {
        var h = new RichReplHarness(Path.GetTempPath(), switchRole: SwitchToSera());
        h.Client.EnqueueTurn(Reply("ok"));
        h.Keys.Line("/role sera").Line("hi").Line("/quit");
        await h.RunAsync();

        Assert.Equal(1, Count(h.ScreenText(), Sera));
        Assert.Equal(["system", "assistant", "user"], Opening(h));
        Assert.Equal(Sera, h.Client.Requests[0].Messages[1].Content);
    }

    [Fact]
    public async Task A_ROLE_SWITCH_INTO_A_CONVERSATION_WITH_TURNS_OPENS_NOTHING()
    {
        var h = new RichReplHarness(Path.GetTempPath(), switchRole: SwitchToSera());
        h.Client.EnqueueTurn(Reply("ok"));
        h.Client.EnqueueTurn(Reply("ok again"));
        h.Keys.Line("hi").Line("/role sera").Line("again").Line("/quit");
        await h.RunAsync();

        Assert.False(h.Saw(Sera), h.ScreenText());
        Assert.Equal(2, h.Client.Requests.Count);
        Assert.DoesNotContain(h.Client.Requests.SelectMany(r => r.Messages), m => m.Content == Sera);
    }

    [Fact]
    public async Task A_ROLE_WITHOUT_A_FIRST_MESSAGE_OPENS_NOTHING_AFTER_NEW()
    {
        var h = new RichReplHarness(Path.GetTempPath());
        h.Client.EnqueueTurn(Reply("ok"));
        h.Keys.Line("/new").Line("hi").Line("/quit");
        await h.RunAsync();

        Assert.True(h.Saw(Gatto.Repl.Repl.NewConversationLine));
        Assert.Equal(["system", "user"], h.Client.Requests[0].Messages.Take(2).Select(m => m.Role));
    }

    //an empty answer leaves the typed record behind, and a switch must neither seed the role's opening nor echo the line again
    [Fact]
    public async Task A_ROLE_SWITCH_AFTER_AN_UNANSWERED_TURN_REPLAYS_NOTHING()
    {
        var h = new RichReplHarness(Path.GetTempPath(), switchRole: SwitchToSera());
        h.Client.EnqueueTurn(new StreamEvent.Finished("stop", null));
        h.Keys.Line("typed once").Line("/role sera").Line("/quit");
        await h.RunAsync();

        Assert.Equal(["system", "user"], h.Client.Requests[0].Messages.Select(m => m.Role));
        Assert.Equal(1, Count(h.ScreenText(), "typed once"));
        Assert.False(h.Saw(Sera), h.ScreenText());
        Assert.True(h.Saw("role: sera"));
    }

    //a connection failure drops the typed record, so the conversation is empty again and the switch opens it
    [Fact]
    public async Task A_ROLE_SWITCH_AFTER_A_DROPPED_TURN_OPENS_WITH_THE_ROLES_FIRST_MESSAGE()
    {
        var h = new RichReplHarness(Path.GetTempPath(), switchRole: SwitchToSera());
        h.Client.EnqueueThrow(new GattoConnectionException("the server went away"));
        h.Keys.Line("typed once").Line("/role sera").Line("/quit");
        await h.RunAsync();

        Assert.Equal(1, Count(h.ScreenText(), "typed once"));
        Assert.Equal(1, Count(h.ScreenText(), Sera));
    }

    //a conversation holding only an opening is empty to a switch, so the old greeting leaves the history and the new one replaces it
    [Fact]
    public async Task A_ROLE_SWITCH_FROM_AN_OPENED_CONVERSATION_REPLACES_THE_OPENING()
    {
        var h = new RichReplHarness(Path.GetTempPath(), firstMessage: Hello, switchRole: SwitchToSera());
        h.Client.EnqueueTurn(Reply("ok"));
        h.Keys.Line("/role sera").Line("hi").Line("/quit");
        await h.RunAsync();

        Assert.Equal(1, Count(h.ScreenText(), Sera));
        var messages = h.Client.Requests[0].Messages;
        Assert.Equal(["system", "assistant", "user"], Opening(h));
        Assert.Equal(Sera, messages[1].Content);
        Assert.DoesNotContain(messages, m => m.Content == Hello);
    }

    [Fact]
    public async Task A_ROLE_SWITCH_FROM_AN_OPENED_CONVERSATION_INTO_A_ROLE_WITHOUT_ONE_EMPTIES_IT()
    {
        var h = new RichReplHarness(Path.GetTempPath(), firstMessage: Hello,
            switchRole: _ => new RoleSwitchResult(true, "plain", "test-model", "sys2", null));
        h.Client.EnqueueTurn(Reply("ok"));
        h.Keys.Line("/role plain").Line("hi").Line("/quit");
        await h.RunAsync();

        Assert.Equal(["system", "user"], h.Client.Requests[0].Messages.Take(2).Select(m => m.Role));
        Assert.DoesNotContain(h.Client.Requests[0].Messages, m => m.Content == Hello);
    }

    [Fact]
    public async Task A_SWITCH_THEN_NEW_OPENS_WITH_THE_NEW_ROLES_MESSAGE()
    {
        var h = new RichReplHarness(Path.GetTempPath(), switchRole: SwitchToSera());
        h.Client.EnqueueTurn(Reply("ok"));
        h.Keys.Line("/role sera").Line("/new").Line("hi").Line("/quit");
        await h.RunAsync();

        Assert.Equal(["system", "assistant", "user"], Opening(h));
        Assert.Equal(Sera, h.Client.Requests[0].Messages[1].Content);
    }

    [Fact]
    public async Task THE_OPENING_RECORD_CARRIES_THE_ROLE()
    {
        var h = new RichReplHarness(Path.GetTempPath(), switchRole: SwitchToSera());
        h.Client.EnqueueTurn(Reply("ok"));
        h.Keys.Line("/role sera").Line("hi").Line("/quit");
        await h.RunAsync();

        Assert.Equal("sera", h.Client.Requests[0].Messages[1].GattoRole);
    }

    [Fact]
    public async Task A_NEW_CONVERSATION_KEEPS_THE_RUNNING_ROLE_ON_ITS_RECORDS()
    {
        var h = new RichReplHarness(Path.GetTempPath(), roleName: "coder");
        h.Client.EnqueueTurn(Reply("ok"));
        h.Keys.Line("/new").Line("hi").Line("/quit");
        await h.RunAsync();

        //a user record carries no role by design, the stamp is on the assistant and tool records
        var records = h.Client.Requests[0].Messages.Where(m => m.Role is "assistant" or "tool").ToList();
        Assert.NotEmpty(records);
        Assert.All(records, m => Assert.Equal("coder", m.GattoRole));
    }
}
