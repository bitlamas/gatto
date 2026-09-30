using Gatto.Core.Client;
using Gatto.Roles;

namespace Gatto.Tests;

//this model's other file in the server isn't a mismatch, the user chose that and was told. the chip warns only for a file this model doesn't name
public class QuantSwitchSeamTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gatto-seam-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private const string Q4 = @"C:\w\m-Q4_K_M.gguf";
    private const string Q6 = @"C:\w\m-Q6_K.gguf";

    private static Model TwoFiles(string active) =>
        new("m",
            new ModelProfile(
                [new ModelFile(Q4, "Q4_K_M", active == Q4), new ModelFile(Q6, "Q6_K", active == Q6)],
                1235, 4096, null, null, null, null, null, []),
            null, null, null);

    //the chip must not warn here, the user chose this state and was told about it
    [Fact]
    public void THE_SERVER_HOLDING_THIS_MODELS_OTHER_FILE_IS_NOT_A_MISMATCH()
    {
        var after = ModelSwitch.DescribeProbe(TwoFiles(Q6), new LoadedModel(Q4, null), _dir);

        Assert.Null(after);
    }

    //a file this model doesn't name must still report, or the fix looks the same as deleting the chip
    [Fact]
    public void BUT_A_FILE_THIS_MODEL_DOES_NOT_NAME_STILL_REPORTS()
    {
        var stranger = ModelSwitch.DescribeProbe(
            TwoFiles(Q6), new LoadedModel(@"C:\w\somebody-else-Q4_K_M.gguf", null), _dir);

        Assert.NotNull(stranger);
        Assert.Contains("somebody-else", stranger!.Value.Name, StringComparison.Ordinal);
        //no shelf model claims that file, so its name must not be offered as a command
        Assert.False(stranger.Value.OnTheShelf);
    }

    //the active file must stay silent too, so the mismatch guard isn't the only thing that ever silences the probe
    [Fact]
    public void AND_THE_ACTIVE_FILE_ITSELF_REPORTS_NOTHING()
    {
        Assert.Null(ModelSwitch.DescribeProbe(TwoFiles(Q6), new LoadedModel(Q6, null), _dir));
    }

    //a probe that names no file stays unknown, so an edit that called it a mismatch fails here
    [Fact]
    public void A_SILENT_PROBE_IS_STILL_UNKNOWN_NOT_A_MISMATCH()
    {
        Assert.Null(ModelSwitch.DescribeProbe(TwoFiles(Q6), new LoadedModel(null, null), _dir));
        Assert.Null(ModelSwitch.DescribeProbe(TwoFiles(Q6), null, _dir));
    }
}
