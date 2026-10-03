using Gatto.Cli.Setup;
using Gatto.Core.Acquire;
using Gatto.Core.Hardware;
using Gatto.Tests.Fakes;
//the alias names the shared fake in Gatto.Tests.Fakes, so the fixtures here keep one spelling
using Probes = Gatto.Tests.Fakes.WizardProbes;

namespace Gatto.Tests.Setup;

//the runner decides when the run ends, and an Info screen with nothing after it ends the run, which a flow-level test cannot see
public class SetupRunnerWalkTests
{
    //answers the walk in order, where a null answer means the user pressed Esc and ends the run there
    private sealed class ScriptedSurface(params string?[] answers) : IWizardSurface
    {
        private int _i;
        public List<WizardScreen> Seen { get; } = [];

        public string? Choose(WizardScreen.Choice c, Func<bool>? watch = null,
            Func<FetchTick?>? tick = null, Func<CheckTick?>? check = null)
        {
            Seen.Add(c);
            if (watch is not null) Watched.Add(c.Key);
            return Next();
        }

        //records which screens the runner handed a poll to, since the face's tests only show a watch resolving
        public List<string> Watched { get; } = [];
        public string? Ask(WizardScreen.Ask a) { Seen.Add(a); return Next(); }
        public void Show(WizardScreen.Info i) => Seen.Add(i);
        public void End(WizardScreen.Terminal t) => Seen.Add(t);

        private string? Next() => _i < answers.Length ? answers[_i++] : null;

        public IEnumerable<string> Keys => Seen.Select(ScreenKey.Of);
    }

    private static ShelfRow Row(string repoId, string file = "model-Q4_K_M.gguf") =>
        new(repoId, repoId.Split('/')[0], new HubQuant(file, 4_000_000_000, null),
            Gatto.Core.Models.FitRegime.FitsGpu, 32768, false, null, 100, false);

    //a typed screen whose footer says Esc back must actually go back, since the flag and the deed must agree
    [Fact]
    public void ESC_ON_A_TYPED_SCREEN_WITH_A_BACK_ROAD_RETURNS_TO_THE_SCREEN_BEFORE_IT()
    {
        var surface = new ScriptedSurface(
            SetupFlow.WelcomeGo, SetupFlow.MachineNext,
            ShelfControls.TypedAnswer(@"C:\llama\llama-cli.exe"),   //the typed path runs with a file that is not llama-server, so the run reaches the steering screen
            null);                       //null means Esc on that screen.
        var flow = new SetupFlow(new Probes
        {
            Llama = null,
            Verify = _ => new Gatto.Core.Tools.ProbeResult(Gatto.Core.Tools.ProbeShape.NotClassic, "d"),
        });

        SetupRunner.Run(flow, surface, homePath: null);

        //the steering screen must show twice, once on the way in and once after Esc. the script is empty by then, so the exit reads as left either way
        var keys = surface.Keys.ToList();
        Assert.Contains(SetupFlow.LlamaPathKey, keys);
        Assert.Equal(2, keys.Count(k => k == SetupFlow.SteerKey));
    }

    //the escape key on a numbered screen still leaves, since going back belongs to the typed screens where the footer says so
    [Fact]
    public void AND_ESC_ON_A_NUMBERED_SCREEN_STILL_LEAVES()
    {
        var surface = new ScriptedSurface(SetupFlow.WelcomeGo, SetupFlow.MachineNext, null);

        SetupRunner.Run(new SetupFlow(new Probes { Llama = null }), surface, homePath: null);

        //the machine screen must show once. the exit cannot discriminate, since a run that stepped back gets another null and leaves anyway
        Assert.Single(surface.Keys, k => k == SetupFlow.MachineKey);
        //this machine has no engine, so the steering screen appears, and the connect option makes it a numbered screen
        Assert.Contains(SetupFlow.SteerKey, surface.Keys);
    }

    [Fact]
    public void PICKING_A_HUB_MODEL_DOES_NOT_END_THE_WIZARD()
    {
        //picking a model must not end the wizard at its info screen, or there is no download link, no watch and nothing written
        var probes = new Probes { Rows = [Row("org/a")] };
        //everything after the pick is unanswered, so the surface backs out, which shows the screen was offered without scripting the whole run
        var face = new ScriptedSurface([.. WalkOpening.PastEngine, "0"]);

        SetupRunner.Run(new SetupFlow(probes), face, homePath: null);

        Assert.Contains(face.Seen, s => s is WizardScreen.Choice c && c.Key == SetupFlow.DownloadKey);
    }

