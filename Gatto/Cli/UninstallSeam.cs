namespace Gatto.Cli;

//the read-only probe side of an uninstall, with sibling gattos read from the machine and the deeds in UninstallActs
internal sealed record UninstallSite(
    string InstallDir, InstallFacts Facts, IReadOnlyList<int> SiblingGattos)
{
    //the real machine, resolved exactly as production did before
    public static UninstallSite Probe()
    {
        var dir = SelfInstall.DefaultDir();
        var exe = SelfInstall.ExeIn(dir);
        return new UninstallSite(dir, new InstallFacts(
            ExePresent: File.Exists(exe),
            ExeBytes: Uninstall.FileSizeOrNull(exe),
            OnPath: PathInstaller.HasEntry(dir)),
            Siblings());
    }

    //a failure to enumerate yields no siblings (a probe that can crash an uninstall is worse than one that says nothing)
    internal static IReadOnlyList<int> Siblings()
    {
        try
        {
            var me = System.Diagnostics.Process.GetCurrentProcess();
            return System.Diagnostics.Process.GetProcessesByName(me.ProcessName)
                .Select(p => p.Id).Where(id => id != me.Id).ToList();
        }
        catch (Exception) { return []; }
    }
}

//the three deeds of gatto uninstall behind a seam (each is irreversible, a test run must never reach the real registry or install folder)
internal sealed record UninstallActs(
    Func<string, string?> RemovePathEntry,   //null means success, and the off-PATH sentence is computed from this outcome
    Func<string, IReadOnlyList<string>, IReadOnlyList<RemovalFailure>> RemoveTree,   //takes the keep set the map produced, so the removal cannot derive its own
    Action<string> ArmSweeper)   //armed last, and it draws nothing
{
    public static UninstallActs Production => new(
        PathInstaller.Remove, Uninstall.RemoveTree, GattoApp.ArmSweeper);
}
