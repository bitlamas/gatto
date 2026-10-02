using Gatto.Core.Loop.Permissions;
using Gatto.Core.Tools;
using Gatto.Terminal;

namespace Gatto.Repl.Render;

//a refused call's row, one spelling for the live renderer and the restore so a resumed session draws what the live one drew
internal static class Refusal
{
    private const string Blocked = "blocked: ";

    //the painted gloss of a denied or cancelled call, null for any other outcome. a reason shows dim and quoted, capped like the error preview since it goes on one row
    internal static string? Gloss(PermissionOutcomeKind? outcome, string? reason, Theme theme, GlyphSet g) => outcome switch
    {
        PermissionOutcomeKind.Denied => theme.Paint(g.Bad + " denied", Theme.Err)
            + (reason is { Length: > 0 }
                ? theme.Paint(" — \"" + TermText.TruncateCells(TermText.Sanitize(reason), 120, glyphs: g) + "\"", Theme.Dim)
                : ""),
        PermissionOutcomeKind.Cancelled => theme.Paint(g.Bad + " cancelled", Theme.Err),
        _ => null,
    };

    //the outcome a stored result holds in the gate's own words, since the session keeps the model's text and not the prompt's answer
    internal static (PermissionOutcomeKind? Outcome, string? Reason) Of(ToolResult r)
    {
        if (r.IsError && r.Text == Gatto.Core.Loop.LoopErrors.Cancelled) return (PermissionOutcomeKind.Cancelled, null);
        if (!r.IsError || !r.Text.StartsWith(Blocked, StringComparison.Ordinal)) return (null, null);
        var said = r.Text[Blocked.Length..];
        //a result hook may append a paragraph, and a reason holds no blank line since the gate strips control characters from it
        if (said.IndexOf("\n\n", StringComparison.Ordinal) is var cut and >= 0) said = said[..cut];
        if (said == PermissionGate.CancelMessage) return (PermissionOutcomeKind.Cancelled, null);
        if (said == PermissionGate.DenyNudge) return (PermissionOutcomeKind.Denied, null);
        if (said.StartsWith(PermissionGate.DenyWithReasonPrefix, StringComparison.Ordinal))
            return (PermissionOutcomeKind.Denied, said[PermissionGate.DenyWithReasonPrefix.Length..]);
        return (null, null);
    }
}