    [Fact]
    public void THE_PICK_SCREEN_CARRIES_THE_LINK_because_a_name_alone_is_CG_8_all_over_again()
    {
        //naming a file is not an instruction, so the screen must give the address with it
        var face = new ScriptedSurface([.. WalkOpening.PastEngine, "0"]);
        SetupRunner.Run(new SetupFlow(new Probes { Rows = [Row("bartowski/Qwen3-8B-GGUF")] }), face, null);

        var screen = Assert.IsType<WizardScreen.Choice>(
            face.Seen.Last(s => s is WizardScreen.Choice c && c.Key == SetupFlow.DownloadKey));
        var body = string.Join(" ", screen.BodyRows!);

        //the address renders as a fact row with no scheme, but it still reaches the screen
        Assert.Contains("huggingface.co/bartowski/Qwen3-8B-GGUF", body, StringComparison.Ordinal);
        Assert.Contains("model-Q4_K_M.gguf", body, StringComparison.Ordinal);
    }

    //builds a shard-set row with a summed size and a file count, where ShardCount one is the single-file world the other runs use
    private static ShelfRow SetRow(string repoId, int shards, long totalBytes) =>
        new(repoId, repoId.Split('/')[0],
            new HubQuant($"m-Q4_K_M-00001-of-{shards:D5}.gguf", totalBytes, null, shards),
            Gatto.Core.Models.FitRegime.FitsGpu, 32768, false, null, 100, false);

    [Fact]
    public void A_SET_PICK_LINKS_THE_FILE_LIST_AND_SAYS_HOW_MANY_FILES()
    {
        //a shard set links the repo's tree page, since a direct download link fetches one file of thirteen
        var face = new ScriptedSurface([.. WalkOpening.PastEngine, "0"]);
        SetupRunner.Run(new SetupFlow(new Probes { Rows = [SetRow("org/big", 13, 52_000_000_000)] }),
            face, homePath: null);

        var screen = Assert.IsType<WizardScreen.Choice>(
            face.Seen.Last(s => s is WizardScreen.Choice c && c.Key == SetupFlow.DownloadKey));
        var body = string.Join(" ", screen.BodyRows!.Select(r => r.Text));

        Assert.Contains("huggingface.co/org/big/tree/main", body, StringComparison.Ordinal);
        Assert.DoesNotContain("?download=true", body, StringComparison.Ordinal);
        //assert the count and the size together, since the size discriminates the whole set's 48.4 GB from one shard's 3.7
        Assert.Contains("13 files (total 48.4 GB)", body, StringComparison.Ordinal);
    }

    [Fact]
    public void A_SINGLE_FILE_PICK_STILL_GETS_THE_DIRECT_LINK()
    {
        //this twin stops the guard above from passing on a screen that always links the file list, since the branch must discriminate
        var face = new ScriptedSurface([.. WalkOpening.PastEngine, "0"]);
        SetupRunner.Run(new SetupFlow(new Probes { Rows = [Row("org/small")] }), face, homePath: null);

        var body = string.Join(" ", Assert.IsType<WizardScreen.Choice>(
            face.Seen.Last(s => s is WizardScreen.Choice c && c.Key == SetupFlow.DownloadKey))
            .BodyRows!.Select(r => r.Text));

        Assert.Contains("?download=true", body, StringComparison.Ordinal);
        Assert.DoesNotContain("/tree/main", body, StringComparison.Ordinal);
        Assert.DoesNotContain("files (total", body, StringComparison.Ordinal);
    }

    //a file kept in a folder is linked by its path in the repo, since the bare name answers 404 on the Hub
    [Fact]
    public void A_SINGLE_FILE_IN_A_FOLDER_IS_LINKED_BY_ITS_PATH()
    {
        var row = Row("org/small") with
        {
            PickedQuant = new HubQuant("model-Q4_K_M.gguf", 4_000_000_000, null, Path: "Q4_K_M/model-Q4_K_M.gguf"),
        };
        var face = new ScriptedSurface([.. WalkOpening.PastEngine, "0"]);
        SetupRunner.Run(new SetupFlow(new Probes { Rows = [row] }), face, homePath: null);

        var body = string.Join(" ", Assert.IsType<WizardScreen.Choice>(
            face.Seen.Last(s => s is WizardScreen.Choice c && c.Key == SetupFlow.DownloadKey))
            .BodyRows!.Select(r => r.Text));

        Assert.Contains("/resolve/main/Q4_K_M/model-Q4_K_M.gguf", body, StringComparison.Ordinal);
    }

