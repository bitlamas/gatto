using Gatto.Repl;
using Gatto.Repl.Render;
using Gatto.Terminal;

namespace Gatto.Tests;

//the pool holds four sets, one drawn per turn at the call site, and index 0 is the default so tests stay deterministic
public class PurrPoolTests
{
    private const string Face = "(=^.w.^)>";

    //four sets, sixteen frames each, every frame padded to exactly the pool width so the timer column never moves
    [Fact]
    public void EVERY_POOL_MEMBER_IS_SIXTEEN_FRAMES_AT_THE_POOL_WIDTH()
    {
        Assert.Equal(4, PurrFrames.Pool.Count);

        foreach (var set in PurrFrames.Pool)
        {
            Assert.Equal(16, set.Frames.Count);
            Assert.Equal(PurrFrames.PoolWidth, set.Frames.Max(f => f.Length));
            Assert.Equal(PurrFrames.PoolWidth, set.Width);
        }
    }

    //a thirteen-cell frame here proves the census can catch one, since PadRight leaves an over-wide frame over-wide
    [Fact]
    public void AND_A_THIRTEEN_CELL_FRAME_WOULD_BE_CAUGHT()
    {
        var wide = new PurrFrames(["purr", "purrrrrrrr..."], PurrFrames.PoolWidth);

        Assert.Equal(13, wide.Frames.Max(f => f.Length));
        Assert.NotEqual(PurrFrames.PoolWidth, wide.Frames.Max(f => f.Length));
        Assert.Equal(13, wide.At(140).Length);   //padding must not shrink an over-wide frame back, so it cannot hide the defect.
    }

    //a set already as wide as the pool proves nothing, so the fixture is five wide and the asserts read At
    [Fact]
    public void A_SET_NARROWER_THAN_THE_POOL_STILL_PADS_TO_THE_POOLS_WIDTH()
    {
        var narrow = new PurrFrames(["purr", "purr."], PurrFrames.PoolWidth);
        var onItsOwn = new PurrFrames(["purr", "purr."]);

        Assert.Equal(PurrFrames.PoolWidth, narrow.At(0).Length);
        Assert.Equal(5, onItsOwn.At(0).Length);
        Assert.NotEqual(onItsOwn.At(0).Length, narrow.At(0).Length);
    }

    //the pool uses only the six allowed characters, since a stray one would be a different decision
    [Fact]
    public void THE_POOL_SPELLS_ITSELF_FROM_SIX_CHARACTERS()
    {
        var allowed = new HashSet<char>("pur~. ");

        var strays = PurrFrames.Pool
            .SelectMany(s => s.Frames)
            .SelectMany(f => f)
            .Where(c => !allowed.Contains(c))
            .Distinct()
            .ToArray();

        Assert.True(strays.Length == 0,
            "the pool uses characters outside p u r ~ . space: " + string.Join(", ", strays));
    }

    //each set grows then shrinks, so assert at most one turn of direction, since the real sets are uneven
    [Fact]
    public void EVERY_SET_GROWS_THEN_SHRINKS_AND_TURNS_ONCE()
    {
        foreach (var set in PurrFrames.Pool)
        {
            var lengths = set.Frames.Select(f => f.Length).ToArray();
            var turns = 0;
            var rising = true;
            for (var i = 1; i < lengths.Length; i++)
            {
                if (lengths[i] == lengths[i - 1]) continue;   //a held length is not a change of direction.
                var up = lengths[i] > lengths[i - 1];
                if (up != rising) { turns++; rising = up; }
            }

            Assert.True(turns <= 1, $"the set turns {turns} times: {string.Join(" ", set.Frames)}");
        }
    }

    //index 0 is the set every default reads, so assert by identity rather than equal strings
    [Fact]
    public void POOL_INDEX_ZERO_IS_THE_SET_EVERY_DEFAULT_USES()
    {
        Assert.Same(PurrFrames.Full, PurrFrames.Pool[0]);
        Assert.Same(PurrFrames.Full, PurrFrames.FromPool(0));
    }

    //the index must wrap, since a bounds exception in a chrome row would take down a whole turn over an animation
    [Theory]
    [InlineData(4, 0)]
    [InlineData(5, 1)]
    [InlineData(-1, 3)]
    public void THE_INDEX_WRAPS(int asked, int expected) =>
        Assert.Same(PurrFrames.Pool[expected], PurrFrames.FromPool(asked));

    //the short set must not join the pool, since a download watch runs for hours and its six calm frames answer that duration
    [Fact]
    public void THE_SHORT_SET_IS_NOT_IN_THE_POOL_AND_KEEPS_ITS_OWN_WIDTH()
    {
        Assert.DoesNotContain(PurrFrames.Short, PurrFrames.Pool);
        Assert.Equal(6, PurrFrames.Short.Frames.Count);
        Assert.Equal(7, PurrFrames.Short.Width);
    }

