using Gatto.Cli.Setup;
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

    private static WizardScreen.Choice Shelf()
    {
        var flow = new SetupFlow(new WizardProbes { Rows = [Row("unsloth/a"), Row("unsloth/b")] }) { CanSwitchSource = true };
        return Assert.IsType<WizardScreen.Choice>(flow.StartPastEngine());
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

    //a chip clicked while a search runs cancels it, and its rows never reach the shelf
    [Fact]
    public void A_CHIP_CLICK_DURING_A_SEARCH()
    {
        using var release = new ManualResetEventSlim(false);
        var tokens = new List<CancellationToken>();
        var probes = new WizardProbes
        {
            Slow = (r, _, ct) =>
            {
                lock (tokens) tokens.Add(ct);
                if (r.Lit.Contains("glm")) return WizardProbes.Outcome([Row("unsloth/new")]);
                release.Wait(TimeSpan.FromSeconds(5), CancellationToken.None);
                return WizardProbes.Outcome([Row("unsloth/old")]);
            },
        };
        var flow = new SetupFlow(probes) { CanSwitchSource = true, LoadsShelfInBackground = true };
        flow.StartPastEngine();
        Assert.True(SpinWait.SpinUntil(() => { lock (tokens) return tokens.Count == 1; }, TimeSpan.FromSeconds(5)));

        flow.Answer(ShelfControls.FamilyAnswer("glm"));
        lock (tokens) Assert.True(tokens[0].IsCancellationRequested);
        release.Set();

        Assert.True(SpinWait.SpinUntil(flow.PollWatch, TimeSpan.FromSeconds(5)));
        var landed = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.Landed));
        Assert.Equal(["new"], landed.Shelf!.Rows.Select(r => r.Model));
    }
}
