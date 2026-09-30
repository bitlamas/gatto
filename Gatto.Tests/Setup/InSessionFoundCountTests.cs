using Gatto.Cli.Setup;
using Gatto.Core.Acquire;
using Gatto.Core.Models;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup;

//the in-session found line counts only models not yet on the list. the setup flow still counts them all, since its sentence says already on this machine
public class InSessionFoundCountTests
{
    private const string Adopted = @"C:\weights\qwen3.5-4b\qwen3.5-4b-Q4_K_M.gguf";
    private const string Loose = @"C:\weights\gemma-4-e2b\gemma-4-e2b-Q4_K_M.gguf";

    //the fixture needs a Hub row, since the discovery sentence appears only on a search screen that has rows
    private static ShelfRow HubRow() =>
        new("qwen/qwen3.5-7b", "qwen", new HubQuant("qwen3.5-7b-Q4_K_M.gguf", 4_000_000_000, null),
            FitRegime.FitsGpu, 262144, false, Badge: null, Downloads: 5, Gated: false,
            Params: 7_000_000_000);

    private static WizardProbes Probes()
    {
        var p = new WizardProbes
        {
            Llama = @"C:\llama\llama-server.exe",
            Rows = [HubRow()],
            Found = [new FoundModel(Adopted, 4_000_000_000, null),
                     new FoundModel(Loose, 3_000_000_000, null)],
        };
        //one model is on the list and one is not, or a count ignoring the list would read the same as one using it
        p.Existing[Adopted] = "qwen3.5-4b";
        return p;
    }

    //this flag normally comes from the face, so a directly built flow shows no discovery row and binds no m
    private static SetupFlow Flow()
    {
        var flow = new SetupFlow(Probes());
        flow.CanSwitchSource = true;
        return flow;
    }

    private static string FoundLine(WizardScreen s) =>
        Assert.IsType<WizardScreen.Choice>(s).BodyRows!
            .Select(r => r.Text)
            .Single(t => t.Contains("gatto found", StringComparison.Ordinal)
                      || t.Contains("also found", StringComparison.Ordinal));

    [Fact]
    public void IN_SESSION_THE_FOUND_LINE_DOES_NOT_COUNT_A_MODEL_ALREADY_ON_THE_LIST()
    {
        var flow = Flow();

        var line = FoundLine(flow.StartAtModelSegment());

        Assert.Contains("gatto found 1 model ", line, StringComparison.Ordinal);
        Assert.Contains("not yet on its list", line, StringComparison.Ordinal);
    }

    //the shelf must keep listing an adopted model, since picking it reuses that model instead of scaffolding a second one
    [Fact]
    public void THE_M_SHELF_STILL_OFFERS_AN_ADOPTED_MODEL_SO_IT_CAN_BE_REUSED()
    {
        var flow = Flow();
        flow.StartAtModelSegment();

        //the test answers with the flow command, since the face maps m to CtlSource. typing m at the flow would drive a seam the product does not have
        var shelf = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.CtlSource)).Shelf!;

        Assert.Equal(2, shelf.Rows.Count);
        Assert.Contains(shelf.Rows, r => r.RepoId.Contains("qwen3.5-4b", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(shelf.Rows, r => r.RepoId.Contains("gemma-4-e2b", StringComparison.OrdinalIgnoreCase));

        //the adopted row must show a have-mark, so the shelf tells the two rows apart without hiding either.
        Assert.Contains(shelf.Facts!, f => f.Have != Gatto.Terminal.HaveMark.None);
        Assert.Contains(shelf.Facts!, f => f.Have == Gatto.Terminal.HaveMark.None);
    }

    //the setup flow has no list yet, so an adopted model must stay in its count
    [Fact]
    public void ON_THE_SETUP_ROAD_AN_ADOPTED_MODEL_IS_STILL_COUNTED()
    {
        var flow = Flow();

        var line = FoundLine(flow.StartPastEngine());

        Assert.Contains("also found 2 models", line, StringComparison.Ordinal);
        Assert.Contains("already on this machine", line, StringComparison.Ordinal);
    }
}