    //assert on the composed row, since the two turns draw different words and the timer keeps its column
    [Fact]
    public void TWO_TURNS_DRAW_DIFFERENT_WORDS_AND_THE_TIMER_KEEPS_ITS_COLUMN()
    {
        const long ms = 1_400;
        var first = ChromeTicker.PurrHead(Face, ms, PurrFrames.FromPool(0));
        var second = ChromeTicker.PurrHead(Face, ms, PurrFrames.FromPool(1));

        var elapsed = ChromeTicker.FormatElapsed(ms);
        Assert.NotEqual(first, second);
        Assert.Equal(first.LastIndexOf(elapsed, StringComparison.Ordinal),
            second.LastIndexOf(elapsed, StringComparison.Ordinal));
        Assert.True(first.LastIndexOf(elapsed, StringComparison.Ordinal) > 0,
            "the elapsed is not on the row, so the columns agree about nothing");
    }

    //sweep every moment of the cycle, since a frame too wide at one position passes at every other moment
    [Fact]
    public void EVERY_MEMBER_KEEPS_THE_COLUMN_AT_EVERY_MOMENT_OF_ITS_CYCLE()
    {
        var expected = -1;
        foreach (var set in PurrFrames.Pool)
            for (long ms = 0; ms < 16 * 140; ms += 20)
            {
                var row = ChromeTicker.PurrHead(Face, ms, set);
                var at = row.LastIndexOf(ChromeTicker.FormatElapsed(ms), StringComparison.Ordinal);
                if (expected < 0) expected = at;
                Assert.Equal(expected, at);
            }
    }

    //the default of PurrRow must be index 0, which keeps the repl's frames unchanged for a caller that names no set
    [Fact]
    public void THE_REPL_ROW_DEFAULTS_TO_INDEX_ZERO()
    {
        var defaulted = ChromeTicker.PurrRow(Face, 1_400, tokens: 12, toolTokens: 0);
        var explicitly = ChromeTicker.PurrRow(Face, 1_400, tokens: 12, toolTokens: 0,
            frames: PurrFrames.FromPool(0));

        Assert.Equal(explicitly, defaulted);
        Assert.NotEqual(
            ChromeTicker.PurrRow(Face, 1_400, tokens: 12, toolTokens: 0, frames: PurrFrames.FromPool(1)),
            defaulted);
    }

    //test this call site on its own, since the audition row is a second site of the same default
    [Fact]
    public void THE_AUDITION_ROW_TAKES_THE_POOL_AND_DEFAULTS_TO_INDEX_ZERO()
    {
        var defaulted = Gatto.Cli.Setup.AuditionTicker.Row(1_400, "task 3 of 5", GlyphSet.Unicode);
        var zero = Gatto.Cli.Setup.AuditionTicker.Row(1_400, "task 3 of 5", GlyphSet.Unicode,
            PurrFrames.FromPool(0));
        var one = Gatto.Cli.Setup.AuditionTicker.Row(1_400, "task 3 of 5", GlyphSet.Unicode,
            PurrFrames.FromPool(1));

        Assert.Equal(zero, defaulted);
        Assert.NotEqual(one, defaulted);
    }

    //assert that every draw answers with a pool member and that more than one member appears, since uniformity is Random.Shared's job
    [Fact]
    public void THE_ONE_RANDOM_DRAW_ALWAYS_ANSWERS_WITH_A_POOL_MEMBER()
    {
        var seen = new HashSet<PurrFrames>();
        for (var i = 0; i < 200; i++)
        {
            var drawn = PurrFrames.RandomFromPool();
            Assert.Contains(drawn, PurrFrames.Pool);
            seen.Add(drawn);
        }

        Assert.True(seen.Count > 1, "200 draws returned one member; this is a constant, not a draw");
    }

    //one file calls the random draw and every seam takes a defaulted chooser, so the matcher runs on a known match and a known miss
    [Fact]
    public void THE_RANDOM_DRAW_IS_CALLED_FROM_ONE_FILE_ONLY()
    {
        const string Call = "PurrFrames.RandomFromPool";

        Assert.Contains(Call, "purrSet: Gatto.Repl.Render.PurrFrames.RandomFromPool,", StringComparison.Ordinal);
        Assert.DoesNotContain(Call, "purrSet: () => PurrFrames.Full", StringComparison.Ordinal);

        var root = Census.SourceTree.RepoRoot();
        var callers = new List<string>();
        var calls = 0;
        foreach (var path in Census.SourceTree.ProductionFiles())
        {
            var rel = Path.GetRelativePath(root, path).Replace('\\', '/');
            var code = Census.SourceTree.CodeOnly(Census.SourceTree.Read(path));
            //skip the declaring file, since its declaration is not a call site
            if (rel.EndsWith("Repl/Render/ChromeTicker.cs", StringComparison.Ordinal)) continue;
            var n = code.Split(Call).Length - 1;
            if (n > 0) { callers.Add($"{rel} ({n})"); calls += n; }
        }

        Assert.Equal(["Gatto/Cli/GattoApp.cs (3)"], callers);
        //the three calls are the one face of gatto setup and gatto model, its probes, and the session's
        Assert.Equal(3, calls);
    }
}
