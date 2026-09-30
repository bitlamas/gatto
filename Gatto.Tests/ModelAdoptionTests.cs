using Gatto.Core.Acquire;

namespace Gatto.Tests;

//a failure at any step leaves the config naming a file that still exists
public class ModelAdoptionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gatto-adopt-" + Guid.NewGuid().ToString("N"));
    private readonly string _src;
    private readonly string _dst;

    public ModelAdoptionTests()
    {
        _src = Path.Combine(_root, "src");
        _dst = Path.Combine(_root, "dst");
        Directory.CreateDirectory(_src);
        Directory.CreateDirectory(_dst);
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private string Model(string name, int bytes = 4096)
    {
        var p = Path.Combine(_src, name);
        File.WriteAllBytes(p, new byte[bytes]);
        return p;
    }

    private sealed class Recorder
    {
        public readonly List<string> Writes = [];
        public Action<string> Write => p => Writes.Add(p);
    }

    private static Action<string> Throwing => _ => throw new InvalidOperationException("config write failed");

    [Fact]
    public async Task A_same_volume_move_is_a_rename_and_records_the_new_path()
    {
        var src = Model("m.gguf");
        var rec = new Recorder();

        var r = await ModelAdoption.MoveAsync(new AdoptionRequest(src, _dst, rec.Write), CancellationToken.None);

        Assert.True(r.Ok);
        Assert.True(r.WasRename);
        Assert.False(File.Exists(src));
        Assert.True(File.Exists(Path.Combine(_dst, "m.gguf")));
        Assert.Equal(Path.Combine(_dst, "m.gguf"), Assert.Single(rec.Writes));
    }

    [Fact]
    public async Task A_cross_volume_move_copies_verifies_then_deletes_the_original()
    {
        //the forceCopy hook exercises the cross-volume branch on a one-volume machine, otherwise this path ships untested
        var src = Model("m.gguf");
        var rec = new Recorder();

        var r = await ModelAdoption.MoveAsync(new AdoptionRequest(src, _dst, rec.Write),
            CancellationToken.None, forceCopy: true);

        Assert.True(r.Ok);
        Assert.False(r.WasRename);
        Assert.False(File.Exists(src));                       //the original is deleted only after the config write.
        Assert.True(File.Exists(Path.Combine(_dst, "m.gguf")));
    }

    [Fact]
    public async Task A_shard_set_moves_as_one_unit()
    {
        //the model is the whole shard set, moving one shard alone leaves a broken model behind
        Model("big-00001-of-00003.gguf");
        Model("big-00002-of-00003.gguf");
        Model("big-00003-of-00003.gguf");
        var rec = new Recorder();

        var r = await ModelAdoption.MoveAsync(
            new AdoptionRequest(Path.Combine(_src, "big-00001-of-00003.gguf"), _dst, rec.Write),
            CancellationToken.None);

        Assert.True(r.Ok);
        Assert.Equal(3, Directory.GetFiles(_dst).Length);
        Assert.Empty(Directory.GetFiles(_src));
        Assert.EndsWith("big-00001-of-00003.gguf", Assert.Single(rec.Writes));   //the config names shard one.
    }

    [Fact]
    public async Task A_config_write_failure_on_the_RENAME_path_puts_the_original_back()
    {
        var src = Model("m.gguf");

        var r = await ModelAdoption.MoveAsync(new AdoptionRequest(src, _dst, Throwing), CancellationToken.None);

        Assert.False(r.Ok);
        Assert.Contains("config write failed", r.Detail);
        Assert.True(File.Exists(src), "the rename must be undone — otherwise the model is nowhere the config knows");
        Assert.False(File.Exists(Path.Combine(_dst, "m.gguf")));
    }

    [Fact]
    public async Task A_config_write_failure_on_the_COPY_path_cleans_up_and_keeps_the_original()
    {
        var src = Model("m.gguf");

        var r = await ModelAdoption.MoveAsync(new AdoptionRequest(src, _dst, Throwing),
            CancellationToken.None, forceCopy: true);

        Assert.False(r.Ok);
        Assert.True(File.Exists(src));
        Assert.False(File.Exists(Path.Combine(_dst, "m.gguf")));
    }

    [Fact]
    public async Task A_config_write_failure_mid_shard_set_rolls_the_whole_set_back()
    {
        Model("big-00001-of-00002.gguf");
        Model("big-00002-of-00002.gguf");

        var r = await ModelAdoption.MoveAsync(
            new AdoptionRequest(Path.Combine(_src, "big-00001-of-00002.gguf"), _dst, Throwing),
            CancellationToken.None);

        Assert.False(r.Ok);
        Assert.Equal(2, Directory.GetFiles(_src).Length);      //the rollback is all or nothing, both shards come back
        Assert.Empty(Directory.GetFiles(_dst));
    }

    [Fact]
    public async Task A_size_mismatch_on_arrival_keeps_the_original_and_never_deletes()
    {
        //the hook truncates the file as it is written, so verification reads a real bad size. nothing in the check is faked
        var src = Model("m.gguf");
        var rec = new Recorder();

        var r = await ModelAdoption.MoveAsync(new AdoptionRequest(src, _dst, rec.Write),
            CancellationToken.None, forceCopy: true,
            afterArrival: p => File.WriteAllBytes(p, new byte[7]));

        Assert.False(r.Ok);
        Assert.Contains("expected", r.Detail);
        Assert.True(File.Exists(src));
        Assert.False(File.Exists(Path.Combine(_dst, "m.gguf")));
        Assert.Empty(rec.Writes);
    }

    [Fact]
    public async Task An_existing_destination_file_is_never_overwritten()
    {
        //the destination file is someone's data, the move stops and names it
        var src = Model("m.gguf");
        File.WriteAllBytes(Path.Combine(_dst, "m.gguf"), new byte[9]);
        var rec = new Recorder();

        var r = await ModelAdoption.MoveAsync(new AdoptionRequest(src, _dst, rec.Write), CancellationToken.None);

        Assert.False(r.Ok);
        //the detail must name the file, the pre-flight check is the only thing that gives it
        Assert.Contains(Path.Combine(_dst, "m.gguf"), r.Detail);
        Assert.Equal(9, new FileInfo(Path.Combine(_dst, "m.gguf")).Length);   //the destination file keeps its original nine bytes.
        Assert.True(File.Exists(src));
        Assert.Empty(rec.Writes);
    }

    [Fact]
    public async Task A_blocked_destination_LATER_in_a_shard_set_stops_before_anything_moves()
    {
        //a collision anywhere leaves the source complete and the destination untouched, and this pins that end state
        Model("big-00001-of-00003.gguf");
        Model("big-00002-of-00003.gguf");
        Model("big-00003-of-00003.gguf");
        File.WriteAllBytes(Path.Combine(_dst, "big-00003-of-00003.gguf"), new byte[9]);
        var rec = new Recorder();

        var r = await ModelAdoption.MoveAsync(
            new AdoptionRequest(Path.Combine(_src, "big-00001-of-00003.gguf"), _dst, rec.Write),
            CancellationToken.None);

        Assert.False(r.Ok);
        Assert.Equal(3, Directory.GetFiles(_src).Length);      //nothing moved out of the source folder.
        Assert.Single(Directory.GetFiles(_dst));               //the destination holds only the file that was there before.
        Assert.Empty(rec.Writes);
    }

    [Fact]
    public async Task A_missing_source_is_a_named_failure_not_a_throw()
    {
        var r = await ModelAdoption.MoveAsync(
            new AdoptionRequest(Path.Combine(_src, "ghost.gguf"), _dst, new Recorder().Write),
            CancellationToken.None);

        Assert.False(r.Ok);
        Assert.Contains("no such model file", r.Detail);
    }

    [Fact]
    public void Storage_sense_state_is_best_effort_and_never_throws()
    {
        //the read may answer null, the feature must not depend on it succeeding
        var state = StorageSense.DescribeState();
        Assert.True(state is null or "armed" or "off", $"unexpected state '{state}'");
    }

    //an empty per-model folder left behind reads as a model with no files, rollback deletes one only when this call made it
    [Fact]
    public async Task A_FAILED_MOVE_into_a_folder_THE_USER_ALREADY_HAD_leaves_it_alone()
    {
        var src = Model("tiny.gguf");
        var perModel = Path.Combine(_dst, "tiny-model");

        //the test makes the folder here so the mover doesn't, and the rollback has to tell the two cases apart
        Directory.CreateDirectory(perModel);
        File.WriteAllText(Path.Combine(perModel, "tiny.gguf"), "someone else's file");

        var result = await ModelAdoption.MoveAsync(
            new AdoptionRequest(src, perModel, _ => { }), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.True(File.Exists(src), "the original must survive a refused move");
        //the folder holding the user's file stays, this call did not make it
        Assert.True(Directory.Exists(perModel));
        Assert.Equal("someone else's file", File.ReadAllText(Path.Combine(perModel, "tiny.gguf")));
    }

    [Fact]
    public async Task A_FAILED_MOVE_into_a_folder_THIS_CALL_created_leaves_nothing_behind()
    {
        var src = Model("tiny.gguf");
        var perModel = Path.Combine(_dst, "fresh-model");

        //the destination is empty so the mover makes the folder and the config write throws, which is the failure the rollback answers
        var result = await ModelAdoption.MoveAsync(
            new AdoptionRequest(src, perModel, Throwing), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.True(File.Exists(src), "the original must survive");
        Assert.False(Directory.Exists(perModel),
            "rollback left an empty per-model folder: the shelf would read it as a model with no files");
    }
}
