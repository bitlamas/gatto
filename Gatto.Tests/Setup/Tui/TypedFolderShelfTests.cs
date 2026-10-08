using Gatto.Cli.Setup;
using Gatto.Cli.Setup.Tui;
using Gatto.Core.Acquire;
using Gatto.Core.Models;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup.Tui;

//a typed folder's shelf holds that folder's models only. an empty folder showed the ambient models because the rows came from a merged sweep
public class TypedFolderShelfTests
{
    private const string Typed = @"C:\models";

    private static FoundModel At(string path) => new(path, 4_000_000_000, null);

    private static readonly FoundModel Weights = At(@"C:\home\weights\one\one-Q4_K_M.gguf");
    private static readonly FoundModel Downloads = At(@"C:\dl\two-Q4_K_M.gguf");
    //a sibling folder whose name starts with the typed one, which a string prefix test takes
    private static readonly FoundModel Sibling = At(@"C:\models-old\old-Q4_K_M.gguf");
    private static readonly FoundModel Inside = At(@"C:\models\sub\three-Q4_K_M.gguf");

    private static ModelRow HubRow() => ShelfRows.Of(
        "unsloth/gemma-4-26B-A4B-it", "unsloth",
        new HubQuant("gemma-4-26B-A4B-it-Q4_K_M.gguf", 4_000_000_000, null),
        FitRegime.FitsGpu, 262144, false, Badge: null, Downloads: 5, Gated: false,
        Params: 25_200_000_000);

    private static WizardProbes Probes(bool hub, params FoundModel[] found) => new()
    {
        Llama = @"C:\llama\llama-server.exe",
        Rows = hub ? [HubRow()] : [],
        Roots = [@"C:\home\weights", @"C:\dl", Typed],
        Found = [.. found],
    };

    private static IReadOnlyList<string> FileRows(WizardScreen.Choice c) =>
        [.. c.Options.Select(o => o.Label).Where(l => l.EndsWith(".gguf", StringComparison.Ordinal))];

    private static WizardScreen.Choice Typing(SetupFlow flow) =>
        Assert.IsType<WizardScreen.Choice>(flow.Answer(ShelfControls.TypedAnswer(Typed)));

    [Fact]
    public void A_TYPED_FOLDER_SHELF_LISTS_ONLY_THAT_FOLDER()
    {
        var flow = new SetupFlow(Probes(hub: false, Weights, Sibling, Inside, Downloads));
        var start = Assert.IsType<WizardScreen.Choice>(flow.StartAtModelSegment());
        Assert.Equal(4, FileRows(start).Count);

        var shelf = Typing(flow);

        Assert.Equal(ShelfSource.Local, shelf.Shelf!.Source);
        Assert.Equal(Typed, shelf.Shelf.Folder);
        Assert.Equal(["three-Q4_K_M.gguf"], FileRows(shelf));
        Assert.Single(shelf.Shelf.Rows);
        Assert.Contains("Found a model", shelf.Question, StringComparison.Ordinal);
    }

    //a typed .gguf that is not there shows its folder's shelf. the sweep uses the folder, passing the file path would make the filter read it as the folder
    [Fact]
    public void A_MISTYPED_GGUF_SHOWS_ITS_FOLDER_S_SHELF()
    {
        var flow = new SetupFlow(Probes(hub: false, Weights, Inside));
        flow.StartAtModelSegment();

        var shelf = Assert.IsType<WizardScreen.Choice>(
            flow.Answer(ShelfControls.TypedAnswer(@"C:\models\sub\thre-Q4_K_M.gguf")));

        Assert.Equal(@"C:\models\sub", shelf.Shelf!.Folder);
        Assert.Equal(["three-Q4_K_M.gguf"], FileRows(shelf));
    }

    //the empty local shelf names only the folder gatto looked in. no chips row is drawn, so the block is where the folder is named
    [Fact]
    public void AN_EMPTY_TYPED_FOLDER_IS_AN_EMPTY_SHELF_UNDER_ITS_OWN_LABEL()
    {
        var flow = new SetupFlow(Probes(hub: false, Weights, Downloads));
        Assert.Equal(2, FileRows(Assert.IsType<WizardScreen.Choice>(flow.StartAtModelSegment())).Count);

        var shelf = Typing(flow);
        var block = string.Join("\n", shelf.Shelf!.Empty ?? []);

        Assert.Equal(SetupFlow.DiscoveredKey, shelf.Key);
        Assert.Empty(shelf.Shelf.Rows);
        Assert.Contains("no models in that folder, gatto looked in:", block, StringComparison.Ordinal);
        Assert.Contains(Typed, block, StringComparison.Ordinal);
        Assert.DoesNotContain(@"C:\home\weights", block, StringComparison.Ordinal);
        Assert.DoesNotContain("on this machine", block, StringComparison.Ordinal);
        Assert.Equal("type a folder path…", shelf.Door);
    }

