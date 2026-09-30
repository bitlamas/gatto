using Gatto.Cli.Setup;
using Gatto.Repl.Render;
using Gatto.Terminal;

namespace Gatto.Tests.Setup.Tui;

//frames are drawn through the plain GutterWrap, a typed row would assert a guess about wrapping. the suite regenerates nothing, so a drifted frame fails
public class ClosingFrameExportTests
{
    private const int Width = 100;

    //the lines are literals on purpose, building them from SetupFlow would make the frames agree with the producer by construction
    public static TheoryData<string, string> Frames => new()
    {
        { "s10-after-swap-100.txt",
          "gemma-4-26B-A4B-it added · gemma-4-26B-A4B-it-UD-Q4_K_M.gguf · check passed" },
        { "s10-after-failed-check-100.txt",
          "gemma-4-26B-A4B-it added · gemma-4-26B-A4B-it-UD-Q4_K_M.gguf · check did not pass"
            + " · gatto audition re-runs it" },
        { "s10-after-unrun-check-100.txt",
          "gemma-4-26B-A4B-it added · gemma-4-26B-A4B-it-UD-Q4_K_M.gguf · check did not run"
            + " · gatto audition re-runs it" },
        { "s10-after-back-100.txt",
          "back to qwen-qwen3.6-35b-a3b · gemma-4-26B-A4B-it added · its check is kept" },
    };

    //the two gutters match what ItemRender.SystemRowsCore passes, marker first and hang after, so the wrap matches what a user sees
    internal static IReadOnlyList<string> Drawn(string line) =>
        GutterWrap.Rows(GlyphSet.Unicode.Sharp + " ", GlyphSet.Unicode.Sharp + " ", line, Width);

    //the record is taken with its continuation rows, a check that stops at the first row passes on a frame that lost the wrap
    [Theory]
    [MemberData(nameof(Frames))]
    public void THE_RECORD_ROWS_ARE_WHAT_THE_RENDERER_DRAWS(string file, string line)
    {
        var frame = File.ReadAllLines(Path.Combine(Golden.Dir, file));
        var drawn = Drawn(line);

        var at = Array.FindIndex(frame, r => r.StartsWith(GlyphSet.Unicode.Sharp + " ", StringComparison.Ordinal)
                                          && r.Contains(line[..12], StringComparison.Ordinal));
        Assert.True(at >= 0, $"{file} carries no record row for this line");

        for (var i = 0; i < drawn.Count; i++)
            Assert.Equal(drawn[i].TrimEnd(), frame[at + i].TrimEnd());

        //the row loop cannot see frame length, so length is compared with the source frame. an indentation rule would read the purr row as a continuation
        var source = File.ReadAllLines(Path.Combine(Golden.Dir, "s10-after-swap-100.txt"));
        Assert.Equal(source.Length - 1 + drawn.Count, frame.Length);
    }

    //the two long records measure 110 cells against a 100-cell frame, so the wrap must stay real. a copy change that lets them fit fails here
    [Theory]
    [InlineData("check did not pass")]
    [InlineData("check did not run")]
    public void THE_LONG_TAILS_REALLY_DO_WRAP(string tail)
    {
        var line = "gemma-4-26B-A4B-it added · gemma-4-26B-A4B-it-UD-Q4_K_M.gguf · " + tail
            + " · gatto audition re-runs it";

        Assert.Equal(2, Drawn(line).Count);
    }

    //the short records are the twin case, a renderer that wrapped everything would still draw the wrapped frames
    [Theory]
    [InlineData("gemma-4-26B-A4B-it added · gemma-4-26B-A4B-it-UD-Q4_K_M.gguf · check passed")]
    [InlineData("back to qwen-qwen3.6-35b-a3b · gemma-4-26B-A4B-it added · its check is kept")]
    public void AND_THE_SHORT_RECORDS_STAY_ON_ONE_ROW(string line) =>
        Assert.Single(Drawn(line));
}
