using System.Web;
using Gatto.Core.Acquire;
using Gatto.Core.Hardware;
using Gatto.Core.Models;
using Gatto.Tests.Setup;

namespace Gatto.Tests;

//which models the shelf shows, the walk back when nothing is fast, and the order, over a fake Hub
public class ShelfSearchAssembleTests
{
    private const long B = 1_000_000_000L;
    private static readonly Families F = Families.Load();
    private static readonly HashSet<string> Qwen = ["qwen"];
    private static readonly HashSet<string> Both = ["gemma", "qwen"];

    private static Task<ShelfOutcome> Run(FakeShelfHub hub, IReadOnlySet<string> lit, HardwareClass hw,
        bool lifted = false, CancellationToken ct = default, Families? families = null,
        int concurrency = HubSearch.HubConcurrency, HubTreeMemo? memo = null, TimeProvider? clock = null,
        IReadOnlyList<ModelRow>? landing = null) =>
        ShelfSearch.AssembleAsync(new HubClient(new HttpClient(hub) { Timeout = Timeout.InfiniteTimeSpan }),
            UploaderAllowlist.Load(), families ?? F, lit, hw, 8192, lifted, ct, concurrency, memo, clock: clock,
            landing: landing);

    //the search terms the fake was asked, one per releaser request
    private static List<string> Terms(FakeShelfHub hub) =>
        [.. hub.Urls.Select(u => HttpUtility.ParseQueryString(new Uri(u).Query)["search"]).OfType<string>()];

    //one qwen model in one generation: its source and unsloth's conversion
    private static FakeShelfHub QwenModel(FakeShelfHub hub, string name, long? total, double gb, string quant = "Q4_K_M") =>
        hub.Source("Qwen/" + name)
           .Conversion("Qwen/" + name, "unsloth/" + name + "-GGUF", total, 1000, "qwen35",
               files: (name + "-" + quant + ".gguf", gb));

    private static FakeShelfHub GemmaModel(FakeShelfHub hub, string name, long? total, double gb) =>
        hub.Source("google/" + name)
           .Conversion("google/" + name, "unsloth/" + name + "-GGUF", total, 1000, "gemma4",
               files: (name + "-Q4_K_M.gguf", gb));

    //the qwen generations as the reach-back meets them: 3.8 runs only in memory on the Vega, 3.6 fits nowhere, 3.5 has two fast models
    private static FakeShelfHub QwenLadder()
    {
        var hub = new FakeShelfHub();
        QwenModel(hub, "Qwen3.8-27B", 27 * B, 12);
        QwenModel(hub, "Qwen3.6-35B-A3B", 35 * B, 20);
        QwenModel(hub, "Qwen3.5-9B", 9 * B, 5.5);
        QwenModel(hub, "Qwen3.5-4B", 4 * B, 2.5);
        QwenModel(hub, "Qwen3.5-35B-A3B", 35 * B, 20);
        QwenModel(hub, "Qwen3-8B", 8 * B, 5);
        return hub;
    }

    [Fact]
    public async Task THE_LANDING_ON_THE_8060S()
    {
        var hub = QwenLadder();
        GemmaModel(hub, "gemma-4-31B-it", 31 * B, 18);
        var o = await Run(hub, Both, ShelfMachines.Apu8060S);
        Assert.Equal(["gemma-4-31B-it", "Qwen3.8-27B"], o.Rows.Select(r => r.Model).Order());
        Assert.All(o.Rows, r => Assert.Equal(FitRegime.FitsGpu, r.Fit));
        Assert.DoesNotContain("Qwen3.6", Terms(hub));
    }

    [Fact]
    public async Task THE_REACH_BACK_ON_THE_VEGA()
    {
        var hub = QwenLadder();
        var o = await Run(hub, Qwen, ShelfMachines.Vega);
        Assert.Equal(["Qwen3.5-9B", "Qwen3.8-27B"], o.Rows.Select(r => r.Model).Order());
        Assert.Equal(FitRegime.FitsRamOnly, o.Rows.Single(r => r.Model == "Qwen3.8-27B").Fit);
        Assert.Equal(["Qwen3.8", "Qwen3.8", "Qwen3.6", "Qwen3.6", "Qwen3.5", "Qwen3.5"], Terms(hub));
    }

    [Fact]
    public async Task THE_NO_CARD_MACHINE()
    {
        var hub = QwenLadder();
        var o = await Run(hub, Qwen, ShelfMachines.NoCard);
        Assert.Equal(["Qwen3.5-9B", "Qwen3.8-27B"], o.Rows.Select(r => r.Model).Order());
        //an older generation reads trees only of models under 20B
        Assert.DoesNotContain(hub.Urls, u => u.Contains("/tree/") && u.Contains("35B-A3B"));
    }

