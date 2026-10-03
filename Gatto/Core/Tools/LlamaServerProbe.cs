using System.Text.RegularExpressions;

namespace Gatto.Core.Tools;

public enum ProbeShape { ClassicServer, NotClassic, TimedOut, Failed, DllNotFound, KnownSibling }

//what one probe of a llama-server binary found
public readonly record struct ProbeResult(
    ProbeShape Shape, string Detail, string? Ran = null, bool VcRuntimeAbsent = false,   //the probed path, so a writer stores what was verified. only the DLL-not-found result sets the vcruntime flag, every other result leaves it false
    string? Build = null, string? SiblingName = null);   //the sibling's name is set only on a KnownSibling result, since the sentence names it

//runs llama_server --version under a deadline and classifies the banner, the classic one showing version: NNNN and built with on stderr
internal static class LlamaServerProbe
{
    //read once, lazily so a missing resource throws where a caller can see it rather than inside a type initializer
    private static readonly Lazy<Gatto.Core.Acquire.LlamaSiblings> Siblings =
        new(Gatto.Core.Acquire.LlamaSiblings.Load);

    //this pattern is blind to which program answered, every tool in the release prints the same banner, so the name gate decides
    private static readonly Regex ClassicBanner = new(   //releases from 10703 print a package version, then the build and the commit, so both shapes must identify the server
        @"version:\s*(?:(?<build>\d+)\s*\([0-9a-f]+\)|[^\s(]+\s*\(build\s+(?<build>\d+),\s*commit\s+[0-9a-f]+\))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    //no default here, a new call site must pass the discriminator rather than inherit the neutral sentence
    internal static ProbeResult Interpret(
        long? exitCode, string stdout, string stderr, bool timedOut, bool vcRuntimeAbsent)
    {
        if (timedOut)
            return new(ProbeShape.TimedOut, "--version did not answer within the deadline");

        var combined = stdout + "\n" + stderr;   //the banner goes to stderr, so read both streams

        //the shape name states what was observed, and the code is compared as unchecked((uint)ec), ExitCode arrives signed
        if (exitCode is { } ec && unchecked((uint)ec) == 0xC0000135)
            return new(ProbeShape.DllNotFound,
                "exited with STATUS_DLL_NOT_FOUND — it starts and immediately stops because a "
                + "library it needs is not there",
                VcRuntimeAbsent: vcRuntimeAbsent);

        if (exitCode is not 0)
            return new(ProbeShape.Failed, $"--version exited {exitCode}");

        //read the build number out of the banner this line is already matching, no second probe of the exe is needed
        if (ClassicBanner.Match(combined) is { Success: true } banner
            && combined.Contains("built with", StringComparison.OrdinalIgnoreCase))
            return new(ProbeShape.ClassicServer, LineAt(combined, banner.Index),   //newer releases log a line before the banner, and doctor quotes this detail
                Build: banner.Groups["build"].Value);

        return new(ProbeShape.NotClassic,
            "did not identify as the classic llama-server.exe gatto drives (expected a " +
            "'version: N (sha)' or 'version: X (build N, commit sha)' banner) — if this is the unified llama.exe CLI (llama.app " +
            "installer), download the classic llama-server release instead");
    }

    private static string LineAt(string text, int index)
    {
        var start = text.LastIndexOf('\n', index) + 1;
        var end = text.IndexOf('\n', index);
        return (end < 0 ? text[start..] : text[start..end]).Trim();
    }

    //spawn llama_server and hand the output to Interpret, with the deadline as the only bound on the awaits
    public static ProbeResult Run(string exePath, TimeSpan deadline)
    {
        //the name gate runs before any spawn, and only a name the release is known to ship is refused, so a renamed server still passes
        if (Siblings.Value.IsSibling(System.IO.Path.GetFileName(exePath)) is true)
            return new(ProbeShape.KnownSibling, "a known llama.cpp release program, not the server",
                SiblingName: System.IO.Path.GetFileName(exePath));

        //ask Windows for its own system folder rather than assuming C:\Windows, read once beside the spawn
        var vcRuntimeAbsent = !VcRuntimePresent();

        var run = Capture(exePath, "--version", deadline);
        if (run.StartError is { } why) return new(ProbeShape.Failed, $"could not run: {why}");
        if (run.TimedOut) return new(ProbeShape.TimedOut, $"--version did not answer within {deadline.TotalSeconds:0}s");
        return Interpret(run.ExitCode, run.Stdout, run.Stderr, timedOut: false, vcRuntimeAbsent);
    }

    //llama-server's backend name for this device, such as Vulkan0, or null. -dev takes it, never the product name
    public static string? DeviceNamed(string exePath, string gpuName, TimeSpan deadline)
    {
        var run = Capture(exePath, "--list-devices", deadline);
        return run.StartError is null && !run.TimedOut && run.ExitCode == 0
            ? DeviceIn(run.Stdout + "\n" + run.Stderr, gpuName)
            : null;
    }

    //one listing line is two spaces, the backend name, a colon, the product name and its memory in brackets
    private static readonly Regex DeviceLine = new(@"^\s*(?<dev>[A-Za-z]+\d+):\s+(?<name>.+?)\s+\(\d+ MiB, \d+ MiB free\)\s*$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    //an exact product-name match, so a near name never pins a model to the wrong card
    internal static string? DeviceIn(string listing, string gpuName) =>
        DeviceLine.Matches(listing).FirstOrDefault(m => m.Groups["name"].Value.Trim() == gpuName.Trim())?.Groups["dev"].Value;

    //one bounded spawn for every question asked of the binary, the deadline the only bound and the whole tree killed on expiry
    private static (string? StartError, bool TimedOut, int ExitCode, string Stdout, string Stderr) Capture(
        string exePath, string arguments, TimeSpan deadline)
    {
        using var p = new System.Diagnostics.Process();
        p.StartInfo = new(exePath, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        try { p.Start(); }
        catch (Exception ex) { return (ex.Message, false, 0, "", ""); }

        var so = p.StandardOutput.ReadToEndAsync();
        var se = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit((int)deadline.TotalMilliseconds))
        {
            try { p.Kill(entireProcessTree: true); } catch { }   //kill the whole tree when the deadline expires
            return (null, true, 0, "", "");
        }
        //after exit wait a bounded grace for the streams to end, a child holding the pipes must not hang the probe
        Task.WaitAll(new Task[] { so, se }, 2000);
        return (null, false, p.ExitCode,
            so.IsCompletedSuccessfully ? so.Result : "",
            se.IsCompletedSuccessfully ? se.Result : "");
    }

    //an unreadable System32 answers present, a failed look is no evidence that the file is missing
    private static bool VcRuntimePresent()
    {
        try { return File.Exists(Path.Combine(Environment.SystemDirectory, "vcruntime140.dll")); }
        catch (Exception) { return true; }
    }
}
