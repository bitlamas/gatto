namespace Gatto.Cli;

//the model a launch takes once the explicit choices are known, one order for every endpoint so a cloud and a local launch cannot disagree
internal static class LaunchModel
{
    internal readonly record struct Choice(string? Model, string? Warning);

    //explicit is -m or the role's model, already narrowed by the caller to the role that may speak for this endpoint
    internal static Choice Resolve(string endpointName, IReadOnlyList<string>? listed, string? explicitModel, string? saved)
    {
        if (explicitModel is not null) return new(explicitModel, null);
        if (listed is not { Count: > 0 }) return new(saved, null);
        if (saved is null || listed.Contains(saved, StringComparer.Ordinal)) return new(saved ?? listed[0], null);
        //a provider can rename a model, so a stale choice falls back and says so instead of refusing every launch
        return new(listed[0], $"the saved model '{saved}' is no longer offered by endpoint '{endpointName}'; using '{listed[0]}'");
    }

    internal static string MissingDefaultEndpoint(string name) =>
        $"default_endpoint '{name}' is not a configured endpoint and no extension contributes it; run with -e local or edit default_endpoint in gatto.json";
}
