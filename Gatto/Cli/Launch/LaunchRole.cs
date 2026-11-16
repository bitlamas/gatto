using Gatto.Core;
using Gatto.Roles;

namespace Gatto.Cli.Launch;

//the role a launch runs, the broken file it stepped past with that file's message, or the message a launch exits 2 on
internal sealed record LaunchRoleResult(string Role, string? BrokenName, string? BrokenMessage, string? Refusal);

//which role a launch runs when the asked one fails to load, a function of the names and the load results so a table can hold every case
internal static class LaunchRole
{
    //a role whose file fails to load runs default_role when that differs, else the seat with no role, and an unknown typed name passes through to the resolver
    internal static LaunchRoleResult Resolve(string? argRole, string? defaultRole, IReadOnlyList<string> namesOnDisk, Func<string, GattoConfigException?> tryLoad)
    {
        //a default_role of default names the seat with no role, which is what an absent key means
        if (RoleFile.IsDefault(defaultRole)) defaultRole = null;
        string? OnDisk(string name) => namesOnDisk.FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        //the config's word is refused in its own sentence, while a typed unknown name keeps the resolver's
        var missingDefault = defaultRole is { } named && OnDisk(named) is null
            ? $"default_role names '{named}', and {Path.Combine("roles", named + ".json")} does not exist" : null;
        var launchRole = argRole ?? defaultRole ?? RoleFile.DefaultName;
        if (argRole is null && missingDefault is not null) return new(launchRole, null, null, missingDefault);
        if (OnDisk(launchRole) is { } onDisk && tryLoad(onDisk) is { } ex)
        {
            var fallback = defaultRole is { } dr && !string.Equals(dr, onDisk, StringComparison.OrdinalIgnoreCase) ? dr : RoleFile.DefaultName;
            if (missingDefault is not null && fallback == defaultRole) return new(launchRole, onDisk, ex.Message, missingDefault);
            return new(fallback, onDisk, ex.Message, null);
        }
        return new(launchRole, null, null, null);
    }
}
