using Gatto.Cli.Setup;
using Gatto.Core.Acquire;
using Gatto.Core.Hardware;
using Gatto.Core.Models;
using Gatto.Terminal;
using Shelf = Gatto.Cli.Setup.Tui.Shelf;

namespace Gatto.Tests.Setup;

//the count row under the hub shelf, as the drawn page writes it, and the footer's a as it reads lifted and not
public class ShelfCountRowTests
{
    private static ModelRow Row(string repo, FitRegime fit) =>
        ShelfRows.Of(repo, repo.Split('/')[0], new HubQuant("m-Q4_K_M.gguf", 4_000_000_000, null), fit);

    private static ShelfView View(int rows, MachineShape shape = MachineShape.Discrete) =>
        new([.. Enumerable.Range(0, rows).Select(i => Row($"unsloth/m{i}-GGUF", FitRegime.FitsGpu))], shape,
            Total: rows);

    private static string Count(ShelfView v, long? shownMs = null) =>
        Shelf.CountLine(v, GlyphSet.Unicode, shownMs);

    [Fact]
    public void THE_COUNT_ROW()
    {
        Assert.Equal("1–7 of 7 · a show all", Count(View(7) with { MoreBehind = true }));
        Assert.Equal("1–7 of 7", Count(View(7)));
    }

    //under a the row counts each regime the lifted shelf holds, a group with none left out
    [Fact]
    public void THE_LIFTED_COUNT_ROW()
    {
        var lifted = View(11) with { Lift = true, OnCard = 4, InMemory = 5, TooBig = 2 };
        Assert.Equal("1–11 of 11 · 4 ✓ GPU · 5 ⚠ RAM · 2 ✗ too big", Count(lifted));
        Assert.Equal("1–11 of 11 · 4 ✓ GPU · 7 ✗ too big", Count(lifted with { InMemory = 0, TooBig = 7 }));

        //on a machine with no card the two fitting regimes read the same mark, so they count as one
        Assert.Equal("1–11 of 11 · 9 ✓ fits · 2 ✗ too big",
            Count(lifted with { Shape = MachineShape.UnifiedWithShare }));
    }

    //a search the deadline stopped says so, and one Hugging Face held back counts the server's seconds down from what the face measured
    [Fact]
    public void A_STOPPED_SEARCH_SAYS_WHY()
    {
        Assert.EndsWith(" · the search stopped before it finished", Count(View(3) with { Cut = true }));

        var held = View(3) with { Cut = true, RateLimited = true, RateLimitedFor = 42 };
        Assert.EndsWith(" · Hugging Face asked gatto to wait 42 s", Count(held, shownMs: 0));
        Assert.EndsWith(" · Hugging Face asked gatto to wait 30 s", Count(held, shownMs: 12_400));
        Assert.EndsWith(" · Hugging Face asked gatto to wait, the wait is over", Count(held, shownMs: 50_000));

        Assert.EndsWith(" · Hugging Face asked gatto to wait", Count(View(3) with { Cut = true, RateLimited = true }));
    }

    //the footer's a reads show all, and show less while the shelf is lifted
    [Fact]
    public void THE_FOOTER()
    {
        var ring = new FocusRing([Region.Families, Region.List]);
        Assert.Contains(new FooterKey("a", "show all"), Shelf.Keys(ring, ShelfSource.Hub, lift: true));
        Assert.Contains(new FooterKey("a", "show less"), Shelf.Keys(ring, ShelfSource.Hub, lift: true, lifted: true));
        Assert.DoesNotContain(Shelf.Keys(ring, ShelfSource.Hub), k => k.Key == "a");
    }
}
