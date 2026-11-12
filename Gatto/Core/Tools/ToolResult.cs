using Gatto.Core.Client;

namespace Gatto.Core.Tools;

//the view is display only, it travels beside the gloss and never into a request. images reach the model on a user record the loop writes after the round
public sealed record ToolResult(string Text, bool IsError = false, string? Gloss = null, EditView? View = null,
    IReadOnlyList<ImageRef>? Images = null)
{
    //the tool sent requests of its own to the session's server, set by a built-in tool only since a script cannot name it
    internal bool Prompted { get; init; }
}
