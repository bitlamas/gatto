using System.Text.RegularExpressions;
using Gatto.Core.Client;

namespace Gatto.Core.Tools;

public sealed partial class ToolRegistry
{
    [GeneratedRegex(@"\A[a-z0-9_]+\z")]   //a dollar anchor also matches before a final newline
    private static partial Regex NamePattern();

    private readonly Dictionary<string, ITool> _tools = new();
    private readonly HashSet<string> _extensions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _origins = new(StringComparer.Ordinal);
    private readonly ArmedSources? _armed;
    private HashSet<string> _hidden = new(StringComparer.Ordinal);

    //null arms every source, so a registry built without the flag reads as it always did
    public ToolRegistry(ArmedSources? armed = null) => _armed = armed;

    //extension marks a tool an extension contributed, so /context can count the built-in and the extension definitions apart. origin names its source for the off flag
    public void Register(ITool tool, bool extension = false, string? origin = null)
    {
        if (!NamePattern().IsMatch(tool.Name))
            throw new InvalidOperationException($"invalid tool name '{tool.Name}' — must match [a-z0-9_]+");
        if (!_tools.TryAdd(tool.Name, tool))
            throw new InvalidOperationException($"duplicate tool name '{tool.Name}'");
        if (extension) _extensions.Add(tool.Name);
        if (origin is not null) _origins[tool.Name] = origin;
    }

    //a seat's role hides built-ins and never removes them, so each keeps its slot and the manifest its order
    public void Hide(IEnumerable<string> names) => _hidden = new HashSet<string>(names, StringComparer.Ordinal);

    public bool IsExtension(string name) => _extensions.Contains(name);

    public ITool? Get(string name) => _hidden.Contains(name) || !Armed(name) ? null : _tools.GetValueOrDefault(name);

    public IReadOnlyList<ToolSpec> Specs() =>
        _tools.Values.Where(t => !_hidden.Contains(t.Name) && Armed(t.Name) && t.IsAvailable)
            .Select(t => new ToolSpec(t.Name, t.Description, t.ParametersSchema)).ToList();

    //a tool of a source the user turned off is skipped beside the role's hidden set, so a role resolve brings nothing back
    private bool Armed(string name) => _armed is null || _armed.IsOn(_origins.GetValueOrDefault(name));
}