    //the mistral chip lists every line of its newest group, and the older group only when a lifts it
    [Fact]
    public async Task THE_MISTRAL_GROUP()
    {
        static FakeShelfHub Hub()
        {
            var hub = new FakeShelfHub();
            foreach (var (name, total) in new[] { ("Ministral-3-14B-Instruct-2512", 14 * B), ("Devstral-Small-2-24B-Instruct-2512", 24 * B),
                         ("Magistral-Small-2509", 24 * B), ("Mistral-Small-3.2-24B-Instruct-2506", 24 * B) })
                hub.Source("mistralai/" + name)
                   .Conversion("mistralai/" + name, "unsloth/" + name + "-GGUF", total, 100, "mistral3",
                       files: (name + "-Q4_K_M.gguf", total / 1e9 * 0.6));
            return hub;
        }
        HashSet<string> lit = ["mistral"];

        var hub = Hub();
        var o = await Run(hub, lit, ShelfMachines.Apu8060S);
        Assert.Equal(["Devstral-Small-2-24B-Instruct-2512", "Magistral-Small-2509", "Ministral-3-14B-Instruct-2512"],
            o.Rows.Select(r => r.Model).Order());
        Assert.Equal(14, Terms(hub).Count);

        var lifted = await Run(Hub(), lit, ShelfMachines.Apu8060S, lifted: true);
        Assert.Contains(lifted.Rows, r => r.Model == "Mistral-Small-3.2-24B-Instruct-2506");
    }

    //a card gatto cannot use is no card for the walk, so the under-20B rule applies
    [Fact]
    public async Task A_CARD_WITH_NO_BUDGET_IS_NO_CARD()
    {
        Assert.Equal(0UL, ShelfMachines.NoShare.GpuBudgetBytes);
        var hub = QwenLadder();
        var o = await Run(hub, Qwen, ShelfMachines.NoShare);
        Assert.Contains(o.Rows, r => r.Model == "Qwen3.5-9B");
        Assert.DoesNotContain(hub.Urls, u => u.Contains("/tree/") && u.Contains("35B-A3B"));
    }

    [Fact]
    public async Task PARAMS_NULL()
    {
        var hub = QwenLadder();
        QwenModel(hub, "Qwen3.8-9B", null, 5);
        QwenModel(hub, "Qwen3.8-30B", null, 10, "Q3_K_L");
        var o = await Run(hub, Qwen, ShelfMachines.NoCard);
        Assert.DoesNotContain(o.Rows, r => r.Model == "Qwen3.8-30B");
        Assert.Null(o.Rows.Single(r => r.Model == "Qwen3.8-9B").Params);
        Assert.Contains("Qwen3.6", Terms(hub));
    }

    //the releaser's own GGUF repo, listed by its own request with no quantized tag, leads its model's publishers
    [Fact]
    public async Task THE_RELEASER_IN_THE_PANE()
    {
        var hub = new FakeShelfHub();
        GemmaModel(hub, "gemma-4-31B-it", 31 * B, 18);
        hub.ReleaserGguf("google/gemma-4-31B-it-qat-q4_0-gguf", 31 * B, 50, "gemma4", ("gemma-4-31B_q4_0-it.gguf", 17));
        hub.Conversion("google/gemma-4-31B-it", "google/gemma-4-31B-it-qat-q4_0-gguf", 31 * B, 50, "gemma4",
            files: ("gemma-4-31B_q4_0-it.gguf", 17));
        var o = await Run(hub, new HashSet<string> { "gemma" }, ShelfMachines.Apu8060S);
        var row = o.Rows.Single(r => r.Model == "gemma-4-31B-it");
        Assert.Equal("google", row.Publishers[0].Org);
        Assert.Single(row.Publishers[0].Repos);
    }

    [Fact]
    public async Task A_GATED_CONVERSION_IS_LEFT_OUT_AND_A_REMOVED_ORG_IS_NOT_IN_THE_SHELF()
    {
        var hub = new FakeShelfHub();
        GemmaModel(hub, "gemma-4-31B-it", 31 * B, 18);
        hub.Conversion("google/gemma-4-31B-it", "bartowski/gemma-4-31B-it-GGUF", 31 * B, 9000, "gemma4", gated: true,
            files: ("g-Q4_K_M.gguf", 18));
        hub.Conversion("google/gemma-4-31B-it", "mradermacher/gemma-4-31B-it-GGUF", 31 * B, 9000, "gemma4",
            files: ("g-Q4_K_M.gguf", 18));
        var o = await Run(hub, new HashSet<string> { "gemma" }, ShelfMachines.Apu8060S);
        Assert.Equal(["unsloth"], o.Rows.Single().Publishers.Select(p => p.Org));
    }

    //a cancel during the conversion queries builds no row after it
    [Fact]
    public async Task A_CANCELLED_SEARCH_PRODUCES_NO_ROW()
    {
        var hub = QwenLadder();
        using var cts = new CancellationTokenSource();
        hub.OnRequest = (kind, n) => { if (kind == "conversions") cts.Cancel(); };
        var o = await Run(hub, Qwen, ShelfMachines.Apu8060S, ct: cts.Token);
        Assert.Empty(o.Rows);
        Assert.Equal(0, hub.Count("tree"));
    }

    //no card, 16 GiB and nothing under 20B fits: the walk lists every generation once and stops
    [Fact]
    public async Task NOTHING_FAST_ANYWHERE()
    {
        var hub = new FakeShelfHub();
        QwenModel(hub, "Qwen3.8-27B", 27 * B, 12);
        QwenModel(hub, "Qwen3.6-35B-A3B", 35 * B, 20);
        QwenModel(hub, "Qwen3.5-27B", 27 * B, 12);
        QwenModel(hub, "Qwen3-32B", 32 * B, 14);
        var o = await Run(hub, Qwen, ShelfMachines.NoCard16);
        Assert.Empty(o.Rows);
        Assert.Equal(HubSearchCause.NothingFits, o.Cause);
        Assert.Equal(["Qwen3.8", "Qwen3.8", "Qwen3.6", "Qwen3.6", "Qwen3.5", "Qwen3.5", "Qwen3", "Qwen3"], Terms(hub));
    }

