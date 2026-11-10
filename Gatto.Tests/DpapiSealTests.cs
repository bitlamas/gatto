using System.Runtime.Versioning;
using System.Text;
using Gatto.Core;
using Gatto.Core.Home;

namespace Gatto.Tests;

//the real data protection API, so these run on Windows and return early elsewhere
[SupportedOSPlatform("windows")]
public sealed class DpapiSealTests
{
    [Fact]
    public void Seal_round_trips_and_the_sealed_bytes_differ_from_the_plain_ones()
    {
        if (!OperatingSystem.IsWindows()) return;
        var plain = Encoding.UTF8.GetBytes("""{"t":"a-token-value"}""");
        var seal = new DpapiSeal();

        var sealedBytes = seal.Seal(plain);

        Assert.NotEqual(plain, sealedBytes);
        Assert.Equal(-1, Encoding.UTF8.GetString(sealedBytes).IndexOf("a-token-value", StringComparison.Ordinal));
        Assert.Equal(plain, seal.Unseal(sealedBytes, "the file"));
    }

    [Fact]
    public void Unseal_of_garbage_throws_the_config_exception_naming_what()
    {
        if (!OperatingSystem.IsWindows()) return;

        var ex = Assert.Throws<GattoConfigException>(() => new DpapiSeal().Unseal([1, 2, 3, 4, 5], "alpha.bin"));

        Assert.StartsWith("cannot unseal alpha.bin: ", ex.Message);
    }
}
