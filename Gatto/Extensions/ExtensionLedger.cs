namespace Gatto.Extensions;

//what the launch learned of each source discovery found: its name in discovery order, off at launch, or the first line of why it did not load
public sealed class ExtensionLedger
{
    private readonly List<string> _names = new();
    private readonly HashSet<string> _off = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _errors = new(StringComparer.Ordinal);

    public IReadOnlyList<string> Names => _names;

    public void Seen(string name)
    {
        if (!_names.Contains(name, StringComparer.Ordinal)) _names.Add(name);
    }

    public void Off(string name) => _off.Add(name);

    public bool IsOff(string name) => _off.Contains(name);

    //the first failure is the one the row shows, a later one is the same file's fallout
    public void Failed(string name, string line) => _errors.TryAdd(name, line.Split('\n')[0].TrimEnd('\r'));

    public string? ErrorOf(string name) => _errors.GetValueOrDefault(name);
}
