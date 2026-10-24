using Gatto.Cli.Setup;
using Gatto.Core.Acquire;
using Gatto.Roles.Models;

namespace Gatto.Tests;

//the profile entry's sha256 is trusted only while the file keeps the size and write time it was hashed at, and the time must survive the file to the tick
public class ProfileKeyTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gatto-key-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (Exception) { } }

    private static readonly string Sha = new('c', 64);

    private string Models => Path.Combine(_dir, "models");

    private string Gguf(string name = "tiny-Q4_K_M.gguf", string? dir = null)
    {
        var folder = dir ?? Path.Combine(_dir, "weights");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "tiny.gguf"), path, overwrite: true);
        //a write time with a fraction of a second, so a format that drops ticks fails here
        File.SetLastWriteTimeUtc(path, new DateTime(2025, 3, 4, 5, 6, 7, DateTimeKind.Utc).AddTicks(1234567));
        return path;
    }

    private ModelFile Entry(string id) => Model.Load(Models, id).Profile.Files.Single(f => f.Active);

    //the format test: written by the scaffold, read by the loader, and the key still matches its own file
    [Fact]
    public void A_SCAFFOLDED_KEY_ROUND_TRIPS_THROUGH_THE_PROFILE_TO_THE_TICK()
    {
        var gguf = Gguf();
        var id = ModelScaffold.Create(Models, gguf, port: 1235, sha256: Sha);

        var entry = Entry(id);
        Assert.Equal(Sha, entry.Sha256);
        Assert.Equal(new FileInfo(gguf).Length, entry.Size);
        Assert.Equal(new FileInfo(gguf).LastWriteTimeUtc.Ticks, entry.Written!.Value.ToUniversalTime().Ticks);
        Assert.Equal(Sha, ModelFiles.LiveKey(entry));
        Assert.Equal(Sha, ModelFiles.LiveKey(Models, id, gguf));
    }

    [Fact]
    public void AN_ENTRY_WITHOUT_THE_KEYS_READS_NO_KEY()
    {
        var id = ModelScaffold.Create(Models, Gguf(), port: 1235);

        var entry = Entry(id);
        Assert.Null(entry.Sha256);
        Assert.Null(entry.Size);
        Assert.Null(entry.Written);
        Assert.Null(ModelFiles.LiveKey(entry));
    }

    //a file copied over the same path keeps the entry's old hash, so the stat must turn it into no key
    [Fact]
    public void A_FILE_REWRITTEN_UNDER_ITS_PATH_HAS_NO_LIVE_KEY()
    {
        var gguf = Gguf();
        var id = ModelScaffold.Create(Models, gguf, port: 1235, sha256: Sha);

        File.SetLastWriteTimeUtc(gguf, DateTime.UtcNow);
        Assert.Null(ModelFiles.LiveKey(Entry(id)));

        var longer = Gguf("long-Q4_K_M.gguf");
        var id2 = ModelScaffold.Create(Models, longer, port: 1236, id: "longer", sha256: Sha);
        var time = File.GetLastWriteTimeUtc(longer);
        File.AppendAllText(longer, "x");
        File.SetLastWriteTimeUtc(longer, time);
        Assert.Null(ModelFiles.LiveKey(Entry(id2)));
    }

    [Fact]
    public void A_JOINED_FILE_CARRIES_ITS_KEY_AND_THE_ACTIVE_ONE_STAYS_BARE()
    {
        var id = ModelScaffold.Create(Models, Gguf(), port: 1235);
        var second = Gguf("tiny-Q6_K.gguf");
        ModelFiles.Add(Models, id, second, Sha);

        var files = Model.Load(Models, id).Profile.Files;
        Assert.Null(files.Single(f => f.Active).Sha256);
        Assert.Equal(Sha, ModelFiles.LiveKey(files.Single(f => !f.Active)));
    }

    //the audition's write-back names one file, and the other entries keep what they had
    [Fact]
    public void RECORD_KEY_WRITES_THE_ENTRY_NAMING_THE_FILE_AND_NO_OTHER()
    {
        var first = Gguf();
        var id = ModelScaffold.Create(Models, first, port: 1235);
        ModelFiles.Add(Models, id, Gguf("tiny-Q6_K.gguf"));

        Assert.True(ModelFiles.RecordKey(Models, id, first, FileKey.Of(Sha, first)));
        Assert.False(ModelFiles.RecordKey(Models, id, Path.Combine(_dir, "elsewhere.gguf"), FileKey.Of(Sha, first)));

        var files = Model.Load(Models, id).Profile.Files;
        Assert.Equal(Sha, ModelFiles.LiveKey(files.Single(f => f.Active)));
        Assert.Null(files.Single(f => !f.Active).Sha256);
    }

    //a move to another volume is a copy that sets no time, so only a stat at the final path names the bytes there
    [Fact]
    public async Task A_KEY_TAKEN_AFTER_A_COPYING_MOVE_MATCHES_AND_ONE_TAKEN_BEFORE_DOES_NOT()
    {
        var source = Gguf();
        var before = FileKey.Of(Sha, source);
        var moved = await ModelAdoption.MoveAsync(
            new AdoptionRequest(source, Path.Combine(_dir, "moved"), _ => { }), CancellationToken.None, forceCopy: true);
        Assert.True(moved.Ok);

        var after = FileKey.Of(Sha, moved.FinalPath);
        var stale = new ModelFile(moved.FinalPath, "Q4_K_M", true, before.Sha256, before.Size, before.Written);
        var live = new ModelFile(moved.FinalPath, "Q4_K_M", true, after.Sha256, after.Size, after.Written);
        Assert.Null(ModelFiles.LiveKey(stale));
        Assert.Equal(Sha, ModelFiles.LiveKey(live));
    }

    //the writer stats the file where the move left it, so the key it records is live
    [Fact]
    public void THE_APPLY_RECORDS_A_LIVE_KEY_AT_THE_PATH_IT_WROTE()
    {
        Gatto.Core.Home.GattoHome.EnsureInitialized(_dir);
        var gguf = Gguf();
        var writes = new WriteSet
        {
            CreateModel = new WriteSet.Model(gguf, Port: 1235, Context: 4096, Id: "keyed", Sha256: Sha),
            MoveTo = Path.Combine(_dir, "into"),
        };

        Assert.Null(WriteSetApply.Apply(_dir, writes, out var id));
        var entry = Entry(id!);
        Assert.StartsWith(Path.Combine(_dir, "into"), entry.Path, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Sha, ModelFiles.LiveKey(entry));
    }
}