    //the card group by total, largest first, then memory by the parameters read per token, smallest first, the unknown last
    [Fact]
    public async Task THE_ORDER()
    {
        var hub = new FakeShelfHub();
        GemmaModel(hub, "gemma-4-E4B-it", 8 * B, 5);
        GemmaModel(hub, "gemma-4-E2B-it", 5 * B, 3);
        GemmaModel(hub, "gemma-4-26B-A4B-it", 26 * B, 10);
        QwenModel(hub, "Qwen3.8-4B", 4 * B, 2.5);
        QwenModel(hub, "Qwen3.8-27B", 27 * B, 12);
        QwenModel(hub, "Qwen3.8-35B-A3B", 35 * B, 11);
        QwenModel(hub, "Qwen3.8-Flash-Next", 30 * B, 11);
        hub.Header("unsloth/Qwen3.8-Flash-Next-GGUF", "Qwen3.8-Flash-Next-Q4_K_M.gguf",
            GgufTestBytes.WithStructure(expertCount: 128, expertUsed: 8, arch: "qwen35"));
        var o = await Run(hub, Both, ShelfMachines.Vega);
        Assert.Equal(["gemma-4-E4B-it", "gemma-4-E2B-it", "Qwen3.8-4B",
                      "Qwen3.8-35B-A3B", "gemma-4-26B-A4B-it", "Qwen3.8-27B", "Qwen3.8-Flash-Next"],
            o.Rows.Select(r => r.Model));
        Assert.Equal((3, 4, 0), (o.OnCard, o.InMemory, o.TooBig));
    }

    [Fact]
    public async Task LIFTED()
    {
        var hub = QwenLadder();
        hub.Source("Qwen/Qwen3.5-2B");
        var o = await Run(hub, Qwen, ShelfMachines.Vega, lifted: true);
        //one total, two models: the newer generation first
        Assert.Equal(["Qwen3.5-9B", "Qwen3-8B", "Qwen3.5-4B", "Qwen3.8-27B", "Qwen3.6-35B-A3B", "Qwen3.5-35B-A3B"],
            o.Rows.Select(r => r.Model));
        Assert.Equal((3, 1, 2), (o.OnCard, o.InMemory, o.TooBig));
        Assert.DoesNotContain(o.Rows, r => r.Model == "Qwen3.5-2B");
    }

    //a asks every lit family's newest generation before any older one, so a stop cannot spend the window on one family
    [Fact]
    public async Task A_ASKS_GENERATION_BY_GENERATION()
    {
        var hub = QwenLadder();
        GemmaModel(hub, "gemma-4-31B-it", 31 * B, 18);
        await Run(hub, Both, ShelfMachines.Vega, lifted: true);
        Assert.Equal(["gemma-4", "gemma-4", "Qwen3.8", "Qwen3.8", "gemma-3n", "gemma-3n", "Qwen3.6", "Qwen3.6",
                      "gemma-3", "gemma-3", "Qwen3.5", "Qwen3.5", "Qwen3", "Qwen3"], Terms(hub));
    }

    //within one launch a lifts from the landing without asking its listings again, and a new launch asks them afresh
    [Fact]
    public async Task A_ASKS_NONE_OF_THE_LANDINGS_LISTINGS()
    {
        var hub = QwenLadder();
        var memo = new HubTreeMemo();
        var landing = await Run(hub, Qwen, ShelfMachines.Vega, memo: memo);
        var asked = Terms(hub).Count;
        var conversions = hub.Count("conversions");

        await Run(hub, Qwen, ShelfMachines.Vega, lifted: true, memo: memo, landing: landing.Rows);
        var lifted = Terms(hub).Skip(asked).ToList();
        Assert.DoesNotContain("Qwen3.8", lifted);
        Assert.DoesNotContain("Qwen3.6", lifted);
        Assert.DoesNotContain("Qwen3.5", lifted);
        Assert.Equal(["Qwen3", "Qwen3"], lifted);
        Assert.Equal(conversions + 1, hub.Count("conversions"));

        var fresh = QwenLadder();
        await Run(fresh, Qwen, ShelfMachines.Vega, memo: new HubTreeMemo());
        Assert.Contains("Qwen3.8", Terms(fresh));
    }

    //the window ending partway through a keeps every landing row, since a lifted search only adds
    [Fact]
    public async Task A_STOP_KEEPS_THE_LANDING()
    {
        var hub = QwenLadder();
        GemmaModel(hub, "gemma-4-31B-it", 31 * B, 18);
        GemmaModel(hub, "gemma-3n-E4B-it", 8 * B, 5);
        var memo = new HubTreeMemo();
        var landing = await Run(hub, Both, ShelfMachines.Vega, memo: memo);
        Assert.Equal(["gemma-3n-E4B-it", "Qwen3.5-9B", "Qwen3.8-27B"], landing.Rows.Select(r => r.Model).Order());

        hub.RateRemaining = ShelfSearch.Reserve + 2;
        var lifted = await Run(hub, Both, ShelfMachines.Vega, lifted: true, memo: memo, landing: landing.Rows);
        Assert.True(lifted.Cut);
        Assert.All(landing.Rows, l => Assert.Contains(lifted.Rows, r => r.Model == l.Model));
    }

