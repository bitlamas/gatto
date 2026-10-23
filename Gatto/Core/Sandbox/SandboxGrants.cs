using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using static Gatto.Core.Sandbox.SandboxNative;

namespace Gatto.Core.Sandbox;

//a folder that could not take the grant. the message is the exception's, which names the descendant when one refused
internal sealed class SandboxPlantException(string folder, Exception inner) : Exception(inner.Message, inner)
{
    public string Folder { get; } = folder;
}

//the folders a low-integrity child may write: an inheritable low mandatory label, planted when the mode starts and removed when the session ends
[SupportedOSPlatform("windows")]
internal static class SandboxGrants
{
    public const string FailedFile = "sandbox-plant-failed";

    //low, NO_WRITE_UP, inherited by folders and files, as SDDL writes the label ACE
    public const string LowLabel = "(ML;OICI;NW;;;LW)";

    private static readonly Regex TempName = new(@"^(?<pid>\d+)-(?<start>\d{14})$", RegexOptions.CultureInvariant);

    //the repository root when the folder is inside one, since git add and git commit write there
    public static string Project(string cwd)
    {
        for (var dir = new DirectoryInfo(Path.GetFullPath(cwd)); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")) || File.Exists(Path.Combine(dir.FullName, ".git")))
                return dir.FullName;
        return Path.GetFullPath(cwd);
    }

    public static string NuGet() => Directory.CreateDirectory(NuGetPath()).FullName;

    //the path alone, since a launch that only looks for a label left behind must not make folders
    public static string NuGetPath() =>
        Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: > 0 } set
            ? Path.GetFullPath(set)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");

    //a cache of downloaded index pages, labelled rather than redirected, since a per-process cache would be cold every time
    public static string NuGetHttpCache() => Directory.CreateDirectory(NuGetHttpCachePath()).FullName;

    public static string NuGetHttpCachePath() =>
        Environment.GetEnvironmentVariable("NUGET_HTTP_CACHE_PATH") is { Length: > 0 } set
            ? Path.GetFullPath(set)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NuGet", "v3-cache");

    //the child's TEMP is redirected under this parent rather than granted, since planting on the user's took about a minute
    public static string TempParent() => Directory.CreateDirectory(TempParentPath()).FullName;

