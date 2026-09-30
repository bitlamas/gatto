namespace Gatto.Tests.Census.LayerPlants.Low;

//each type here reaches the high namespace through exactly one channel the layer reader must see. the last one reaches nothing.
internal sealed class ViaBase : High.Base;

internal sealed class ViaInterface : High.IMark;

[High.Mark]
internal sealed class ViaAttribute;

internal sealed class ViaField
{
    internal High.Target? Held = null;
}

internal sealed class ViaProperty
{
    internal High.Target? Held { get; set; }
}

internal static class ViaParameter
{
    internal static bool Take(High.Target? t) => t is null;
}

internal static class ViaReturn
{
    internal static High.Target? Give() => null;
}

internal static class ViaLocal
{
    internal static bool Hold()
    {
        High.Target? held = null;
        return held is null;
    }
}

internal static class ViaGeneric
{
    internal static int Count(List<object> items) => items.OfType<High.Target>().Count();
}

internal static class ViaCall
{
    internal static void Call() => High.Target.Touch();
}

internal static class ViaNew
{
    internal static object Make() => new High.Target();
}

internal static class ViaLambda
{
    internal static readonly Func<object> Make = () => new High.Target();
}

internal static class ViaAsync
{
    internal static async Task<object> MakeAsync()
    {
        await Task.Yield();
        return new High.Target();
    }
}

internal static class ViaStaticField
{
    internal static int Read() => High.Target.Value;
}

internal static class ViaNothing
{
    internal static int Zero() => 0;
}
