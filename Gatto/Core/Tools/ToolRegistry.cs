using System.Text.RegularExpressions;
using Gatto.Core.Client;

namespace Gatto.Core.Tools;

public sealed partial class ToolRegistry
{
    [GeneratedRegex(@"\A[a-z0-9_]+\z")]   //a dollar anchor also matches before a final newline
    private static partial Regex NamePattern();

    //the one name rule, read by Register and by a role file's tools list so the two cannot disagree
    internal static bool IsValidName(string name) => NamePattern().IsMatch(name);

    private readonly Dictionary<string, ITool> _tools = new();
    private readonly HashSet<string> _extensions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _origins = new(StringComparer.Ordinal);
    private readonly ArmedSources? _armed;
    private HashSet<string>? _allowed;

    //null arms every source, so a registry built without the flag reads as it always did
    public ToolRegistry(ArmedSources? armed = null) => _armed = armed;

    //extension marks a tool an extension contributed, so /context can count the built-in and the extension definitions apart. origin names its source for the off flag
    public void Register(ITool tool, bool extension = false, string? origin = null)
    {
        if (!IsValidName(tool.Name))
            throw new InvalidOperationException($"invalid tool name '{tool.Name}' — must match [a-z0-9_]+");
        if (!_tools.TryAdd(tool.Name, tool))
            throw new InvalidOperationException($"duplicate tool name '{tool.Name}'");
        if (extension) _extensions.Add(tool.Name);
        if (origin is not null) _origins[tool.Name] = origin;
    }

    //the seat's whole list, null allows every name. tested on each read, so a tool registered after it is set obeys it and every tool keeps its slot
    public void Allow(IEnumerable<string>? names) => _allowed = names is null ? null : new HashSet<string>(names, StringComparer.Ordinal);

    //registration, not the seat: /context splits what Specs already allowed
    public bool IsExtension(string name) => _extensions.Contains(name);

    //the raw table, so a clash check sees a name the seat leaves out or the user turned off
    public bool IsRegistered(string name) => _tools.ContainsKey(name);

    //the names an extension registered, in registration order, whatever the seat allows
    public IReadOnlyList<string> NamesFrom(string origin) =>
        [.. _tools.Keys.Where(n => _origins.TryGetValue(n, out var o) && o == origin)];

    public ITool? Get(string name) => Holds(name) ? _tools.GetValueOrDefault(name) : null;

    public IReadOnlyList<ToolSpec> Specs() =>
        _tools.Values.Where(t => Holds(t.Name) && t.IsAvailable)
            .Select(t => new ToolSpec(t.Name, t.Description, t.ParametersSchema)).ToList();

    //a tool of a source the user turned off is skipped beside the seat's list, so a role resolve brings nothing back
    private bool Holds(string name) =>
        (_allowed is null || _allowed.Contains(name)) && (_armed is null || _armed.IsOn(_origins.GetValueOrDefault(name)));
}
