using Gatto.Cli.Setup;
using Gatto.Cli.Setup.Tui;
using Gatto.Core.Acquire;
using Gatto.Core.Hardware;
using Gatto.Terminal;
using Gatto.Tests.Fakes;
using Gatto.Tests.Setup;
using Gatto.Tests.Setup.Tui;

namespace Gatto.Tests.Copy;

//the populated shelf from a real walk: the engine prices a rich fake Hub for each machine, the flow builds the screen and the face paints it
public sealed class ShelfCorpusTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-shelf-corpus-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); }
        catch (IOException) { }
    }

    public static TheoryData<string, CorpusMode> Machines() => new()
    {
        { "discrete", CorpusMode.Dark }, { "discrete", CorpusMode.Plain },
        { "unified", CorpusMode.Dark }, { "unified", CorpusMode.Plain },
        { "nocard", CorpusMode.Dark }, { "nocard", CorpusMode.Plain },
    };

    public static TheoryData<CorpusMode> Modes() => new() { CorpusMode.Dark, CorpusMode.Plain };

    //the setup walk's landing shelf from the shared fixtures, its home this class's own
    private (SetupFlow Flow, WizardScreen.Choice Shelf) Walk(string machine)
    {
        var (flow, screen) = ShelfFixtures.Flow(machine, home: _home);
        return (flow, Assert.IsType<WizardScreen.Choice>(screen));
    }

    //the settled frame at 120 by 30, inked in the dark mode so a wrong colour moves a golden. the plain mode pins text, since the face has no uncoloured theme
    private static string Frame(WizardScreen.Choice c, CorpusMode mode, IEnumerable<ConsoleKeyInfo>? keys = null,
        int height = 30, string command = ScreenPainter.DefaultCommand) =>
        mode == CorpusMode.Dark
            ? CorpusDriver.Visible(string.Join("\n",
                WalkRender.Inked(c, 120, height, new Theme(new TermCaps(true, true), ThemeMode.Dark), keys, command))) + "\n"
            : string.Join("\n", WalkRender.SettledFrame(c, 120, script: keys, height: height, command: command).Rows.Select(r => r.TrimEnd())) + "\n";

    private const string ModelCommand = "gatto model";

    //gatto model's landing, entered at the model segment as the in-session command enters it
    private (SetupFlow Flow, WizardScreen.Choice Shelf) ModelWalk(string machine, IReadOnlyList<Gatto.Core.Acquire.FoundModel>? found = null)
    {
        var (flow, screen) = ShelfFixtures.Flow(machine, ShelfFixtures.Entry.Model, _home, found);
        return (flow, Assert.IsType<WizardScreen.Choice>(screen));
    }

    [Theory]
    [MemberData(nameof(Machines))]
    public void THE_MODEL_LANDING_SHELF(string machine, CorpusMode mode)
    {
        var (_, shelf) = ModelWalk(machine);
        Assert.Equal(SetupFlow.SearchKey, shelf.Key);
        CopyGolden.Check($"model-shelf-landing-{machine}-{mode}", Frame(shelf, mode, command: ModelCommand));
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void THE_MODEL_LIFTED_SHELF(CorpusMode mode)
    {
        var (flow, _) = ModelWalk("discrete");
        var lifted = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.CtlLift));
        CopyGolden.Check($"model-shelf-lifted-discrete-{mode}", Frame(lifted, mode, command: ModelCommand));
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void THE_MODEL_OPEN_ACCORDION(CorpusMode mode)
    {
        var (_, shelf) = ModelWalk("discrete");
        CopyGolden.Check($"model-shelf-open-discrete-{mode}", Frame(shelf, mode, ShelfFixtures.OpenKeys(shelf), command: ModelCommand));
    }

    //one model in two folders, its row's folder opened by the keys
    private (SetupFlow Flow, WizardScreen.Choice Shelf) GroupedLocal()
    {
        var (flow, _) = ModelWalk("unified", ShelfFixtures.GroupedScan());
        var local = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.CtlSource));
        Assert.Equal(SetupFlow.DiscoveredKey, local.Key);
        return (flow, local);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void THE_MODEL_LOCAL_GROUPED_SHELF(CorpusMode mode)
    {
        var (_, local) = GroupedLocal();
        CopyGolden.Check($"model-shelf-local-grouped-{mode}", Frame(local, mode, ShelfFixtures.OpenKeys(local), command: ModelCommand));
    }

    [Theory]
    [InlineData(20)]
    [InlineData(30)]
    public void THE_LOCAL_GROUPED_FRAME_AT_HEIGHT(int height)
    {
        var (_, local) = GroupedLocal();
        CopyGolden.Check($"shelf-local-grouped-120x{height}-Plain",
            Frame(local, CorpusMode.Plain, ShelfFixtures.OpenKeys(local), height, ModelCommand));
    }

    [Theory]
    [MemberData(nameof(Machines))]
    public void THE_LANDING_SHELF(string machine, CorpusMode mode)
    {
        var (_, shelf) = Walk(machine);
        CopyGolden.Check($"shelf-landing-{machine}-{mode}", Frame(shelf, mode));
    }

    //the a view on the discrete card, every regime counted on the count row
    [Theory]
    [MemberData(nameof(Modes))]
    public void THE_LIFTED_SHELF(CorpusMode mode)
    {
        var (flow, _) = Walk("discrete");
        var lifted = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.CtlLift));
        CopyGolden.Check($"shelf-lifted-discrete-{mode}", Frame(lifted, mode));
    }

    //the row's publisher open on its pick, the keys in the pane
    [Theory]
    [MemberData(nameof(Modes))]
    public void THE_OPEN_ACCORDION(CorpusMode mode)
    {
        var (_, shelf) = Walk("discrete");
        CopyGolden.Check($"shelf-open-discrete-{mode}", Frame(shelf, mode, ShelfFixtures.OpenKeys(shelf)));
    }
}
