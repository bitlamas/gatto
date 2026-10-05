using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Gatto.Tests;

//every temp folder a test makes lands under one folder per run, since hundreds of tests make one and many never delete it
internal static class TestTempRoot
{
    internal const string Prefix = "gatto-run-";

    internal static string? Root { get; private set; }

    //runs before any test touches the temp path, and a child process the run starts inherits the same folder
    [ModuleInitializer]
    internal static void Redirect()
    {
        var system = Path.GetTempPath();
        SweepFinishedRuns(system);
        Root = Directory.CreateDirectory(Path.Combine(system, Prefix + Environment.ProcessId)).FullName;
        Environment.SetEnvironmentVariable("TMP", Root);
        Environment.SetEnvironmentVariable("TEMP", Root);
    }

    //the test host ends before an exit handler can finish, so each run removes the folders of runs whose process is gone
    internal static void SweepFinishedRuns(string parent)
    {
        foreach (var dir in Directory.EnumerateDirectories(parent, Prefix + "*"))
            if (!int.TryParse(Path.GetFileName(dir).AsSpan(Prefix.Length), out var pid) || !IsRunning(pid))
                TryDelete(dir);
    }

    private static bool IsRunning(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return true; }   //a reused pid this user cannot open keeps its folder, a later sweep finds it gone
    }

    //git writes its objects read-only, which Directory.Delete refuses, so a failed delete clears the flag and tries once more
    private static void TryDelete(string dir)
    {
        if (TryDeleteOnce(dir)) return;
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        TryDeleteOnce(dir);
    }

    private static bool TryDeleteOnce(string dir)
    {
        try { Directory.Delete(dir, true); return true; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
