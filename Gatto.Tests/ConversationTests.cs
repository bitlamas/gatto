using Gatto.Core.Client;
using Gatto.Core.Loop;
using Gatto.Core.Tools;

namespace Gatto.Tests;

public class ConversationTests
{
    //the timestamp is stamped at creation and survives a load. a save rewrites the whole file, so stamping at write time would reset every record
    [Fact]
    public void Conversation_stamps_creation_ts_from_the_injected_clock_and_Load_preserves_it()
    {
        var t = new DateTime(2026, 7, 21, 6, 0, 0, DateTimeKind.Utc);
        var c = new Conversation("sys", now: () => t);
        c.AddUser("q");
        c.AddAssistant("a");
        Assert.Equal(t, c.Messages[0].Ts!.Value.ToUniversalTime());
        Assert.Equal(t, c.Messages[1].Ts!.Value.ToUniversalTime());
        Assert.Equal(t, c.Messages[2].Ts!.Value.ToUniversalTime());
        var older = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var c2 = new Conversation(null, now: () => t);
        c2.Load(new[] { new ChatMessage("user", "old", Ts: older) });
        Assert.Equal(older, c2.Messages[0].Ts!.Value.ToUniversalTime());
    }

    [Fact]
    public void AddAssistant_and_AddToolResult_stamp_the_current_gatto_role()
    {
        var c = new Conversation(null);
        c.SetGattoRole("generalist");
        c.AddAssistant("hi");
        c.SetGattoRole("coder");
        c.AddToolResult("c1", new ToolResult("boom", IsError: true, Gloss: "exit 1"));
        var msgs = c.Messages;
        Assert.Equal("generalist", msgs[0].GattoRole);
        Assert.Equal("coder", msgs[1].GattoRole);
        Assert.True(msgs[1].IsError);
        Assert.Equal("exit 1", msgs[1].Gloss);
    }

    //usage and timings are stamped at AddAssistant's creation site, like role and Ts
    [Fact]
    public void AddAssistant_stamps_usage_and_timings_when_provided()
    {
        var c = new Conversation(null);
        var usage = new Usage(5, 2);
        var timings = "{\"predicted_per_second\":12.3}";
        c.AddAssistant("hi", usage: usage, timings: timings);
        Assert.Equal(usage, c.Messages[0].Usage);
        Assert.Equal(timings, c.Messages[0].Timings);
    }

    [Fact]
    public void AddAssistant_leaves_usage_and_timings_null_when_omitted()
    {
        var c = new Conversation(null);
        c.AddAssistant("hi");
        Assert.Null(c.Messages[0].Usage);
        Assert.Null(c.Messages[0].Timings);
    }

    [Fact]
    public void AddUser_and_AddToolResult_never_carry_usage_or_timings()
    {
        var c = new Conversation(null);
        c.AddUser("q");
        c.AddToolResult("c1", new ToolResult("ok"));
        Assert.Null(c.Messages[0].Usage);
        Assert.Null(c.Messages[0].Timings);
        Assert.Null(c.Messages[1].Usage);
        Assert.Null(c.Messages[1].Timings);
    }

    [Fact]
    public void AddUser_is_not_stamped()
    {
        var c = new Conversation(null);
        c.SetGattoRole("coder");
        c.AddUser("q");
        Assert.Null(c.Messages[0].GattoRole);
    }

    [Fact]
    public void TruncateTo_RollsBackToMark()
    {
        var convo = new Conversation(null);
        convo.AddUser("kept");
        var mark = convo.Count;
        convo.AddUser("dangling");
        convo.TruncateTo(mark);
        Assert.Equal(mark, convo.Count);
        Assert.Equal("kept", convo.Messages[^1].Content);
    }

    [Fact]
    public void TruncateTo_CountAtOrAboveCurrent_IsNoOp()
    {
        var convo = new Conversation(null);
        convo.AddUser("kept");
        var count = convo.Count;
        convo.TruncateTo(count);          //the boundary where the argument equals the current count.
        Assert.Equal(count, convo.Count);
        convo.TruncateTo(count + 5);       //the boundary above the current count.
        Assert.Equal(count, convo.Count);
    }

    [Fact]
    public void TruncateTo_NegativeCount_IsNoOp()
    {
        var convo = new Conversation(null);
        convo.AddUser("kept");
        var count = convo.Count;
        convo.TruncateTo(-1);
        Assert.Equal(count, convo.Count);
    }

    [Fact]
    public void ReplaceSystem_ReplacesExistingSystemMessage()
    {
        var convo = new Conversation("old system");
        convo.AddUser("hi");
        convo.ReplaceSystem("new system", null);

        Assert.Equal(2, convo.Count);
        Assert.Equal("system", convo.Messages[0].Role);
        Assert.Equal("new system", convo.Messages[0].Content);
        Assert.Equal("user", convo.Messages[1].Role);
        Assert.Equal("hi", convo.Messages[1].Content);
    }

    [Fact]
    public void ReplaceSystem_InsertsAtZeroWhenNoSystemMessage()
    {
        var convo = new Conversation(null);
        convo.AddUser("first");
        convo.AddAssistant("second");
        convo.ReplaceSystem("fresh system", null);

        Assert.Equal(3, convo.Count);
        Assert.Equal("system", convo.Messages[0].Role);
        Assert.Equal("fresh system", convo.Messages[0].Content);
        //the insertion shifts existing messages down without reordering them.
        Assert.Equal("first", convo.Messages[1].Content);
        Assert.Equal("second", convo.Messages[2].Content);
    }
}
