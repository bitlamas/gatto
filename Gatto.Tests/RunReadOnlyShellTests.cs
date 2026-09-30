using System.Diagnostics;
using Gatto.Cli;

namespace Gatto.Tests;

public class RunReadOnlyShellTests
{
    [Fact]
    public void Surviving_child_cannot_hold_the_checkpoint_shell_open()
    {
        //a background child holding the output pipe must not hold the checkpoint open. the partial output goes away, a truncated result is worse than none
        var sw = Stopwatch.StartNew();
        var result = GattoApp.RunReadOnlyShell(
            "Start-Process -NoNewWindow -FilePath powershell -ArgumentList '-NoProfile','-NonInteractive','-Command','Start-Sleep -Seconds 30'; Write-Output 'ok'",
            Directory.GetCurrentDirectory());
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), $"checkpoint shell blocked for {sw.Elapsed} behind a background child");
        Assert.Contains("still holds the output pipe", result);
    }

    //the start info must redirect stdin so the caller can close it, the read half is tested in ChildStdinTests
    [Fact]
    public void The_start_info_redirects_stdin()
    {
        var psi = GattoApp.ReadOnlyShellStartInfo("exit 0", Directory.GetCurrentDirectory());
        Assert.True(psi.CreateNoWindow);   //the no-window flag proves this psi came from the helper, otherwise the redirect assert proves nothing
        Assert.True(psi.RedirectStandardInput);
    }
}
