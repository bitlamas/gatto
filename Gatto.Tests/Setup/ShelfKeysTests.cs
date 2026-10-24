using Gatto.Cli.Setup;
using Gatto.Cli.Setup.Shelves;
using Gatto.Cli.Setup.Tui;
using Gatto.Core.Acquire;
using Gatto.Core.Models;
using Gatto.Tests.Fakes;
using Gatto.Tests.Setup.Tui;

namespace Gatto.Tests.Setup;

//the shelf's keys: ? searches, d and / and s and f and the digits are unbound, and a chip during a search drops the old search's rows
public class ShelfKeysTests
{
    private static ModelRow Row(string id) => ShelfRows.Of(
        RepoId: id, Publisher: "unsloth", PickedQuant: new HubQuant("m-Q4_K_M.gguf", 4_000_000_000, null),
        Fit: FitRegime.FitsGpu, NativeCtx: 262144, Params: 9_000_000_000);

    private static SetupScreen.Choice Shelf()
    {
        var flow = new SetupFlow(new FakeSetupProbes { Rows = [Row("unsloth/a"), Row("unsloth/b")] }) { CanSwitchSource = true };
        return Assert.IsType<SetupScreen.Choice>(flow.StartPastEngine());
    }

    private static ConsoleKeyInfo Ch(char c) => new(c, ConsoleKey.NoName, false, false, false);

    private static readonly ConsoleKeyInfo Enter = new('\r', ConsoleKey.Enter, false, false, false);

    [Fact]
    public void QUESTION_MARK_SEARCHES()
    {
        var c = Shelf();
        Assert.Equal(ShelfControls.TypedAnswer("qwe"), WalkRender.AnswerAfter(c, 100, [Ch('?'), Ch('q'), Ch('w'), Ch('e'), Enter]));
    }

    [Fact]
    public void D_AND_SLASH_DO_NOTHING()
    {
        var c = Shelf();
        Assert.Equal(c.Options[0].Key, WalkRender.AnswerAfter(c, 100, [Ch('/'), Ch('d'), Enter]));
    }

    [Fact]
    public void DIGITS_ARE_UNBOUND()
    {
        var c = Shelf();
        Assert.Equal(c.Options[0].Key, WalkRender.AnswerAfter(c, 100, [Ch('2'), Enter]));
    }

    [Fact]
    public void S_AND_F_DO_NOTHING()
    {
        var c = Shelf();
        Assert.Equal(c.Options[0].Key, WalkRender.AnswerAfter(c, 100, [Ch('s'), Ch('f'), Enter]));
    }

    //the strip the plain face shows names ? for search and no retired key
    [Fact]
    public void THE_STRIP_NAMES_QUESTION_MARK()
    {
        var keys = ShelfControls.Keys(Shelf().Shelf!);
        Assert.Contains('?', keys);
        Assert.DoesNotContain('/', keys);
        Assert.DoesNotContain('d', keys);
    }

    //a wait returns at the signal, and the bound only caps a failure, as the loading shelf's tests bound theirs
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    //what the pool holds when a background start never came, so a failure says whether the pool or the flow kept it
    private static string PoolNow() =>
        $"pool {ThreadPool.ThreadCount} threads, {ThreadPool.PendingWorkItemCount} queued";

    //a chip clicked while a search runs cancels it, and its rows never reach the shelf
    [Fact]
    public void A_CHIP_CLICK_DURING_A_SEARCH()
    {
        using var release = new ManualResetEventSlim(false);
        using var started = new ManualResetEventSlim(false);
        var tokens = new List<CancellationToken>();
        var probes = new FakeSetupProbes
        {
            Slow = (r, _, ct) =>
            {
                lock (tokens) tokens.Add(ct);
                started.Set();
                if (r.Lit.Contains("glm")) return FakeSetupProbes.Outcome([Row("unsloth/new")]);
                release.Wait(TimeSpan.FromSeconds(5), CancellationToken.None);
                return FakeSetupProbes.Outcome([Row("unsloth/old")]);
            },
        };
        var flow = new SetupFlow(probes) { CanSwitchSource = true, LoadsShelfInBackground = true };
        flow.StartPastEngine();
        Assert.True(started.Wait(Bound), "the background search never started, " + PoolNow());

        flow.Answer(ShelfControls.FamilyAnswer("glm"));
        lock (tokens) Assert.True(tokens[0].IsCancellationRequested);
        release.Set();

        Assert.True(SpinWait.SpinUntil(flow.PollWatch, Bound), "the chip's search never resolved, " + PoolNow());
        var landed = Assert.IsType<SetupScreen.Choice>(flow.Answer(SetupFlow.Landed));
        Assert.Equal(["new"], landed.Shelf!.Rows.Select(r => r.Model));
    }
}
