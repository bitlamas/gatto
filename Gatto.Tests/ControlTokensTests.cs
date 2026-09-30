using Gatto.Core;

namespace Gatto.Tests;

public sealed class ControlTokensTests
{
    [Theory]
    [InlineData("<|tool_calls_section_begin|>")]
    [InlineData("<|tool_calls_section_end|>")]
    [InlineData("<|tool_call_begin|>")]
    [InlineData("<|tool_call_end|>")]
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
        var (empty, n) = ControlTokens.Strip("");
        Assert.Equal("", empty);
        Assert.Equal(0, n);
    }
}
