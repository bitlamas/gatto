using Gatto.Cli.Setup;
using Gatto.Cli.Setup.Tui;
using Gatto.Core.Acquire;
using Gatto.Core.Hardware;
using Gatto.Core.Models;
using Gatto.Terminal;

namespace Gatto.Tests.Setup.Tui;

//the machine source, the shelf drawn over the models already on this disk (the fixture copies the mock's table, apart from the params column)
public class LocalShelfTests
{
    private static long Gib(double gb) => (long)Math.Round(gb * 1024 * 1024 * 1024);

    private const string Dir = @"C:\Users\you\.lmstudio\models\";

    private static readonly IReadOnlyList<string> Ladder =
        ["gemma", "qwen", "deepseek", "glm", "mistral", "all"];

    private static readonly IReadOnlyList<StripSection> Strip =
    [
        new("machine", StripState.Done), new("engine", StripState.Done),
        new("model", StripState.Current), new("check", StripState.Pending),
        new("done", StripState.Pending),
    ];

    private static readonly (string Name, string Family, double Gb, string Kind, bool Vision,
        long Ctx, string Files, string Tail)[] Local =
    [
        ("minimax-m2.7-REAP-139B-A10B-Q4_K_S", "all", 82.3, "MoE A10B", false, 196608,
            "3 of 3 files here", @"minimaxai\minimax-m2.7-REAP"),
        ("Llama-3.3-70B-Instruct-IQ2_XS", "all", 21.1, "dense", false, 131072,
            "1 file", @"lmstudio-community\Llama-3.3-70B"),
        ("Qwen3-30B-A3B-Instruct-2507-UD-Q4_K_M", "qwen", 18.6, "MoE A3B", false, 262144,
            "1 file", @"unsloth\Qwen3-30B-A3B-2507"),
        ("gemma-4-26B-A4B-it-UD-Q4_K_M", "gemma", 16.9, "MoE A4B", true, 262144,
            "1 file + mmproj", @"unsloth\gemma-4-26B-A4B-it"),
        ("gemma-3-12b-it-Q4_K_M", "gemma", 7.3, "dense", true, 131072,
            "1 file + mmproj", @"google\gemma-3-12b-it"),
    ];

    private static ModelRow Row(int i) =>
        ShelfRows.Of(Local[i].Name, "", new HubQuant(Local[i].Name + ".gguf", Gib(Local[i].Gb), null),
            FitRegime.FitsGpu, Local[i].Ctx, Local[i].Vision, Badge: null, Downloads: 0,
            Gated: false, Params: null,
            Structure: Local[i].Kind.StartsWith("MoE", StringComparison.Ordinal) ? Local[i].Kind : "dense");

    private static ModelFacts Facts(int i) =>
        new(Structure: Row(i).Structure,
            LocalPath: Dir + Local[i].Tail + @"\",
            FilesHere: Local[i].Files);

    private static ShelfView View(string family = "all")
    {
        var keep = Enumerable.Range(0, Local.Length)
            .Where(i => family == "all" || Local[i].Family == family).ToArray();
        return new ShelfView(
            [.. keep.Select(Row)], MachineShape.UnifiedWithShare,
            Families: Ladder, Family: family, Total: Local.Length,
            Facts: [.. keep.Select(Facts)],
            Source: ShelfSource.Local, Folder: Dir,
            HiddenByFamily: Local.Length - keep.Length);
    }

    private static WizardScreen.Choice Screen(ShelfView v) =>
        new(SetupFlow.SearchKey, "Which model should gatto start with?",
            [.. v.Rows.Select((r, i) => new ChoiceOption(i.ToString(), r.Model))],
            Shelf: v, Door: SetupFlow.LocalShelfDoorPlaceholderOf(Gatto.Terminal.GlyphSet.Unicode))
        { Strip = Strip };

    private static ConsoleKeyInfo Key(ConsoleKey k) => new('\0', k, false, false, false);

