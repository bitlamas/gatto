using Gatto.Terminal;

namespace Gatto.Tests.Fakes;

//holds the console input mode and records every write, so a test reads the final mode and the order the writes came in
internal sealed class FakeConsoleModeControl(uint initial = 0) : IConsoleModeControl
{
    public uint Mode = initial;
    public List<uint> Sets { get; } = [];
    public uint Get() => Mode;
    public void Set(uint mode) { Mode = mode; Sets.Add(mode); }
}
