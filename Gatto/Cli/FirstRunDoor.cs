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

        try
        {
            var config = GattoConfig.Load(homePath);
            if (config.DefaultModel is not null) return false;
            //a bare launch takes the first listed model of the default endpoint, so a list with nothing saved is configured
            return !(config.Endpoints.TryGetValue(config.DefaultEndpoint, out var ep) && ep.Models is { Count: > 0 });
        }
        catch (GattoConfigException) { return false; }
    }
}
