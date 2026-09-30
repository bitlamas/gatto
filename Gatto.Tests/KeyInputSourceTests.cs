using System.Collections.Concurrent;
using Gatto.Terminal;

namespace Gatto.Tests;

public class KeyInputSourceTests
{
    private sealed class FakeKeys : IKeySource
    {
        public BlockingCollection<ConsoleKeyInfo> Feed { get; } = new();
        public ConsoleKeyInfo ReadKey() => Feed.Take();
        public bool KeyAvailable => Feed.Count > 0;
    }

    [Fact]
    public void Read_wraps_each_key_as_a_KeyEvent()
    {
        var a = new ConsoleKeyInfo('a', ConsoleKey.A, false, false, false);
        var fake = new FakeKeys();
        fake.Feed.Add(a);
        var src = new KeyInputSource(fake);

        var ev = Assert.IsType<KeyEvent>(src.Read());
        Assert.Equal(a, ev.Key);
    }

    [Fact]
    public void KeyDownAvailable_delegates_to_inner_KeyAvailable()
    {
        var fake = new FakeKeys();
        var src = new KeyInputSource(fake);
        Assert.False(src.KeyDownAvailable);
        fake.Feed.Add(new ConsoleKeyInfo('x', ConsoleKey.X, false, false, false));
        Assert.True(src.KeyDownAvailable);
    }
}
