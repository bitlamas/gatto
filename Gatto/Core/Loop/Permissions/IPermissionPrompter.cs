namespace Gatto.Core.Loop.Permissions;

//the user's answer to one permission prompt
public enum PermissionAnswer
{
    //allow this one call, record nothing
    Once,
    //allow and persist a standing grant, so matching calls run unprompted later
    Always,
    //refuse, the gate throws so the loop blocks the call and the tool never runs
    Deny,
    //a cancel throws like Deny, but with the pinned cancelled message, calling it a decline would be a lie the model acts on
    Cancel,
}

//the GrantOffer is what Always persists, null when nothing safe can be offered. the Agent names the subagent, and a missing preview is never invented
public sealed record PermissionRequest(
    string Tool, string Summary, string? GrantOffer,
    string? Agent = null, IReadOnlyList<string>? PreviewLines = null, int PreviewTotalLines = 0,
    //an edit's two strings and where it lands in the file as it is now, so the prompt shows the change before the answer
    string? EditOld = null, string? EditNew = null, Gatto.Core.Tools.EditView? View = null);

//the reason on a deny goes into the gate's exception message, which the loop passes to the model so the deny can steer it
public sealed record PermissionDecision(PermissionAnswer Answer, string? Reason = null);

//a caller hands a request to this and acts on the answer, deliberately synchronous since a prompt is a blocking question
public interface IPermissionPrompter
{
    PermissionAnswer Ask(PermissionRequest request);

    //defaults to Ask with no reason, so an implementer that only defines Ask keeps its bare deny message
    PermissionDecision AskWithReason(PermissionRequest request) => new(Ask(request));
}