    public static string TempParentPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "gatto", "sandbox", "tmp");

    //the explicit low label, an inherited one not counting since the folder it came from can lose it
    public static bool Holds(string folder) => Label(folder).Contains(LowLabel, StringComparison.Ordinal);

    //the label part of the path's descriptor in SDDL, inherited entries marked ID, empty when it has none
    public static string Label(string path)
    {
        var error = GetNamedSecurityInfoW(path, SeFileObject, LabelSecurityInformation,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out var descriptor);
        if (error != 0) throw new Win32Exception((int)error);
        try
        {
            if (!ConvertSecurityDescriptorToStringSecurityDescriptorW(descriptor, SddlRevision1, LabelSecurityInformation, out var text, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            try { return Marshal.PtrToStringUni(text) ?? ""; }
            finally { LocalFree(text); }
        }
        finally { LocalFree(descriptor); }
    }

    //present means no write. a folder a failed plant listed is planted again, since whether its root kept the label is unknown
    public static void Plant(string folder, string homePath, Action<string>? set = null)
    {
        var full = Path.GetFullPath(folder);
        var listed = Failed(homePath).Contains(full, StringComparer.OrdinalIgnoreCase);
        if (!listed && Holds(full)) return;
        try
        {
            (set ?? SetLow)(full);
        }
        catch (Exception ex)
        {
            if (!listed) File.AppendAllText(Path.Combine(Directory.CreateDirectory(homePath).FullName, FailedFile), full + "\n");
            throw new SandboxPlantException(full, ex);
        }
        if (listed) Unlist(homePath, full);
    }

    //the project, the two NuGet folders, the temp parent and the user's own grants, in that order. the first failure stops the mode
    public static IReadOnlyList<string> PlantAll(string cwd, IReadOnlyList<string> homeGrants, string homePath,
        string nuget, string httpCache, string tempParent)
    {
        var folders = new List<string> { Project(cwd), nuget, httpCache, tempParent };
        folders.AddRange(homeGrants.Select(Path.GetFullPath));
        foreach (var folder in folders) Plant(folder, homePath);
        return folders;
    }

    //the label replaces any the folder had and reaches every descendant. the first descendant that refuses it stops the walk and is named
    private static void SetLow(string folder)
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW("S:" + LowLabel, SddlRevision1, out var descriptor, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            if (!GetSecurityDescriptorSacl(descriptor, out _, out var sacl, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            (string Path, uint Status)? refused = null;
            TreeProgress progress = (string name, uint status, ref int invoke, IntPtr _, bool _) =>
            {
                if (status == 0) return;
                refused ??= (name, status);
                invoke = ProgressCancelOperation;
            };
            var error = TreeSetNamedSecurityInfoW(folder, SeFileObject, LabelSecurityInformation, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, sacl,
                TreeSecInfoSet, progress, ProgressInvokeOnError, IntPtr.Zero);
            GC.KeepAlive(progress);
            if (refused is { } r) throw new Win32Exception((int)r.Status, $"{new Win32Exception((int)r.Status).Message.TrimEnd('.')}: {r.Path}");
            if (error != 0) throw new Win32Exception((int)error);
        }
        finally { LocalFree(descriptor); }
    }

    //the roots that still hold the label, which only a gatto that ended without its unplant leaves. a root that cannot be read is not counted
    public static IReadOnlyList<string> Residue(IEnumerable<string> roots) =>
        [.. roots.Where(Directory.Exists).Where(r =>
        {
            try { return Holds(r); }
            catch (Win32Exception) { return false; }
        })];

    //the empty label on the root takes every inherited one beneath it, then a walk strips the explicit low ones the parent no longer hands back
    public static void Unplant(string folder)
    {
        var full = Path.GetFullPath(folder);
        SetEmptyLabel(full);
        (string Path, Exception Error)? failed = null;
        //a junction is not walked, so the walk never leaves the tree it unplants
        var walk = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
        foreach (var entry in Directory.EnumerateFileSystemEntries(full, "*", walk))
        {
            try
            {
                //an explicit label above low is the user's own and stays
                if (Label(entry).Contains(";;;LW)", StringComparison.Ordinal)) SetEmptyLabel(entry);
            }
            catch (Win32Exception ex) { failed ??= (entry, ex); }
        }
        if (failed is { } f) throw new SandboxPlantException(f.Path, f.Error);
    }

    private static void SetEmptyLabel(string path)
    {
        var acl = Marshal.AllocHGlobal(8);
        try
        {
            if (!InitializeAcl(acl, 8, AclRevision)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var error = SetNamedSecurityInfoW(path, SeFileObject, LabelSecurityInformation, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, acl);
            if (error != 0) throw new Win32Exception((int)error, $"{new Win32Exception((int)error).Message.TrimEnd('.')}: {path}");
        }
        finally { Marshal.FreeHGlobal(acl); }
    }

    //named by pid and start time, so a later process given the same pid never finds old content
    public static string ProcessTemp(string parent)
    {
        using var self = Process.GetCurrentProcess();
        var name = $"{Environment.ProcessId}-{self.StartTime.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)}";
        return Directory.CreateDirectory(Path.Combine(parent, name)).FullName;
    }

    //a folder whose pid is not a running gatto is left from a process that ended. its own folder stays whatever its name says
    public static void Sweep(string parent)
    {
        if (!Directory.Exists(parent)) return;
        foreach (var dir in Directory.EnumerateDirectories(parent))
        {
            if (TempName.Match(Path.GetFileName(dir)) is not { Success: true } m
                || !int.TryParse(m.Groups["pid"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var pid)) continue;
            if (pid == Environment.ProcessId || RunningGatto(pid)) continue;
            try { Directory.Delete(dir, recursive: true); }
            catch (Exception) { }   //a file still open stays for the next sweep
        }
    }

    private static bool RunningGatto(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return string.Equals(p.ProcessName, "gatto", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static IReadOnlyList<string> Failed(string homePath)
    {
        var path = Path.Combine(homePath, FailedFile);
        return File.Exists(path)
            ? [.. File.ReadAllLines(path).Select(l => l.Trim()).Where(l => l.Length > 0)]
            : [];
    }

    private static void Unlist(string homePath, string folder)
    {
        var path = Path.Combine(homePath, FailedFile);
        var rest = Failed(homePath).Where(l => !string.Equals(l, folder, StringComparison.OrdinalIgnoreCase)).ToList();
        if (rest.Count == 0) File.Delete(path);
        else File.WriteAllText(path, string.Join("\n", rest) + "\n");
    }
}
