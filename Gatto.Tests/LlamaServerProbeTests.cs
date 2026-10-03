using Gatto.Core.Tools;

namespace Gatto.Tests;

//the probe is tested at the pure Interpret seam, so no process is spawned, and the banner is a real capture from stderr
public class LlamaServerProbeTests
{
    private const string RealClassicStderr = "version: 10076 (305ba519a)\nbuilt with Clang 20.1.8 for Windows x86_64\n";

    [Fact]
    public void Classic_server_banner_on_stderr_passes()
    {
        var r = LlamaServerProbe.Interpret(0, "", RealClassicStderr, timedOut: false, vcRuntimeAbsent: false);
        Assert.Equal(ProbeShape.ClassicServer, r.Shape);
    }

    [Fact]
    public void Classic_banner_on_stdout_also_passes()
    {
        //the probe reads both streams, since a future build may move the banner to stdout
        var r = LlamaServerProbe.Interpret(0, RealClassicStderr, "", timedOut: false, vcRuntimeAbsent: false);
        Assert.Equal(ProbeShape.ClassicServer, r.Shape);
    }

    [Fact]
    public void Non_classic_output_is_named_with_the_unified_cli_hypothesis()
    {
        var r = LlamaServerProbe.Interpret(0, "llama CLI - usage: llama [command]", "", timedOut: false, vcRuntimeAbsent: false);
        Assert.Equal(ProbeShape.NotClassic, r.Shape);
        Assert.Contains("llama-server", r.Detail);   //the detail must name llama-server, the shape gatto drives
        Assert.Contains("unified", r.Detail);        //the detail also offers the unified cli as the likely cause.
    }

    [Fact]
    public void A_banner_matching_neither_shape_names_both_shapes_in_the_detail()
    {
        //the classic banner has two accepted shapes, so the refusal must quote both or it promises the wrong one
        var r = LlamaServerProbe.Interpret(0, "",
            "version: q9 (not a commit)\nbuilt with Clang 20.1.8 for Windows x86_64\n",
            timedOut: false, vcRuntimeAbsent: false);
        Assert.Equal(ProbeShape.NotClassic, r.Shape);
        Assert.Contains("'version: N (sha)'", r.Detail, StringComparison.Ordinal);
        Assert.Contains("'version: X (build N, commit sha)'", r.Detail, StringComparison.Ordinal);
    }

    //both spellings of the exit code must map to DllNotFound. the probe names only the observation, since a missing cudart dies like a missing runtime
    [Theory]
    [InlineData(-1073741515L)]   //the signed value a real Process.ExitCode reports
    [InlineData(3221225781L)]    //the unsigned spelling of the same bits.
    public void A_dll_not_found_death_is_named_by_what_was_observed(long exitCode)
    {
        var r = LlamaServerProbe.Interpret(exitCode, "", "", timedOut: false, vcRuntimeAbsent: false);
        Assert.Equal(ProbeShape.DllNotFound, r.Shape);
        Assert.Contains("STATUS_DLL_NOT_FOUND", r.Detail, StringComparison.Ordinal);
        //the detail must name neither a cause nor a vendor, so a later helpful addition has to pass this test first
        Assert.DoesNotContain("cudart", r.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CUDA", r.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_ordinary_nonzero_exit_is_still_just_failed()
    {
        //the general failure branch stays pinned, so the specific dll branch cannot swallow it.
        var r = LlamaServerProbe.Interpret(1, "", "", timedOut: false, vcRuntimeAbsent: false);
        Assert.Equal(ProbeShape.Failed, r.Shape);
    }

    [Fact]
    public void Timeout_is_reported_as_timeout()
    {
        var r = LlamaServerProbe.Interpret(null, "", "", timedOut: true, vcRuntimeAbsent: false);
        Assert.Equal(ProbeShape.TimedOut, r.Shape);
    }
}
