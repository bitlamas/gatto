using Gatto.Core.Loop;

namespace Gatto.Tests.Fakes;

//a composed system text with no baseline, for tests that compose no real prompt, and kept here so production has no short way to drop one
public static class Composed
{
    public static ComposedSystem Text(string text) => new(text, null);
}
