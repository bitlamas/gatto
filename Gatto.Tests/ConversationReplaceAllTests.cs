using Gatto.Core.Client;
using Gatto.Core.Loop;
using Xunit;

namespace Gatto.Tests;

public sealed class ConversationReplaceAllTests
{
    [Fact]
    public void ReplaceAll_SameObjectObservedByAlias()
    {
        var convo = new Conversation("old system");
        convo.AddUser("old user");
        convo.AddAssistant("old assistant");
        var alias = convo;                                   //the alias plays the caller's own reference to this conversation.

        convo.ReplaceAll("new system", new[] { new ChatMessage("user", "continue prompt") }, null);

        Assert.Equal(2, alias.Count);
        Assert.Equal("system", alias.Messages[0].Role);
        Assert.Equal("new system", alias.Messages[0].Content);
        Assert.Equal("user", alias.Messages[1].Role);
        Assert.Equal("continue prompt", alias.Messages[1].Content);
    }

    [Fact]
    public void ReplaceAll_ThenAdd_AppendsAfterRebuilt()      //appending after a rebuild must work, so the saved session stays clean.
    {
        var convo = new Conversation("s");
        convo.AddUser("u1");
        convo.ReplaceAll("s2", new[] { new ChatMessage("user", "u2") }, null);
        convo.AddAssistant("a");
        Assert.Equal(3, convo.Count);
        Assert.Equal("assistant", convo.Messages[2].Role);
    }

    //messages built at ReplaceAll get a Ts when they lack one, and a message that already has one keeps it
    [Fact]
    public void ReplaceAll_StampsTs_OnMessagesLackingOne()
    {
        var t0 = new System.DateTime(2026, 7, 21, 12, 0, 0, System.DateTimeKind.Utc);
        var convo = new Conversation("s", () => t0);
        var kept = new System.DateTime(2026, 7, 20, 8, 0, 0, System.DateTimeKind.Utc);

        convo.ReplaceAll("s2", new[]
        {
            new ChatMessage("user", "continue prompt"),                 //a bare message, expected to be stamped here.
            new ChatMessage("user", "carried", Ts: kept),               //an already stamped message must keep its stamp.
        }, null);

        Assert.Equal(t0, convo.Messages[0].Ts);   //the rebuilt system message gets a fresh timestamp.
        Assert.Equal(t0, convo.Messages[1].Ts);   //the bare user message is stamped at the swap.
        Assert.Equal(kept, convo.Messages[2].Ts); //a message that already has its own Ts keeps its creation time
    }

    [Fact]
    public void ReplaceAll_EmptySystem_StillSeedsSystemMessage()
    {
        var convo = new Conversation(null);
        convo.ReplaceAll("sys", System.Array.Empty<ChatMessage>(), null);
        Assert.Equal(1, convo.Count);
        Assert.Equal("system", convo.Messages[0].Role);
    }
}
