using Gatto.Cli.Setup;

namespace Gatto.Tests.Setup;

//a browser is still writing the file when a .part or .crdownload sibling exists. the length proves nothing, and an empty file alone is a broken file
public class MoveOfferProbeTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gatto-partial-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (Exception) { }
    }

    private string Placeholder(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, []);
        return path;
    }

    [Fact]
    public void A_FIREFOX_PART_FILE_MEANS_THE_DOWNLOAD_IS_STILL_ARRIVING()
    {
        var gguf = Placeholder("LFM2-700M-Q6_K.gguf");
        File.WriteAllBytes(gguf + ".part", new byte[16]);

        Assert.True(Gatto.Core.Acquire.ModelDiscovery.DownloadInProgress(gguf));
    }

    [Fact]
    public void A_CHROMIUM_CRDOWNLOAD_COUNTS_TOO()
    {
        //both browser families must count, naming only one leaves the defect live for the other
        var gguf = Placeholder("qwen.gguf");
        File.WriteAllBytes(gguf + ".crdownload", new byte[16]);

        Assert.True(Gatto.Core.Acquire.ModelDiscovery.DownloadInProgress(gguf));
    }

    [Fact]
    public void A_FINISHED_FILE_IS_NOT_A_DOWNLOAD_IN_PROGRESS()
    {
        //a plain file with no sibling is what the move offer is for, suppressing it would trade one silent dead end for another
        var gguf = Placeholder("finished.gguf");

        Assert.False(Gatto.Core.Acquire.ModelDiscovery.DownloadInProgress(gguf));
    }

    [Fact]
    public void AN_EMPTY_FILE_ALONE_PROVES_NOTHING()
    {
        //a zero-length file with no sibling must not count as a download in progress, deciding by length would excuse the broken file
        var gguf = Placeholder("empty.gguf");

        Assert.Equal(0, new FileInfo(gguf).Length);
        Assert.False(Gatto.Core.Acquire.ModelDiscovery.DownloadInProgress(gguf));
    }
}
