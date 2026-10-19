namespace Gatto.Roles.Models;

//resolves /effort: parse the level and name it
public static class EffortSwitch
{
    public static (ThinkingLevel Effective, string LevelName, string Message) Resolve(string levelArg)
    {
        var effective = Thinking.Parse(levelArg);   //junk throws GattoConfigException, the caller catches it
        var name = Name(effective);
        return (effective, name, $"effort: {name}");
    }

    public static string Name(ThinkingLevel l) => l.ToString().ToLowerInvariant();

    //on a model with no thinking levels, any real level turns reasoning on and none or off is off. the name returned is the picker's own, none or on
    public static (bool On, string Name) OnToggle(string levelArg)
    {
        var a = levelArg.Trim().ToLowerInvariant();
        if (a is "off" or "none") return (false, "none");
        //skip Parse for on (it isn't a ThinkingLevel keyword), everything else still throws on junk
        if (a != "on") Thinking.Parse(a);
        return (true, "on");
    }
}
