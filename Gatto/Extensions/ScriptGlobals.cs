using Gatto.Core.Home;

namespace Gatto.Extensions;

//the globals a .csx script compiles against. the settings and secrets are bound to this script's name at load, since one api serves every script
public sealed class ScriptGlobals(GattoApi gatto, ExtensionSettings settings, ExtensionSecrets secrets)
{
    public GattoApi Gatto { get; } = gatto;
    public ExtensionSettings Settings { get; } = settings;
    public ExtensionSecrets Secrets { get; } = secrets;
}
