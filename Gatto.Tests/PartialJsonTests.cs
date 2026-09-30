using System.Text.Json;
using Gatto.Core.Tools;

namespace Gatto.Tests;

public sealed class PartialJsonTests
{
    private static string Full(string command, string? before = null) =>
        before is null
            ? JsonSerializer.Serialize(new { command })
            : "{" + before + "," + JsonSerializer.Serialize(new { command })[1..];

    [Fact]
    public void Every_prefix_decodes_to_a_prefix_of_the_command_and_the_whole_buffer_to_the_command()
    {
        const string command = "$s = @'\necho \"=== tree ===\"\nls /tmp\t2>/dev/null\n'@; $s | ssh host \"bash -s\" \u00e9\u65e5";
        var json = Full(command);
        for (var n = 0; n <= json.Length; n++)
        {
            var got = PartialJson.StringValue(json[..n], "command") ?? "";
            Assert.True(command.StartsWith(got, StringComparison.Ordinal), $"prefix {n}: {got}");
        }
        Assert.Equal(JsonDocument.Parse(json).RootElement.GetProperty("command").GetString(), PartialJson.StringValue(json, "command"));
    }

    //a decoder that returns nothing passes the prefix oracle, so a planted head must come back whole
    [Fact]
    public void A_prefix_past_the_opening_quote_returns_the_planted_head()
    {
        var json = Full("Get-ChildItem -Recurse");
        var cut = json.IndexOf("-Recurse", StringComparison.Ordinal);
        Assert.Equal("Get-ChildItem ", PartialJson.StringValue(json[..cut], "command"));
    }

    [Fact]
    public void A_surrogate_pair_split_across_two_deltas_is_held_until_both_halves_arrive()
    {
        var json = "{\"command\":\"a\\ud83d\\ude00b\"}";
        var cut = json.IndexOf("\\ude00", StringComparison.Ordinal) + 3;
        Assert.Equal("a", PartialJson.StringValue(json[..cut], "command"));
        Assert.Equal("a\ud83d\ude00b", PartialJson.StringValue(json, "command"));
    }

    [Fact]
    public void An_escape_cut_at_the_buffer_end_stops_before_it()
    {
        Assert.Equal("ab", PartialJson.StringValue("{\"command\":\"ab\\", "command"));
        Assert.Equal("ab", PartialJson.StringValue("{\"command\":\"ab\\u00", "command"));
    }

    [Fact]
    public void Another_key_before_the_command_is_skipped()
    {
        Assert.Equal("ls", PartialJson.StringValue(Full("ls", "\"timeout_ms\":5000"), "command"));
        Assert.Equal("ls", PartialJson.StringValue(Full("ls", "\"nested\":{\"a\":[1,\"}\"]}"), "command"));
    }

    [Fact]
    public void The_key_name_inside_another_string_value_is_not_the_key()
    {
        var json = "{\"note\":\"use \\\"command\\\": x\",\"command\":\"ls\"}";
        Assert.Equal("ls", PartialJson.StringValue(json, "command"));
        Assert.Null(PartialJson.StringValue(json[..json.IndexOf(",\"command\"", StringComparison.Ordinal)], "command"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("{\"comm")]
    [InlineData("{\"command\"")]
    [InlineData("{\"command\":")]
    [InlineData("{\"command\":5}")]
    [InlineData("[\"command\"]")]
    public void Nothing_decodes_before_the_value_starts_or_when_it_is_not_a_string(string? json) =>
        Assert.True(string.IsNullOrEmpty(PartialJson.StringValue(json, "command")));
}
