namespace Gatto.Core.Loop.Permissions;

//the one session switch that auto-allows every permission and skips the commit checkpoint, kept separate from --yes and --auto
public sealed class WildState
{
    public bool On { get; set; }

    //the .gatto.json that forbids wild mode in this project, null when none does. every way of turning wild on checks it
    public string? ForbiddenBy { get; init; }
}