    //a fetched file's model records the repo and file it came from, which the hub row match and the audition badge read
    [Fact]
    public void A_FETCHED_MODEL_RECORDS_ITS_SOURCE_AND_A_FOUND_ONE_DOES_NOT()
    {
        var probes = new Probes { Rows = [Row("org/a")], Found = [] };
        var flow = new SetupFlow(probes);
        flow.StartPastEngine();
        flow.Answer("0");
        probes.Found = [new FoundModel(@"C:\Users\me\Downloads\model-Q4_K_M.gguf", 4_000_000_000, null)];
        Assert.True(flow.PollForDownload());
        flow.Answer(SetupFlow.Landed);

        Assert.Equal(new Gatto.Roles.ModelSource("org/a", "model-Q4_K_M.gguf"), flow.Writes.CreateModel!.Source);

        var found = new SetupFlow(new Probes { Found = [new FoundModel(@"D:\m\qwen.gguf", 4_000_000_000, null)] });
        found.StartPastEngine();
        found.Answer("0");
        Assert.Null(found.Writes.CreateModel?.Source);
    }

    //drive the poll through PollForDownload, the only reachable path, since pressing Landed by hand asserts on state production never fills
    [Fact]
    public void THE_WATCH_PICKS_THE_FILE_UP_WHEN_IT_LANDS()
    {
        var probes = new Probes { Rows = [Row("org/a")], Found = [] };
        var flow = new SetupFlow(probes);
        flow.StartPastEngine();
        flow.Answer("0");                                  //the pick opens the download screen.

        //with nothing in the folder the poll says so and nothing is intended, so the assertion goes on the poll
        Assert.False(flow.PollForDownload(), "an empty folder must not wake the watch");
        //check Writes.CreateModel rather than Writes.IsEmpty, since the engine step records its own write first
        Assert.Null(flow.Writes.CreateModel);

        //the browser finishes, and the poll wakes once, which is what produces the answer.
        probes.Found = [new FoundModel(@"C:\Users\me\Downloads\model-Q4_K_M.gguf", 4_000_000_000, null)];
        Assert.True(flow.PollForDownload(), "the file arriving must wake the watch");
        flow.Answer(SetupFlow.Landed);

        Assert.Equal(@"C:\Users\me\Downloads\model-Q4_K_M.gguf", flow.Writes.CreateModel!.GgufPath);
        Assert.True(flow.NeedsWritesApplied);              //a found model reaches this same completion point.
    }




    //one wait shows the screen once and shows nothing while it watches, since the poll loop runs inside the prompt
    [Fact]
    public void A_WHOLE_WAIT_IS_ONE_SCREEN_not_one_per_look()
    {
        var probes = new Probes { Rows = [Row("org/a")], Found = [] };
        //the watch advances this screen, so a script that presses keys would drive an affordance the product does not have
        var face = new ScriptedSurface([.. WalkOpening.PastEngine, "0"]);

        SetupRunner.Run(new SetupFlow(probes), face, homePath: null);

        //this count is scene-setting, since the scripted surface counts one however the wiring is set, and the assert below carries the test
        var downloads = face.Keys.Count(k => k == SetupFlow.DownloadKey);
        Assert.True(downloads == 1,
            $"the wait put the download screen in front of the user {downloads} times; a watch shows "
            + "it once and then watches.");

        //the runner handed the screen a poll, and breaking that wiring fails here and nowhere else
        Assert.Contains(SetupFlow.DownloadKey, face.Watched);
    }

    //a half-arrived set that nobody picked must still be watched, or that user has no way forward
    [Fact]
    public void A_PARTIAL_SET_NOBODY_PICKED_IS_STILL_WATCHED()
    {
        //no Hub pick happens here, since the set is already half-arrived before the wizard runs
        var probes = new Probes { Found = [FoundSet(present: 1, of: 3)] };
        var flow = new SetupFlow(probes);
        var face = new ScriptedSurface([.. WalkOpening.PastEngine, "0"]);

        SetupRunner.Run(flow, face, homePath: null);

        var screen = Assert.IsType<WizardScreen.Choice>(
            face.Seen.Last(s => s is WizardScreen.Choice c && c.Key == SetupFlow.PartialSetKey));
        Assert.True(screen.Watching, "a set nobody picked is still a set gatto can watch fill up");
        Assert.Contains(SetupFlow.PartialSetKey, face.Watched);

        //an unchanged folder must not wake the watch and an arriving shard must, or the route watches for nothing
        Assert.False(flow.PollWatch(), "nothing changed");
        probes.Found = [FoundSet(2, 3)];
        Assert.True(flow.PollWatch(), "a shard arrived and nobody had to press anything");
    }

