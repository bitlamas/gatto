using System.Net;
using Gatto.Core.Acquire;
using Gatto.Core.Hardware;

namespace Gatto.Tests;

//a sidecar must never be a candidate, since Pick takes the graphics tier first and a 0.86 GB file would beat every real quant
public class SidecarCandidateTests
{
    //three sidecars sit in an MTP subfolder, so the rule must be read from the file name rather than the path
    private static readonly (string Path, long Bytes)[] Gemma4 =
    [
        ("MTP/mtp-gemma-4-26B-A4B-it-Q8_0.gguf",  460_000_000L),
        ("mtp-gemma-4-26B-A4B-it.gguf",           460_000_000L),
        ("MTP/mtp-gemma-4-26B-A4B-it-BF16.gguf",  860_000_000L),
        ("MTP/mtp-gemma-4-26B-A4B-it-F16.gguf",   860_000_000L),
        ("mmproj-F16.gguf",                     1_190_000_000L),
        ("gemma-4-26B-A4B-it-UD-IQ2_XXS.gguf",  9_920_000_000L),
        ("gemma-4-26B-A4B-it-UD-Q3_K_M.gguf",  12_730_000_000L),
        ("gemma-4-26B-A4B-it-UD-Q4_K_S.gguf",  16_490_000_000L),
        ("gemma-4-26B-A4B-it-UD-Q6_K.gguf",    23_170_000_000L),
    ];

    //the product-level oracle: what the user is offered

    [Theory]
    [InlineData(6_000_000_000UL,  "6 GB discrete")]
    [InlineData(8_000_000_000UL,  "8 GB discrete")]
    [InlineData(12_000_000_000UL, "12 GB discrete")]
    [InlineData(1_000_000_000UL,  "no real GPU budget")]
    public async Task A_DRAFT_HEAD_IS_NEVER_OFFERED_AS_THE_MODEL(ulong vram, string label)
    {
        //the oracle is the offered file, since a guard on the candidate count would pass a build that still picked the sidecar
        var row = await Pick(Gemma4, vram, 32_000_000_000UL);

        Assert.NotNull(row);
        Assert.DoesNotContain("mtp", row!.PickedQuant.FileName, StringComparison.OrdinalIgnoreCase);
        //stated positively, so the guard can't be satisfied by picking nothing at all
        Assert.True(row.PickedQuant.Bytes > 5_000_000_000L,
            $"[{label}] offered {row.PickedQuant.FileName} at {row.PickedQuant.Bytes / 1e9:F2} GB — "
            + "a file that small cannot be this 26B model");
    }

    //each sidecar kind, with the measured row it was found in

    [Theory]
    [InlineData("mtp-Qwen3.8-27B-Q4_0.gguf", "unsloth/Qwen3.8-27B-GGUF — 81 files across the census")]
    [InlineData("mtp-Qwen3.8-27B-BF16.gguf", "ggml-org/Qwen3.8-27B — 5.95 GB, which no size rule catches")]
    [InlineData("mtp-gemma-4-26B-A4B-it.gguf", "unsloth/gemma-4-26B-A4B-it — no quant token at all")]
    [InlineData("dflash-kquant.gguf", "unsloth/Muse-Glimmer-30B — 1.63 GB for a 30B model is 0.43 bits/param")]
    [InlineData("dflash-Qwen3.6-35B-A3B-Q8_0.gguf", "ggml-org/Qwen3.6-35B-A3B")]
    [InlineData("eagle3-gpt-oss-120b-BF16.gguf", "ggml-org/gpt-oss-120b")]
    [InlineData("imatrix_unsloth.gguf", "unsloth, every imatrix repo")]
    [InlineData("Qwen_Qwen3.6-35B-A3B-imatrix.gguf", "bartowski's convention — the token is at the END")]
    [InlineData("Llama3.3-8B-Instruct-Heretic.imatrix.gguf", "mradermacher's convention — dot-separated")]
    public void A_MEASURED_SIDECAR_KIND_IS_NOT_A_CANDIDATE(string fileName, string provenance) =>
        Assert.True(ModelDiscovery.IsCompanionArtifact(fileName), provenance);

