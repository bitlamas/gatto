namespace Gatto.Cli.Install;

//where this gatto stands relative to being installed
internal enum InstallState
{
    //running from the install folder, so this is the installed gatto
    Installed,
    //an installed copy is on PATH but this process is a different one, so there is nothing to offer
    AlreadyInstalledElsewhere,
    //a copy is installed but its folder is off PATH, so a new terminal still can't run the command
    InstalledButNotOnPath,
    //nothing is installed, so there is something to offer
    NotInstalled,

    //this copy needs gatto.dll beside it, so installing it alone would leave a command that will not start
    DevBuild,
}

//put a copy where PATH can find it. a running exe can be renamed aside but not deleted, so it moves to a numbered aside name and a later launch sweeps those
internal static class SelfInstall
{
    private const string ExeName = "gatto.exe";
    private const string AsidePrefix = ExeName + ".old-";   //the free aside names: gatto.exe.old-1, -2, ...

    //per-user, no elevation, and the conventional home for a user-scoped program
    public static string DefaultDir() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "gatto");

    public static string ExeIn(string dir) => Path.Combine(dir, ExeName);

    //one home for whether gatto is installed, read by the weekly line, doctor and gatto update
    public static bool IsInstalled() => File.Exists(ExeIn(DefaultDir()));

    //the installed copy's version, or null when it can't be read. strip the + tail, as GattoVersion.Compute does
    public static string? InstalledVersion(string dir)
    {
        try
        {
            var exe = ExeIn(dir);
            if (!File.Exists(exe)) return null;
            var pv = System.Diagnostics.FileVersionInfo.GetVersionInfo(exe).ProductVersion;
            if (pv is not { Length: > 0 }) return null;
            var plus = pv.IndexOf('+');
            return plus >= 0 ? pv[..plus] : pv;
        }
        catch (Exception) { return null; }   //an unreadable exe is an unknown version, so return null
    }

    //what to offer, pure so every branch is testable. the frameworkDependent parameter stays required, or a caller would offer an install that cannot work
    public static InstallState Probe(string dir, string? runningExe, bool installedExeExists,
        bool dirOnPath, bool frameworkDependent)
    {
        if (SameFolder(runningExe, dir)) return InstallState.Installed;

        var state = installedExeExists
            //a copy whose folder is off PATH gets its own state, since the command still fails from a new terminal
            ? dirOnPath ? InstallState.AlreadyInstalledElsewhere : InstallState.InstalledButNotOnPath
            : InstallState.NotInstalled;

        //the DevBuild answer goes only where there would otherwise be an offer, an already installed build has none
        return frameworkDependent && state is InstallState.NotInstalled or InstallState.InstalledButNotOnPath
            ? InstallState.DevBuild
            : state;
    }

    //a launcher that needs its folder, told by the managed dll sitting beside the exe
    public static bool IsFrameworkDependent(string? runningExe)
    {
        if (runningExe is not { Length: > 0 }) return false;
        try
        {
            var dll = Path.ChangeExtension(runningExe, ".dll");
            return dll is not null && File.Exists(dll);
        }
        catch (Exception) { return false; }   //an unreadable path is no evidence of a dev build
    }

    //copies the artifact into dir with any running predecessor moved aside, null on success or the sentence naming the failure
    public static string? Apply(string sourceExe, string dir)
    {
        //the rule rather than the offer, so a caller that skips the probe still cannot install a launcher with nothing to launch
        if (IsFrameworkDependent(sourceExe))
            return "this gatto is a development build. It needs the files beside it, so it can't be "
                 + "installed by copying one file. Install a released gatto instead.";

        try
        {
            Directory.CreateDirectory(dir);
            var target = ExeIn(dir);

            //a running exe moves to the first free aside name, so one that an older process still holds never blocks the copy
            if (File.Exists(target))
            {
                var n = 1;
                while (File.Exists(Path.Combine(dir, AsidePrefix + n))) n++;
                File.Move(target, Path.Combine(dir, AsidePrefix + n));
            }

            File.Copy(sourceExe, target, overwrite: false);
            return null;
        }
        catch (Exception ex) { return $"couldn't install gatto to {dir}: {ex.Message}"; }
    }

    //best-effort removal of the numbered asides, each in its own try, silent when one is still held by an older process. the next launch tries again
    public static void SweepOld(string dir)
    {
        string[] asides;
        try { asides = Directory.GetFiles(dir, AsidePrefix + "*"); }
        catch (Exception) { return; }   //no readable folder, nothing to sweep
        foreach (var aside in asides)
        {
            if (!int.TryParse(Path.GetFileName(aside)[AsidePrefix.Length..], out var n) || n < 1) continue;
            try { File.Delete(aside); } catch (Exception) { }   //still held, the next launch tries again
        }
    }

    private static bool SameFolder(string? exePath, string dir)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return false;
        try
        {
            var a = Path.GetFullPath(Path.GetDirectoryName(exePath) ?? "").TrimEnd('\\');
            var b = Path.GetFullPath(dir).TrimEnd('\\');
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception) { return false; }
    }
}