    //each generation's rows read their headers before the next generation is asked, so a later deadline leaves them their kind
    [Fact]
    public async Task A_DEADLINE_LEAVES_EARLIER_ROWS_THEIR_KIND()
    {
        var hub = QwenLadder();
        using var cts = new CancellationTokenSource();
        hub.OnRequest = (kind, n) => { if (kind == "tree" && n == 2) cts.Cancel(); };
        var o = await Run(hub, Qwen, ShelfMachines.Vega, lifted: true, ct: cts.Token, concurrency: 1);
        Assert.True(o.Cut);
        Assert.Equal("dense", o.Rows.Single(r => r.Model == "Qwen3.8-27B").Structure);
    }

    [Fact]
    public async Task MORE_BEHIND_A()
    {
        var older = await Run(QwenLadder(), Qwen, ShelfMachines.Apu8060S);
        Assert.True(older.MoreBehindA);

        var one = F with { Entries = new Dictionary<string, FamilyEntry>(F.Entries, StringComparer.OrdinalIgnoreCase)
        {
            ["qwen"] = F.Entries["qwen"] with { Generations = [["Qwen3.8-"]] },
        } };
        var hub = new FakeShelfHub();
        QwenModel(hub, "Qwen3.8-27B", 27 * B, 12);
        Assert.False((await Run(hub, Qwen, ShelfMachines.Apu8060S, families: one)).MoreBehindA);

        //a model of the newest generation where nothing fits is not shown, and a would show it as too big
        var tooBig = new FakeShelfHub();
        QwenModel(tooBig, "Qwen3.8-4B", 4 * B, 2.5);
        QwenModel(tooBig, "Qwen3.8-27B", 27 * B, 20);
        var small = await Run(tooBig, Qwen, ShelfMachines.Vega, families: one);
        Assert.Equal(["Qwen3.8-4B"], small.Rows.Select(r => r.Model));
        Assert.True(small.MoreBehindA);

        //a file under the floor is already in the pane, so it promises nothing behind a
        var under = new FakeShelfHub();
        under.Source("Qwen/Qwen3.8-27B").Conversion("Qwen/Qwen3.8-27B", "unsloth/Qwen3.8-27B-GGUF", 27 * B, 1, "qwen35",
            files: [("q-Q4_K_M.gguf", 12), ("q-IQ2_XXS.gguf", 7)]);
        Assert.False((await Run(under, Qwen, ShelfMachines.Apu8060S, families: one)).MoreBehindA);
    }

    //under a the row's file keeps the floor: a model whose only fitting file is under it shows its smallest file above it as too big
    [Fact]
    public async Task A_KEEPS_THE_FLOOR()
    {
        var one = F with { Entries = new Dictionary<string, FamilyEntry>(F.Entries, StringComparer.OrdinalIgnoreCase)
        {
            ["qwen"] = F.Entries["qwen"] with { Generations = [["Qwen3.8-"]] },
        } };
        var hub = new FakeShelfHub();
        hub.Source("Qwen/Qwen3.8-235B-A22B").Conversion("Qwen/Qwen3.8-235B-A22B", "unsloth/Qwen3.8-235B-A22B-GGUF",
            235 * B, 1, "qwen35", files: [("v-IQ1_S.gguf", 12), ("v-Q3_K_M.gguf", 30), ("v-Q4_K_M.gguf", 40)]);
        hub.Source("Qwen/Qwen3.8-1T").Conversion("Qwen/Qwen3.8-1T", "unsloth/Qwen3.8-1T-GGUF",
            1000 * B, 1, "qwen35", files: ("t-IQ2_XXS.gguf", 12));

        var plain = await Run(hub, Qwen, ShelfMachines.Vega, families: one);
        Assert.Empty(plain.Rows);
        Assert.True(plain.MoreBehindA);

        var o = await Run(hub, Qwen, ShelfMachines.Vega, lifted: true, families: one);
        var row = Assert.Single(o.Rows);
        Assert.Equal("v-Q3_K_M.gguf", row.RowFile!.Path);
        Assert.Equal(FitRegime.DoesNotFit, row.Fit);
        Assert.Contains(row.Publishers[0].Repos[0].Quants, q => q.FileName == "v-IQ1_S.gguf");
    }

