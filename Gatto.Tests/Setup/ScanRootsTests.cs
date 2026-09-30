using Gatto.Cli.Setup;
using Gatto.Core.Acquire;
using Gatto.Core.Home;
using Gatto.Terminal;

namespace Gatto.Tests.Setup;

//the sweep must take the shared Downloads location ModelLocation owns, a private copy agrees with it until the folder moves
public class ScanRootsTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-roots-").FullName;
    public void Dispose() { try { Directory.Delete(_home, true); } catch (Exception) { } }

    //the case only discriminates with a relocated value, at the default location both copies agree and any test passes
    [Fact]
    public void THE_SWEEP_TAKES_THE_DOWNLOADS_IT_IS_GIVEN_not_a_second_concatenation()
    {
        var roots = LiveSetupProbes.ScanRootsFor(
            @"C:\home", configWeightsDir: null, downloadsDir: @"D:\relocated downloads", extraRoot: null);

        Assert.Contains(@"D:\relocated downloads", roots);
        Assert.DoesNotContain(roots, r => r.EndsWith(@"\Downloads", StringComparison.OrdinalIgnoreCase));
    }

    //compare the sweep against the shared policy, a fixed list of roots passes on a default machine even when the code grows one again
    [Fact]
    public void SCAN_DELEGATES_TO_THE_SHARED_ROOT_POLICY()
    {
        GattoHome.EnsureInitialized(_home);
        using var probes = new LiveSetupProbes(_home, GlyphSet.Unicode, TextWriter.Null);

        var swept = probes.Scan(null).Roots;
        var policy = LiveSetupProbes.ScanRootsFor(
            _home, GattoConfig.Load(_home).WeightsRoot, ModelLocation.DownloadsDir, null);

        Assert.Equal(policy, swept);
    }

    [Fact]
    public void THE_ORDER_IS_CONFIG_THEN_HOME_THEN_DOWNLOADS_THEN_THE_TYPED_FOLDER()
    {
        var roots = LiveSetupProbes.ScanRootsFor(
            @"C:\home", @"E:\my models", @"D:\dl", @"F:\typed");

        //the home's models folder holds setups and never a gguf, so it stays out of the roots
        Assert.Equal([@"E:\my models", @"D:\dl", @"F:\typed"], roots);
    }

    //an absent input drops out instead of becoming an empty root, which would reach a directory scan with a much worse message
    [Fact]
    public void ABSENT_INPUTS_DROP_OUT()
    {
        var roots = LiveSetupProbes.ScanRootsFor(@"C:\home", "", "", null);

        //with no configured weights folder the root is the suggested weights folder. the home models folder holds setups, so the miss screen would name the wrong folder.
        Assert.Equal([Path.Combine(@"C:\home", "weights")], roots);
        Assert.DoesNotContain(Path.Combine(@"C:\home", "models"), roots);
    }

    //a sweep that failed still reports the roots it tried, and an unreadable disk is when the user needs them most
    [Fact]
    public void AN_UNREADABLE_SWEEP_STILL_REPORTS_ITS_ROOTS()
    {
        GattoHome.EnsureInitialized(_home);
        using var probes = new LiveSetupProbes(_home, GlyphSet.Unicode, TextWriter.Null);

        //a file where a directory is expected cannot be enumerated.
        var notADir = Path.Combine(_home, "not-a-directory");
        File.WriteAllText(notADir, "x");

        var result = probes.Scan(notADir);

        Assert.Contains(notADir, result.Roots);
    }
}
