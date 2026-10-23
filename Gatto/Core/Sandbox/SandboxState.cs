using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Gatto.Core.Sandbox;

//the mode for this process: off until a door turns it on, then built once and kept until exit, whatever /new, /role or a resume do
[SupportedOSPlatform("windows")]
public sealed class SandboxState(string cwd, string home, IReadOnlyList<string> grants,
    string? nugetFolder = null, string? httpCache = null, string? tempParent = null) : IDisposable   //the three folders are the machine's own unless a test names its own
{
    public bool On { get; private set; }

    //what turned it on: the --sandbox flag, gatto.json, the .gatto.json that asked, or /sandbox on
    public string? Source { get; private set; }

    //the line said instead of the consent line when a folder refused the grant, null otherwise
    public string? DidNotStart { get; private set; }

    public string? Project { get; private set; }

    //the child's TEMP and TMP, one folder per process, deleted at exit
    public string? Temp { get; private set; }

    internal SafeAccessTokenHandle? Token { get; private set; }

    //every folder this process planted, which the refusal note reads paths against and the end of the session unplants
    public IReadOnlyList<string> Roots { get; private set; } = [];

    //one kill-on-close job per sandboxed child, held until gatto ends, so a child's background process lives as long as gatto and no longer
    private readonly List<SandboxNative.JobHandle> _jobs = [];

    internal IReadOnlyList<SandboxNative.JobHandle> Jobs { get { lock (_jobs) return [.. _jobs]; } }

    //a job whose processes have all ended is closed here, so a long session holds one handle per child still running
    internal void Hold(SandboxNative.JobHandle job)
    {
        lock (_jobs)
        {
            _jobs.RemoveAll(held =>
            {
                if (!SandboxNative.QueryInformationJobObject(held, SandboxNative.JobObjectBasicAccountingInformation, out var info,
                        System.Runtime.InteropServices.Marshal.SizeOf<SandboxNative.JobBasicAccounting>(), IntPtr.Zero)
                    || info.ActiveProcesses > 0) return false;
                held.Dispose();
                return true;
            });
            _jobs.Add(job);
        }
    }

    public const string ResidueLine = "sandbox: labels left by an earlier session removed";

    //the folders a start would plant, by path alone, since a launch with the mode off must not make them
    private IEnumerable<string> PlantedSet() =>
        [SandboxGrants.Project(cwd), nugetFolder ?? SandboxGrants.NuGetPath(), httpCache ?? SandboxGrants.NuGetHttpCachePath(),
            tempParent ?? SandboxGrants.TempParentPath(), .. grants.Select(Path.GetFullPath)];

    //read on the roots alone, so a launch with nothing left says nothing and waits on nothing
    public bool HasResidue() => SandboxGrants.Residue(PlantedSet()).Count > 0;

    //a root still holding the label is what a killed gatto leaves, removed before anything plants, whether the mode turns on or not
    public string? RemoveResidue()
    {
        var left = SandboxGrants.Residue(PlantedSet());
        if (left.Count == 0) return null;
        foreach (var root in left)
        {
            try { SandboxGrants.Unplant(root); }
            catch (SandboxPlantException ex) { return $"sandbox: labels left by an earlier session were not all removed: {ex.Folder} ({ex.Message})"; }
            catch (System.ComponentModel.Win32Exception ex) { return $"sandbox: labels left by an earlier session were not all removed: {root} ({ex.Message})"; }
        }
        return ResidueLine;
    }

    //once on it stays on. a folder that cannot take the grant leaves the mode off and the session runs unsandboxed with that said
    public bool TurnOn(string source)
    {
        if (On) return true;
        var token = SandboxToken.Create();
        try
        {
            var parent = tempParent ?? SandboxGrants.TempParent();
            SandboxGrants.Sweep(parent);
            var folders = SandboxGrants.PlantAll(cwd, grants, home,
                nugetFolder ?? SandboxGrants.NuGet(), httpCache ?? SandboxGrants.NuGetHttpCache(), parent);
            Temp = SandboxGrants.ProcessTemp(parent);
            Roots = folders;
            Project = folders[0];
            Token = token;
            Source = source;
            DidNotStart = null;
            return On = true;
        }
        catch (SandboxPlantException ex)
        {
            token.Dispose();
            DidNotStart = $"sandbox did not start: {ex.Folder} could not take the grant ({ex.Message})";
            return false;
        }
        catch
        {
            token.Dispose();
            throw;
        }
    }

    //what turning the mode on and /sandbox say, in one sentence a user reads at a glance
    public string Line => On
        ? "sandbox on: the shell tool and derivatives cannot write outside the project folder, excluding folders in sandbox_grants and the NuGet cache."
        : DidNotStart ?? "sandbox off";

    //the mode cannot be turned off inside the process, so /sandbox off names what turned it on
    public string OffRefusal => $"sandbox stays on: turned on by {Source}";

    //the end of the session, after the screen is restored: nothing is said, and a root that keeps its label is found by the next launch
    public void Dispose()
    {
        Token?.Dispose();
        //closing a job ends every process still in it, before the TEMP and the labels go
        lock (_jobs)
        {
            foreach (var job in _jobs) job.Dispose();
            _jobs.Clear();
        }
        if (Temp is { } temp)
            try { Directory.Delete(temp, recursive: true); }
            catch (Exception) { }   //a file a child still holds stays for the next start's sweep
        foreach (var root in Roots)
            try { SandboxGrants.Unplant(root); }
            catch (Exception ex) when (ex is SandboxPlantException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException) { }
        Roots = [];
    }
}
