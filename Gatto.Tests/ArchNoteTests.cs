using Gatto.Cli.Setup;
using Gatto.Core.Acquire;
using Gatto.Core.Models;
using Gatto.Terminal;

namespace Gatto.Tests;

//what the wizard says about a model the build cannot load, and silence about the ones it can
public class ArchNoteTests
{
    private static SupportedArchitectures Set(params string[] archs) =>
        new("b10076", "2026-08-13", new HashSet<string>(archs, StringComparer.Ordinal));

    private static ShelfRow Row(string? arch) => new(
        "o/m", "o", new HubQuant("m-Q4_K_M.gguf", 4_000_000_000, null),
        FitRegime.FitsGpu, 32768, false, null, 10, false, Arch: arch);

    [Fact]
    public void AN_ARCHITECTURE_THE_BUILD_CANNOT_LOAD_carries_the_marker()
    {
        Assert.Equal("needs a different llama.cpp build", ArchNote.Marker(Row("futurearch"), Set("llama")));
    }

    [Fact]
    public void A_KNOWN_ARCHITECTURE_CARRIES_NOTHING_and_never_a_supported_tick()
    {
        //membership in the set proves nothing, a known architecture gets no marker (and the absence needs its own assertion)
        Assert.Null(ArchNote.Marker(Row("llama"), Set("llama")));
        Assert.Empty(ArchNote.AskRows("llama", Set("llama")));
    }

    [Fact]
    public void AN_ARCHITECTURE_WE_WERE_NOT_TOLD_carries_nothing()
    {
        //a listing without the gguf expand reports no architecture, so a row from a degraded response gets no marker
        Assert.Null(ArchNote.Marker(Row(null), Set("llama")));
        Assert.Empty(ArchNote.AskRows(null, Set("llama")));
    }

    [Fact]
    public void AN_EMPTY_SET_MARKS_NOTHING()
    {
        Assert.Null(ArchNote.Marker(Row("futurearch"), Set()));
    }

    //the test asks AskRows, the live surface, so it asserts the wording that surface actually shows
    [Fact]
    public void THE_ASK_NAMES_THE_ARCH_THE_BUILD_AND_THE_ESCAPE_HATCH()
    {
        var all = string.Join(" | ", ArchNote.AskRows("futurearch", Set("llama")));

        //the row must show the arch string, so the user can search for support for it.
        Assert.Contains("futurearch", all, StringComparison.Ordinal);
        //the row must name the build, so the user knows which build to ask about.
        Assert.Contains("b10076", all, StringComparison.Ordinal);
        //the copy must state the escape hatch, otherwise a user has to discover it
        Assert.Contains("A build that can will run it fine", all, StringComparison.Ordinal);
        //the copy must say the fix is for this model only, otherwise users fear their whole install is affected
        Assert.Contains("for this model only", all, StringComparison.Ordinal);
    }

    [Fact]
    public void THE_COPY_BLAMES_THE_BUILD_AND_NEVER_THE_MODEL()
    {
        //the model works under the right binary, so the copy blames the build
        var all = string.Join(" | ", ArchNote.AskRows("futurearch", Set("llama")))
                  + " | " + ArchNote.Marker(Row("futurearch"), Set("llama"));

        foreach (var banned in new[]
                 { "unsupported", "incompatible", "not supported", "bad", "broken", "won't work" })
            Assert.DoesNotContain(banned, all, StringComparison.OrdinalIgnoreCase);

        //staying clear of blame words is not enough, the copy must also name the llama.cpp build that differs
        Assert.Contains("llama.cpp build", all, StringComparison.Ordinal);
    }

    //this test goes through the embedded-set overload and asserts both halves (a check that only sees the marking case cannot fail on the other)
    [Fact]
    public void THE_SCAFFOLD_ASK_EXPLAINS_through_the_embedded_set()
    {
        var ask = string.Join(" | ", ArchNote.AskRows("futurearch"));
        Assert.Contains("futurearch", ask, StringComparison.Ordinal);
        Assert.Contains("A build that can will run it fine", ask, StringComparison.Ordinal);

        Assert.Empty(ArchNote.AskRows("qwen3"));
    }

    //the arch glyph must never show without its legend segment, a bare glyph reads as a verdict on the model
    [Fact]
    public void THE_MARKS_COLUMN_CARRIES_THE_ARCH_MARK_and_the_LEGEND_EXPLAINS_IT()
    {
        //the row holds two marks on purpose, so the assertion covers their order (one glyph would prove nothing about it)
        var loaded = Row("futurearch") with
        {
            Vision = true,
            Badge = new Badge("o/m", new DateOnly(2026, 8, 13), "abc", "vendor", Passed: 5, Ran: 5),
        };

        var spec = ShelfTable.Spec([loaded], ShelfStage.Full, curatedPublisher: null, Gatto.Core.Hardware.MachineShape.Discrete, glyphs: GlyphSet.Unicode);

        //the grid holds models only, so body row 0 is the model and the marks cell is last.
        Assert.Equal($"{Gatto.Terminal.GlyphSet.Unicode.Vision} {Gatto.Terminal.GlyphSet.Unicode.Ok} {Gatto.Terminal.GlyphSet.Unicode.OtherBuild}", spec.Rows[0][^1]);

        //read the legend words from ArchNote.MarkerText rather than retyping them, retyped copy drifts from the sentence
        var legend = ShelfTable.Legend([loaded], glyphs: GlyphSet.Unicode);
        Assert.Contains($"{Gatto.Terminal.GlyphSet.Unicode.OtherBuild} {ArchNote.MarkerText}", legend, StringComparison.Ordinal);
    }

    [Fact]
    public void A_SHELF_WITH_NOTHING_TO_EXPLAIN_HAS_NO_LEGEND_SEGMENT_FOR_IT()
    {
        //a legend segment must appear only when a row shows that glyph, an unseen mark reads as information the shelf cannot back
        var plain = Row("qwen3") with { Vision = true };

        var legend = ShelfTable.Legend([plain], glyphs: GlyphSet.Unicode);
        Assert.Contains(Gatto.Terminal.GlyphSet.Unicode.Vision, legend!, StringComparison.Ordinal);
        Assert.DoesNotContain(Gatto.Terminal.GlyphSet.Unicode.OtherBuild, legend!, StringComparison.Ordinal);
        Assert.DoesNotContain(Gatto.Terminal.GlyphSet.Unicode.Ok, legend!, StringComparison.Ordinal);

        //a shelf with no marks at all must render no legend row.
        Assert.Null(ShelfTable.Legend([Row("qwen3")], glyphs: GlyphSet.Unicode));
    }
}
