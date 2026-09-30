using Gatto.Cli.Setup;
using Gatto.Cli.Setup.Tui;
using Gatto.Core.Acquire;
using Gatto.Core.Models;
using Gatto.Repl;
using Gatto.Terminal;
using Gatto.Tests.Fakes;
using Gatto.Tests.Setup.Tui;

namespace Gatto.Tests.Setup;

//a search that finds nothing keeps the shelf and says why it is bare, so drive it through the flow
public class SearchFoundNothingTests
{
    //answer null so Choose returns, and the recorded ask is the screen this face fully composed
    private sealed class Recorder : IWizardPrompter
    {
        public List<WizardAsk> Asked { get; } = [];

        public WizardAnswer? AskOne(WizardAsk ask)
        {
            Asked.Add(ask);
            return null;
        }
    }

    private static ShelfRow Ranked(string id) => new(
        RepoId: id, Publisher: id.Split('/')[0],
        PickedQuant: new HubQuant("m-Q4_K_M.gguf", 18_000_000_000, null),
        Fit: FitRegime.FitsGpu, NativeCtx: 262144, Vision: false, Badge: null,
        Downloads: 900, Gated: false, Params: 3_000_000_000, Arch: "qwen3");

    //the fake must answer rows for the unfiltered browse and honour IncludeUnfittable, or the a key answers empty
    private static SetupFlow Flow(HubSearchCause? cause, int tooBig = 0)
    {
        IReadOnlyList<ShelfRow> rows = [Ranked("o/qwen-small"), Ranked("o/qwen-tiny")];
        var probes = new WizardProbes
        {
            Rows = rows,
            Answer = req => req.Search is { Length: > 0 } && !req.IncludeUnfittable
                ? new HubSearchOutcome([], cause, null, HiddenByFit: tooBig)
                : new HubSearchOutcome(rows, null, null, HiddenByFit: 0),
        };
        var flow = new SetupFlow(probes);
        Assert.IsType<WizardScreen.Choice>(flow.StartPastEngine());
        return flow;
    }

    private static WizardScreen.Choice AfterSearch(
        string term, HubSearchCause? cause, int tooBig = 0)
    {
        var flow = Flow(cause, tooBig);
        return Assert.IsType<WizardScreen.Choice>(flow.Answer(ShelfControls.TypedAnswer(term)));
    }

    private static string Painted(WizardScreen.Choice screen, int width = 100) =>
        string.Join(" | ", Shelf.Body(screen.Shelf!, row: 0, chip: 0, file: -1,
            Region.List, width, glyphs: GlyphSet.Unicode).Select(r => r.Text));

    //the frame must survive and the body must say why it is bare
    [Fact]
    public void A_SEARCH_THAT_FINDS_NOTHING_KEEPS_THE_SHELF_AND_SAYS_SO()
    {
        var screen = AfterSearch("test", cause: null);

        Assert.NotNull(screen.Shelf);
        Assert.NotNull(screen.Door);

        var painted = Painted(screen);
        Assert.Contains(Gatto.Repl.Cats.EmptyOf(GlyphSet.Unicode), painted, StringComparison.Ordinal);
        Assert.Contains("nothing on Hugging Face answers \"test\"", painted, StringComparison.Ordinal);

        //the sentence must name two ways forward, or the screen states a failure and offers nothing
        Assert.Contains("or paste a full repo id", painted, StringComparison.Ordinal);
    }

    //the typed word stays in the draft, the golden pins one word so this asserts it for any word typed
    [Fact]
    public void THE_TYPED_WORD_STAYS_IN_THE_DOOR()
    {
        Assert.Equal("wharrgarbl", AfterSearch("wharrgarbl", cause: null).Draft);

        //the draft belongs only to the screen that reports an empty search, so it must not leak onto a populated shelf
        Assert.Null(AfterSearch("test", HubSearchCause.HubFailed).Draft);
    }

    //the heading stays the shelf's own question and the discovery body goes, two nothing messages on one screen read as a fault
    [Fact]
    public void THE_EMPTY_SEARCH_DROPS_THE_DISCOVERY_BODY_AND_KEEPS_THE_SHELFS_QUESTION()
    {
        var screen = AfterSearch("test", cause: null);

        Assert.Equal(SetupFlow.ModelTitleFor(inSession: false), screen.Question);
        Assert.Empty(screen.BodyRows!);
    }

    //a row that is also a key must not stay on the list, the framed shelf keeps its strip and footer. don't assert Elsewhere absent here, the face hides it
    [Fact]
    public void THE_ESCAPE_ROWS_LEAVE_THE_SHELF_THAT_HAS_A_FRAME()
    {
        var labels = AfterSearch("test", cause: null).Options.Select(o => o.Label).ToList();

        Assert.DoesNotContain(labels, l => l.Contains("Type a model's name", StringComparison.Ordinal));
        Assert.DoesNotContain(labels, l => l.Contains("every approved publisher", StringComparison.Ordinal));

        //they stay on the screen with no frame, so the rule was moved rather than deleted
        var offline = AfterSearch("test", HubSearchCause.HubFailed).Options.Select(o => o.Label);
        Assert.Contains(offline, l => l.Contains("Type a model's name", StringComparison.Ordinal));
    }

