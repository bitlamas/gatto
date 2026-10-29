namespace Gatto.Core.Tools;

//the extension sources the user turned off, one flag per source name that the tools, the hooks and the commands all read
public sealed class ArmedSources
{
    private readonly object _lock = new();
    private readonly HashSet<string> _off = new(StringComparer.Ordinal);

    public ArmedSources(IEnumerable<string>? off = null)
    {
        if (off is not null) _off.UnionWith(off);
    }

    //a null origin is a built-in, which no flag turns off
    public bool IsOn(string? origin)
    {
        if (origin is null) return true;
        lock (_lock) return !_off.Contains(origin);
    }

    public void Set(string name, bool on)
    {
        lock (_lock)
        {
            if (on) _off.Remove(name);
            else _off.Add(name);
        }
    }

    public IReadOnlyList<string> Off
    {
        get { lock (_lock) return [.. _off.Order(StringComparer.Ordinal)]; }
    }
}
