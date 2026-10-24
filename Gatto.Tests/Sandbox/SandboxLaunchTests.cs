using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using Gatto.Core.Client;
using Gatto.Core.Sandbox;
using Gatto.Core.Tools;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Sandbox;

//the shell child under the real token, on folders the test makes and deletes whole, so no label outlives it
[SupportedOSPlatform("windows")]
[Collection("e2e")]
public sealed class SandboxLaunchTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("gatto-launch-").FullName;
    private readonly SandboxState _state;
    private string Project => Path.Combine(_root, "project");
    private string Outside => Path.Combine(_root, "outside");

    private sealed class Ctx(string cwd, string home, SandboxState? sandbox) : IToolContext
    {
        public string Cwd => cwd;
        public string HomePath => home;
        public IUserPrompter? Prompter => null;
        public SandboxState? Sandbox => sandbox;
    }

    public SandboxLaunchTests()
    {
        foreach (var d in new[] { "project", "outside", "home", "nuget", "http-cache", "tmp" }) Directory.CreateDirectory(Path.Combine(_root, d));
        _state = new SandboxState(Project, Path.Combine(_root, "home"), [],
            nugetFolder: Path.Combine(_root, "nuget"), httpCache: Path.Combine(_root, "http-cache"), tempParent: Path.Combine(_root, "tmp"));
        Assert.True(_state.TurnOn("--sandbox"), _state.Line);
    }

    public void Dispose()
    {
        _state.Dispose();
        try { Directory.Delete(_root, true); } catch { }
    }

    private Task<ToolResult> Run(string command, int? timeoutMs = null, SandboxState? state = null) =>
        new ShellTool().ExecuteAsync(
            JsonSerializer.SerializeToElement(timeoutMs is { } t ? new { command, timeout_ms = t } : (object)new { command }),
            new Ctx(Project, Path.Combine(_root, "home"), state ?? _state), CancellationToken.None);

    [Fact]
    public async Task OUTPUT_ARRIVES_AND_A_NATIVE_RUNS_IN_A_PIPELINE()
    {
        var r = await Run("'hello'; git --version 2>&1 | Select-String git");
        Assert.False(r.IsError, r.Text);
        Assert.Contains("hello", r.Text, StringComparison.Ordinal);
        Assert.Contains("git version", r.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_WRITE_INSIDE_SUCCEEDS_AND_A_WRITE_OUTSIDE_IS_REFUSED()
    {
        var inside = await Run($"Set-Content -Path '{Path.Combine(Project, "in.txt")}' -Value x");
        Assert.False(inside.IsError, inside.Text);
        Assert.True(File.Exists(Path.Combine(Project, "in.txt")));

        var outside = await Run($"Set-Content -Path '{Path.Combine(Outside, "out.txt")}' -Value x");
        Assert.True(outside.IsError, outside.Text);
        Assert.False(File.Exists(Path.Combine(Outside, "out.txt")));
        Assert.Contains("PermissionDenied", outside.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("CLIXML", outside.Text, StringComparison.Ordinal);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(outside.Text, "^sandbox: writes outside ",
            System.Text.RegularExpressions.RegexOptions.Multiline));
        Assert.Contains(SandboxRefusal.WriteLine(Project), outside.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("sandbox:", inside.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_BACKGROUND_PROCESS_THE_CHILD_STARTS_IS_IN_ITS_JOB_AND_ENDS_WHEN_THE_JOB_CLOSES()
    {
        var r = await Run("(Start-Process powershell.exe -ArgumentList '-NoProfile','-Command','Start-Sleep 120' -WindowStyle Hidden -PassThru).Id");
        Assert.False(r.IsError, r.Text);
        using var background = Process.GetProcessById(int.Parse(r.Text.Split('\n')[0].Trim()));
        Assert.False(background.HasExited);
        var jobs = _state.Jobs;
        Assert.Contains(jobs, job => SandboxNative.IsProcessInJob(background.Handle, job, out var inJob) && inJob);
        Assert.DoesNotContain(jobs, job => SandboxNative.IsProcessInJob(SandboxNative.GetCurrentProcess(), job, out var inJob) && inJob);

        _state.Dispose();
        Assert.True(background.WaitForExit(2_000), "the background process outlived the closed job");
    }

    [Fact]
    public async Task A_SHELL_WITH_THE_MODE_OFF_MAKES_NO_JOB()
    {
        using var off = new SandboxState(Project, Path.Combine(_root, "home"), [],
            nugetFolder: Path.Combine(_root, "nuget"), httpCache: Path.Combine(_root, "http-cache"), tempParent: Path.Combine(_root, "tmp"));
        var r = await Run("'off'", state: off);
        Assert.False(r.IsError, r.Text);
        Assert.Empty(off.Jobs);
    }

    [Fact]
    public async Task THE_CHILD_RUNS_AT_LOW_INTEGRITY()
    {
        //the Windows whoami by its path, since Git's msys one can come first on PATH and dies at low while a medium msys process is alive
        var r = await Run("& \"$env:SystemRoot\\System32\\whoami.exe\" /groups | Select-String 'Mandatory Label'");
        Assert.False(r.IsError, r.Text);
        Assert.Contains("Low Mandatory Level", r.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_REGISTRY_WRITE_IS_REFUSED_WITH_THE_REGISTRY_LINE()
    {
        var key = @"Software\gatto-sandbox-test-" + Guid.NewGuid().ToString("N");
        try
        {
            var r = await Run($"New-Item -Path 'HKCU:\\{key}'");
            Assert.True(r.IsError, r.Text);
            Assert.Contains(SandboxRefusal.RegistryLine, r.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("sandbox: writes outside", r.Text, StringComparison.Ordinal);
            using var opened = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(key);
            Assert.Null(opened);
        }
        finally { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false); }
    }

    //the reason the token is low integrity and not write-restricted: schannel must still hand the child a credential
    [Fact]
    public async Task AN_HTTPS_REQUEST_FROM_THE_CHILD_GETS_ITS_ANSWER()
    {
        using var server = LoopbackHttps.Start();
        var r = await Run($"curl.exe -s -k --ssl-no-revoke -o NUL -w '%{{http_code}}' https://127.0.0.1:{server.Port}/; ' exit ' + $LASTEXITCODE");
        Assert.False(r.IsError, r.Text);
        Assert.Contains("200 exit 0", r.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task THE_SAME_WORDS_ON_STDOUT_APPEND_NOTHING()
    {
        var r = await Run("'PermissionDenied UnauthorizedAccessException MSB3021'");
        Assert.DoesNotContain("sandbox:", r.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_READ_OUTSIDE_IS_NOT_LIMITED()
    {
        File.WriteAllText(Path.Combine(Outside, "notes.txt"), "readable");
        var r = await Run($"Get-Content '{Path.Combine(Outside, "notes.txt")}'");
        Assert.False(r.IsError, r.Text);
        Assert.Contains("readable", r.Text, StringComparison.Ordinal);
    }

    //cmd runs ahead of PowerShell, so a quote, a pipe, a percent or an ampersand in the command must reach PowerShell untouched
    [Fact]
    public async Task NON_ASCII_AND_CMD_METACHARACTERS_COME_BACK_INTACT()
    {
        var r = await Run("'héllo 日本'; Write-Output \"a | b & c > d %PATH% ^e\"; Write-Progress -Activity busy; 'done'");
        Assert.False(r.IsError, r.Text);
        Assert.Contains("héllo 日本", r.Text, StringComparison.Ordinal);
        Assert.Contains("a | b & c > d %PATH% ^e", r.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("CLIXML", r.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TEMP_IS_THE_PROCESS_FOLDER()
    {
        var r = await Run("$env:TEMP; $env:TMP");
        Assert.Equal([_state.Temp!, _state.Temp!], r.Text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray());
    }

    [Fact]
    public async Task THE_CHILD_STARTS_NO_BUILD_SERVER()
    {
        var r = await Run("$env:UseSharedCompilation; $env:MSBUILDDISABLENODEREUSE; $env:DOTNET_CLI_USE_MSBUILD_SERVER");
        Assert.Equal(["false", "1", "0"], r.Text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray());
    }

    //a compiler server left at low integrity takes the user's next build over its shared pipe and cannot write that build's output
    [Fact]
    public async Task A_SANDBOXED_BUILD_LEAVES_NO_LOW_COMPILER_SERVER_AND_THE_NEXT_BUILD_OUTSIDE_SUCCEEDS()
    {
        File.WriteAllText(Path.Combine(Project, "app.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(Project, "Program.cs"), "System.Console.WriteLine(1);");
        //the compiler logs whether a server ran it, and a server already running at medium would write the child's output unconfined
        var inside = await Run("dotnet build -nologo -v d | Select-String 'CompilerServer:|Build succeeded'", timeoutMs: 300000);
        Assert.False(inside.IsError, inside.Text);
        Assert.Contains("Build succeeded", inside.Text, StringComparison.Ordinal);
        Assert.Contains("CompilerServer: tool", inside.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("CompilerServer: server", inside.Text, StringComparison.Ordinal);
        Assert.Empty(LowCompilerServers());

        var psi = new ProcessStartInfo("dotnet", ["build", Project, "-nologo", "-v", "q", "--no-incremental"])
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        using var outside = Process.Start(psi)!;
        var err = outside.StandardError.ReadToEndAsync();
        var text = await outside.StandardOutput.ReadToEndAsync();
        await outside.WaitForExitAsync();
        Assert.True(outside.ExitCode == 0, text + await err);
    }

    //a low-labelled image starts low for every later process, so the session's end must leave nothing low and the user's own build after it must start medium
    [Fact]
    public async Task THE_END_OF_THE_SESSION_UNPLANTS_AND_A_BUILD_AFTER_IT_STARTS_AT_MEDIUM()
    {
        File.WriteAllText(Path.Combine(Project, "app.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(Project, "Program.cs"), "System.Console.ReadLine();");
        var inside = await Run("dotnet build -nologo -v q", timeoutMs: 300000);
        Assert.False(inside.IsError, inside.Text);
        var image = Directory.GetFiles(Path.Combine(Project, "bin"), "app.exe", SearchOption.AllDirectories).Single();
        Assert.Contains(";;;LW)", SandboxGrants.Label(image), StringComparison.Ordinal);
        var roots = _state.Roots;

        _state.Dispose();

        foreach (var root in roots)
            foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Prepend(root))
                Assert.DoesNotContain(";;;LW)", SandboxGrants.Label(path), StringComparison.Ordinal);
        var psi = new ProcessStartInfo("dotnet", ["build", Project, "-nologo", "-v", "q", "--no-incremental"])
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        using (var build = Process.Start(psi)!)
        {
            var err = build.StandardError.ReadToEndAsync();
            var text = await build.StandardOutput.ReadToEndAsync();
            await build.WaitForExitAsync();
            Assert.True(build.ExitCode == 0, text + await err);
        }
        using var app = Process.Start(new ProcessStartInfo(image) { RedirectStandardInput = true, UseShellExecute = false })!;
        try
        {
            Assert.True(SandboxNative.OpenProcessToken(app.Handle, 0x8, out var token));   //0x8 is TOKEN_QUERY
            using (token) Assert.Equal(new System.Security.Principal.SecurityIdentifier("S-1-16-8192"), SandboxToken.IntegrityLevel(token));
        }
        finally
        {
            app.StandardInput.Close();
            await app.WaitForExitAsync();
        }
    }

    private static List<int> LowCompilerServers()
    {
        var low = new List<int>();
        foreach (var p in Process.GetProcessesByName("VBCSCompiler"))
            using (p)
            {
                try
                {
                    if (!SandboxNative.OpenProcessToken(p.Handle, 0x8, out var token)) continue;   //0x8 is TOKEN_QUERY
                    using (token)
                        if (SandboxToken.IntegrityLevel(token) == SandboxToken.Low) low.Add(p.Id);
                }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        return low;
    }

    [Fact]
    public async Task A_CHILD_PAST_ITS_TIMEOUT_IS_KILLED_WITH_ITS_TREE()
    {
        var pidFile = Path.Combine(Project, "pid.txt");
        var r = await Run($"$p = Start-Process -PassThru -WindowStyle Hidden powershell -ArgumentList '-NoProfile','-Command','Start-Sleep 60'; " +
            $"Set-Content -Path '{pidFile}' -Value $p.Id; Start-Sleep 60", timeoutMs: 8000);
        Assert.StartsWith("command timed out after 8000ms", r.Text, StringComparison.Ordinal);
        var pid = int.Parse(File.ReadAllText(pidFile).Trim());
        //the tree kill has been asked for, so wait on the grandchild's own exit, which ends at once when it is already gone
        try
        {
            using var grandchild = Process.GetProcessById(pid);
            Assert.True(grandchild.WaitForExit(Waits.Bound), $"process {pid} outlived the tree kill");
        }
        catch (ArgumentException) { }
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(pid));
    }

    //a second child must not inherit the first's pipe ends, or it holds the first's output open
    [Fact]
    public async Task A_CHILD_STARTED_DURING_ANOTHER_HOLDS_NONE_OF_ITS_PIPES()
    {
        var started = Path.Combine(Project, "slow-started.txt");
        var slow = Run($"Set-Content -Path '{started}' -Value 1; Start-Sleep 6; 'slow'");
        //the first child runs once its marker exists, so the second starts while the first holds its pipes
        Waits.Until(() => File.Exists(started), () => $"the first child never wrote {started}");
        var clock = Stopwatch.StartNew();
        var quick = await Run("'quick'");
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(4), clock.Elapsed.ToString());
        Assert.DoesNotContain(ShellTool.PipeHeldNote, quick.Text, StringComparison.Ordinal);
        var done = await slow;
        Assert.DoesNotContain(ShellTool.PipeHeldNote, done.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void THE_OFF_PATH_KEEPS_TODAY_S_START_AND_PRELUDE()
    {
        var psi = ShellTool.StartInfo("'x'", _root);
        Assert.True(psi.RedirectStandardInput && psi.RedirectStandardOutput && psi.RedirectStandardError && psi.CreateNoWindow);
        Assert.False(psi.UseShellExecute);
        Assert.Equal(["-NoProfile", "-NonInteractive", "-Command"], psi.ArgumentList.Take(3));
        Assert.Equal(
            "$__e = New-Object System.Text.UTF8Encoding $false; [Console]::InputEncoding = $__e; [Console]::OutputEncoding = $__e; $OutputEncoding = $__e;\n'x'",
            psi.ArgumentList[3]);
        Assert.Equal("$OutputEncoding = [Text.Encoding]::UTF8;\n'x'", PowerShellUtf8.WithPrelude("'x'", sandbox: true));
    }

    [Fact]
    public async Task A_STATE_THAT_IS_OFF_TAKES_THE_OFF_PATH()
    {
        using var off = new SandboxState(Project, Path.Combine(_root, "home"), []);
        var r = await Run($"Set-Content -Path '{Path.Combine(Outside, "free.txt")}' -Value x", state: off);
        Assert.False(r.IsError, r.Text);
        Assert.True(File.Exists(Path.Combine(Outside, "free.txt")));
        var refused = await Run("Write-Error -Message 'x' -Category PermissionDenied", state: off);
        Assert.DoesNotContain("sandbox:", refused.Text, StringComparison.Ordinal);
    }
}