    private static IReadOnlyList<string> Render(string family = "all") =>
        WalkRender.Choice(Screen(View(family)), 100, script: [Key(ConsoleKey.Enter)]).Rows;

    //80 is a width where the local shelf really folds, the pane clears its floor down to 94
    private static IReadOnlyList<string> Folded() =>
        WalkRender.Choice(Screen(View("all")), 80, script: [Key(ConsoleKey.Enter)]).Rows;

    private static string RowContaining(IReadOnlyList<string> rows, string needle) =>
        Assert.Single(rows, r => r.Contains(needle, StringComparison.Ordinal));

    //the detail block

    //a local row has no publisher or origin, so the detail block must not use the hub's by/from line
    [Fact]
    public void THE_LOCAL_DETAIL_BLOCK_SAYS_WHERE_NOT_WHO()
    {
        var rows = Folded();

        Assert.DoesNotContain(rows, r => r.Contains("·  by", StringComparison.Ordinal));
        Assert.Contains(rows, r => r.Trim() == Local[0].Name);
        Assert.Contains(rows, r => r.Trim() == Dir + Local[0].Tail + @"\");
    }

    //the path takes its own row below the name, it is what tells two identically-named files apart
    [Fact]
    public void THE_PATH_TAKES_ITS_OWN_ROW_DIRECTLY_BELOW_THE_NAME()
    {
        var rows = Folded();
        var name = rows.ToList().FindIndex(r => r.Trim() == Local[0].Name);

        Assert.True(name >= 0, "the name row is not on screen");
        Assert.Equal(Dir + Local[0].Tail + @"\", rows[name + 1].Trim());
    }

    //the files-here words come from the producer, a renderer that recounted would be a second opinion on whether the model is all there
    [Fact]
    public void THE_FILES_LINE_SAYS_HOW_MUCH_OF_THE_MODEL_IS_HERE()
    {
        var line = RowContaining(Folded(), "files · ");

        Assert.Contains("Q4_K_S 82.3 GB", line, StringComparison.Ordinal);
        Assert.Contains("· 3 of 3 files here", line, StringComparison.Ordinal);
        Assert.Contains("✓ fits", line, StringComparison.Ordinal);
    }

    //the local block has no builds line, builds describe a repo's tree and a disk folder has none
    [Fact]
    public void THE_LOCAL_BLOCK_HAS_NO_BUILDS_LINE() =>
        Assert.DoesNotContain(Render(), r => r.Contains("builds · ", StringComparison.Ordinal));

    //the source-labelled chrome

    //the m key names its destination, so the test checks both directions (one direction passes on a renderer that always says the same thing)
    [Fact]
    public void THE_M_KEY_NAMES_WHERE_IT_TAKES_YOU()
    {
        var ring = new FocusRing([Region.List], Region.List);

        Assert.Equal("hub",
            Assert.Single(Shelf.Keys(ring, ShelfSource.Local), k => k.Key == "m").Verb);
        Assert.Equal("local",
            Assert.Single(Shelf.Keys(ring, ShelfSource.Hub), k => k.Key == "m").Verb);
    }

    //the local shelf's chips row ends in the folder, and the hub's ends at its chips, so each label is checked on both shelves
    [Fact]
    public void THE_FOLDER_SLOT_IS_THE_LOCAL_SHELFS_ALONE()
    {
        var local = Shelf.Chips(View(), chip: 0, focus: Region.List, width: 100, glyphs: GlyphSet.Unicode).Text;
        var hub = Shelf.Chips(View() with { Source = ShelfSource.Hub },
            chip: 0, focus: Region.List, width: 100, glyphs: GlyphSet.Unicode).Text;

        Assert.Contains("folder  ", local, StringComparison.Ordinal);
        Assert.DoesNotContain("publisher", local, StringComparison.Ordinal);
        Assert.DoesNotContain("publisher", hub, StringComparison.Ordinal);
        Assert.DoesNotContain("folder", hub, StringComparison.Ordinal);
    }

    //the slot's caret

    //every local row leaves Params null, so the params column stays out of the local table and the hub shelf keeps its own
    [Fact]
    public void THE_PARAMS_COLUMN_IS_ON_BOTH_SHELVES()
    {
        var local = Shelf.Table(View(), row: 0, focused: true, glyphs: GlyphSet.Unicode)[0].Text;
        var hub = Shelf.Table(View() with { Source = ShelfSource.Hub }, row: 0, focused: true,
            glyphs: GlyphSet.Unicode)[0].Text;

        Assert.Contains("params", local, StringComparison.Ordinal);
        Assert.Contains("params", hub, StringComparison.Ordinal);
    }

    //the folder slot is drawn by the same code, so it drops the caret too. no key opens the folder on either face
    [Fact]
    public void NOR_DOES_THE_LOCAL_SHELFS_FOLDER_SLOT() =>
        Assert.DoesNotContain("▾", Shelf.Chips(View(), chip: 0, focus: Region.List, width: 100, glyphs: GlyphSet.Unicode).Text,
            StringComparison.Ordinal);

    //the slot sizes its gap from the string it draws, so dropping the caret must shorten that string too
    [Fact]
    public void THE_SLOT_STILL_REACHES_THE_RIGHT_EDGE()
    {
        var text = Shelf.Chips(View(), chip: 0, focus: Region.List, width: 100, glyphs: GlyphSet.Unicode).Text;

        //the slot ends two cells short of the right edge, so 98 is pinned as a literal rather than read from Margins
        Assert.Equal(98, Gatto.Terminal.UnicodeWidth.Of(text));
    }

    //a long path shows its tail cut at a separator, a cut inside a segment reads as a folder name
    [Theory]
    [InlineData(@"C:\Users\you\.lmstudio\models\", 34, @"C:\Users\you\.lmstudio\models\")]
    [InlineData(@"D:\ai\test\exploration\.lmstudio\models\", 34, @"…\exploration\.lmstudio\models\")]
    [InlineData(@"C:\Users\you\.lmstudio\models\minimaxai\minimax-m2.7-REAP\", 32,
        @"…\minimaxai\minimax-m2.7-REAP\")]
    public void A_LONG_PATH_SHOWS_ITS_TAIL_CUT_AT_A_SEPARATOR(string path, int width, string want) =>
        Assert.Equal(want, Pane.PathTail(path, width, Gatto.Terminal.GlyphSet.Unicode));

    //a single segment longer than the column has no separator to cut at, so characters go
    [Fact]
    public void A_SINGLE_ENORMOUS_SEGMENT_FALLS_BACK_TO_A_CHARACTER_CUT()
    {
        var got = Pane.PathTail(new string('x', 60), 10, Gatto.Terminal.GlyphSet.Unicode);

        Assert.Equal(10, got.Length);
        Assert.StartsWith("…", got, StringComparison.Ordinal);
    }

    //the local door's hint names its key and no click, a press on the door needs no words
    [Fact]
    public void THE_LOCAL_DOOR_ENDS_WITH_QUESTION_MARK_TO_SEARCH_AND_OFFERS_NO_CLICK()
    {
        var door = RowContaining(Render(), "type a .gguf to add");

        Assert.EndsWith("? to search", door.TrimEnd(), StringComparison.Ordinal);
        Assert.DoesNotContain("click", door, StringComparison.Ordinal);
    }

    //the count line

    //the family clause names the all chip, the a key only lifts the fit filter
    [Fact]
    public void THE_FAMILY_CLAUSE_NAMES_THE_CHIP_THAT_LIFTS_IT()
    {
        Assert.Equal("1–2 of 5 · 3 in other families, all shows them", Shelf.CountLine(View("gemma"), glyphs: GlyphSet.Unicode));

        //the all shelf hides nothing by family, so its line says only where you are
        Assert.Equal("1–5 of 5", Shelf.CountLine(View(), glyphs: GlyphSet.Unicode));
    }

    //the two tails compose in a decided order, the fit clause before the family clause
    [Fact]
    public void BOTH_TAILS_COMPOSE_IN_A_DECIDED_ORDER() =>
        Assert.Equal("1–2 of 5 · 4 too big, a shows all · 3 in other families, all shows them",
            Shelf.CountLine(View("gemma") with { HiddenByFit = 4 }, glyphs: GlyphSet.Unicode));

    //the mapper's honesty table

    //one file as the group of one a scan of a single file makes
    private static ModelRow OneRow(FoundModel m, HardwareClass? hw, Badge? badge = null) =>
        LocalShelf.Row(Assert.Single(LocalShelf.Group([m])), m, hw, badge: badge);

    private static FoundModel Found(string name, double gb, int shards = 1) =>
        new(Path.Combine(Dir, "unsloth", name), Gib(gb), null, shards);

    private static HardwareClass Hw() =>
        HardwareClassifier.Classify(new Gatto.Tests.Fakes.WizardProbes().Hardware()!);

    //a local row leaves every field it cannot know empty, Params stays null and summing shard headers would report a third of the parameters
    [Fact]
    public void A_FOUND_MODEL_MAPS_ONTO_A_ROW_THAT_CLAIMS_NOTHING_IT_CANNOT_KNOW()
    {
        var r = OneRow(Found("gemma-4-26B-A4B-it-UD-Q4_K_M.gguf", 16.9), Hw());

        Assert.Single(r.Publishers);
        Assert.Null(r.Badge);
        Assert.Null(r.Params);
        Assert.Null(r.Active);
        Assert.Equal(0, r.RowOffer!.Downloads);

        //assert the fields it does fill, otherwise a mapper that fills nothing passes the nulls above
        Assert.Equal("gemma-4-26B-A4B-it", r.Model);   //the model's name, the quant and its UD segment cut
        Assert.Equal(Gib(16.9), r.RowQuant!.Bytes);
        Assert.Equal("Q4_K_M", QuantToken.Of(r.RowQuant!.FileName));
    }

    //the folder is the one publisher, the file its one repo, and the badge the register gave rides on the row
    [Fact]
    public void A_LOCAL_ROW_IS_THE_FOLDER_WITH_ONE_REPO_AND_KEEPS_ITS_BADGE()
    {
        var badge = new Badge("org/model", new DateOnly(2026, 8, 10), "abc1234", "temp 0.7");
        var r = OneRow(Found("gemma-4-26B-A4B-it-UD-Q4_K_M.gguf", 16.9), Hw(), badge);

        var publisher = Assert.Single(r.Publishers);
        var repo = Assert.Single(publisher.Repos);
        Assert.Single(repo.Quants);
        Assert.Equal(badge, r.Badge);
    }

    //a shard set is one model, so the stem strips the -00001-of-00003 suffix, otherwise the shelf names the model after shard one
    [Theory]
    [InlineData("big-00001-of-00003.gguf", "big")]
    [InlineData("plain.gguf", "plain")]
    [InlineData("gemma-4-26B-A4B-it-UD-Q4_K_M.gguf", "gemma-4-26B-A4B-it-UD-Q4_K_M")]
    public void A_SHARD_SET_AND_A_PLAIN_FILE_BOTH_NAME_THE_MODEL(string file, string want) =>
        Assert.Equal(want, LocalShelf.StemOf(file));

    //fit runs the same arithmetic as the hub shelf, and a file with no readable header is Unknown, which draws no glyph
    [Fact]
    public void A_FILE_WITH_NO_READABLE_HEADER_IS_UNKNOWN_NOT_FITTING() =>
        Assert.Equal(FitRegime.Unknown, OneRow(Found("mystery.gguf", 4), Hw()).Fit);
}
