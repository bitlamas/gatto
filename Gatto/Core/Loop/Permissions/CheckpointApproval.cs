namespace Gatto.Core.Loop.Permissions;

//the exact call the user approved at a checkpoint pause, reference-keyed and cleared on the first match so it can't leak
public sealed class CheckpointApproval
{
    public Gatto.Core.Client.ToolCall? Call { get; set; }
}
