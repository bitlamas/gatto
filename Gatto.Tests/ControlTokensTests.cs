using Gatto.Core;

namespace Gatto.Tests;

public sealed class ControlTokensTests
{
    [Theory]
    [InlineData("<|tool_calls_section_begin|>")]
    [InlineData("<|tool_calls_section_end|>")]
    [InlineData("<|tool_call_begin|>")]
    [InlineData("<|tool_call_end|>")]
    [InlineData("<think>")]
    [InlineData("</think>")]
    public void Strips_each_allowlisted_token(string token)
    {
        var (text, count) = ControlTokens.Strip($"before {token} after");
        Assert.Equal("before  after", text);
        Assert.Equal(1, count);
    }

    [Fact]
    public void Strips_adjacent_repeats_the_423gate3_record34_shape()
    {
        //the input holds the real shipped shape, adjacent begin tokens then section ends
        var input = "<|tool_call_begin|><|tool_call_begin|><|tool_call_begin|>" +
                    "<|tool_calls_section_end|><|tool_call_begin|>Okay, I need to handle this.";
        var (text, count) = ControlTokens.Strip(input);
        Assert.Equal("Okay, I need to handle this.", text);
        Assert.Equal(5, count);
    }

    [Fact]
    public void All_pollution_strips_to_empty()
    {
        var (text, count) = ControlTokens.Strip("<|tool_calls_section_end|>");
        Assert.Equal("", text);
        Assert.Equal(1, count);
    }

    [Theory]
    [InlineData("<|im_start|>user")]              //markers like ChatML in tool-result content are legitimate and not on the allowlist, so they must survive.
    [InlineData("<|endoftext|>")]
    [InlineData("<|tool_call|>")]                 //the row tests a spelling close to an allowlisted token.
    [InlineData("<|tool_call_beg")]               //the row covers a partial token left by a stream drop.
    [InlineData("tool_call_begin")]               //the row has the name without its delimiters.
    [InlineData("<thinking>")]                    //the row has a longer tag that only begins like the think tag.
    [InlineData("< /think>")]                     //the row has the tag broken by a space.
    [InlineData("a < b and b > c")]               //the row has comparison text that reaches the slow path and matches nothing.
    public void Leaves_non_allowlisted_text_untouched(string input)
    {
        var (text, count) = ControlTokens.Strip(input);
        Assert.Equal(input, text);
        Assert.Equal(0, count);
    }

    [Fact]
    public void Zero_match_fast_path_returns_same_instance()
    {
        var input = "plain prose with no markers at all";
        var (text, count) = ControlTokens.Strip(input);
        Assert.Same(input, text);
        Assert.Equal(0, count);
        //text with a '<' that matches nothing comes back as the same instance too
        var compare = "a < b";
        Assert.Same(compare, ControlTokens.Strip(compare).Text);
        var (empty, n) = ControlTokens.Strip("");
        Assert.Equal("", empty);
        Assert.Equal(0, n);
    }

    private static string Streamed(params string[] chunks)
    {
        var filter = new ControlTokens.StreamFilter();
        var shown = string.Concat(chunks.Select(filter.Push));
        return shown + filter.Flush();
    }

    [Theory]
    [InlineData("a<think>b</think>c")]
    [InlineData("<|tool_calls_section_begin|><|tool_call_begin|>x<|tool_call_end|><|tool_calls_section_end|>")]
    [InlineData("I'm running it.\n</think>\n\nWhat next?")]
    [InlineData("<thinking> a < b <|im_start|> </thi")]          //the row has near misses and a partial token at the end.
    [InlineData("<thi</think>nk>")]                                //the row has a token inside a partial one, which a single pass leaves as a whole token.
    [InlineData("plain")]
    public void Streamed_output_equals_the_whole_strip_at_every_split(string whole)
    {
        var expected = ControlTokens.Strip(whole).Text;
        for (var i = 0; i <= whole.Length; i++)
            for (var j = i; j <= whole.Length; j++)
                Assert.Equal(expected, Streamed(whole[..i], whole[i..j], whole[j..]));
        Assert.Equal(expected, Streamed(whole.Select(c => c.ToString()).ToArray()));
    }

    [Fact]
    public void A_partial_token_at_the_end_of_the_stream_is_flushed_as_text()
    {
        var filter = new ControlTokens.StreamFilter();
        Assert.Equal("answer ", filter.Push("answer <thi"));
        Assert.Equal("<thi", filter.Flush());
    }

    [Fact]
    public void A_token_completed_by_the_last_chunk_is_dropped()
    {
        var filter = new ControlTokens.StreamFilter();
        Assert.Equal("answer ", filter.Push("answer </thi"));
        Assert.Equal("", filter.Push("nk>"));
        Assert.Equal("", filter.Flush());
    }

    [Fact]
    public void A_lone_angle_bracket_is_released_once_it_cannot_be_a_token()
    {
        var filter = new ControlTokens.StreamFilter();
        Assert.Equal("a ", filter.Push("a <"));
        Assert.Equal("< b", filter.Push(" b"));
    }

    [Fact]
    public void A_chunk_with_no_angle_bracket_passes_as_the_same_instance()
    {
        var chunk = "plain prose";
        Assert.Same(chunk, new ControlTokens.StreamFilter().Push(chunk));
    }

    [Fact]
    public void Removed_lists_each_token_the_strip_takes_in_order()
    {
        Assert.Equal(["</think>", "<think>", "</think>"], ControlTokens.Removed("x</think>y<think>z</think>"));
        Assert.Empty(ControlTokens.Removed("a < b <thinking>"));
    }

    [Fact]
    public void The_notice_names_the_one_token_the_model_and_the_channel()
    {
        Assert.Equal("removed a stray </think> from qwen3.5-9b's reply",
            ControlTokens.Notice("qwen3.5-9b", "reply", ["</think>"]));
    }

    [Fact]
    public void The_notice_counts_several_tokens_and_names_each_distinct_one_once()
    {
        Assert.Equal("removed 3 stray template tags from qwen3.5-9b's reply and reasoning: </think>, <|tool_call_end|>",
            ControlTokens.Notice("qwen3.5-9b", "reply and reasoning", ["</think>", "<|tool_call_end|>", "</think>"]));
    }
}
