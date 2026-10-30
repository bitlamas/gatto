using Gatto.Core.Loop.Permissions;
using Gatto.Repl.Prompts;
using Gatto.Terminal;

namespace Gatto.Repl.Commands;

//the /permissions panel's rows and words, pure in the store's entries so a test reads them without a session
internal static class PermissionsPanel
{
    //the two facts the store records per grant, so no column names a tool or a scope it does not hold
    public const string LabelHeader = "kind";
    public static readonly IReadOnlyList<string> Header = ["target"];

    //one row per grant in RevokeAt's order, keyed by kind and text so a shell prefix and a tool sharing a word stay two rows
    public static IReadOnlyList<SelectOption> Rows(IReadOnlyList<PermissionEntry> entries) =>
        [.. entries.Select(e =>
        {
            var kind = CommandText.PermissionKindLabel(e.Kind);
            return new SelectOption(kind, Key: kind + "\0" + e.Text, Columns: [e.Text]);
        })];

    public static string Count(int n) => Gatto.Core.Plural.Of(n, "grant");

    //the mode the list is under, said only while it holds
    public static string? Subtitle(bool wildOn, GlyphSet g) => wildOn ? $"wild on {g.Dot} prompts off this session" : null;

    //the line the typed revoke says, one body for the key and the command
    public static string Revoked(PermissionEntry e) => "revoked: " + CommandText.FormatKindAndText(e.Kind, e.Text);
}
