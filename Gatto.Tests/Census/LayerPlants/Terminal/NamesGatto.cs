namespace Gatto.Tests.Census.LayerPlants.Terminal;

//a terminal type that names gatto. the library itself cannot compile one, so this is the only way to prove the census would see it.
internal static class NamesGatto
{
    internal static Type Named() => typeof(global::Gatto.Cli.GattoApp);
}
