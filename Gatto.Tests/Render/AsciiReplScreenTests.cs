using System.Reflection;
using Gatto.Repl.Render;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Render;

//compose the whole screen under the ascii set, a guard that only hands in a glyph set stays green while the screen draws unicode
public class AsciiReplScreenTests
{
    private static readonly Theme Plain = new(new TermCaps(true, true));

    //the one em dash the ascii sweep lets through, a deliberate exclusion rather than a hole
    private const char EmDash = '—';

    //every transcript item kind, each in a state that draws chrome. a bare ReasoningItem draws no mark, so the reasoning states are their own entries
    private static IReadOnlyList<(string Name, TranscriptItem Item)> Corpus() =>
    [
        ("banner", Gatto.Repl.Repl.BannerItem(Plain, "generalist", "a-model", "gatto 0.5.0",
            GlyphSet.Ascii)),
        ("assistant", new AssistantBlockItem(["a line of prose", "", "- a bullet", "> a quote"],
            "generalist")),
        ("assistant-table", new AssistantBlockItem(
            ["| a | b |", "| --- | --- |", "| 1 | 2 |"], "generalist")),
        ("reasoning-capped", new ReasoningItem([.. Enumerable.Range(0, 40).Select(i => $"line {i}")])
            { Streaming = true, Collapsed = true }),
        ("reasoning-expanded", new ReasoningItem(["a thought", "another"])
            { Streaming = false, Collapsed = false, Elapsed = TimeSpan.FromSeconds(3), ClickHint = true }),
        ("reasoning-collapsed", new ReasoningItem(["a thought"])
            { Streaming = false, Collapsed = true, Elapsed = TimeSpan.FromSeconds(3), ClickHint = true }),
        ("tool", new ToolBlockItem("read_file", "{\"path\":\"a.txt\"}", "read a.txt", true,
            "generalist")),
        ("tool-dangling", new ToolBlockItem("run_agent", "{\"task\":\"go\"}", "", false, "generalist")),
        ("user", new UserEchoItem(["what does this do?", "and this?"])),
        ("listing", new ListingItem(new TableSpec(
            ["name", "size"], [ColumnAlign.Left, ColumnAlign.Right],
            [["one", "1 GB"], ["two", "2 GB"]]), null)),
        ("system", new SystemLineItem("compacted at 80%", null)),
        ("completion", new CompletionItem("done in 3s", null)),
        ("lead", new SessionLeadItem("resumed from a session", null)),
        ("command-echo", new CommandEchoItem(["/model", "  switched"], null)),
    ];

    //the chrome is composed under the ascii set like the transcript. the purr line is chrome rather than an item, so it belongs in this block
    private static ChromeBlock Chrome(int width)
    {
        var g = GlyphSet.Ascii;
        var purr = ChromeTicker.PurrHead(Gatto.Repl.Cats.Face(g), 4200,
            PurrFrames.Short);
        var composer = $"{g.Prompt} a line being typed";
        var status = $"{g.Sharp} generalist {g.Dot} a-model {g.Dot} ctx 12%";
        return ChromeBlock.FromText([purr, "", composer, status],
            caretRow: 2, caretCol: 2);
    }

    //two widths, because an edge defect only fires where a row ends exactly at the border
    [Theory]
    [InlineData(60)]
    [InlineData(100)]
    public void EVERY_COMPOSED_SCREEN_ROW_IS_ASCII_UNDER_THE_ASCII_SET(int width)
    {
        //collect every offender instead of throwing on the first one, so one run reports how many leaks there are
        var offenders = new List<string>();

        foreach (var (name, item) in Corpus())
        {
            var surface = new VtScreenSurface(width, 30);
            surface.Write(Ansi.AltScreenEnter);
            var model = new TranscriptModel("generalist");
            model.Append(item);

            var index = new LineIndex(model, Plain, GlyphSet.Ascii);
            var comp = new ViewportCompositor(surface, index, new object(), GlyphSet.Ascii)
            {
                ComposeChrome = (_, _) => Chrome(width),
            };
            comp.Paint(bottomOffset: 0, following: true);

            var rows = surface.Viewport;
            for (var r = 0; r < rows.Count; r++)
                foreach (var ch in rows[r])
                    if (ch > 0x7F && ch != EmDash)
                    {
                        offenders.Add($"{name} row {r + 1}: U+{(int)ch:X4} in  {rows[r].Trim()}");
                        break;
                    }
        }

        Assert.True(offenders.Count == 0,
            $"at width {width} these screens draw a character a legacy console has no glyph for:\n"
            + string.Join("\n", offenders));
    }

    //the same screens under the unicode set must draw non-ascii marks, or the ascii sweep passes on a transcript that never drew one
    [Fact]
    public void AND_THE_UNICODE_SET_STILL_DRAWS_MARKS_ON_THESE_SCREENS()
    {
        var marked = new List<string>();

        foreach (var (name, item) in Corpus())
        {
            var surface = new VtScreenSurface(100, 30);
            surface.Write(Ansi.AltScreenEnter);
            var model = new TranscriptModel("generalist");
            model.Append(item);

            var comp = new ViewportCompositor(surface, new LineIndex(model, Plain, glyphs: GlyphSet.Unicode), new object(), glyphs: GlyphSet.Unicode)
            {
                ComposeChrome = (_, _) => ChromeBlock.FromText([""], 0, 0),
            };
            comp.Paint(bottomOffset: 0, following: true);

            if (surface.Viewport.Any(row => row.Any(c => c > 0x7F))) marked.Add(name);
        }

        Assert.True(marked.Count >= 6,
            "only these screens draw a non-ASCII character under the Unicode set, so the sweep above "
            + "is mostly comparing ASCII with ASCII: " + string.Join(", ", marked));
    }

    //census the corpus against every concrete transcript item kind, so a kind added later cannot slip through with the guards green
    [Fact]
    public void THE_CORPUS_COVERS_EVERY_TRANSCRIPT_ITEM_KIND()
    {
        var kinds = typeof(TranscriptItem).Assembly.GetTypes()
            .Where(t => t.IsSubclassOf(typeof(TranscriptItem)) && !t.IsAbstract)
            .Select(t => t.Name)
            .ToHashSet(StringComparer.Ordinal);

        var covered = Corpus().Select(c => c.Item.GetType().Name).ToHashSet(StringComparer.Ordinal);

        Assert.True(kinds.SetEquals(covered),
            "the corpus and the transcript's item kinds disagree.\n  missing: "
            + string.Join(", ", kinds.Except(covered).OrderBy(s => s, StringComparer.Ordinal))
            + "\n  unknown: "
            + string.Join(", ", covered.Except(kinds).OrderBy(s => s, StringComparer.Ordinal)));
    }

    //the reflection query must find item kinds, or a query that returns nothing passes the census on an empty set
    [Fact]
    public void THE_KIND_CENSUS_FINDS_THE_KINDS()
    {
        var kinds = typeof(TranscriptItem).Assembly.GetTypes()
            .Where(t => t.IsSubclassOf(typeof(TranscriptItem)) && !t.IsAbstract)
            .ToList();

        Assert.True(kinds.Count >= 9, $"the census found only {kinds.Count} item kinds");
        Assert.Contains(kinds, t => t.Name == nameof(ToolBlockItem));
    }
}
