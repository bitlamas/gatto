using System.Text.Json;
using Gatto.Core.Tools;

namespace Gatto.Tests;

public class ShellToolTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gatto-test-").FullName;
    private IToolContext Ctx => new TestToolContext(_dir);
    private static JsonElement Args(object o) => JsonSerializer.SerializeToElement(o);
    //teardown must never fail a passing test, so a delete that races the killed child's handle retries and then gives up quietly
    public void Dispose()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try { Directory.Delete(_dir, recursive: true); return; }
            catch (IOException) { Thread.Sleep(25); }
            catch (UnauthorizedAccessException) { Thread.Sleep(25); }
        }
        try { Directory.Delete(_dir, recursive: true); } catch (Exception) { } //give up quietly, an untidy temp directory is not worth failing a test over
    }

    [Fact]
    public async Task Runs_command_in_cwd_and_captures_stdout()
    {
        var r = await new ShellTool().ExecuteAsync(Args(new { command = "Get-Location | Select-Object -ExpandProperty Path" }), Ctx, default);
        Assert.False(r.IsError);
        Assert.Contains(Path.GetFileName(_dir), r.Text);
    }

    [Fact]
    public async Task Nonzero_exit_is_error_with_code()
    {
        var r = await new ShellTool().ExecuteAsync(Args(new { command = "exit 3" }), Ctx, default);
        Assert.True(r.IsError);
        Assert.Contains("exit code 3", r.Text);
    }

    [Fact]
    public async Task Stderr_is_captured()
    {
        var r = await new ShellTool().ExecuteAsync(
            Args(new { command = "[Console]::Error.WriteLine('boom'); exit 0" }), Ctx, default);
        Assert.Contains("boom", r.Text);
    }

    [Fact]
    public async Task Timeout_kills_and_reports()
    {
        var r = await new ShellTool().ExecuteAsync(
            Args(new { command = "Start-Sleep -Seconds 30", timeout_ms = 1500 }), Ctx, default);
        Assert.True(r.IsError);
        Assert.Contains("timed out", r.Text);
    }

    [Fact]
    public async Task Shell_GlossIsExitCode()
    {
        var r = await new ShellTool().ExecuteAsync(Args(new { command = "exit 0" }), Ctx, default);
        Assert.Equal("exit 0", r.Gloss);
    }

    //the start info redirects stdin, so the tool can close it (ChildStdinTests drives the read)
    [Fact]
    public void The_start_info_redirects_stdin()
    {
        var psi = ShellTool.StartInfo("exit 0", _dir);
        Assert.True(psi.CreateNoWindow);   //a known match, so the assertion proves these are the tool's own settings
        Assert.True(psi.RedirectStandardInput);
    }

    //the tool must leave no child of its own running when it returns, so the timeout path is the one this test drives
    [Fact]
    public async Task The_tools_own_child_has_exited_when_the_tool_returns()
    {
        var r = await new ShellTool().ExecuteAsync(
            Args(new { command = "Set-Content -Path pid.txt -Value $PID; Start-Sleep -Seconds 30", timeout_ms = 5000 }), Ctx, default);
        var returned = DateTime.Now;

        Assert.Contains("timed out", r.Text);
        var pid = int.Parse(File.ReadAllText(Path.Combine(_dir, "pid.txt")).Trim());
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            //the 5 s wait is well below the 30 s sleep, and a pid that started after the return is a reused one
            Assert.True(p.WaitForExit(5_000) || p.StartTime > returned, $"the tool's child {pid} is still running");
        }
        catch (ArgumentException) { } //the process is gone, which is the property this test wants
    }

    [Fact]
    public async Task Backgrounded_child_holding_the_pipe_does_not_hang_the_tool()
    {
        //a grandchild holding the stdout handle means EOF never comes, so the 20 s bound is what tells a hang from a bounded drain
        var run = new ShellTool().ExecuteAsync(Args(new
        {
            //the sleeper must not inherit the test's temp cwd, or it holds that directory open past Dispose
            command = "Start-Process -NoNewWindow -FilePath powershell -WorkingDirectory $env:TEMP -ArgumentList '-NoProfile','-NonInteractive','-Command','Start-Sleep -Seconds 30'; Write-Output 'launched'",
            timeout_ms = 60_000,   //the child exits cleanly, so the shell timeout must not be what saves the test
        }), Ctx, default);

        var winner = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(20)));
        Assert.True(winner == run, "shell tool did not return while a background child held the stdout pipe");
        var r = await run;
        Assert.False(r.IsError);
        Assert.Contains("launched", r.Text);          //output written before the exit still comes back
        Assert.Contains("background child", r.Text);  //a held pipe is disclosed in the text
    }

    [Fact]
    public async Task Captures_non_ascii_output_without_corruption()
    {
        //PS 5.1 writes redirected stdout through the OEM codepage, so CJK comes back as ? unless the prelude forces UTF-8
        var r = await new ShellTool().ExecuteAsync(
            Args(new { command = "Write-Output 'terra 月球 café'" }), Ctx, default);
        Assert.False(r.IsError);
        //the exact equality also fails on a leading UTF-8 BOM, which would corrupt a path the model reuses
        Assert.Equal("terra 月球 café", r.Text);
    }

    [Fact]
    public async Task Leading_using_statement_still_runs_with_utf8()
    {
        //a using statement has to be first in a PowerShell script, so the UTF-8 prelude goes after it
        var r = await new ShellTool().ExecuteAsync(
            Args(new { command = "using namespace System.Text; Write-Output '月球 café'" }), Ctx, default);
        Assert.False(r.IsError);
        Assert.Equal("月球 café", r.Text);
    }

    [Fact]
    public async Task Leading_param_block_still_runs_with_utf8()
    {
        //a leading param() block is the other shape the prelude must follow, and the default value still applies
        var r = await new ShellTool().ExecuteAsync(
            Args(new { command = "param([int]$n=2) Write-Output \"$n 月球\"" }), Ctx, default);
        Assert.False(r.IsError);
        Assert.Equal("2 月球", r.Text);
    }

    //a nested powershell prints the bytes it reads from its stdin, so the test reads what a native program receives
    [Fact]
    public async Task A_string_piped_to_a_native_program_arrives_as_utf8_without_a_bom()
    {
        const string dump = "$ms = New-Object IO.MemoryStream; [Console]::OpenStandardInput().CopyTo($ms); [BitConverter]::ToString($ms.ToArray())";
        var command = "('a' + [char]0xE9 + [char]0x65E5) | powershell -NoProfile -NonInteractive -Command '" + dump + "'";
        var r = await new ShellTool().ExecuteAsync(Args(new { command }), Ctx, default);
        Assert.False(r.IsError, r.Text);
        Assert.StartsWith("61-C3-A9-E6-97-A5", r.Text.Trim(), StringComparison.Ordinal);
        Assert.DoesNotContain("EF-BB-BF", r.Text, StringComparison.Ordinal);
    }

    //both spawn sites read the one prelude, so a fix at one reaches the other
    [Fact]
    public void The_read_only_shell_carries_the_same_prelude_as_the_shell_tool()
    {
        var tool = ShellTool.StartInfo("x", Path.GetTempPath()).ArgumentList[^1];
        var readOnly = Gatto.Cli.GattoApp.ReadOnlyShellStartInfo("x", Path.GetTempPath()).ArgumentList[^1];
        Assert.Equal(tool, readOnly);
        //the assignment, not the bare words (a comment keeps those). only a real console shows it took effect
        Assert.Contains("[Console]::InputEncoding = $__e;", tool, StringComparison.Ordinal);
    }
}
