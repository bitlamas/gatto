using Gatto.Core.Client;
using Gatto.Core.Loop;
using Gatto.Core.Tools;

namespace Gatto.Tests;

public class ContextBudgetTests
{
    private static ChatMessage User(string text) => new("user", text);
    private static ChatMessage Assistant(string? text, params ToolCall[] calls) =>
        new("assistant", text, calls.Length > 0 ? calls : null);
    private static ChatMessage Tool(string content, string id) => new("tool", content, ToolCallId: id);

    [Fact]
    public void Estimate_SumsContentCharsDividedByFour()
    {
        Assert.Equal(2, ContextBudget.Estimate(new[] { User("12345678") }));
    }

    [Fact]
    public void Estimate_CountsToolCallArgumentJson()
    {
        //the expected value divides 4 content plus 7 argument chars by 4.
        var m = Assistant("abcd", new ToolCall("id", "name", "{\"k\":1}"));
        Assert.Equal(2, ContextBudget.Estimate(new[] { m }));
    }

    [Fact]
    public void Estimate_TreatsNullContentAsZero()
    {
        //null content adds nothing, and 2 argument chars round down to 0.
        var m = Assistant(null, new ToolCall("i", "n", "ab"));
        Assert.Equal(0, ContextBudget.Estimate(new[] { m }));
        //null content is zero for a tool message too.
        Assert.Equal(0, ContextBudget.Estimate(new[] { new ChatMessage("tool", null, ToolCallId: "x") }));
    }

    [Fact]
    public void Estimate_SumsAcrossMessages()
    {
        //the expected value divides the sum of every string, 19 chars, by 4.
        var msgs = new[]
        {
            User("12345678"),
            Assistant("abcd", new ToolCall("id", "name", "{\"k\":1}")),
            new ChatMessage("tool", null, ToolCallId: "id"),
        };
        Assert.Equal(4, ContextBudget.Estimate(msgs));
    }

    [Fact]
    public void Estimate_CountsReasoningContent()
    {
        //reasoning text enters every request, the estimate must count it. uncounted, the largest window consumer stays invisible to elision, warnings and compaction
        var withReasoning = new ChatMessage("assistant", "abcd", ReasoningContent: new string('r', 400));

        Assert.Equal((4 + 400) / 4, ContextBudget.Estimate(new[] { withReasoning }));
        Assert.Equal(1, ContextBudget.Estimate(new[] { Assistant("abcd") }));   //the same message without reasoning keeps its old estimate.
    }

