using Gatto.Core;

namespace Gatto.Cli.Launch;

//the role a launch runs, the broken file it stepped past with that file's message, or the message a launch exits 2 on
internal sealed record LaunchRoleResult(string Role, string? BrokenName, string? BrokenMessage, string? Refusal);

//which role a launch runs when the asked one fails to load, a function of the names and the load results so a table can hold every case
internal static class LaunchRole
{
    //a role whose file fails to load runs default_role when that differs, else generalist, and an unknown name passes through to the resolver
    internal static LaunchRoleResult Resolve(string? argRole, string? defaultRole, IReadOnlyList<string> namesOnDisk, Func<string, GattoConfigException?> tryLoad)
    {
        var launchRole = argRole ?? defaultRole ?? "generalist";
        if (namesOnDisk.FirstOrDefault(n => string.Equals(n, launchRole, StringComparison.OrdinalIgnoreCase)) is { } onDisk
            && tryLoad(onDisk) is { } ex)
        {
            var fallback = defaultRole is { } dr && !string.Equals(dr, onDisk, StringComparison.OrdinalIgnoreCase) ? dr : "generalist";
            //the fallback is the broken file itself, so nothing is left to run
            if (string.Equals(fallback, onDisk, StringComparison.OrdinalIgnoreCase)) return new(launchRole, null, null, ex.Message);
            return new(fallback, onDisk, ex.Message, null);
        }
        return new(launchRole, null, null, null);
    }
}
