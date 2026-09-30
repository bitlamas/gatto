namespace Gatto.Core.Loop.Permissions;

//the one session switch that auto-allows every permission and skips the commit checkpoint, kept separate from --yes and --auto
public sealed class WildState
{
    public bool On { get; set; }
}
