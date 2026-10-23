using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Gatto.Core.Tools;

public sealed class ShellTool : ITool
{
    //the REPL's one-line error preview must skip this marker, a command with no stdout starts the result with it
    public const string StderrMarker = "--- stderr ---";

    //the note a result carries when a background child kept the pipe open, one source for the tool and the parser
    public const string PipeHeldNote = "(a background child still holds the output pipe; showing output captured so far)";

    //how long the drains wait for EOF after the process exits. generous for a flush, tiny against a turn hung on a grandchild's inherited handle
    private const int DrainGraceMs = 5_000;

    public string Name => "shell";
    public string Description => "Run a PowerShell command in the working directory. Args: command, optional timeout_ms (default 120000).";
    public JsonElement ParametersSchema => ToolArgs.Schema("""
        {"type":"object","properties":{"command":{"type":"string"},"timeout_ms":{"type":"integer"}},"required":["command"]}
        """);

    private static readonly Lazy<string> Exe = new(() =>
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            if (dir.Length > 0 && File.Exists(Path.Combine(dir.Trim(), "pwsh.exe")))
                return "pwsh.exe";
        return "powershell.exe";
    });

    //the PowerShell start settings, in one place a test can read. stdin is redirected and closed right away, so a command that reads it gets an EOF at once
    internal static ProcessStartInfo StartInfo(string command, string cwd,
        Gatto.Core.SessionIdentity? session = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = Exe.Value,
            WorkingDirectory = cwd,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        PowerShellUtf8.PinReadEncoding(psi);   //read side: decode both pipes as UTF-8
        //the session is set per call, since /new and a resume change it inside one process. with none, an inherited value is removed rather than passed on
        if (session is { } live)
        {
            psi.Environment[Gatto.Core.SessionIdentity.IdVariable] = live.IdText;
            psi.Environment[Gatto.Core.SessionIdentity.StartVariable] = live.StartText;
            if (live.Name is { } name) psi.Environment[Gatto.Core.SessionIdentity.NameVariable] = name;
            else psi.Environment.Remove(Gatto.Core.SessionIdentity.NameVariable);
        }
        else
        {
            psi.Environment.Remove(Gatto.Core.SessionIdentity.IdVariable);
            psi.Environment.Remove(Gatto.Core.SessionIdentity.StartVariable);
            psi.Environment.Remove(Gatto.Core.SessionIdentity.NameVariable);
        }
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-Command");
        //read-side decode alone is not enough, PS 5.1 writes redirected stdout through the OEM codepage. CJK becomes question marks before we read it
        psi.ArgumentList.Add(PowerShellUtf8.WithPrelude(command));
        return psi;
    }

    public async Task<ToolResult> ExecuteAsync(JsonElement args, IToolContext ctx, CancellationToken ct)
    {
        var command = ToolArgs.RequiredString(args, "command");
        var timeoutMs = ToolArgs.OptionalInt(args, "timeout_ms") ?? 120_000;

        var startInfo = StartInfo(command, ctx.Cwd, ctx.Session);
        Process proc;
        StreamReader stdout, stderr;
        IDisposable? sandboxed = null;
        string? sandboxProject = null;
        IReadOnlyList<string>? sandboxRoots = null;
        if (OperatingSystem.IsWindows() && ctx.Sandbox is { On: true } box)
        {
            sandboxProject = box.Project ?? ctx.Cwd;
            sandboxRoots = box.Roots;
            //the same child under the sandbox token, stdin already closed by the launch
            var launch = Gatto.Core.Sandbox.SandboxLaunch.Start(box, startInfo, PowerShellUtf8.WithPrelude(command, sandbox: true));
            (proc, stdout, stderr, sandboxed) = (launch.Process, launch.Stdout, launch.Stderr, launch);
        }
        else
        {
            proc = Process.Start(startInfo)!;
            //stdin is closed at once, so a command that waits for input gets an EOF
            proc.StandardInput.Close();
            (stdout, stderr) = (proc.StandardOutput, proc.StandardError);
        }
        using var launchScope = sandboxed;
        using var processScope = proc;
        //chunked drains rather than ReadToEndAsync, a grandchild keeps the pipe open so EOF can never come. no await here may be bounded only by the session token
        var outBuf = new StringBuilder();
        var errBuf = new StringBuilder();
        var gate = new object();
        var stdoutTask = DrainAsync(stdout, outBuf, gate, ct);
        var stderrTask = DrainAsync(stderr, errBuf, gate, ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeoutMs);
        try
        {
            await proc.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            if (ct.IsCancellationRequested) throw;
            return new ToolResult($"command timed out after {timeoutMs}ms", IsError: true);
        }

        //the drains can still be blocked after the process exits, so wait a short grace and take what arrived. the turn must not wait on a reader that is still blocked
        var pipeHeld = false;
        using (var drain = ctx.Trace.Open(Trace.ShellDrain, ctx.TraceParent))
        {
            var drainFrom = ctx.Trace.Enabled ? Stopwatch.GetTimestamp() : 0;
            try
            {
                await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(TimeSpan.FromMilliseconds(DrainGraceMs), ct);
            }
            catch (TimeoutException)
            {
                pipeHeld = true;
            }
            drain.Set("exit", proc.ExitCode);
            if (ctx.Trace.Enabled) drain.Set("drain_us", (Stopwatch.GetTimestamp() - drainFrom) * 1_000_000 / Stopwatch.Frequency);
            drain.Set("background_child", pipeHeld ? 1 : 0);
        }

        string outText, errText;
        lock (gate) { outText = outBuf.ToString().TrimEnd(); errText = errBuf.ToString().TrimEnd(); }
        var sb = new StringBuilder(outText);
        if (errText.Length > 0) sb.AppendLine().AppendLine(StderrMarker).Append(errText);
        //the model reads why the write failed, the error itself stays and so does the exit code's verdict
        if (sandboxProject is not null && Gatto.Core.Sandbox.SandboxRefusal.Note(errText, sandboxProject, sandboxRoots) is { } refusal)
            sb.AppendLine().Append(refusal);
        if (pipeHeld) sb.AppendLine().Append(PipeHeldNote);
        if (proc.ExitCode != 0) sb.AppendLine().Append($"(exit code {proc.ExitCode})");
        var text = ToolArgs.HeadCap(sb.ToString(), 50_000);
        return new ToolResult(text, IsError: proc.ExitCode != 0, Gloss: $"exit {proc.ExitCode}");
    }

    //read the stream in chunks into the shared builder. the caller snapshots the builder under the same gate, so a late drain is harmless
    private static async Task DrainAsync(StreamReader reader, StringBuilder into, object gate, CancellationToken ct)
    {
        var buf = new char[4096];
        while (true)
        {
            var n = await reader.ReadAsync(buf.AsMemory(), ct);
            if (n == 0) return;
            lock (gate) into.Append(buf, 0, n);
        }
    }
}
