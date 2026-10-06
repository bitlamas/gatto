using System.Text.Json.Nodes;
using Gatto.Core.Acquire;

namespace Gatto.Tests;

//the disk scan keeps its header reads under the home, so a second run reads only what changed
public class LocalReadStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gatto-localreads-" + Guid.NewGuid().ToString("N"));

    public LocalReadStoreTests() => Directory.CreateDirectory(Path.Combine(_root, "home"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (Exception) { }
    }

    private string Home => Path.Combine(_root, "home");
    private string Models => Path.Combine(_root, "models");

    private string Write(string name, uint context, string arch = "qwen3")
    {
        var path = Path.Combine(Models, name);
        Directory.CreateDirectory(Models);
        File.WriteAllBytes(path, GgufTestBytes.SyntheticHeader(kv =>
        {
            kv.Str("general.architecture", arch);
            kv.U32(arch + ".context_length", context);
        }));
        return path;
    }

    //the same length and the same last write, with bytes no parser reads as a header, so only the store can answer
    private static void Garble(string path)
    {
        var when = File.GetLastWriteTimeUtc(path);
        var length = new FileInfo(path).Length;
        File.WriteAllBytes(path, Enumerable.Repeat((byte)0xFF, (int)length).ToArray());
        File.SetLastWriteTimeUtc(path, when);
    }

    private IReadOnlyList<FoundModel> Scan(LocalReadStore? disk) => ModelDiscovery.Scan([Models], disk: disk);

    [Fact]
    public void A_SECOND_SCAN_READS_NO_HEADER()
    {
        var path = Write("a-Q4_K_M.gguf", 4096);
        var disk = new LocalReadStore(Home);
        Assert.Equal(4096, Assert.Single(Scan(disk)).Header!.ContextLength);

        Garble(path);

        Assert.Equal(4096, Assert.Single(Scan(disk)).Header!.ContextLength);
        //the garbled file read live has no context length, so the answer above came from the store
        Assert.Null(Assert.Single(Scan(null)).Header?.ContextLength);
    }

    [Fact]
    public void A_CHANGED_FILE_IS_READ_AGAIN()
    {
        var path = Write("a-Q4_K_M.gguf", 4096);
        var disk = new LocalReadStore(Home);
        Scan(disk);

        var when = File.GetLastWriteTimeUtc(path);
        Write("a-Q4_K_M.gguf", 8192);
        File.SetLastWriteTimeUtc(path, when.AddMinutes(1));

        Assert.Equal(8192, Assert.Single(Scan(disk)).Header!.ContextLength);
    }

    //a set is keyed by every shard, so a change to the second shard reads the set again even though shard one is unchanged
    [Fact]
    public void A_SHARDED_SET_HITS_THE_STORE_AND_A_CHANGED_SHARD_MISSES_IT()
    {
        var one = Write("big-00001-of-00002.gguf", 4096);
        var two = Write("big-00002-of-00002.gguf", 4096);
        var disk = new LocalReadStore(Home);
        Assert.Equal(2, Assert.Single(Scan(disk)).ShardsPresent);

        Garble(one);
        Assert.Equal(4096, Assert.Single(Scan(disk)).Header!.ContextLength);

        File.SetLastWriteTimeUtc(two, File.GetLastWriteTimeUtc(two).AddMinutes(1));
        Assert.Null(Assert.Single(Scan(disk)).Header?.ContextLength);
    }

    //an entry is the store's own answer only while it holds every value in its kind, so an edited value reads back and a wrong kind reads as a miss
    [Fact]
    public void A_GOOD_ENTRY_ANSWERS_AND_A_MALFORMED_ONE_READS_AS_A_MISS()
    {
        Write("a-Q4_K_M.gguf", 4096);
        var disk = new LocalReadStore(Home);
        Scan(disk);
        var entry = Assert.Single(Directory.GetFiles(disk.Dir));

        var node = JsonNode.Parse(File.ReadAllText(entry))!;
        node["architecture"] = "kept";
        File.WriteAllText(entry, node.ToJsonString());
        Assert.Equal("kept", Assert.Single(Scan(disk)).Header!.Architecture);

        node["context_length"] = "4096";
        File.WriteAllText(entry, node.ToJsonString());
        Assert.Equal("qwen3", Assert.Single(Scan(disk)).Header!.Architecture);
    }

    [Fact]
    public void NOTHING_IS_WRITTEN_BEFORE_THE_HOME_EXISTS()
    {
        Write("a-Q4_K_M.gguf", 4096);
        var missing = Path.Combine(_root, "no-home");

        Assert.Equal(4096, Assert.Single(Scan(new LocalReadStore(missing))).Header!.ContextLength);
        Assert.False(Directory.Exists(missing));
    }

    //a found model carries the header's values only, read or kept, so a warm run hands the shelf what a cold one did
    [Fact]
    public void A_FOUND_MODEL_CARRIES_NO_TABLE_OR_TEMPLATE_COLD_OR_WARM()
    {
        Write("a-Q4_K_M.gguf", 4096);
        var disk = new LocalReadStore(Home);

        var cold = Assert.Single(Scan(disk)).Header!;
        var warm = Assert.Single(Scan(disk)).Header!;

        Assert.Null(cold.Tensors);
        Assert.Null(cold.ChatTemplate);
        Assert.Equal(cold, warm);
    }
}
