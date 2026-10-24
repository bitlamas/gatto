using Gatto.Cli;
using Gatto.Cli.Setup;
using Gatto.Roles.Audition;
using Gatto.Roles.Models;

namespace Gatto.Tests.Audition;

//the audition stamps the bytes it measured: the entry's live key when the file is unchanged, else one read on a live row, written back
public class AuditionKeyTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-akey-").FullName;
    public void Dispose() { try { Directory.Delete(_home, true); } catch (Exception) { } }

    private sealed class Recorder : IProgress<AuditionProgress>
    {
        public List<AuditionProgress> Events { get; } = [];
        public void Report(AuditionProgress value) => Events.Add(value);
    }

    private string Models => Path.Combine(_home, "models");

    private (string Id, string Path) Scaffold(string? sha256 = null)
    {
        var dir = Path.Combine(_home, "weights");
        Directory.CreateDirectory(dir);
        var gguf = Path.Combine(dir, "tiny-Q4_K_M.gguf");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "tiny.gguf"), gguf, overwrite: true);
        return (ModelScaffold.Create(Models, gguf, port: 1235, sha256: sha256), gguf);
    }

    private static string Sha256Of(string path) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));

    private Task<string?> Run(string id, Recorder progress) =>
        AuditionRunner.LiveKeyOrReadAsync(_home, id, Model.Load(Models, id), progress, CancellationToken.None);

    //an unchanged file is trusted by its stat, so nothing is read and no row is shown
    [Fact]
    public async Task A_LIVE_KEY_IS_STAMPED_WITHOUT_A_READ()
    {
        var planted = new string('d', 64);
        var (id, _) = Scaffold(planted);
        var progress = new Recorder();

        Assert.Equal(planted, await Run(id, progress));
        Assert.Empty(progress.Events);
    }

    [Fact]
    public async Task AN_ENTRY_WITHOUT_A_KEY_IS_READ_ON_A_ROW_AND_WRITTEN_BACK()
    {
        var (id, gguf) = Scaffold();
        var progress = new Recorder();

        var stamped = await Run(id, progress);

        Assert.Equal(Sha256Of(gguf), stamped);
        var reading = Assert.Single(progress.Events);
        Assert.Equal(AuditionStage.Reading, reading.Stage);
        Assert.Equal("reading tiny-Q4_K_M.gguf", AuditionProgress.ReadingWords(reading.Text));
        Assert.Equal(stamped, ModelFiles.LiveKey(Model.Load(Models, id).Profile.Files.Single(f => f.Active)));
    }

    //a file rewritten under its path keeps the entry's old hash, and the stat sends the run back to the bytes
    [Fact]
    public async Task A_STALE_KEY_IS_READ_AGAIN_AND_ALL_THREE_KEYS_ARE_REWRITTEN()
    {
        var (id, gguf) = Scaffold(new string('d', 64));
        File.AppendAllText(gguf, "more bytes");
        var progress = new Recorder();

        var stamped = await Run(id, progress);

        Assert.Equal(Sha256Of(gguf), stamped);
        var entry = Model.Load(Models, id).Profile.Files.Single(f => f.Active);
        Assert.Equal(new FileInfo(gguf).Length, entry.Size);
        Assert.Equal(stamped, ModelFiles.LiveKey(entry));
    }

    //the stamp is a pure reading of what it is handed, so the record carries exactly the run's hash
    [Fact]
    public void THE_STAMP_CARRIES_THE_HASH_IT_IS_HANDED()
    {
        var (id, _) = Scaffold();
        var model = Model.Load(Models, id);
        Assert.Equal("abc", AuditionRunner.StampFor(model, null, "abc").Sha256);
        Assert.Null(AuditionRunner.StampFor(model, null).Sha256);
    }

    //the command's row moves while the file is read, and the next event takes it down
    [Fact]
    public void THE_COMMAND_DRAWS_THE_READ_AND_STOPS_ON_THE_NEXT_EVENT()
    {
        var output = new StringWriter();
        using var console = new AuditionConsole(output, rich: true, windowWidth: () => 120);
        console.Report(AuditionProgress.Reading("tiny-Q4_K_M.gguf"));
        Assert.True(SpinWait.SpinUntil(() => output.ToString().Contains("reading tiny-Q4_K_M.gguf ", StringComparison.Ordinal),
            TimeSpan.FromSeconds(5)), output.ToString());

        console.Report(AuditionProgress.Note("starting tiny"));
        var after = output.ToString().Length;
        Thread.Sleep(600);
        Assert.Equal(after, output.ToString().Length);
    }

    //a log has no moving row, so the read is committed once as a line
    [Fact]
    public void A_LOG_SAYS_THE_READ_ONCE()
    {
        var command = new StringWriter();
        using (var console = new AuditionConsole(command, rich: false))
            console.Report(AuditionProgress.Reading("tiny-Q4_K_M.gguf"));
        Assert.Contains("  reading tiny-Q4_K_M.gguf", command.ToString(), StringComparison.Ordinal);

        var wizard = new StringWriter();
        using (var ticker = new AuditionTicker(wizard, rich: false, Gatto.Terminal.GlyphSet.Unicode))
            ticker.Report(AuditionProgress.Reading("tiny-Q4_K_M.gguf"));
        Assert.Contains("  reading tiny-Q4_K_M.gguf", wizard.ToString(), StringComparison.Ordinal);
    }

    //the wizard's ticker reads the file under the purr, whose head carries the live timer
    [Fact]
    public void THE_WIZARDS_ROW_IS_THE_PURR_WITH_THE_READ()
    {
        var row = AuditionTicker.Row(72_800, AuditionProgress.ReadingWords("tiny-Q4_K_M.gguf"), Gatto.Terminal.GlyphSet.Unicode);
        Assert.EndsWith("1m 12s · reading tiny-Q4_K_M.gguf", row, StringComparison.Ordinal);
    }
}