    //a memory row with no A-term in its key reads its row file's header: dense gives its total, MoE A3B gives 3B, a bare MoE gives nothing
    [Fact]
    public async Task NO_KEY_A_TERM_READS_THE_HEADER()
    {
        var hub = new FakeShelfHub();
        QwenModel(hub, "Qwen3.8-4B", 4 * B, 2.5);
        QwenModel(hub, "Qwen3.8-27B", 27 * B, 12);
        QwenModel(hub, "Qwen3.8-Coder", 30 * B, 11);
        QwenModel(hub, "Qwen3.8-Flash-Next", 30 * B, 11);
        hub.Header("unsloth/Qwen3.8-Coder-GGUF", "Qwen3.8-Coder-Q4_K_M.gguf",
            GgufTestBytes.WithStructure(expertCount: 128, expertUsed: 8, sizeLabel: "30B-A3B", arch: "qwen35"));
        hub.Header("unsloth/Qwen3.8-Flash-Next-GGUF", "Qwen3.8-Flash-Next-Q4_K_M.gguf",
            GgufTestBytes.WithStructure(expertCount: 128, expertUsed: 8, arch: "qwen35"));
        var o = await Run(hub, Qwen, ShelfMachines.Vega);
        Assert.Equal(27 * B, o.Rows.Single(r => r.Model == "Qwen3.8-27B").Active);
        Assert.Equal(3 * B, o.Rows.Single(r => r.Model == "Qwen3.8-Coder").Active);
        Assert.Null(o.Rows.Single(r => r.Model == "Qwen3.8-Flash-Next").Active);
        Assert.Equal("dense", o.Rows.Single(r => r.Model == "Qwen3.8-27B").Structure);
        Assert.Equal(o.Rows.Count, hub.Count("file"));
    }

    private static string Shape(ModelRow r) =>
        $"{r.Model}|{r.RowFile}|{r.Fit}|{string.Join(",", r.Publishers.Select(p => p.Org))}";

    //one source's query failing costs that model only, and the shelf is not an outage
    [Fact]
    public async Task ONE_SOURCE_FAILS()
    {
        static FakeShelfHub Hub()
        {
            var hub = QwenLadder();
            QwenModel(hub, "Qwen3.8-4B", 4 * B, 2.5);
            return hub;
        }
        var whole = await Run(Hub(), Qwen, ShelfMachines.Apu8060S);
        var hub = Hub();
        hub.Fail = u => Uri.UnescapeDataString(u).Contains("quantized:Qwen/Qwen3.8-4B");
        var o = await Run(hub, Qwen, ShelfMachines.Apu8060S);
        Assert.DoesNotContain(o.Rows, r => r.Model == "Qwen3.8-4B");
        Assert.Equal(whole.Rows.Where(r => r.Model != "Qwen3.8-4B").Select(Shape), o.Rows.Select(Shape));
        Assert.Null(o.Cause);
    }

    [Fact]
    public async Task EVERY_REQUEST_FAILS()
    {
        var hub = QwenLadder();
        hub.Fail = _ => true;
        var o = await Run(hub, Qwen, ShelfMachines.Apu8060S);
        Assert.Empty(o.Rows);
        Assert.Equal(HubSearchCause.HubFailed, o.Cause);
    }

    //the deadline after the listings and during the trees keeps what is priced and says the search stopped
    [Fact]
    public async Task THE_DEADLINE_DURING_THE_TREES()
    {
        var hub = new FakeShelfHub();
        GemmaModel(hub, "gemma-4-31B-it", 31 * B, 18);
        GemmaModel(hub, "gemma-4-E4B-it", 8 * B, 5);
        GemmaModel(hub, "gemma-4-E2B-it", 5 * B, 3);
        using var cts = new CancellationTokenSource();
        hub.OnRequest = (kind, n) => { if (kind == "tree" && n == 2) cts.Cancel(); };
        var o = await Run(hub, new HashSet<string> { "gemma" }, ShelfMachines.Apu8060S, ct: cts.Token, concurrency: 1);
        Assert.Single(o.Rows);
        Assert.True(o.Cut);
        //the deadline knows no reset, so the count row says the search stopped and never that Hugging Face asked gatto to wait
        Assert.False(o.RateLimited);
        Assert.Null(o.RateLimitedFor);
        Assert.NotEqual(HubSearchCause.HubFailed, o.Cause);
    }

    //the search stops before the api window's remaining count reaches the reserve, and says how long the server asks it to wait
    [Fact]
    public async Task THE_RESERVE_STOPS_THE_SEARCH()
    {
        var hub = QwenLadder();
        hub.RateRemaining = 25;
        var o = await Run(hub, Qwen, ShelfMachines.Vega, concurrency: 1);
        Assert.True(hub.MinRemaining >= ShelfSearch.Reserve);
        Assert.Contains(o.Rows, r => r.Model == "Qwen3.8-27B");
        Assert.True(o.Cut);
        //the reserve knows the window's reset, so the count row says how long to wait
        Assert.True(o.RateLimited);
        Assert.Equal(120, o.RateLimitedFor);
    }

    [Fact]
    public async Task A_429_MID_SEARCH()
    {
        var hub = new FakeShelfHub();
        for (var i = 1; i <= 12; i++) GemmaModel(hub, $"gemma-4-{i}B-it", i * B, i * 0.6);
        hub.TooMany = ("tree", 10);
        var o = await Run(hub, new HashSet<string> { "gemma" }, ShelfMachines.Apu8060S, concurrency: 1);
        Assert.Equal(9, o.Rows.Count);
        Assert.Equal(230, o.RateLimitedFor);
        Assert.True(o.Cut);
        Assert.NotEqual(HubSearchCause.HubFailed, o.Cause);
    }

