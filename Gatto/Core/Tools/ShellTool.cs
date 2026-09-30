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
    internal static ProcessStartInfo StartInfo(string command, string cwd)
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

        using var proc = Process.Start(StartInfo(command, ctx.Cwd))!;
        //stdin is closed at once, so a command that waits for input gets an EOF
        proc.StandardInput.Close();
        //chunked drains rather than ReadToEndAsync, a grandchild keeps the pipe open so EOF can never come. no await here may be bounded only by the session token
        var outBuf = new StringBuilder();
        var errBuf = new StringBuilder();
        var gate = new object();
        var stdoutTask = DrainAsync(proc.StandardOutput, outBuf, gate, ct);
        var stderrTask = DrainAsync(proc.StandardError, errBuf, gate, ct);

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
        try
        {
            await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(TimeSpan.FromMilliseconds(DrainGraceMs), ct);
        }
        catch (TimeoutException)
        {
            pipeHeld = true;
        }

        string outText, errText;
        lock (gate) { outText = outBuf.ToString().TrimEnd(); errText = errBuf.ToString().TrimEnd(); }
        var sb = new StringBuilder(outText);
        if (errText.Length > 0) sb.AppendLine().AppendLine(StderrMarker).Append(errText);
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
