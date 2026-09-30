using Gatto.Cli.Setup;
using Gatto.Core.Home;
using Gatto.Roles;
using Gatto.Terminal;

namespace Gatto.Tests.Setup;

//the probe ServerHeldByAnother gates the swap consent, so it is tested at its producer. only the process lookup is replaced, and no test may spawn llama-server
public class HeldServerProbeTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-held-").FullName;

    public void Dispose() { try { Directory.Delete(_home, true); } catch (Exception) { } }

    //a fake live server matching the recorded pid, so the probe reads the server as held.
    private sealed class LiveLlama : ServeManager.IServeProcess
    {
        public int Pid => 4242;
        public bool HasExited => false;
        public string ProcessName => "llama-server";
        public void Kill() => throw new Xunit.Sdk.XunitException("nothing here may kill anything");
    }

    private LiveSetupProbes Probes() =>
        new(_home, serverLookup: pid => pid == 4242 ? new LiveLlama() : null, glyphs: GlyphSet.Unicode);

    private void Recorded(string model) =>
        File.WriteAllText(Path.Combine(_home, "serve.json"),
            $$"""{"pid":4242,"model":"{{model}}","port":1235,"started":"2026-09-03T00:00:00Z"}""");

    //a server holding another model must come back with its recorded name, or a null-always stub would pass the other assertions
    [Fact]
    public void A_SERVER_HOLDING_ANOTHER_MODEL_IS_NAMED_AND_THE_ONE_HOLDING_THIS_ONE_IS_NOT()
    {
        GattoHome.EnsureInitialized(_home);
        var probes = Probes();

        //no record means nothing is held, the state when the wizard opens.
        Assert.Null(probes.ServerHeldByAnother("tiny-qwen"));

        //the two ids are deliberately different strings. a fixture where they agree cannot fail when the wrong one is named.
        Recorded("qwen3.6-35b-a3b");
        Assert.Equal("qwen3.6-35b-a3b", probes.ServerHeldByAnother("tiny-qwen"));

        //the record now names the asked model itself, so it is not holding another and the answer is null
        Recorded("tiny-qwen");
        Assert.Null(probes.ServerHeldByAnother("tiny-qwen"));
    }

    //a recorded pid whose process is not ours holds nothing, so a stale record cannot make the wizard think a model is loaded
    [Fact]
    public void A_RECORD_WHOSE_PROCESS_IS_NOT_OURS_HOLDS_NOTHING()
    {
        GattoHome.EnsureInitialized(_home);
        Recorded("qwen3.6-35b-a3b");
        Assert.Equal("qwen3.6-35b-a3b", Probes().ServerHeldByAnother("tiny-qwen"));   //proves the record worked before the pid changed.

        File.WriteAllText(Path.Combine(_home, "serve.json"),
            """{"pid":9999,"model":"qwen3.6-35b-a3b","port":1235,"started":"2026-09-03T00:00:00Z"}""");
        Assert.Null(Probes().ServerHeldByAnother("tiny-qwen"));
    }
}