    //the message names the fit, since quoting the word back would be false (the word was answered)
    [Fact]
    public void A_SEARCH_WHOSE_MODELS_ARE_ALL_TOO_BIG_NAMES_THE_FIT_NOT_THE_WORD()
    {
        var painted = Painted(AfterSearch("test", HubSearchCause.NothingFits, tooBig: 9));

        //assert the front of the sentence, the tail comes from Shelf.HiddenClause and is pinned elsewhere
        Assert.Contains("nothing answering \"test\" fits this machine's memory",
            painted, StringComparison.Ordinal);
        Assert.Contains("9 too big", painted, StringComparison.Ordinal);
        Assert.Contains("a shows all", painted, StringComparison.Ordinal);
    }

    //pressing a must show rows. promising a key that leads nowhere is the defect this sentence avoids.
    [Fact]
    public void THE_SENTENCE_NAMES_a_AND_THE_SCREEN_ANSWERS_IT()
    {
        var flow = Flow(HubSearchCause.NothingFits, tooBig: 9);
        var empty = Assert.IsType<WizardScreen.Choice>(flow.Answer(ShelfControls.TypedAnswer("test")));
        Assert.Empty(empty.Shelf!.Rows);

        var lifted = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.CtlLift));

        Assert.NotEmpty(lifted.Shelf!.Rows);
    }

    //a shelf emptied by newer rows counts them and must not name a fit that never ran, the fixture sets only the newer count
    [Fact]
    public void ROWS_HELD_BACK_FOR_BEING_NEWER_DO_NOT_EARN_A_SENTENCE_ABOUT_MEMORY()
    {
        IReadOnlyList<ShelfRow> rows = [Ranked("o/qwen-small")];
        var probes = new WizardProbes
        {
            Rows = rows,
            Answer = req => req.Search is { Length: > 0 }
                ? new HubSearchOutcome([], null, null, HiddenByFit: 0, HiddenNewer: 3)
                : new HubSearchOutcome(rows, null, null),
        };
        var flow = new SetupFlow(probes);
        Assert.IsType<WizardScreen.Choice>(flow.StartPastEngine());

        var painted = Painted(Assert.IsType<WizardScreen.Choice>(
            flow.Answer(ShelfControls.TypedAnswer("test"))));

        Assert.DoesNotContain("fits this machine's memory", painted, StringComparison.Ordinal);
        Assert.Contains("3 newer", painted, StringComparison.Ordinal);
        Assert.Contains("a shows all", painted, StringComparison.Ordinal);
    }

    //an unreachable Hub keeps the discovery screen, since the search never ran, there is no shelf and no honest sentence about the word
    [Fact]
    public void AN_UNREACHABLE_HUB_STILL_DRAWS_THE_DISCOVERY_SCREEN()
    {
        var screen = AfterSearch("test", HubSearchCause.HubFailed);

        Assert.Null(screen.Shelf);
        Assert.Contains(screen.BodyRows!,
            r => r.Text.Contains("couldn't reach Hugging Face", StringComparison.Ordinal));
    }

    //an empty browse keeps the discovery screen too, nothing was typed so there is no word to quote back
    [Fact]
    public void AN_EMPTY_BROWSE_STILL_DRAWS_THE_DISCOVERY_SCREEN()
    {
        var probes = new WizardProbes
        {
            Rows = [],
            Answer = _ => new HubSearchOutcome([], HubSearchCause.NothingFits, null, HiddenByFit: 0),
        };
        var flow = new SetupFlow(probes);

        var screen = Assert.IsType<WizardScreen.Choice>(flow.StartPastEngine());

        Assert.Null(screen.Shelf);
        //assert the cause's own sentence, every arm of EmptyRows produces a non-empty body so a row count proves nothing
        Assert.Contains(screen.BodyRows!,
            r => r.Text.Contains("Every model gatto looked at", StringComparison.Ordinal));
    }

    //an unreachable Hub must not blame the typed word, since the search never ran
    [Fact]
    public void AN_UNREACHABLE_HUB_DOES_NOT_ALSO_BLAME_THE_TYPED_WORD()
    {
        var screen = AfterSearch("test", HubSearchCause.HubFailed);

        Assert.DoesNotContain("answers \"test\"", screen.Question!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Couldn't reach the model list", screen.Question);
    }

    //the plain face states the empty search too and drops the blank spacing rows, since an empty row reaches the user here
    [Fact]
    public void THE_PLAIN_FACE_DRAWS_THE_EMPTY_BLOCK_TOO()
    {
        var screen = AfterSearch("test", cause: null);
        var prompter = new Recorder();
        new PrompterWizardSurface(prompter, TextWriter.Null).Choose(screen);

        var body = string.Join(" | ", prompter.Asked.Single().BodyRows!.Select(r => r.Text));

        Assert.Contains("nothing on Hugging Face answers \"test\"", body, StringComparison.Ordinal);
        Assert.Contains("or paste a full repo id", body, StringComparison.Ordinal);
        Assert.DoesNotContain(body.Split(" | "), string.IsNullOrWhiteSpace);
    }
}
