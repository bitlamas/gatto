using Gatto.Core.Acquire;
using Gatto.Tests.Setup;

namespace Gatto.Tests;

//trees kept on disk under the repo's last change, so a warm landing asks no tree and a changed repo is asked again
public sealed class HubTreeStoreTests : IDisposable
{
    private const long B = 1_000_000_000L;
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-tree-store-").FullName;

    public void Dispose() => Directory.Delete(_home, recursive: true);

    private static readonly DateTimeOffset Day = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    private static string Shape(HubQuant q) =>
        $"{q.FileName}|{q.Bytes}|{q.Sha256}|{q.ShardCount}|{q.Path}|{q.Files is null}|"
        + string.Join(",", q.Members.Select(m => $"{m.FileName}:{m.Bytes}:{m.Sha256}:{m.Path}"));

    [Fact]
    public void A_TREE_READS_BACK_AS_IT_WAS_WRITTEN()
    {
        var tree = new HubTree(
            [
                new HubQuant("m-Q4_K_M.gguf", 5, "aa", Path: "m-Q4_K_M.gguf"),
                new HubQuant("m-Q8_0-00001-of-00002.gguf", 30, null, 2,
                    [new HubFile("m-Q8_0-00001-of-00002.gguf", 14, "b1", "Q8_0/m-Q8_0-00001-of-00002.gguf"),
                     new HubFile("m-Q8_0-00002-of-00002.gguf", 16, "b2", "Q8_0/m-Q8_0-00002-of-00002.gguf")],
                    Path: "Q8_0/m-Q8_0-00001-of-00002.gguf"),
            ],
            [new HubQuant("mmproj-F16.gguf", 1, "cc")],
            FileCount: 7);
        var store = new HubReadStore(_home);
        store.PutTree("o/m-GGUF", Day, tree);

        Assert.True(new HubReadStore(_home).TryGetTree("o/m-GGUF", Day, out var back));
        Assert.Equal(tree.Quants.Select(Shape), back.Quants.Select(Shape));
        Assert.Equal(tree.Projectors.Select(Shape), back.Projectors.Select(Shape));
        Assert.Equal(7, back.FileCount);
        Assert.False(store.TryGetTree("o/m-GGUF", Day.AddSeconds(1), out _));
    }

    [Fact]
    public void A_REPO_WITH_NO_DATE_IS_NOT_KEPT()
    {
        var store = new HubReadStore(_home);
        store.PutTree("o/m-GGUF", null, new HubTree([new HubQuant("m-Q4_K_M.gguf", 5, "aa")], []));
        Assert.False(store.TryGetTree("o/m-GGUF", null, out _));
    }

    private static FakeShelfHub Hub() => new FakeShelfHub()
        .Source("Qwen/Qwen3.8-27B").Conversion("Qwen/Qwen3.8-27B", "unsloth/Qwen3.8-27B-GGUF", 27 * B, 10, "qwen35",
            files: ("q-Q4_K_M.gguf", 12))
        .Source("Qwen/Qwen3.8-9B").Conversion("Qwen/Qwen3.8-9B", "unsloth/Qwen3.8-9B-GGUF", 9 * B, 10, "qwen35",
            files: ("n-Q4_K_M.gguf", 5));

    private Task<ShelfOutcome> Land(FakeShelfHub hub) =>
        ShelfSearch.AssembleAsync(new HubClient(new HttpClient(hub) { Timeout = Timeout.InfiniteTimeSpan }),
            UploaderAllowlist.Load(), Families.Load(), new HashSet<string> { "qwen" }, ShelfMachines.Apu8060S, 8192,
            lifted: false, CancellationToken.None, memo: new HubTreeMemo(new HubReadStore(_home)));

    [Fact]
    public async Task A_WARM_LANDING_ASKS_NO_TREE()
    {
        var cold = Hub();
        var first = await Land(cold);
        Assert.Equal(2, cold.Count("tree"));

        var warm = Hub();
        var second = await Land(warm);
        Assert.Equal(0, warm.Count("tree"));
        Assert.Equal(first.Rows.Select(r => $"{r.Model}|{r.RowFile}|{r.Fit}"), second.Rows.Select(r => $"{r.Model}|{r.RowFile}|{r.Fit}"));

        var changed = Hub().Touch("unsloth/Qwen3.8-9B-GGUF", Day.AddDays(1));
        await Land(changed);
        Assert.Equal(1, changed.Count("tree"));
        Assert.Contains(changed.Urls, u => u.Contains("/tree/") && u.Contains("Qwen3.8-9B"));
    }

    private Task<ShelfOutcome> Typed(FakeShelfHub hub) =>
        HubSearch.TypedSearchAsync(new HubClient(new HttpClient(hub) { Timeout = Timeout.InfiniteTimeSpan }),
            UploaderAllowlist.Load(), Families.Load(), "Qwen3.8", ShelfMachines.Apu8060S, 8192, CancellationToken.None,
            memo: new HubTreeMemo(new HubReadStore(_home)));

    [Fact]
    public async Task A_TYPED_SEARCH_KEEPS_ITS_TREES_TOO()
    {
        var cold = Hub();
        var first = await Typed(cold);
        Assert.Equal(2, cold.Count("tree"));

        var warm = Hub();
        var second = await Typed(warm);
        Assert.Equal(0, warm.Count("tree"));
        Assert.Equal(first.Rows.Select(r => r.Model), second.Rows.Select(r => r.Model));
    }
}