    //leaving the local shelf drops the typed folder, so m to the Hub and back lists every model with no folder slot
    [Fact]
    public void M_TO_THE_HUB_AND_BACK_SHOWS_THE_MACHINE_AGAIN()
    {
        var flow = new SetupFlow(Probes(hub: true, Weights, Inside)) { CanSwitchSource = true };
        Assert.Equal(SetupFlow.SearchKey, Assert.IsType<WizardScreen.Choice>(flow.StartPastEngine()).Key);
        Assert.Single(FileRows(Typing(flow)));
        var hub = Assert.IsType<WizardScreen.Choice>(flow.Answer(ShelfControls.SourceAnswer()));
        Assert.Equal(SetupFlow.SearchKey, hub.Key);

        var local = Assert.IsType<WizardScreen.Choice>(flow.Answer(ShelfControls.SourceAnswer()));

        Assert.Equal(ShelfSource.Local, local.Shelf!.Source);
        Assert.Null(local.Shelf.Folder);
        Assert.Equal(["one-Q4_K_M.gguf", "three-Q4_K_M.gguf"], FileRows(local));
    }

    //back from a typed folder's shelf lands on the machine's shelf. the redraw has no folder slot, the typed folder waits in the back-step snapshot
    [Fact]
    public void BACK_FROM_A_TYPED_FOLDER_DROPS_ITS_LABEL()
    {
        var flow = new SetupFlow(Probes(hub: false, Weights, Inside));
        Assert.Equal(2, FileRows(Assert.IsType<WizardScreen.Choice>(flow.StartAtModelSegment())).Count);
        Assert.Single(FileRows(Typing(flow)));
        Assert.Equal(2, FileRows(Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.BackKey))).Count);

        var redrawn = Assert.IsType<WizardScreen.Choice>(flow.Answer(ShelfControls.FamilyAnswer("all")));

        Assert.Null(redrawn.Shelf!.Folder);
        Assert.Equal(["one-Q4_K_M.gguf", "three-Q4_K_M.gguf"], FileRows(redrawn));
    }

    //back onto a typed folder's shelf keeps its folder slot on the redraw, the snapshot restores the folder with its rows
    [Fact]
    public void BACK_ONTO_A_TYPED_FOLDER_S_SHELF_KEEPS_ITS_LABEL()
    {
        var flow = new SetupFlow(Probes(hub: true, Weights, Inside));
        Assert.Equal(ShelfSource.Local, Assert.IsType<WizardScreen.Choice>(flow.StartAtModelSegment()).Shelf!.Source);
        Assert.Single(FileRows(Typing(flow)));
        Assert.Equal(SetupFlow.SearchKey, Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.SearchInstead)).Key);
        Assert.Equal(SetupFlow.DiscoveredKey, Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.BackKey)).Key);

        var redrawn = Assert.IsType<WizardScreen.Choice>(flow.Answer(ShelfControls.FamilyAnswer("all")));

        Assert.Equal(Typed, redrawn.Shelf!.Folder);
        Assert.Equal(["three-Q4_K_M.gguf"], FileRows(redrawn));
    }

    //a typed folder does not label the isn't-there-yet shelf after a Hub pick. that shelf lists the models gatto can see, so it has no folder slot
    [Fact]
    public void THE_WATCH_S_LANDED_PATH_HAS_NO_STALE_FOLDER()
    {
        var landed = At(@"D:\dl\other-Q4_K_M.gguf");
        var flow = new SetupFlow(Probes(hub: true, Weights, Inside, landed));
        Assert.Equal(ShelfSource.Local, Assert.IsType<WizardScreen.Choice>(flow.StartAtModelSegment()).Shelf!.Source);
        Assert.Single(FileRows(Typing(flow)));
        Assert.Equal(SetupFlow.SearchKey, Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.SearchInstead)).Key);
        flow.Answer("0");
        flow.Answer(SetupFlow.Elsewhere);

        var shelf = Assert.IsType<WizardScreen.Choice>(flow.Answer(ShelfControls.TypedAnswer(@"D:\dl")));

        Assert.Equal(SetupFlow.DiscoveredKey, shelf.Key);
        Assert.Null(shelf.Shelf!.Folder);
        Assert.Equal(["one-Q4_K_M.gguf", "three-Q4_K_M.gguf", "other-Q4_K_M.gguf"], FileRows(shelf));
    }

    //a bare .gguf name has no folder to sweep, it latches nothing, clears a folder typed before it and lists the machine's models
    [Fact]
    public void A_BARE_GGUF_NAME_LATCHES_NO_FOLDER()
    {
        var flow = new SetupFlow(Probes(hub: false, Weights, Inside));
        flow.StartAtModelSegment();
        Assert.Single(FileRows(Typing(flow)));

        var shelf = Assert.IsType<WizardScreen.Choice>(flow.Answer(ShelfControls.TypedAnswer("thre-Q4_K_M.gguf")));

        Assert.Null(shelf.Shelf!.Folder);
        Assert.Equal(["one-Q4_K_M.gguf", "three-Q4_K_M.gguf"], FileRows(shelf));
    }

    //the empty typed shelf shows the sentence, the typed folder on its own row and no ambient root, checked at a sweep of widths
    [Fact]
    public void THE_EMPTY_TYPED_SHELF_NAMES_ONLY_THAT_FOLDER_AT_EVERY_WIDTH()
    {
        var flow = new SetupFlow(Probes(hub: false, Weights, Downloads));
        flow.StartAtModelSegment();
        var shelf = Typing(flow);

        foreach (var width in new[] { 80, 90, 100, 120, 140 })
        {
            var rows = WalkRender.SettledFrame(shelf, width).Rows;
            var frame = string.Join("\n", rows);

            Assert.True(rows.Any(r => r.Contains("no models in that folder, gatto looked in:", StringComparison.Ordinal)),
                $"at {width} the empty sentence is missing:\n{frame}");
            Assert.True(rows.Any(r => r.Trim() == Typed), $"at {width} no row is the typed folder:\n{frame}");
            Assert.DoesNotContain(rows, r => r.Contains(@"C:\home\weights", StringComparison.Ordinal)
                                             || r.Contains(@"C:\dl", StringComparison.Ordinal));
            Assert.All(rows, r => Assert.True(UnicodeWidth.Of(r) <= width, $"at {width} a row is wider: [{r}]"));
        }
    }

    private static readonly string[] Machine = ["one-Q4_K_M.gguf", "two-Q4_K_M.gguf"];

    //esc on the empty typed-folder shelf goes back to the machine's shelf. the ring holds only the field, so it has no list to return the keys to
    [Fact]
    public void ESC_ON_THE_EMPTY_TYPED_SHELF_GOES_BACK_TO_THE_MACHINE_S_SHELF()
    {
        var flow = new SetupFlow(Probes(hub: false, Weights, Downloads));
        flow.StartAtModelSegment();
        var empty = Typing(flow);
        Assert.True(empty.AllowBack);

        var (answer, _) = WalkRender.Answered(empty, 100,
            [new ConsoleKeyInfo('\0', ConsoleKey.Escape, false, false, false)]);

        Assert.Equal(SetupFlow.BackKey, answer);
        var home = Assert.IsType<WizardScreen.Choice>(flow.Answer(answer!));
        Assert.Null(home.Shelf!.Folder);
        Assert.Equal(Machine, FileRows(home));
    }

    //back from an empty folder reached through the folder question lands on the machine's shelf
    [Fact]
    public void BACK_FROM_AN_EMPTY_FOLDER_REACHED_THROUGH_THE_QUESTION_LANDS_ON_THE_MACHINE_S_SHELF()
    {
        var flow = new SetupFlow(Probes(hub: false, Weights, Downloads));
        flow.StartAtModelSegment();
        Assert.IsType<WizardScreen.Ask>(flow.Answer(SetupFlow.Elsewhere));
        var empty = Assert.IsType<WizardScreen.Choice>(flow.Answer(Typed));
        Assert.Empty(empty.Shelf!.Rows);

        var home = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.BackKey));

        Assert.Equal(SetupFlow.DiscoveredKey, home.Key);
        Assert.Null(home.Shelf!.Folder);
        Assert.Equal(Machine, FileRows(home));
    }

    //back from a second empty folder, typed into the first empty shelf's own field, lands on the machine's shelf
    [Fact]
    public void BACK_FROM_A_SECOND_EMPTY_FOLDER_LANDS_ON_THE_MACHINE_S_SHELF()
    {
        var flow = new SetupFlow(Probes(hub: false, Weights, Downloads));
        flow.StartAtModelSegment();
        Typing(flow);
        var second = Assert.IsType<WizardScreen.Choice>(flow.Answer(ShelfControls.TypedAnswer(@"C:\elsewhere")));
        Assert.Contains(@"C:\elsewhere", string.Join("\n", second.Shelf!.Empty ?? []), StringComparison.Ordinal);

        var home = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.BackKey));

        Assert.Null(home.Shelf!.Folder);
        Assert.Equal(Machine, FileRows(home));
    }

    //the keys are in the field on the empty typed shelf, so the field's row takes the cursor. the footer says neither Esc leave nor b back
    [Fact]
    public void THE_EMPTY_TYPED_SHELF_DRAWS_ONE_CURSOR_AND_NO_LEAVE()
    {
        var flow = new SetupFlow(Probes(hub: false, Weights, Downloads));
        flow.StartAtModelSegment();
        var rows = WalkRender.SettledFrame(Typing(flow), 100).Rows;
        var frame = string.Join("\n", rows);

        Assert.True(rows.Any(r => r.StartsWith("\u276f", StringComparison.Ordinal)
                                  && r.Contains("type a folder path", StringComparison.Ordinal)),
            $"the field's row has no cursor:\n{frame}");
        Assert.False(rows.Any(r => r.StartsWith("\u276f Look in another folder", StringComparison.Ordinal)
                                   || r.StartsWith("\u276f Find one to download", StringComparison.Ordinal)),
            $"an option row carries a cursor while the keys are in the field:\n{frame}");
        Assert.DoesNotContain(rows, r => r.Contains("Esc leave", StringComparison.Ordinal));
        Assert.DoesNotContain(rows, r => r.Contains("b back", StringComparison.Ordinal));
    }
}
