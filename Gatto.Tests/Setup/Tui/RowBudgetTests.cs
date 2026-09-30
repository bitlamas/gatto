using Gatto.Cli.Setup;
using Gatto.Cli.Setup.Tui;
using Gatto.Core.Acquire;
using Gatto.Core.Hardware;
using Gatto.Core.Models;
using Gatto.Repl;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup.Tui;

//the row budget must reach both the engine and the painter, since a check at one end leaves the other free to drift
public class RowBudgetTests
{
    //write the chrome count here as a literal, since a test that reads Shelf.FrameRows on both sides can't fail about its value
    private const int FrameRowsLiteral = 15;

    [Fact]
    public void A_TALLER_TERMINAL_AFFORDS_MORE_ROWS()
    {
        Assert.Equal(FrameRowsLiteral, Shelf.FrameRows);

        //check several heights, since a height that happens to give the default would hide a budget that ignores height
        Assert.Equal(13, Shelf.RowBudget(28));
        Assert.Equal(35, Shelf.RowBudget(50));
        Assert.Equal(1, Shelf.RowBudget(FrameRowsLiteral));

        //a window with no room still gets one row, since a shelf that shows nothing is not a shelf
        Assert.Equal(1, Shelf.RowBudget(3));

        //a height of 0 means unknown in the ITermSurface contract, so a terminal that won't say its height gets the plain default
        Assert.Equal(HubSearch.DefaultRowBudget, Shelf.RowBudget(0));
    }

    //only the TUI face overrides the default budget, since a face that forgot to would show six rows on a tall screen
    [Fact]
    public void THE_TUI_FACE_TAKES_ITS_BUDGET_FROM_THE_TERMINAL_AND_THE_PLAIN_FACES_DO_NOT()
    {
        var tall = Face(height: 60);
        var short_ = Face(height: 24);

        Assert.True(tall.RowBudget > short_.RowBudget,
            $"a 60-row terminal affords {tall.RowBudget} and a 24-row one {short_.RowBudget}");
        Assert.Equal(Shelf.RowBudget(60), tall.RowBudget);

        //the in-session face numbers its rows, so the interface default is the right answer here
        IWizardSurface prompter = new PrompterWizardSurface(new SilentPrompter(), TextWriter.Null);
        Assert.Equal(HubSearch.DefaultRowBudget, prompter.RowBudget);
    }

    //test the face capabilities together, since a capability that is true everywhere gates nothing and one false everywhere deletes the feature
    [Fact]
    public void ONLY_THE_FACE_WITH_A_KEY_LOOP_CLAIMS_THE_SOURCE_SWITCH()
    {
        IWizardSurface prompter = new PrompterWizardSurface(new SilentPrompter(), TextWriter.Null);

        Assert.True(Face(height: 40).CanSwitchSource);
        Assert.False(prompter.CanSwitchSource);
    }

    //the runner hands the face's capability to the flow, since the flow has no face of its own
    [Fact]
    public void THE_RUNNER_HANDS_THE_FACES_SOURCE_SWITCH_TO_THE_FLOW()
    {
        var flow = new SetupFlow(new WizardProbes { Llama = null });
        Assert.False(flow.CanSwitchSource);

        SetupRunner.Run(flow, Face(height: 60));

        Assert.True(flow.CanSwitchSource);
    }

    //the runner wires the budget, since the flow and the face never meet
    [Fact]
    public void THE_RUNNER_HANDS_THE_FACES_BUDGET_TO_THE_FLOW()
    {
        var probes = new WizardProbes { Llama = null };
        var flow = new SetupFlow(probes);
        Assert.Equal(HubSearch.DefaultRowBudget, flow.RowBudget);

        SetupRunner.Run(flow, Face(height: 60));

        Assert.Equal(Shelf.RowBudget(60), flow.RowBudget);
        Assert.NotEqual(HubSearch.DefaultRowBudget, flow.RowBudget);
    }

    //the budget must reach the search request, since a number stopped at the painter shows the same screen and spends twice the requests
    [Fact]
    public void THE_BUDGET_RIDES_THE_SEARCH_REQUEST()
    {
        List<HubSearchRequest> asked = [];
        ShelfOf(new SetupFlow(Spy(asked, [Model("m0")])) { RowBudget = 11 });

        Assert.NotEmpty(asked);
        Assert.All(asked, r => Assert.Equal(11, r.RowBudget));
    }

    //the shelf must count the rows the budget left off, since a silently stopping list can't be told from a complete one
    [Fact]
    public void THE_SHELF_SHOWS_WHAT_FITS_AND_SAYS_WHAT_IT_LEFT_OFF()
    {
        var rows = Enumerable.Range(0, 12).Select(i => Model($"m{i}")).ToList();
        var shelf = ShelfOf(new SetupFlow(Spy([], rows)) { RowBudget = 4 });

        Assert.Equal(4, shelf.Rows.Count);
        Assert.Equal(8, shelf.MoreBelow);
        Assert.Contains("1–4 of 12", Gatto.Cli.Setup.Tui.Shelf.CountLine(shelf, glyphs: GlyphSet.Unicode), StringComparison.Ordinal);

        //this twin uses a bigger budget over the same rows, since a shelf hard-coded to four would pass the half above
        var wide = ShelfOf(new SetupFlow(Spy([], rows)) { RowBudget = 20 });
        Assert.Equal(12, wide.Rows.Count);
        Assert.Equal(0, wide.MoreBelow);
    }

    //the row count reads the surface's height and nothing else, since no caller supplies a panel ceiling


    //a face that owns the screen still has no panel to ask, so it reads the terminal (asking always would return the plain default)
    [Fact]
    public void A_FACE_THAT_OWNS_THE_SCREEN_STILL_SIZES_FOR_THE_TERMINAL()
    {
        Assert.Equal(Shelf.RowBudget(40), Face(height: 40).RowBudget);
    }

    private static TuiWizardSurface Face(int height) =>
        new(new RecordingSurface { Width = 100, Height = height },
            new NoKeys(), new Theme(new TermCaps(true, true)),
            version: "0.5.0", build: "1a2b3c4", nowMs: () => 0);

    private sealed class NoKeys : Gatto.Terminal.IKeySource
    {
        public bool KeyAvailable => false;
        public ConsoleKeyInfo ReadKey() => new('\0', ConsoleKey.Escape, false, false, false);
    }

    //answering nothing is the in-session Esc, since the subject is a property read before any flow starts
    private sealed class SilentPrompter : IWizardPrompter
    {
        public WizardAnswer? AskOne(WizardAsk ask) => null;
    }

    private static ShelfRow Model(string name) =>
        new($"unsloth/{name}", "unsloth", new HubQuant($"{name}.gguf", 1_000_000_000, null),
            FitRegime.FitsGpu, 4096, false, null, 0, false);

    //reach the shelf through StartPastEngine, since a private route here would drift from the screens the product really shows first
    private static ShelfView ShelfOf(SetupFlow flow) =>
        Assert.IsType<WizardScreen.Choice>(flow.StartPastEngine()).Shelf
        ?? throw new Xunit.Sdk.XunitException("the walk reached a choice with no shelf on it");

    //record every request, so the budget's route is observable rather than inferred from a screen that looks the same either way
    private static WizardProbes Spy(List<HubSearchRequest> asked, IReadOnlyList<ShelfRow> rows) =>
        //keep the default llama path rather than null, since StartPastEngine expects an engine and a machine with none stops on the steering screen
        new() { Answer = r => { asked.Add(r); return new HubSearchOutcome(rows, null); } };
}