    //a 429 that names no reset still stops the search and says so, with no seconds to count down
    [Fact]
    public async Task A_429_WITH_NO_RESET()
    {
        var hub = new FakeShelfHub();
        for (var i = 1; i <= 3; i++) GemmaModel(hub, $"gemma-4-{i}B-it", i * B, i * 0.6);
        hub.TooMany = ("tree", 2);
        hub.TooManyBare = true;
        var o = await Run(hub, new HashSet<string> { "gemma" }, ShelfMachines.Apu8060S, concurrency: 1);
        Assert.True(o.RateLimited);
        Assert.Null(o.RateLimitedFor);
        Assert.True(o.Cut);
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    //an older generation's conversion listings are kept on disk for a day, and the newest is always asked
    [Fact]
    public async Task OLDER_GENERATIONS_COME_FROM_DISK()
    {
        var home = Directory.CreateTempSubdirectory("gatto-shelf-").FullName;
        try
        {
            var clock = new Clock(new DateTimeOffset(2026, 10, 7, 9, 0, 0, TimeSpan.Zero));
            var first = QwenLadder();
            await Run(first, Qwen, ShelfMachines.Vega, lifted: true, memo: new HubTreeMemo(new HubReadStore(home)), clock: clock);
            Assert.Equal(6, first.Count("conversions"));

            clock.Now = clock.Now.AddHours(1);
            var second = QwenLadder();
            var o = await Run(second, Qwen, ShelfMachines.Vega, lifted: true, memo: new HubTreeMemo(new HubReadStore(home)), clock: clock);
            Assert.Equal(1, second.Count("conversions"));
            Assert.Contains(second.Urls, u => Uri.UnescapeDataString(u).Contains("quantized:Qwen/Qwen3.8-27B"));
            Assert.Equal(6, o.Rows.Count);

            clock.Now = clock.Now.AddDays(1);
            var third = QwenLadder();
            await Run(third, Qwen, ShelfMachines.Vega, lifted: true, memo: new HubTreeMemo(new HubReadStore(home)), clock: clock);
            Assert.Equal(6, third.Count("conversions"));
        }
        finally { Directory.Delete(home, recursive: true); }
    }

    private static HubClient Client(FakeShelfHub hub) => new(new HttpClient(hub) { Timeout = Timeout.InfiniteTimeSpan });

    //a typed id whose every file is under the floor still comes back, every file listed, so the pane can choose one
    [Fact]
    public async Task A_TYPED_ID_UNDER_THE_FLOOR()
    {
        var hub = new FakeShelfHub().Untagged("bartowski/odd-27B-GGUF", 27 * B, 5, "qwen35",
            ("odd-IQ2_XXS.gguf", 8), ("odd-IQ2_M.gguf", 9));
        var (row, noWeights) = await HubSearch.LookupModelAsync(Client(hub), "bartowski/odd-27B-GGUF",
            ShelfMachines.Apu8060S, 8192, CancellationToken.None);
        Assert.False(noWeights);
        Assert.Null(row!.Publishers[0].Pick);
        Assert.Equal(2, row.Publishers[0].Repos[0].Quants.Count);

        //the lifted re-price takes only a file whose name holds its token, so the consent screen's file name names the quant
        var lifted = ShelfSearch.Lifted(row, ShelfMachines.Apu8060S, 8192);
        Assert.NotNull(lifted.RowQuant);
        Assert.NotNull(QuantToken.Of(lifted.RowQuant!.FileName));
    }

    //a typed id's row is named for its model, the source when the repo is tagged, and reads its kind from its header as every shelf row does
    [Fact]
    public async Task A_TYPED_ID_S_ROW_HAS_THE_MODEL_S_NAME_AND_ITS_KIND()
    {
        var tagged = new FakeShelfHub().Source("Qwen/Qwen3.5-9B")
            .Conversion("Qwen/Qwen3.5-9B", "unsloth/Qwen3.5-9B-GGUF", 9 * B, 5, "qwen35", files: ("q-Q4_K_M.gguf", 5.5));
        var (row, _) = await HubSearch.LookupModelAsync(Client(tagged), "unsloth/Qwen3.5-9B-GGUF",
            ShelfMachines.Apu8060S, 8192, CancellationToken.None);
        Assert.Equal("Qwen3.5-9B", row!.Model);
        Assert.Equal("dense", row.Structure);

        var untagged = new FakeShelfHub().Untagged("bartowski/ddh0_gemma-3-40b-GGUF", 40 * B, 5, "gemma3",
            ("g-Q6_K.gguf", 30.4));
        var (named, _) = await HubSearch.LookupModelAsync(Client(untagged), "bartowski/ddh0_gemma-3-40b-GGUF",
            ShelfMachines.Apu8060S, 8192, CancellationToken.None);
        Assert.Equal("ddh0_gemma-3-40b", named!.Model);
    }

    //an untagged repo in a typed search is named for its repo less the -GGUF a conversion adds, and the org inside the name stays
    [Fact]
    public async Task A_TYPED_SEARCH_DROPS_THE_GGUF_SUFFIX()
    {
        var hub = new FakeShelfHub().Untagged("bartowski/ddh0_gemma-3-40b-GGUF", 40 * B, 5, "gemma3", ("g-Q6_K.gguf", 30.4));
        var o = await HubSearch.TypedSearchAsync(Client(hub), UploaderAllowlist.Load(), F, "gemma-3",
            ShelfMachines.Apu8060S, 8192, CancellationToken.None);
        Assert.Equal(["ddh0_gemma-3-40b"], o.Rows.Select(r => r.Model));
    }

    //the Hub's loose match brings a gemma 3 repo to a gemma-4 search, and only the names that hold the typed word stay, separators and case aside
    [Fact]
    public async Task A_TYPED_SEARCH_KEEPS_ONLY_NAMES_THAT_HOLD_EVERY_WORD()
    {
        static FakeShelfHub Hub() => new FakeShelfHub { LooseSearch = true }
            .Untagged("bartowski/ddh0_gemma-3-40b-GGUF", 40 * B, 50, "gemma3", ("a-Q4_K_M.gguf", 20))
            .Untagged("bartowski/Gemma4-31b-Gembrain-Equinox-GGUF", 31 * B, 40, "gemma4", ("b-Q4_K_M.gguf", 18))
            .Untagged("unsloth/gemma-4-31B-it-GGUF", 31 * B, 30, "gemma4", ("c-Q4_K_M.gguf", 18));

        var one = await HubSearch.TypedSearchAsync(Client(Hub()), UploaderAllowlist.Load(), F, "gemma-4",
            ShelfMachines.Apu8060S, 8192, CancellationToken.None);
        Assert.Equal(["Gemma4-31b-Gembrain-Equinox", "gemma-4-31B-it"], one.Rows.Select(r => r.Model).Order(StringComparer.Ordinal));

        var two = await HubSearch.TypedSearchAsync(Client(Hub()), UploaderAllowlist.Load(), F, "gemma-4 it",
            ShelfMachines.Apu8060S, 8192, CancellationToken.None);
        Assert.Equal(["gemma-4-31B-it"], two.Rows.Select(r => r.Model));
    }

    //the org is not part of the match, so typing an org's name keeps none of that org's other models
    [Fact]
    public void THE_ORG_IS_NOT_PART_OF_THE_MATCH()
    {
        Assert.False(HubSearch.NameHoldsEveryWord("unsloth/gemma-4-31B-it-GGUF", "unsloth"));
        Assert.True(HubSearch.NameHoldsEveryWord("unsloth/gemma-4-31B-it-GGUF", "GEMMA_4 31b"));
        Assert.False(HubSearch.NameHoldsEveryWord("bartowski/ddh0_gemma-3-40b-GGUF", "gemma-4"));
    }

    [Fact]
    public async Task THE_TYPED_ID_IS_ONE_PUBLISHER()
    {
        var hub = new FakeShelfHub().Untagged("unsloth/Qwen3.5-9B-GGUF", 9 * B, 5, "qwen35", ("q-Q4_K_M.gguf", 5.5));
        var (row, _) = await HubSearch.LookupModelAsync(Client(hub), "unsloth/Qwen3.5-9B-GGUF",
            ShelfMachines.Apu8060S, 8192, CancellationToken.None);
        var publisher = Assert.Single(row!.Publishers);
        Assert.Single(publisher.Repos);
        Assert.Equal("q-Q4_K_M.gguf", row.RowFile!.Path);
    }

    //a typed search keeps the floor under a too: a adds the model too big above the floor, and a model all under it stays out
    [Fact]
    public async Task A_TYPED_SEARCH_UNDER_THE_FLOOR()
    {
        static FakeShelfHub Hub() => new FakeShelfHub()
            .Untagged("unsloth/tiny-heretic-GGUF", 27 * B, 50, "qwen35", ("t-IQ2_XXS.gguf", 8))
            .Untagged("unsloth/huge-heretic-GGUF", 270 * B, 45, "qwen35", ("h-IQ1_S.gguf", 8), ("h-Q3_K_M.gguf", 120))
            .Untagged("unsloth/fine-heretic-GGUF", 9 * B, 40, "qwen35", ("f-Q4_K_M.gguf", 5.5));
        var plain = await HubSearch.TypedSearchAsync(Client(Hub()), UploaderAllowlist.Load(), F, "heretic",
            ShelfMachines.Apu8060S, 8192, CancellationToken.None);
        Assert.Equal(["fine-heretic"], plain.Rows.Select(r => r.Model));
        Assert.True(plain.MoreBehindA);

        var lifted = await HubSearch.TypedSearchAsync(Client(Hub()), UploaderAllowlist.Load(), F, "heretic",
            ShelfMachines.Apu8060S, 8192, CancellationToken.None, lifted: true);
        Assert.Equal(["fine-heretic", "huge-heretic"], lifted.Rows.Select(r => r.Model));
        Assert.Equal("h-Q3_K_M.gguf", lifted.Rows[1].RowFile!.Path);
        Assert.False(lifted.MoreBehindA);
    }

    //a deadline that cuts the header reads leaves the priced rows their kind, read after it on a bound of their own
    [Fact]
    public async Task A_CUT_SEARCH_S_ROWS_STILL_GET_THEIR_KIND()
    {
        var hub = new FakeShelfHub();
        GemmaModel(hub, "gemma-4-31B-it", 31 * B, 18);
        GemmaModel(hub, "gemma-4-E4B-it", 8 * B, 5);
        using var cts = new CancellationTokenSource();
        hub.OnRequest = (kind, n) => { if (kind == "file" && n == 1) cts.Cancel(); };
        var o = await Run(hub, new HashSet<string> { "gemma" }, ShelfMachines.Apu8060S, ct: cts.Token, concurrency: 1);

        Assert.True(o.Cut);
        Assert.Equal(2, o.Rows.Count);
        Assert.All(o.Rows, r => Assert.Equal("dense", r.Structure));
    }

    //a caller that cancels, a leave or a restarted search, reads no header after the cut, since nobody will read what it brings
    [Fact]
    public async Task A_CALLER_CANCEL_READS_NO_HEADER_AFTER_THE_CUT()
    {
        var hub = new FakeShelfHub();
        GemmaModel(hub, "gemma-4-31B-it", 31 * B, 18);
        GemmaModel(hub, "gemma-4-E4B-it", 8 * B, 5);
        using var caller = new CancellationTokenSource();
        hub.OnRequest = (kind, n) => { if (kind == "file" && n == 1) caller.Cancel(); };
        var o = await ShelfSearch.AssembleAsync(Client(hub), UploaderAllowlist.Load(), F,
            new HashSet<string> { "gemma" }, ShelfMachines.Apu8060S, 8192, false, caller.Token, concurrency: 1,
            caller: caller.Token);

        Assert.True(o.Cut);
        Assert.Equal(1, hub.Count("file"));
        Assert.All(o.Rows, r => Assert.Null(r.Structure));

        var typedHub = new FakeShelfHub()
            .Untagged("unsloth/fine-heretic-GGUF", 9 * B, 40, "qwen35", ("f-Q4_K_M.gguf", 5.5))
            .Untagged("unsloth/other-heretic-GGUF", 4 * B, 30, "qwen35", ("o-Q4_K_M.gguf", 2.5));
        using var typedCaller = new CancellationTokenSource();
        typedHub.OnRequest = (kind, n) => { if (kind == "file" && n == 1) typedCaller.Cancel(); };
        var typed = await HubSearch.TypedSearchAsync(Client(typedHub), UploaderAllowlist.Load(), F, "heretic",
            ShelfMachines.Apu8060S, 8192, typedCaller.Token, caller: typedCaller.Token);
        Assert.Contains(typed.Rows, r => r.Structure is null);
    }

    [Fact]
    public async Task A_CUT_TYPED_SEARCH_S_ROWS_STILL_GET_THEIR_KIND()
    {
        var hub = new FakeShelfHub()
            .Untagged("unsloth/fine-heretic-GGUF", 9 * B, 40, "qwen35", ("f-Q4_K_M.gguf", 5.5))
            .Untagged("unsloth/other-heretic-GGUF", 4 * B, 30, "qwen35", ("o-Q4_K_M.gguf", 2.5));
        using var cts = new CancellationTokenSource();
        hub.OnRequest = (kind, n) => { if (kind == "file" && n == 1) cts.Cancel(); };
        var o = await HubSearch.TypedSearchAsync(Client(hub), UploaderAllowlist.Load(), F, "heretic",
            ShelfMachines.Apu8060S, 8192, cts.Token);

        Assert.True(o.Cut);
        Assert.All(o.Rows, r => Assert.Equal("dense", r.Structure));
    }

    //a typed search reads each shown row's header, so its kind cell is filled as the shelf's is
    [Fact]
    public async Task A_TYPED_SEARCH_READS_THE_KIND()
    {
        var hub = new FakeShelfHub().Untagged("unsloth/fine-heretic-GGUF", 9 * B, 40, "qwen35", ("f-Q4_K_M.gguf", 5.5));
        var o = await HubSearch.TypedSearchAsync(Client(hub), UploaderAllowlist.Load(), F, "heretic",
            ShelfMachines.Apu8060S, 8192, CancellationToken.None);
        Assert.Equal("dense", o.Rows.Single().Structure);
        Assert.Equal(1, hub.Count("file"));
    }

    //a typed search keeps the per-org listing, groups repos by their quantized source, and says which org came back full
    [Fact]
    public async Task THE_TYPED_SEARCH_GROUPS_BY_SOURCE()
    {
        var hub = new FakeShelfHub()
            .Conversion("someone/Qwen3.5-9B-heretic", "unsloth/Qwen3.5-9B-heretic-GGUF", 9 * B, 500, "qwen35",
                files: ("h-Q4_K_M.gguf", 5.5))
            .Conversion("someone/Qwen3.5-9B-heretic", "bartowski/someone_Qwen3.5-9B-heretic-GGUF", 9 * B, 300, "qwen35",
                files: ("h-Q4_K_M.gguf", 5.5))
            .Untagged("bartowski/gemma-heretic-GGUF", 4 * B, 100, "gemma4", ("g-Q4_K_M.gguf", 2.5))
            .Pad("bartowski", "heretic", 998);
        var o = await HubSearch.TypedSearchAsync(Client(hub), UploaderAllowlist.Load(), F, "heretic",
            ShelfMachines.Apu8060S, 8192, CancellationToken.None);
        var heretic = o.Rows.Single(r => r.Model == "Qwen3.5-9B-heretic");
        Assert.Equal(["unsloth", "bartowski"], heretic.Publishers.Select(p => p.Org));
        Assert.Contains(o.Rows, r => r.Model == "gemma-heretic");
        Assert.Equal(["bartowski"], o.FullPageOrgs);
        Assert.True(hub.Count("tree") <= HubSearch.MaxTreeCalls);
    }
}