    [Fact]
    public void Apply_NullBudget_IsPassthrough()
    {
        var msgs = new[] { User("hello"), Tool(new string('a', 100), "c1") };
        var (result, elided, over) = ContextBudget.Apply(msgs, null, msgs.Length);
        Assert.Same(msgs, result);
        Assert.Equal(0, elided);
        Assert.False(over);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-50)]
    public void Apply_ZeroOrNegativeBudget_IsPassthrough(int budget)
    {
        var msgs = new[] { User("hello"), Tool(new string('a', 100), "c1") };
        var (result, elided, over) = ContextBudget.Apply(msgs, budget, msgs.Length);
        Assert.Same(msgs, result);
        Assert.Equal(0, elided);
        Assert.False(over);
    }

    [Fact]
    public void Apply_UnderNinetyPercent_IsPassthrough()
    {
        var msgs = new[] { User("hello"), Tool(new string('a', 100), "c1") };
        //the fixture estimates about 26 tokens, far under 90 percent of the budget.
        var (result, elided, over) = ContextBudget.Apply(msgs, 10000, msgs.Length);
        Assert.Same(msgs, result);
        Assert.Equal(0, elided);
        Assert.False(over);
    }

    [Fact]
    public void Apply_ElidesOldestFirst_WithExactStubText_AndReEstimatesToStop()
    {
        var msgs = new[]
        {
            User("u"),
            Assistant(null, new ToolCall("c1", "shell", "{}")),
            Tool(new string('a', 400), "c1"),
            Assistant(null, new ToolCall("c2", "read_file", "{}")),
            Tool(new string('b', 400), "c2"),
            User("u2"),
        };
        //estimate starts at 201 against a 198 limit. eliding the oldest tool drops below it, so exactly one elision happens.
        var (result, elided, over) = ContextBudget.Apply(msgs, 220, msgs.Length);

        Assert.Equal(1, elided);
        Assert.Equal("[elided: shell result, 400 chars — re-run if needed]", result[2].Content);
        Assert.Equal(new string('b', 400), result[4].Content); //the newer tool result must survive unelided.
        Assert.False(over);
        //elision must not change the caller's list or records.
        Assert.Equal(new string('a', 400), msgs[2].Content);
    }

    [Fact]
    public void Apply_FallsBackToLiteralTool_WhenIdUnresolvable()
    {
        var msgs = new[]
        {
            User("u"),
            Tool(new string('a', 400), "orphan"), //no earlier assistant maps this id, forcing the fallback stub.
            User("u2"),
        };
        //estimate of 100 against the 90 percent limit of budget 100, so elision triggers.
        var (result, elided, _) = ContextBudget.Apply(msgs, 100, msgs.Length);
        Assert.Equal(1, elided);
        Assert.Equal("[elided: tool result, 400 chars — re-run if needed]", result[1].Content);
    }

    private static ChatMessage Thought(string text, string reasoning) =>
        new("assistant", text, ReasoningContent: reasoning);

    private static IReadOnlyList<ChatMessage> ThoughtChain(int n) =>
        Enumerable.Range(0, n).Select(i => Thought($"answer {i}", $"thinking {i}")).ToList();

    [Fact]
    public void ShapeReasoning_leaves_content_and_message_count_alone()
    {
        var input = ThoughtChain(6);
        var shaped = ContextBudget.ShapeReasoning(input, ReasoningHistory.None);

        Assert.Equal(input.Count, shaped.Count);
        Assert.Equal(input.Select(m => m.Content), shaped.Select(m => m.Content));
        //shaping must not mutate the caller's list, so the session file keeps every reasoning block.
        Assert.All(input, m => Assert.NotNull(m.ReasoningContent));
    }

    [Fact]
    public void ShapeReasoning_all_is_the_identity_and_none_strips_everything()
    {
        var input = ThoughtChain(5);

        Assert.Same(input, ContextBudget.ShapeReasoning(input, ReasoningHistory.All));
        Assert.All(ContextBudget.ShapeReasoning(input, ReasoningHistory.None), m => Assert.Null(m.ReasoningContent));
    }

    [Fact]
    public void ShapeReasoning_returns_the_input_when_there_is_nothing_to_strip()
    {
        //when no message has reasoning, the call returns the input itself, with no allocation
        var input = new[] { User("hi"), Assistant("there") };
        Assert.Same(input, ContextBudget.ShapeReasoning(input, ReasoningHistory.None));
    }

    [Theory]
    [InlineData(ReasoningHistory.All)]
    [InlineData(ReasoningHistory.None)]
    public void ShapeReasoning_is_prefix_stable_as_the_conversation_grows(ReasoningHistory policy)
    {
        //each round's request must repeat the previous bytes, one differing token re-prefills all after it. a longer request alone is not enough, the boundary moves
        var convo = new List<ChatMessage>();
        IReadOnlyList<ChatMessage>? prev = null;
        for (var round = 0; round < 12; round++)
        {
            convo.Add(Thought($"answer {round}", $"thinking {round}"));
            var shaped = ContextBudget.ShapeReasoning(convo, policy);

            if (prev is not null)
                for (var i = 0; i < prev.Count; i++)
                {
                    Assert.Equal(prev[i].Content, shaped[i].Content);
                    Assert.True(prev[i].ReasoningContent == shaped[i].ReasoningContent,
                        $"round {round}: message {i} changed mid-history ('{prev[i].ReasoningContent}' → '{shaped[i].ReasoningContent}') — prefix cache invalidated");
                }
            prev = shaped;
        }
    }

    [Fact]
    public void ElideBefore_protects_the_whole_turn_until_the_window_fills()
    {
        const int turnUser = 7;
        for (var rounds = 1; rounds <= ContextBudget.RecentRounds; rounds++)
        {
            var starts = Enumerable.Range(0, rounds).Select(i => turnUser + 1 + i * 2).ToList();
            Assert.Equal(turnUser, ContextBudget.ElideBefore(starts, turnUser));
        }
    }

    [Fact]
    public void ElideBefore_advances_one_round_at_a_time_once_older_rounds_exist()
    {
        //protecting the last three rounds makes the boundary the start of round two, index 4.
        const int turnUser = 0;
        var starts = new List<int> { 1, 4, 9, 16 };
        Assert.Equal(4, ContextBudget.ElideBefore(starts, turnUser));

        starts.Add(25);                                                  //adding round 5 shifts the protected window to rounds 3 through 5.
        Assert.Equal(9, ContextBudget.ElideBefore(starts, turnUser));
    }

    [Fact]
    public void ElideBefore_never_moves_backwards_across_a_turn()
    {
        //the boundary may only advance, so each round's stub set is a superset of the last. a retreat would un-elide a result and re-prefill the whole conversation
        const int turnUser = 3;
        var starts = new List<int>();
        var previous = 0;
        for (var round = 1; round <= 40; round++)
        {
            starts.Add(turnUser + 1 + round * 3);
            var boundary = ContextBudget.ElideBefore(starts, turnUser);
            Assert.True(boundary >= previous, $"round {round}: boundary went backwards {previous} → {boundary}");
            previous = boundary;
        }
        Assert.True(previous > turnUser, "after 40 rounds the boundary must have advanced past the turn start");
    }

    [Fact]
    public void Apply_skips_results_too_small_to_be_worth_a_prefix_invalidation()
    {
        //each elision invalidates the cached prefix from that index onward. stubbing a small result buys nothing for that cost.
        var msgs = new[]
        {
            Assistant(null, new ToolCall("c1", "shell", "{}")),
            Tool(new string('s', 150), "c1"),
            Assistant(null, new ToolCall("c2", "read_file", "{}")),
            Tool(new string('a', 4000), "c2"),
        };

        var (result, elided, _) = ContextBudget.Apply(msgs, 100, msgs.Length);

        Assert.Equal(1, elided);
        Assert.Equal(new string('s', 150), result[1].Content);
        Assert.StartsWith("[elided: read_file result", result[3].Content);
    }

    [Fact]
    public void Apply_NeverElidesAtOrAfterProtectFromIndex()
    {
        var msgs = new[]
        {
            User("u"),
            Assistant(null, new ToolCall("c1", "shell", "{}")),
            Tool(new string('a', 400), "c1"),
        };
        //over budget but with index 0 as the only eligible message, a user one, so nothing can be elided.
        var (result, elided, over) = ContextBudget.Apply(msgs, 50, protectFromIndex: 1);
        Assert.Equal(0, elided);
        Assert.Same(msgs, result);
        Assert.True(over);               //the over flag reports the budget exceeded even after every possible elision.
    }

    [Fact]
    public void Apply_StillOver_TrueWhenNoToolMessagesButOverBudget()
    {
        var msgs = new[] { User(new string('x', 500)) };
        var (_, elided, over) = ContextBudget.Apply(msgs, 100, msgs.Length);
        Assert.Equal(0, elided);
        Assert.True(over);
    }

    [Fact]
    public void Apply_StillOver_FalseAfterElisionBringsItUnderWindow()
    {
        var msgs = new[]
        {
            Assistant(null, new ToolCall("c1", "shell", "{}")),
            Tool(new string('a', 4000), "c1"),
        };
        //estimate 1000 against the 900 limit, so one elision brings it far under 100 percent.
        var (_, elided, over) = ContextBudget.Apply(msgs, 1000, msgs.Length);
        Assert.Equal(1, elided);
        Assert.False(over);
    }

    [Fact]
    public void Apply_SkipsShortToolResults_ButElidesLongOnes()
    {
        //eliding a result no longer than its stub grows the request, so the pass skips it without counting. skipping must not stop the pass reaching the long one.
        var msgs = new[]
        {
            Assistant(null, new ToolCall("c1", "shell", "{}")),
            Tool("x", "c1"),
            Assistant(null, new ToolCall("c2", "read_file", "{}")),
            Tool(new string('a', 4000), "c2"),
        };
        var (result, elided, over) = ContextBudget.Apply(msgs, 100, msgs.Length);

        Assert.Equal(1, elided);
        Assert.Equal("x", result[1].Content);
        Assert.Equal("[elided: read_file result, 4000 chars — re-run if needed]", result[3].Content);
        Assert.False(over);
        //elision must not change the caller's records.
        Assert.Equal(new string('a', 4000), msgs[3].Content);
    }

    [Fact]
    public void Apply_TerminatesWhenOnlyShortToolResultsExist()
    {
        //when every candidate is shorter than its stub, nothing is rewritten and the pass ends. over stays true, the request cannot shrink any further
        var msgs = new[]
        {
            Assistant(null, new ToolCall("c1", "shell", "{}")),
            Tool("x", "c1"),
            User(new string('p', 4000)),     //the bulk sits in a user message, which elision never rewrites.
        };
        var (result, elided, over) = ContextBudget.Apply(msgs, 100, msgs.Length);
        Assert.Equal(0, elided);
        Assert.Equal("x", result[1].Content);
        Assert.True(over);
    }

    [Fact]
    public void Elision_never_touches_an_image_bearing_message()
    {
        //elision must never touch an image-bearing message, stripping the image would blind the model. this test bars widening elision past the tool role
        var img = new ImageRef(@"C:\shots.png", 1234, "abc123", "image/png");
        var msgs = new[]
        {
            new ChatMessage("user", "look at this", Images: new[] { img }),
            new ChatMessage("assistant", null, new[] { new ToolCall("c1", "shell", "{}") }),
            new ChatMessage("tool", new string('t', 4000), ToolCallId: "c1"),
            new ChatMessage("user", "and now?"),
        };

        var (result, elided, _) = ContextBudget.Apply(msgs, 200, msgs.Length);

        Assert.True(elided > 0, "precondition: the fat tool result must have been elided");
        Assert.Same(img, Assert.Single(result[0].Images!));
        Assert.Equal("look at this", result[0].Content);
    }

    [Fact]
    public void UsedTokens_counts_what_landed_since_the_last_server_reading()
    {
        var convo = new Conversation("you are gatto");
        convo.AddUser("read the file and fix the bug");
        convo.AddAssistant("looking now");

        var usage = new ContextUsageState();
        usage.Record(promptTokens: 10_000, estimateAtRequest: 10_000, messageCount: convo.Count);

        //with nothing appended, the figure equals the server's reading exactly.
        Assert.Equal(10_000, ContextBudget.UsedTokens(convo, usage));

        //the result arrives mid-turn with no new server reading, so the figure must include it.
        convo.AddToolResult("call_1", new ToolResult(new string('x', 70_000)));

        var used = ContextBudget.UsedTokens(convo, usage);
        Assert.NotNull(used);
        Assert.True(used > 10_000, $"the appended result must be counted; got {used}");
        Assert.Equal(10_000 + ContextBudget.GrowthSinceLastRequest(convo, usage), used);
    }

    [Fact]
    public void UsedTokens_is_null_before_any_server_reading()
    {
        var convo = new Conversation("you are gatto");
        convo.AddUser("go");
        Assert.Null(ContextBudget.UsedTokens(convo, new ContextUsageState()));
        Assert.Null(ContextBudget.UsedTokens(convo, null));
    }
}