    [Theory]
    //the predicate reads a file name, so a repo id whose name holds the token must never be handed to it
    [InlineData("Qwen3.5-35B-A3B-UD-IQ4_XS.gguf")]
    [InlineData("gemma-4-26B-A4B-it-UD-Q4_K_M.gguf")]
    [InlineData("Qwen3.8-27B-Q8_0.gguf")]
    [InlineData("model-Q4_K_M.gguf")]
    //a token in the middle is part of the model name, so this row keeps such a model on the shelf
    [InlineData("Qwen3.5-35B-A3B-MTP-UD-Q4_K_M.gguf")]
    [InlineData("gemma-4-26B-A4B-it-mtp-tuned-Q4_K_M.gguf")]
    public void A_REAL_QUANT_IS_NEVER_MISTAKEN_FOR_A_SIDECAR(string fileName) =>
        Assert.False(ModelDiscovery.IsCompanionArtifact(fileName));

    //the drift: two copies of one convention

    [Theory]
    [InlineData("mmproj-F16.gguf", "the prefix form — the only one the tree walk used to know")]
    [InlineData("gemma-4-31B-it-mmproj.gguf", "google/gemma-4-31B-it-qat-q4_0-gguf")]
    [InlineData("Ministral-3-8B-Instruct-2512-BF16-mmproj.gguf", "mistralai/Ministral-3-8B-Instruct-2512")]
    [InlineData("Qwen3-VL-8B-Instruct-abliterated.mmproj-Q8_0.gguf", "mradermacher — dot-separated")]
    public async Task AN_MMPROJ_IS_A_PROJECTOR_WHEREVER_THE_NAME_CARRIES_IT(string name, string provenance)
    {
        //the tree asks IsProjector rather than restating the mmproj convention, which drifted between two copies
        var tree = await Tree([(name, 1_190_000_000L), ("model-UD-Q4_K_M.gguf", 16_000_000_000L)]);

        Assert.True(tree.HasProjector, provenance);
        Assert.Equal("model-UD-Q4_K_M.gguf", Assert.Single(tree.Quants).FileName);
        Assert.Equal(name, Assert.Single(tree.Projectors).FileName);
    }

    [Fact]
    public async Task THE_SIDECARS_ARE_DROPPED_RATHER_THAN_COUNTED_AS_VISION()
    {
        //a draft head must not count as a projector, or the tree would claim vision a file knows nothing about
        var tree = await Tree(Gemma4);

        Assert.All(tree.Quants, q =>
            Assert.False(ModelDiscovery.IsCompanionArtifact(q.FileName), $"{q.FileName} is a sidecar"));
        Assert.All(tree.Projectors, p => Assert.Contains("mmproj", p.FileName, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(4, tree.Quants.Count);      //the sidecars are dropped, so four quants remain
        Assert.True(tree.HasProjector);
    }

    //5.95 GB against 27.3 B parameters passes the floor, so only the name excludes it
    [Fact]
    public async Task A_DRAFT_HEAD_THE_ARITHMETIC_CANNOT_SEE_IS_STILL_NEVER_OFFERED()
    {
        Assert.False(ModelDiscovery.IsTooSmallToBeQuantization(5_950_000_000L, 27_300_000_000L),
            "if this ever becomes true the guard below stops testing the NAME rule");

        var row = await Pick(
            [("mtp-Qwen3.8-27B-BF16.gguf", 5_950_000_000L),
             ("Qwen3.8-27B-UD-Q4_K_M.gguf", 16_500_000_000L)],
            8_000_000_000UL, 32_000_000_000UL, prms: 27_300_000_000L);

        Assert.NotNull(row);
        Assert.Equal("Qwen3.8-27B-UD-Q4_K_M.gguf", row!.PickedQuant.FileName);
    }

    //the second signal: the arithmetic floor

    //both the shelf and the typed id door reach the floor in Pick, so each site is driven here
    [Fact]
    public async Task A_FILE_TOO_SMALL_TO_BE_THIS_MODEL_IS_NOT_A_CANDIDATE_via_the_typed_id_door()
    {
        //0.5 GB for 26 B parameters is 0.15 bits per weight, and no quantization goes below ~1.5
        var row = await Pick(
            [("flashdraft-gemma-4-26B-BF16.gguf", 500_000_000L),
             ("gemma-4-26B-A4B-it-UD-Q3_K_M.gguf", 12_730_000_000L)],
            6_000_000_000UL, 32_000_000_000UL);

        Assert.NotNull(row);
        Assert.Equal("gemma-4-26B-A4B-it-UD-Q3_K_M.gguf", row!.PickedQuant.FileName);
        //the name rule cannot see this file either, asserted so the row can't become a second sidecar guard later
        Assert.False(ModelDiscovery.IsCompanionArtifact("flashdraft-gemma-4-26B-BF16.gguf"));
    }

    [Fact]
    public async Task A_FILE_TOO_SMALL_TO_BE_THIS_MODEL_IS_NOT_A_CANDIDATE_via_the_browse_walk()
    {
        var hub = new BrowseHandler();
        var client = new HubClient(new HttpClient(hub) { Timeout = Timeout.InfiniteTimeSpan });
        var outcome = await HubSearch.AssembleAsync(client, new UploaderAllowlist("2026-08-23", ["unsloth"]),
            new HardwareClass(MemoryTopology.Discrete, ShareKind.None, 6_000_000_000UL, 32_000_000_000UL,
                new HardwareSnapshot(0UL, 1UL, GpuKind.Discrete, 0UL), 0, BudgetBound.None),
            4096, _ => null, CancellationToken.None);

        var row = Assert.Single(outcome.Rows);
        Assert.Equal("gemma-4-26B-A4B-it-UD-Q3_K_M.gguf", row.PickedQuant.FileName);
    }

    [Fact]
    public void THE_FLOOR_IS_SILENT_WHEN_THE_REPO_DID_NOT_SAY_HOW_BIG_THE_MODEL_IS()
    {
        //an absent total is not zero, so the floor stays silent and the name rule covers the case
        Assert.False(ModelDiscovery.IsTooSmallToBeQuantization(500_000_000L, null));
        Assert.False(ModelDiscovery.IsTooSmallToBeQuantization(500_000_000L, 0L));
        //half a bit per weight is the boundary
        Assert.True(ModelDiscovery.IsTooSmallToBeQuantization(26_000_000_000L / 16 - 1, 26_000_000_000L));
        Assert.False(ModelDiscovery.IsTooSmallToBeQuantization(26_000_000_000L / 16, 26_000_000_000L));
        //a real IQ1_M of a 26B model is ~1.6 bits per weight, far above the floor
        Assert.False(ModelDiscovery.IsTooSmallToBeQuantization(5_200_000_000L, 26_000_000_000L));
    }

    private sealed class BrowseHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var url = r.RequestUri!.ToString();
            var body = url.Contains("/tree/main")
                ? Json([("flashdraft-gemma-4-26B-BF16.gguf", 500_000_000L),
                        ("gemma-4-26B-A4B-it-UD-Q3_K_M.gguf", 12_730_000_000L)])
                : "[" + ModelJson + "]";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
        }
    }

