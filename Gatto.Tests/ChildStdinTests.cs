using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using Gatto.Cli;
using Gatto.Core.Tools;

namespace Gatto.Tests;

//this collection replaces the stdin handle of this process, so it runs after the parallel collections and no other test starts a child meanwhile
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessStdinCollection
{
    public const string Name = "process stdin";
}

//a shell child reading stdin must get an EOF at once, at both spawn sites. the host's stdin is already at its end, so each test swaps in one that never ends
[Collection(ProcessStdinCollection.Name)]
public sealed class ChildStdinTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gatto-test-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (Exception) { } //a leftover temp folder is untidy and must not fail the test
    }

    [Fact]
    public async Task A_shell_tool_child_that_reads_stdin_gets_EOF_and_its_own_exit_code()
    {
        ToolResult r;
        using (new StdinThatNeverEnds())
            r = await new ShellTool().ExecuteAsync(
                JsonSerializer.SerializeToElement(new { command = "$line = [Console]::In.ReadLine(); exit 7", timeout_ms = 8000 }),
                new TestToolContext(_dir), default);

        Assert.DoesNotContain("timed out", r.Text);
        Assert.Contains("exit code 7", r.Text);
        Assert.Equal("exit 7", r.Gloss);
    }

    [Fact]
    public void A_read_only_shell_child_that_reads_stdin_gets_EOF()
    {
        string result;
        using (new StdinThatNeverEnds())
            result = GattoApp.RunReadOnlyShell("$line = [Console]::In.ReadLine(); Write-Output 'read done'", _dir);

        Assert.DoesNotContain("timed out", result);
        Assert.Contains("read done", result);
    }

    //the child inherits the fixture's stdin and is still reading. if the fixture stopped reaching the child, this test goes red and the read tests above would pass
    [Fact]
    public async Task A_child_that_inherits_the_fixture_stdin_is_still_reading()
    {
        var psi = ShellTool.StartInfo(
            "[Console]::Out.WriteLine('reading'); [Console]::Out.Flush(); $line = [Console]::In.ReadLine(); exit 7", _dir);
        psi.RedirectStandardInput = false;
        using (new StdinThatNeverEnds())
        {
            using var p = Process.Start(psi)!;
            try
            {
                Assert.Equal("reading", await p.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)));
                Assert.False(p.WaitForExit(2_000), "the child read an EOF, so the fixture does not reach it");
            }
            finally { try { p.Kill(entireProcessTree: true); } catch { } }
        }
    }
}

//point the process's stdin at a pipe with its write end held open, so a read never returns. dispose puts the old handle back
internal sealed class StdinThatNeverEnds : IDisposable
{
    private const int StdInputHandle = -10;
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint GetStdHandle(int nStdHandle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetStdHandle(int nStdHandle, nint handle);

    private readonly nint _old = GetStdHandle(StdInputHandle);
    private readonly AnonymousPipeServerStream _pipe = new(PipeDirection.Out, HandleInheritability.Inheritable);

    public StdinThatNeverEnds()
    {
        if (!SetStdHandle(StdInputHandle, _pipe.ClientSafePipeHandle.DangerousGetHandle()))
        {
            var error = Marshal.GetLastWin32Error();
            _pipe.Dispose();
            throw new InvalidOperationException($"SetStdHandle failed with error {error}");
        }
    }

    public void Dispose()
    {
        SetStdHandle(StdInputHandle, _old);
        _pipe.Dispose();
    }
}
