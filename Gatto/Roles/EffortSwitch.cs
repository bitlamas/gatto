namespace Gatto.Roles;

//resolves /effort: parse the level, clamp it to the model's cap, and say when the cap lowered it
public static class EffortSwitch
{
    public static (ThinkingLevel Effective, string LevelName, string Message) Resolve(
        string levelArg, ThinkingLevel cap, string? modelId)
    {
        var requested = Thinking.Parse(levelArg);   //junk throws GattoConfigException, the caller catches it
        var effective = Thinking.Min(requested, cap);
        var name = Name(effective);
        var message = effective < requested
            ? $"effort: {name} — requested {Name(requested)}, capped by model{(modelId is null ? "" : $" '{modelId}'")}"
            : $"effort: {name}";
        return (effective, name, message);
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