    //harness

    private static string Json((string Path, long Bytes)[] files) => "[" + string.Join(",", files.Select(f =>
        $"{{\"type\":\"file\",\"path\":\"{f.Path}\",\"size\":{f.Bytes},\"lfs\":{{\"oid\":\"abc\"}}}}")) + "]";

    private static string ModelJsonFor(long prms) =>
        "{\"id\":\"unsloth/gemma-4-26B-A4B-it-GGUF\",\"downloads\":1202107,\"gated\":false,"
        + "\"pipeline_tag\":\"image-text-to-text\","
        + "\"gguf\":{\"architecture\":\"gemma4\",\"context_length\":262144,\"total\":" + prms + "}}";

    private const string ModelJson =
        "{\"id\":\"unsloth/gemma-4-26B-A4B-it-GGUF\",\"downloads\":1202107,\"gated\":false,"
        + "\"pipeline_tag\":\"image-text-to-text\","
        + "\"gguf\":{\"architecture\":\"gemma4\",\"context_length\":262144,\"total\":26000000000}}";

    private static HubClient Client((string Path, long Bytes)[] files, long prms = 26_000_000_000L) =>
        new(new HttpClient(new Handler(Json(files), ModelJsonFor(prms))) { Timeout = Timeout.InfiniteTimeSpan });

    private static Task<HubTree> Tree((string Path, long Bytes)[] files) =>
        Client(files).TreeAsync("unsloth/gemma-4-26B-A4B-it-GGUF", CancellationToken.None);

    private static async Task<ShelfRow?> Pick(
        (string Path, long Bytes)[] files, ulong vram, ulong ram, long prms = 26_000_000_000L) =>
        (await HubSearch.LookupAsync(Client(files, prms), "unsloth/gemma-4-26B-A4B-it-GGUF",
            new HardwareClass(MemoryTopology.Discrete, ShareKind.None, vram, ram,
                new HardwareSnapshot(0UL, 1UL, GpuKind.Discrete, 0UL), 0, BudgetBound.None),
            4096, _ => null, CancellationToken.None)).Row;

    private sealed class Handler(string tree, string model) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    r.RequestUri!.ToString().Contains("/tree/main") ? tree : model,
                    System.Text.Encoding.UTF8, "application/json"),
            });
    }
}
