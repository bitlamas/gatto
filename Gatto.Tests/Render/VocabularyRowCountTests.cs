using Gatto.Repl.Render;
using Gatto.Terminal;

namespace Gatto.Tests.Render;

//the row count must not depend on the glyph vocabulary, since the glyph-bearing rows are truncated to one row
public class VocabularyRowCountTests
{
    private static readonly Theme Plain = new(TermCaps.Plain);

    //the corpus sweeps a gloss across the wrap boundary one cell at a time, so a break at one exact width is still caught
    public static TheoryData<string> Items()
    {
        var data = new TheoryData<string>();
        foreach (var k in new[]
                 {
                     "reasoning-1", "reasoning-40", "reasoning-capped", "reasoning-expanded",
                     "reasoning-collapsed", "assistant-short", "assistant-long",
                     "tool", "tool-noresult", "user", "system", "completion", "lead",
                 })
            data.Add(k);
        for (var n = 1; n <= 60; n++) data.Add($"gloss-{n}");
        return data;
    }

    private static TranscriptItem Build(string key) => key switch
    {
        "reasoning-1" => new ReasoningItem(["a short thought"]),
        "reasoning-40" => new ReasoningItem([.. Enumerable.Range(0, 40).Select(i => $"line {i}")]),
        //only these states draw a glyph mark, so a corpus of bare items would cover none of the rows where the two sets differ
        "reasoning-capped" => new ReasoningItem(
            [.. Enumerable.Range(0, 40).Select(i => $"line {i}")]) { Streaming = true, Collapsed = true },
        "reasoning-expanded" => new ReasoningItem(
            [.. Enumerable.Range(0, 6).Select(i => $"line {i}")])
            { Streaming = false, Collapsed = false, Elapsed = TimeSpan.FromSeconds(3), ClickHint = true },
        "reasoning-collapsed" => new ReasoningItem(["one"])
            { Streaming = false, Collapsed = true, Elapsed = TimeSpan.FromSeconds(3), ClickHint = true },
        "assistant-short" => new AssistantBlockItem(["a line"], ""),
        "assistant-long" => new AssistantBlockItem(
            [.. Enumerable.Range(0, 40).Select(i => $"a prose line number {i} with words in it")], ""),
        "tool" => new ToolBlockItem("read_file", "{\"path\":\"a.txt\"}", "read a.txt", true, ""),
        "tool-noresult" => new ToolBlockItem("run_agent", "{}", "", false, ""),
        "user" => new UserEchoItem(["a user line"]),
        "system" => new SystemLineItem("a system row", null),
        "completion" => new CompletionItem("done", null),
        "lead" => new SessionLeadItem("resumed", null),
        _ => new ToolBlockItem("read_file", "{}",
            new string('x', int.Parse(key["gloss-".Length..])), true, ""),
    };

    //a row count must be the same in both vocabularies. while this holds, LineIndex may be handed either set without the scroll count drifting.
    [Theory]
    [MemberData(nameof(Items))]
    public void A_ROW_COUNT_DOES_NOT_DEPEND_ON_THE_VOCABULARY(string key)
    {
        var item = Build(key);

        for (var w = 10; w <= 100; w++)
        {
            var unicode = item.Render(w, Plain, GlyphSet.Unicode).Count;
            var ascii = item.Render(w, Plain, GlyphSet.Ascii).Count;
            Assert.True(unicode == ascii,
                $"{key} at width {w} is {unicode} rows under the Unicode set and {ascii} under the "
                + "ASCII one. A glyph-bearing row now WRAPS rather than truncating, so LineIndex's "
                + "vocabulary decides scroll arithmetic - deviation 500 is live, and the index must be "
                + "given the set the compositor draws with at every construction.");
        }
    }

    //the two sets must really render different rows. the count test above would otherwise compare a render with itself.
    [Fact]
    public void THE_TWO_SETS_REALLY_DO_RENDER_DIFFERENT_ROWS()
    {
        var item = new AssistantBlockItem(["a line of prose"], "");

        var unicode = item.Render(60, Plain, GlyphSet.Unicode);
        var ascii = item.Render(60, Plain, GlyphSet.Ascii);

        Assert.False(unicode.SequenceEqual(ascii),
            "the ASCII set changed nothing about this item, so the row above is comparing a render "
            + "with itself");
    }

    //the index counts with the set it was handed, checked against the item's own render under that same set
    [Fact]
    public void THE_INDEX_COUNTS_WITH_THE_SET_IT_WAS_GIVEN()
    {
        var model = new TranscriptModel("generalist");
        var item = new AssistantBlockItem(["a line of prose that runs on for a while"], "");
        model.Append(item);

        var index = new LineIndex(model, Plain, GlyphSet.Ascii);

        for (var w = 20; w <= 80; w += 10)
            Assert.Equal(item.Render(w, Plain, GlyphSet.Ascii).Count, index.Count(item, w));
    }
}
