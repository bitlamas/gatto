using Gatto.Core.Home;

namespace Gatto.Cli;

//does this machine need the setup funnel? don't gate on the home folder existing, anything that reads the home creates it
internal static class FirstRunDoor
{
    //a model on disk counts as configured, and a config we can't parse keeps the door shut. read it through GattoConfig.Load, one home for what a config means
    public static bool NotConfigured(string homePath)
    {
        if (Gatto.Roles.Model.ListIds(Path.Combine(homePath, "models")).Count > 0) return false;

        if (!File.Exists(Path.Combine(homePath, "gatto.json"))) return true;

        try { return GattoConfig.Load(homePath).DefaultModel is null; }
        catch (GattoConfigException) { return false; }
    }
}
