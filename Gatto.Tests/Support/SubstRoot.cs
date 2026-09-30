using System.Diagnostics;

namespace Gatto.Tests.Support;

//a temp tree mounted as its own drive root, so an upward search stops there. hold a mount for one test only, since a leaked mapping outlasts the host.
internal sealed class SubstRoot : IDisposable
{
    //the real directory behind the mount, for files that must sit outside the mounted root
    internal string Backing { get; }

    //the mounted drive root, where a search from below stops
    internal string Root { get; }

    private readonly char _drive;
    private bool _disposed;

    internal static SubstRoot Mount() => new();

    private SubstRoot()
    {
        Backing = Directory.CreateTempSubdirectory("gatto-subst-").FullName;
        try
        {
            //every free letter is tried, since another process can take one between the drive list read and the subst call
            _drive = MountOnAnyFreeLetter(Backing);
        }
        catch
        {
            //the mount failed, so only the temp directory is undone here
            Directory.Delete(Backing, recursive: true);
            throw;
        }
        Root = $"{_drive}:\\";
    }

    private static char MountOnAnyFreeLetter(string backing)
    {
        var used = new HashSet<char>(DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])));
        var failures = new List<string>();
        for (var c = 'Z'; c >= 'D'; c--)
        {
            if (used.Contains(c)) continue;
            try { Subst(c, backing, mount: true); return c; }
            catch (InvalidOperationException ex) { failures.Add(ex.Message); }
        }
        throw new InvalidOperationException(
            "no free drive letter could be mounted for subst: " + string.Join(" | ", failures));
    }

    //create a directory under the mounted root, whose full path comes back
    internal string Dir(params string[] segments)
    {
        var path = Path.Combine([Root, .. segments]);
        Directory.CreateDirectory(path);
        return path;
    }

    //write a file under the mounted root, creating its folder first
    internal static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            Subst(_drive, Backing, mount: false);
        }
        finally
        {
            //this runs even if the unmount threw, so a failed unmount does not also leak the temp tree
            try { Directory.Delete(Backing, recursive: true); } catch (IOException) { }
        }
    }

    private static void Subst(char drive, string path, bool mount)
    {
        var args = mount ? $"{drive}: \"{path}\"" : $"{drive}: /D";
        var psi = new ProcessStartInfo("subst", args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var proc = Process.Start(psi)!;
        proc.WaitForExit();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException(
                $"subst {args} failed with exit code {proc.ExitCode}: {proc.StandardError.ReadToEnd()}");
    }
}

//every test class that mounts a SubstRoot joins this collection, so two classes cannot pick the same free letter
[CollectionDefinition(SubstDriveCollection.Name)]
public sealed class SubstDriveCollection
{
    public const string Name = "subst-drive";
}
