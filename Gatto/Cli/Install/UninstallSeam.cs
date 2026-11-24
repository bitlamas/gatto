using Gatto.Cli.Inbox;

namespace Gatto.Cli.Install;

//the read-only probe side of an uninstall, with sibling gattos read from the machine and the deeds in UninstallActs
internal sealed record UninstallSite(
    string InstallDir, InstallFacts Facts, IReadOnlyList<int> SiblingGattos)
{
    //the real machine, resolved exactly as production did before, with the inbox hooks left out of the siblings
    public static UninstallSite Probe(string home)
    {
        var dir = SelfInstall.DefaultDir();
        var exe = SelfInstall.ExeIn(dir);
        return new UninstallSite(dir, new InstallFacts(
            ExePresent: File.Exists(exe),
            ExeBytes: Uninstall.FileSizeOrNull(exe),
            OnPath: PathInstaller.HasEntry(dir)),
            Siblings(home));
    }

    //the other processes of this exe, its inbox hooks under this home left out. a failure to enumerate yields no siblings (a probe that can crash an uninstall is worse than one that says nothing)
    internal static IReadOnlyList<int> Siblings(string home)
    {
        try
        {
            using var me = System.Diagnostics.Process.GetCurrentProcess();
            var running = System.Diagnostics.Process.GetProcessesByName(me.ProcessName).Select(p => p.Id);
            return Others(running, me.Id, InboxState.HookPids(home, me.ProcessName));
        }
        catch (Exception) { return []; }
    }

    //every running pid but this process and the hooks, the filter kept pure so a test hands it the pids
    internal static IReadOnlyList<int> Others(IEnumerable<int> running, int me, IReadOnlyCollection<int> hooks) =>
        [.. running.Where(id => id != me && !hooks.Contains(id))];
}

//the four deeds of gatto uninstall behind a seam (each is irreversible, a test run must never reach the real registry or install folder)
internal sealed record UninstallActs(
    Func<string, string?> RemovePathEntry,   //null means success, and the off-PATH sentence is computed from this outcome
    Func<string, IReadOnlyList<string>, IReadOnlyList<RemovalFailure>> RemoveTree,   //takes the keep set the map produced, so the removal cannot derive its own
    Action<string> ArmSweeper,   //armed last, and it draws nothing
    Action<string> EndInboxHooks)   //the hooks of this install end before anything is removed, since gatto is leaving
{
    public static UninstallActs Production => new(
        PathInstaller.Remove, Uninstall.RemoveTree, UninstallCommand.ArmSweeper,
        home => InboxState.EndHooks(home, System.Diagnostics.Process.GetCurrentProcess().ProcessName));
}
