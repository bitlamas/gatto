namespace Gatto.Extensions;

//the globals a .csx script compiles against, exposing the one Gatto member that is the whole host surface
public sealed class ScriptGlobals(GattoApi gatto)
{
    public GattoApi Gatto { get; } = gatto;
}
