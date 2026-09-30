namespace Gatto.Core.Tools;

//the view is display only, it travels beside the gloss and never into a request
public sealed record ToolResult(string Text, bool IsError = false, string? Gloss = null, EditView? View = null);
