namespace Gatto.Tests.Census.LayerPlants.High;

//the types the low plants reach. this namespace references nothing in the low one, so any edge the reader reports from here is invented.
internal class Base;

internal interface IMark;

[AttributeUsage(AttributeTargets.Class)]
internal sealed class MarkAttribute : Attribute;

internal sealed class Target
{
    internal static int Value;

    internal static void Touch() => Value++;
}