    [Fact]
    public void A_NEW_PICK_EXPLAINS_ITSELF_AGAIN()
    {
        //the once-only explanation belongs to one watch, since a user who picks a different model has not been told where gatto looks
        var probes = new Probes { Rows = [Row("org/a"), Row("org/b")], Found = [], Roots = [@"D:\dl"] };
        var flow = new SetupFlow(probes);
        flow.StartPastEngine();
        flow.Answer("0");
        flow.Answer(SetupFlow.Landed);                   //the first miss gets the explanation.
        flow.Answer(SetupFlow.Landed);                   //the second miss repeats no explanation.
        flow.Answer(SetupFlow.PickAnother);                  //back to the shelf.
        flow.Answer("1");                                    //a different model is picked.

        var miss = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.Landed));
        //the download screen has its own present-tense heading, and the shelf keeps the past-tense line
        Assert.Contains(miss.BodyRows!, r => r.Text.Contains("It is watching:", StringComparison.Ordinal));
    }

    //builds a shard set as discovery reports it, the first shard's path, the summed bytes and the count present
    private static FoundModel FoundSet(int present, int of, string dir = @"C:\Users\me\Downloads") =>
        new(System.IO.Path.Combine(dir, $"m-Q4_K_M-00001-of-{of:D5}.gguf"),
            4_000_000_000L * present, null, present);

    //the watch must wake only when the shard count changes, since an always-true predicate resolves the partial screen on sight and bounces the run
    [Fact]
    public void THE_WATCH_WAKES_ON_A_CHANGE_AND_NOT_ON_A_REPEAT()
    {
        var probes = new Probes { Rows = [SetRow("org/big", 3, 12_000_000_000)], Found = [] };
        var flow = new SetupFlow(probes);
        flow.StartPastEngine();
        flow.Answer("0");                                  //choosing a row arms the download watch.

        (FoundModel[] Found, string What)[] steps =
        [
            ([], "an empty folder"),
            ([FoundSet(1, 3)], "the first shard"),
            ([FoundSet(2, 3)], "the second shard"),
            ([FoundSet(3, 3)], "the set completing"),
        ];

        for (var i = 0; i < steps.Length; i++)
        {
            var (found, what) = steps[i];
            probes.Found = [.. found];

            //the empty first step is not a transition, so the watch stays asleep
            var wakesFirst = i > 0;
            Assert.True(flow.PollForDownload() == wakesFirst,
                $"{what}: the first poll should {(wakesFirst ? "wake the watch" : "leave it asleep")}");
            Assert.False(flow.PollForDownload(),
                $"{what}: polling again with nothing changed must NOT wake it, that is the spin");
        }
    }

    [Fact]
    public void A_HALF_ARRIVED_SET_IS_REFUSED_AND_THE_SCREEN_SAYS_HOW_FAR_ALONG()
    {
        //a partial set never adopts, and the count tells the user the download is progressing
        var probes = new Probes { Found = [FoundSet(present: 6, of: 13)] };
        var flow = new SetupFlow(probes);
        var face = new ScriptedSurface([.. WalkOpening.PastEngine, "0"]);

        SetupRunner.Run(flow, face, homePath: null);

        var screen = Assert.IsType<WizardScreen.Choice>(
            face.Seen.Last(s => s is WizardScreen.Choice c && c.Key == SetupFlow.PartialSetKey));
        Assert.Contains("6 of 13", screen.Question!, StringComparison.Ordinal);
        Assert.Contains(@"C:\Users\me\Downloads",
            string.Join("\n", screen.BodyRows!.Select(r => r.Text)), StringComparison.Ordinal);
        //check Writes.CreateModel alone, since the engine step records its own write on purpose
        Assert.Null(flow.Writes.CreateModel);
    }

    [Fact]
    public void A_PARTIAL_SET_WRITES_NOTHING_and_the_COMPLETE_one_carries_on()
    {
        //a refusal-only assertion passes on a wizard that refuses everything, so the complete case must continue in the same test
        var partial = new Probes { Rows = [SetRow("org/big", 13, 52_000_000_000)], Found = [] };
        var half = new SetupFlow(partial);
        half.StartPastEngine();          //no model found, so setup shows the search shelf.
        half.Answer("0");                          //choosing the set row opens the download screen.
        partial.Found = [FoundSet(4, 13)];
        //a half-arrived set wakes the watch, since the poll matches on the set. refusing to adopt is the job of Adopt, one layer down
        Assert.True(half.PollForDownload());
        half.Answer(SetupFlow.Landed);

        //check Writes.CreateModel alone, since the engine step records its own write on purpose
        Assert.Null(half.Writes.CreateModel);
        Assert.Null(half.Selected);
        Assert.False(half.NeedsWritesApplied);

        var full = new Probes { Rows = [SetRow("org/big", 13, 52_000_000_000)], Found = [] };
        var whole = new SetupFlow(full);
        whole.StartPastEngine();
        whole.Answer("0");
        full.Found = [FoundSet(13, 13)];
        Assert.True(whole.PollForDownload());
        whole.Answer(SetupFlow.Landed);

        //the created model must name shard one, an adopted tail shard breaks ModelScaffold.ResolveShardOne
        Assert.EndsWith("m-Q4_K_M-00001-of-00013.gguf", whole.Writes.CreateModel!.GgufPath,
            StringComparison.Ordinal);
    }

    [Fact]
    public void LOOKING_AGAIN_AT_A_PARTIAL_SET_PICKS_IT_UP_ONCE_IT_FINISHES()
    {
        //the partial screen keeps the look-again option, so the user can retry while files arrive. assert the count changes, a frozen count passes a key-only check
        var probes = new Probes { Rows = [SetRow("org/big", 3, 12_000_000_000)], Found = [] };
        var flow = new SetupFlow(probes);
        flow.StartPastEngine();
        flow.Answer("0");

        probes.Found = [FoundSet(1, 3)];
        Assert.True(flow.PollForDownload());
        var first = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.Landed));
        Assert.Equal(SetupFlow.PartialSetKey, first.Key);
        Assert.Contains("1 of 3", first.Question!, StringComparison.Ordinal);

        probes.Found = [FoundSet(2, 3)];
        Assert.True(flow.PollForDownload(), "another shard is a state change and must wake it");
        var again = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.Landed));
        Assert.Contains("2 of 3", again.Question!, StringComparison.Ordinal);

        probes.Found = [FoundSet(3, 3)];
        Assert.True(flow.PollForDownload());
        flow.Answer(SetupFlow.Landed);
        Assert.NotNull(flow.Writes.CreateModel);
    }

    [Fact]
    public void THE_WATCH_SEES_A_SET_ARRIVING_OUT_OF_ORDER()
    {
        //shards need not arrive in order, so the watch matches the whole set. whether to adopt it is Adopt's call, and here the answer is not yet
        var probes = new Probes { Rows = [SetRow("org/big", 5, 20_000_000_000)], Found = [] };
        var flow = new SetupFlow(probes);
        flow.StartPastEngine();
        flow.Answer("0");

        probes.Found = [new FoundModel(@"C:\Users\me\Downloads\m-Q4_K_M-00003-of-00005.gguf",
            8_000_000_000, null, 2)];
        Assert.True(flow.PollForDownload());
        var screen = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.Landed));

        Assert.Equal(SetupFlow.PartialSetKey, screen.Key);
        Assert.Contains("2 of 5", screen.Question!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_DOWNLOAD_THAT_LANDS_SOMEWHERE_ELSE_HAS_A_WAY_OUT()
    {
        //browsers save to chosen folders and people move files, so the watch needs a typed way out. the scan roots are a policy the user can't argue with
        var flow = new SetupFlow(new Probes { Rows = [Row("org/a")] });
        flow.StartPastEngine();
        flow.Answer("0");

        //the typed field on this screen is the way out, so the user isn't stuck in a folder the file will never appear in
        var watch = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.Landed));
        Assert.Equal(SetupFlow.DownloadKey, watch.Key);
        Assert.NotNull(watch.Door);
    }

    //the folder answer runs the same file-name match as the watch, since only the folder to look in differs. the assertion reads Selected, which only Adopt sets
    [Fact]
    public void THE_FOLDER_ANSWER_ADOPTS_THE_FILE_IT_WAS_SENT_FOR()
    {
        var probes = new Probes { Rows = [Row("org/a")], Found = [] };
        var flow = new SetupFlow(probes);
        flow.StartPastEngine();
        flow.Answer("0");                                   //0 picks the row, which arms the watch and opens the download screen
        flow.Answer(SetupFlow.Elsewhere);                   //the Elsewhere answer opens the folder prompt

        //put the file in the folder the answer names, so the file-name match can hit
        probes.Found = [new FoundModel(@"D:\my downloads\model-Q4_K_M.gguf", 4_000_000_000, null)];
        flow.Answer(ShelfControls.TypedAnswer(@"D:\my downloads"));

        Assert.Equal(@"D:\my downloads\model-Q4_K_M.gguf", flow.Selected?.Path);
    }

    //the scan must look in the folder it is given, and a scan that ignores its argument fails here
    [Fact]
    public void THE_FOLDER_ANSWER_LOOKS_IN_THE_FOLDER_THE_USER_NAMED()
    {
        var probes = new Probes { Rows = [Row("org/a")], Found = [] };
        var flow = new SetupFlow(probes);
        flow.StartPastEngine();
        flow.Answer("0");
        flow.Answer(SetupFlow.Elsewhere);
        flow.Answer(ShelfControls.TypedAnswer(@"D:\my downloads"));

        Assert.Contains(@"D:\my downloads", probes.ScanRoots);
    }

    //a folder without the steered file must not claim a model on this machine, and the generic rows stay offered
    [Fact]
    public void A_FOLDER_WITHOUT_THE_STEERED_FILE_DOES_NOT_CLAIM_TO_HAVE_FOUND_ONE()
    {
        var probes = new Probes { Rows = [Row("org/a")], Found = [] };
        var flow = new SetupFlow(probes);
        flow.StartPastEngine();
        flow.Answer("0");
        flow.Answer(SetupFlow.Elsewhere);

        //put another file in that folder, so the list has rows without the one asked for
        probes.Found = [new FoundModel(@"D:\my downloads\something-else.gguf", 4_000_000_000, null)];
        var screen = Assert.IsType<WizardScreen.Choice>(flow.Answer(ShelfControls.TypedAnswer(@"D:\my downloads")));

        Assert.Equal(SetupFlow.DiscoveredKey, screen.Key);
        Assert.Null(flow.Selected);
        Assert.DoesNotContain("already on this machine", screen.Question!, StringComparison.Ordinal);
        Assert.Contains(screen.Options, o => o.Label == "something-else.gguf");
    }

    //the startup scan runs before the wizard proposes anything, so the already-here sentence is true here
    [Fact]
    public void THE_STARTUP_SCAN_STILL_SAYS_A_MODEL_WAS_ALREADY_HERE()
    {
        var probes = new Probes
        {
            Found = [new FoundModel(@"C:\models\already-here.gguf", 4_000_000_000, null)],
        };
        var flow = new SetupFlow(probes);
        var screen = Assert.IsType<WizardScreen.Choice>(flow.StartPastEngine());

        Assert.Equal(SetupFlow.DiscoveredKey, screen.Key);
        Assert.Contains("already on this machine", screen.Question!, StringComparison.Ordinal);
    }

    //a download miss must list every folder it searched, since a bare "nothing new" reads as a broken install
    [Fact]
    public void A_DOWNLOAD_MISS_SAYS_WHERE_GATTO_LOOKED()
    {
        var probes = new Probes
        {
            Rows = [Row("org/a")],
            Found = [],
            Roots = [@"C:\home\models", @"D:\relocated downloads"],
        };
        var flow = new SetupFlow(probes);
        flow.StartPastEngine();
        flow.Answer("0");                                  //0 picks the row, which opens the download screen

        var miss = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.Landed));
        Assert.Equal(SetupFlow.DownloadKey, miss.Key);

        var body = string.Join("\n", miss.BodyRows!.Select(r => r.Text));
        //the miss screen names every folder it swept, so the user can spot a wrong one. the heading is the watch's own, in present tense
        Assert.Contains("It is watching:", body, StringComparison.Ordinal);
        Assert.Contains(@"D:\relocated downloads", body, StringComparison.Ordinal);
        Assert.Contains(@"C:\home\models", body, StringComparison.Ordinal);
    }

    //the first download screen names every folder it watches, which is what makes "i saved it elsewhere" a real choice
    [Fact]
    public void THE_FIRST_DOWNLOAD_SCREEN_SAYS_WHERE_IT_IS_WATCHING()
    {
        var probes = new Probes { Rows = [Row("org/a")], Found = [], Roots = [@"D:\relocated downloads"] };
        var flow = new SetupFlow(probes);
        flow.StartPastEngine();

        var first = Assert.IsType<WizardScreen.Choice>(flow.Answer("0"));
        var body = string.Join("\n", first.BodyRows!.Select(r => r.Text));

        Assert.Equal(SetupFlow.DownloadKey, first.Key);

        //this screen names where it looks in the present tense, since the watch is running now
        Assert.Contains("It is watching:", body, StringComparison.Ordinal);
        //the shelf's past-tense phrase must not reappear on this screen
        Assert.DoesNotContain("gatto looked in:", body, StringComparison.Ordinal);
        //the test needs the root itself, since a heading with nothing under it passes a laxer needle
        Assert.Contains(@"D:\relocated downloads", body, StringComparison.Ordinal);
        //the body says the watch is running, so the user knows nothing needs doing
        Assert.Contains("watching", body, StringComparison.OrdinalIgnoreCase);
    }

    //the discovery miss shows the same root list
    [Fact]
    public void A_DISCOVERY_MISS_CARRIES_THE_SAME_LIST()
    {
        var probes = new Probes { Rows = [Row("org/a")], Found = [], Roots = [@"D:\relocated downloads"] };
        var flow = new SetupFlow(probes);
        flow.StartPastEngine();

        //the narration is what renders, and model.none has no screen yet, so the test reads it
        var none = Assert.Single(flow.TakeNarration(), i => i.Key == "model.none");
        var text = string.Join("\n", none.Rows.Select(r => r.Text));
        Assert.Contains("gatto looked in:", text, StringComparison.Ordinal);
        Assert.Contains(@"D:\relocated downloads", text, StringComparison.Ordinal);
    }

    [Fact]
    public void THE_TYPED_ID_DOOR_REACHES_THE_SAME_DOWNLOAD_SCREEN()
    {
        //the typed-id path must reach the same download screen as the shelf path, since the step has one home
        var flow = new SetupFlow(new ProbesWithTypedId { Rows = [] });
        flow.StartPastEngine();
        flow.Answer(SetupFlow.TypeAnId);
        var screen = Assert.IsType<WizardScreen.Choice>(flow.Answer("someone/model"));

        Assert.Equal(SetupFlow.DownloadKey, screen.Key);
    }

    private sealed class ProbesWithTypedId : ISetupProbes
    {
        //answering the update question here keeps it out of the run
        public bool? UpdateConsent() => false;
        //these fakes model a fresh home, where no model id is taken by another file. a fixed RunningFrom path lets a screen render a chosen state
        public string RunningFrom() => @"C:\Users\you\Downloads\gatto\gatto.exe";

        public (Gatto.Cli.InstallState State, string Dir) InstallStatus() =>
            (Gatto.Cli.InstallState.Installed, @"C:\Programs\gatto");

        public string? InstalledVersion() => null;


        public string RunningVersion() => Gatto.Core.GattoVersion.String;
        public (Gatto.Roles.IdClash Kind, string? Id) ClashFor(string ggufPath, string? incomingRepoId) => (Gatto.Roles.IdClash.Free, null);
        public IReadOnlyList<ShelfRow> Rows { get; init; } = [];

        public HardwareSnapshot? Hardware() => new(34359738368, 34093496320, GpuKind.Discrete, 8589934592);

        //the name is display only, so null is the honest default (an invented one would show hardware no probe reported)
        public HardwareNames HardwareNames() => default;
        public string? LlamaServerPath() => @"C:\llama\llama-server.exe";
        public bool HasResolvableModel() => false;
        public ConnectProbe? ProbeServer(IReadOnlyList<int>? skip = null) => null;
        public string? GattoServingOn(int port) => null;

        public ConnectProbe? ProbeAt(string baseUrl) => null;
        public IReadOnlyList<string> Roots { get; init; } = [];
        public ScanResult Scan(string? r) => new([], Roots);
        public HubSearchOutcome Search(HubSearchRequest request) => new(Rows, null);
        public (string Path, long Bytes)? ProjectorFor(string p) => null;
        public string? ArchitectureOf(string p) => null;
        public MoveOffer? MoveOfferFor(string p) => null;
        public (Gatto.Core.Models.FitRegime Alone, Gatto.Core.Models.FitRegime WithProjector)? PairFit(string p, long b) => null;
        public TypedIdOutcome EvaluateTypedId(string id) => new TypedIdOutcome.Ok(Row("someone/model"));
        public Gatto.Roles.LlamaAsset? ChooseLlamaAsset() => null;
        public Gatto.Core.Tools.ProbeResult VerifyLlamaServer(string p) =>
            new(Gatto.Core.Tools.ProbeShape.ClassicServer, "d",
                //a ClassicServer must name its build, since the probe reads the shape from that banner. a shape without a build models an impossible machine
                Build: Gatto.Roles.LlamaAssetSteering.PinnedRelease);
        public int ContextFor(string p) => 8192;
        public AuditionCheck RunAudition(string p, string? repoId, IProgress<Gatto.Roles.Audition.AuditionProgress>? progress, CancellationToken ct) =>
            new(AuditionOutcome.Passed);
        public string? ExistingModel { get; init; }
        //the model id another file already holds, or null when nothing collides
        public string? Colliding { get; init; }
        public string? ExistingEndpoint { get; init; }
        public string? ExistingModelFor(string ggufPath) => ExistingModel;
        public string? ExistingEndpointFor(string baseUrl) => ExistingEndpoint;
        public string ConfigPath() => @"C:\home\gatto.json";
        public ProveOutcome ProveIt(string? modelId,
            IProgress<Gatto.Roles.Audition.AuditionProgress>? progress = null) => new(true, "hi", TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void THE_STEP_MARKERS_ACTUALLY_REACH_THE_FACE_ON_A_REAL_WALK()
    {
        //the step markers must reach the face on the real runner, and an installed probe shows no install heading
        var face = new ScriptedSurface([.. WalkOpening.Answers, SetupFlow.ForkConnect]);

        SetupRunner.Run(new SetupFlow(new Probes()), face, homePath: null);

        var shown = face.Seen.OfType<WizardScreen.Info>().Select(i => i.Key).ToList();
        Assert.Contains("step.model", shown);
        Assert.DoesNotContain("step.install", shown);

        //the heading must render before the screen it heads
        var marker = face.Seen.FindIndex(x => x is WizardScreen.Info { Key: "step.model" });
        var fork = face.Seen.FindIndex(x => x is WizardScreen.Choice { Key: SetupFlow.FoundKey });
        Assert.True(marker >= 0 && fork > marker,
            "the Model heading must render before the fork it heads, not after it");
    }


    [Fact]
    public void A_DEV_BUILDS_WALK_RENDERS_ITS_REFUSAL()
    {
        //a development build must refuse to install itself, since copying one file of a hundred leaves a gatto that will not start
        var home = Path.Combine(Path.GetTempPath(), "gatto-runnerwalk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        var gguf = Path.Combine(home, "model-Q4_K_M.gguf");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "tiny.gguf"), gguf);
        Gatto.Core.Home.GattoHome.EnsureInitialized(home);

        var probes = new Probes
        {
            Install = Gatto.Cli.InstallState.DevBuild,
            Llama = @"C:\llama\llama-server.exe",
            Found = [new FoundModel(gguf, 4_000_000_000, null)],
            Audition = new AuditionCheck(
                AuditionOutcome.Passed,
                new AuditionFacts("Q4_K_M", "defaults", "template default", 31.4, 5, 5),
                new DateOnly(2026, 8, 24)),
        };
        var face = new ScriptedSurface([.. WalkOpening.PastEngine, "0", SetupFlow.Yes,
            SetupFlow.Landed, SetupFlow.AuditionPassedNext, SetupFlow.OpenRepl]);

        SetupRunner.Run(new SetupFlow(probes), face, home);

        var narrated = string.Join(" ", face.Seen.OfType<WizardScreen.Info>()
            .SelectMany(i => i.Rows.Select(r => r.Text)));

        Assert.Contains("development build", narrated, StringComparison.Ordinal);
        Assert.Contains("skipping the install step", narrated, StringComparison.Ordinal);
    }

    //the watch answers Enter with its first option, since it has a typed field and something to advance to. both repo kinds run, a vision model and a plain one
    [Theory]
    [InlineData("bartowski/Qwen3-8B-GGUF")]
    [InlineData("org/a")]
    public void THE_DOWNLOAD_WATCHS_ENTER_REACHES_ITS_FIRST_OPTION(string repo)
    {
        var face = new ScriptedSurface([.. WalkOpening.PastEngine, "0"]);
        SetupRunner.Run(new SetupFlow(new Probes { Rows = [Row(repo)] }), face, null);

        var screen = Assert.IsType<WizardScreen.Choice>(
            face.Seen.Last(s => s is WizardScreen.Choice c && c.Key == SetupFlow.DownloadKey));

        //these two assertions prove the screen can be answered before Enter is judged
        Assert.False(screen.OnlyTheWatchAdvances);
        Assert.NotEmpty(screen.Options);

        //the Enter key answers the option under the cursor, which is the first one here
        Assert.Equal(screen.Options[0].Key,
            Gatto.Tests.Setup.Tui.WalkRender.WatchAfterKeys(screen, 100, tick: null,
                [new ConsoleKeyInfo('\0', ConsoleKey.Enter, false, false, false)]));
    }
    //the Enter key must stay inert while a set is still arriving. the screen says no press is needed, and its one option does not advance
    [Fact]
    public void ENTER_DOES_NOTHING_WHILE_A_SET_IS_STILL_ARRIVING()
    {
        var probes = new Probes { Rows = [SetRow("org/big", 3, 12_000_000_000)], Found = [] };
        var flow = new SetupFlow(probes);
        flow.StartPastEngine();
        flow.Answer("0");

        probes.Found = [FoundSet(1, 3)];
        Assert.True(flow.PollForDownload());
        var partial = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.Landed));
        Assert.Equal(SetupFlow.PartialSetKey, partial.Key);
        Assert.True(partial.OnlyTheWatchAdvances, "this screen must be render-and-wait");

        Assert.Equal(SetupFlow.Landed,
            Gatto.Tests.Setup.Tui.WalkRender.WatchAfterKeys(partial, 100, tick: null,
                [new ConsoleKeyInfo('\0', ConsoleKey.Enter, false, false, false)]));
    }
}
