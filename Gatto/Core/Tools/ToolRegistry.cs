using System.Text.RegularExpressions;
using Gatto.Core.Client;

namespace Gatto.Core.Tools;

public sealed partial class ToolRegistry
{
    [GeneratedRegex("^[a-z0-9_]+$")]
    private static partial Regex NamePattern();

    private readonly Dictionary<string, ITool> _tools = new();
    private readonly HashSet<string> _extensions = new(StringComparer.Ordinal);

    //extension marks a tool an extension contributed, so /context can count the built-in and the extension definitions apart
    public void Register(ITool tool, bool extension = false)
    {
        if (!NamePattern().IsMatch(tool.Name))
            throw new InvalidOperationException($"invalid tool name '{tool.Name}' — must match [a-z0-9_]+");
        if (!_tools.TryAdd(tool.Name, tool))
            throw new InvalidOperationException($"duplicate tool name '{tool.Name}'");
        if (extension) _extensions.Add(tool.Name);
    }

    public bool IsExtension(string name) => _extensions.Contains(name);

    public ITool? Get(string name) => _tools.GetValueOrDefault(name);

    public IReadOnlyList<ToolSpec> Specs() =>
        _tools.Values.Where(t => t.IsAvailable)
            .Select(t => new ToolSpec(t.Name, t.Description, t.ParametersSchema)).ToList();
}
