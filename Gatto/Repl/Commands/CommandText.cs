using System.Text;
using Gatto.Core.Loop.Permissions;
using Gatto.Repl.Prompts;
using Gatto.Repl.Render;
using Gatto.Terminal;

namespace Gatto.Repl.Commands;

//the text the slash commands print, built from their arguments alone so no session is needed to read it
internal static class CommandText
{
    //the 85% offer line, a function because the next step it names depends on the auto-compact setting
    public static string CompactOfferLineFor(double? autoCompactAt) =>
        autoCompactAt is double t
            ? $"context 85% full — gatto auto-compacts at {(int)Math.Round(t * 100)}%; /compact now if you would rather pick the moment"
            : "context 85% full — /compact summarizes this session and continues fresh (auto-compact is off)";

    //prompt sugar that sends the text verbatim in a turn asking the model to bank it with memory_write, so no new write path exists
    internal static string RememberPrompt(string text) =>
        "Bank the following to project memory with memory_write, choosing a slug for it (reuse an " +
        "existing slug if this revises a fact already there), stated compactly in your own words: " + text;

    //the state line /wild prints, plain text with no combining marks so the width math stays exact
    internal static string WildLine(bool on, Gatto.Terminal.GlyphSet? glyphs) => on
        ? $"wild mode on — permission prompts are OFF for this session  {(glyphs ?? Gatto.Terminal.GlyphSet.Unicode).Wild}"
        : "wild mode off — permission prompts are back on";

    //the one order both tool renders read: built-ins by name, then each extension's tools by origin, then name, all ordinal
    internal static List<ToolInfo> Ordered(IReadOnlyList<ToolInfo> tools) => tools
        .OrderBy(t => t.Origin is null ? 0 : 1)
        .ThenBy(t => t.Origin ?? "", StringComparer.Ordinal)
        .ThenBy(t => t.Name, StringComparer.Ordinal)
        .ToList();

    //every tool gatto advertises this turn with the extension that armed it, taken from the harness registration. rows group by origin, built-ins first then extensions ordinal, with a policy line after that extension's last tool
    internal static TableSpec ToolsSpec(
        IReadOnlyList<ToolInfo> tools, IReadOnlyList<(string Extension, string Line)> policy,
        Gatto.Terminal.GlyphSet? glyphs)
    {
        var prefix = PolicyPrefixOf(glyphs ?? Gatto.Terminal.GlyphSet.Unicode);
        var ordered = Ordered(tools);

        var rows = new List<IReadOnlyList<string>>();
        for (var i = 0; i < ordered.Count; i++)
        {
            var t = ordered[i];
            //sanitizing happens inside TableLayout before it measures, so nothing stripped can push the columns out of line
            rows.Add(new[]
            {
                t.Name,
                FirstLine(t.Description) + (t.Origin is null ? "" : "  (" + t.Origin + ")"),
            });

            //the last row of this origin's group, the next row belongs to another extension or there is none
            if (t.Origin is null) continue;
            var lastOfGroup = i + 1 == ordered.Count
                || !string.Equals(ordered[i + 1].Origin, t.Origin, StringComparison.Ordinal);
            if (!lastOfGroup) continue;
            foreach (var (_, line) in policy.Where(p => string.Equals(p.Extension, t.Origin, StringComparison.Ordinal)))
                rows.Add(new[] { "", prefix + line });
        }

        //a policy line for an extension that armed no tool, it joins no group and no built-in can be keyed to it
        var armed = new HashSet<string>(
            ordered.Where(t => t.Origin is not null).Select(t => t.Origin!), StringComparer.Ordinal);
        foreach (var (ext, line) in policy.Where(p => !armed.Contains(p.Extension))
                                          .OrderBy(p => p.Extension, StringComparer.Ordinal))
            rows.Add(new[] { "", prefix + line + "  (" + ext + ")" });

        //two columns with the origin inline in parentheses, a third column would strand it far from the row it belongs to
        return new(new[] { "tool", "description" }, new[] { ColumnAlign.Left, ColumnAlign.Left }, rows);
    }

    //a function of the glyph set, since a const cannot read one, and it is a render-only prefix that nothing compares against
    internal static string PolicyPrefixOf(Gatto.Terminal.GlyphSet g) => $"{g.Continue} Policy: ";   //the arrow and the capital P separate the policy row from the description above it, TableSpec has no style channel

    internal static string ToolsHeadline(int count) =>
        count.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + (count == 1 ? " tool armed this session" : " tools armed this session");

    //the plain loop's projection of the same spec, borderless, so both loops render through one core. the empty check reads both lists, an extension with policy and no tools still exists, while the headline counts tools only
    internal static string RenderToolsListFor(
        IReadOnlyList<ToolInfo> tools, IReadOnlyList<(string Extension, string Line)> policy,
        Theme theme, int width, Gatto.Terminal.GlyphSet? glyphs)
    {
        if (tools.Count == 0 && policy.Count == 0) return "no tools are armed this session";
        var sb = new StringBuilder(ToolsHeadline(tools.Count));
        foreach (var r in TableLayout.AlignedRows(ToolsSpec(tools, policy, glyphs), theme, width))
            sb.Append('\n').Append("  ").Append(r.Text);
        return sb.ToString();
    }

    //the first line of a description, with the keys of a colon-led list folded in so the row still reads as finished
    internal static string FirstLine(string description)
    {
        var text = description.Replace("\r\n", "\n").Trim();
        var nl = text.IndexOf('\n');
        if (nl < 0) return text;

        var first = text[..nl].TrimEnd();
        if (!first.EndsWith(':')) return first;

        var keys = text[(nl + 1)..].Split('\n')
            .Select(l => l.TrimStart())
            .Where(l => l.StartsWith("- ", StringComparison.Ordinal))
            .Select(l => l[2..].Split(':')[0].Trim())
            .Where(k => k.Length > 0)
            .ToList();

        return keys.Count > 0 ? first + " " + string.Join(", ", keys) + "." : first[..^1];
    }

    //how many grants the bare listing shows before it offers /permissions all, and numbering still runs against the full set
    internal const int PermissionsListCap = 10;

    //the revoke hint, shown only when there is something to revoke, and it uses the same <n> the usage line does
    internal const string PermissionsHint =
        @"/permissions revoke <n> removes one — stored in your gatto home";

    internal const string PermissionsEmpty =
        "no standing grants yet — every shell command, write, and extension tool call still prompts";

    internal static string PermissionKindLabel(PermissionKind kind) => kind switch
    {
        PermissionKind.ShellPrefix => "shell",
        PermissionKind.WriteDir => "write",
        PermissionKind.Tool => "tool",
        _ => "?",
    };

    //the wild row, /wild and Shift+Tab for this session first, the per-project persistence second
    internal static string WildRow(bool on) =>
        "wild mode: " + (on ? "on" : "off")
        + " — /wild or Shift+Tab toggles it for this session; /wild always|never persists it per project";

    //only the revoke confirmation uses this now, the listing measures its own kind column and a prose line has no column to align
    internal static string FormatKindAndText(PermissionKind kind, string text) =>
        PermissionKindLabel(kind).PadRight(6) + text;

    //the grants as three columns with no headers, numbered off the same ordering RevokeAt consumes and padded to the full count
    internal static TableSpec PermissionsSpec(PermissionStore store, bool all)
    {
        var entries = store.ListEntries();
        var numberWidth = entries.Count.ToString(System.Globalization.CultureInfo.InvariantCulture).Length;
        var shown = all ? entries.Count : Math.Min(entries.Count, PermissionsListCap);
        var rows = new List<IReadOnlyList<string>>();
        for (var i = 0; i < shown; i++)
            rows.Add(new[]
            {
                (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(numberWidth) + ".",
                PermissionKindLabel(entries[i].Kind),
                entries[i].Text,
            });
        return new TableSpec(
            new[] { "", "", "" },
            new[] { ColumnAlign.Left, ColumnAlign.Left, ColumnAlign.Left },
            rows);
    }

    //every grant numbered off the same ordering RevokeAt consumes, plus the wild row at the end, which says the session's state when there is one
    internal static string RenderPermissionsList(PermissionStore store, bool all = false,
        Gatto.Terminal.GlyphSet? glyphs = null, bool? wildOn = null)
    {
        var entries = store.ListEntries();
        var sb = new StringBuilder();
        if (entries.Count == 0)
        {
            sb.Append(PermissionsEmpty).Append('\n');
        }
        else
        {
            //the width comes from the full count, since a hidden entry's number is still a revoke key a user can type
            var shown = all ? entries.Count : Math.Min(entries.Count, PermissionsListCap);
            foreach (var r in TableLayout.AlignedRows(PermissionsSpec(store, all), new Theme(TermCaps.Plain), 200))
                sb.Append(r.Text).Append('\n');
            if (shown < entries.Count)
                sb.Append(($"{(glyphs ?? Gatto.Terminal.GlyphSet.Unicode).Ellipsis} ")).Append((entries.Count - shown).ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .Append(" more — /permissions all").Append('\n');
        }
        sb.Append(WildRow(wildOn ?? store.Wild));
        if (entries.Count > 0) sb.Append('\n').Append(PermissionsHint);
        return sb.ToString();
    }

    //the line /role prints after a successful switch
    internal static string RoleLine(string role) => "role: " + role;

    //the line /model prints after a successful switch
    internal static string ModelLine(string model) => "model: " + Gatto.Core.Models.ServedModelName.Display(model);

    //the model's files and which one it uses, three separate answers so a session without a profile never claims a count
    internal static string QuantLines(Func<IReadOnlyList<(string Quant, bool Active)>>? files,
        Gatto.Terminal.GlyphSet? glyphs)
    {
        if (files is null) return "this session has no model profile to read files from";

        var held = files();
        if (held.Count == 0) return "this model names no files";

        var g = glyphs ?? Gatto.Terminal.GlyphSet.Unicode;
        var rows = held.Select(f => f.Active ? $"  {g.Loaded} {f.Quant}   in use" : $"    {f.Quant}");
        var tail = held.Count == 1
            ? ""
            : $"{Environment.NewLine}{Environment.NewLine}/model quant <name> to use another one";
        return string.Join(Environment.NewLine, rows) + tail;
    }


    //the bare /effort line, the level in force plus the usage, and unavailable wins over every other answer
    internal static string EffortUsage(string? thinking, bool isToggle, bool unavailable) => unavailable
        ? "this model doesn't support changing the reasoning mode."
        : isToggle
            ? $"{thinking ?? "thinking off"}, usage: /effort none|on (this model has two reasoning options)"
            : thinking is null
                ? "effort: not configured, no thinking map; usage: /effort none|low|medium|high|xhigh|max"
                : $"effort: {thinking}, usage: /effort none|low|medium|high|xhigh|max";

    //a pure translation of the picker's answer, Enter persists, the s key does not and a cancelled pick applies nothing
    internal static (string LevelArg, bool Persist)? InterpretEffortPick(
        IReadOnlyList<PickerItem> items, PickOutcome outcome)
    {
        if (outcome is PickOutcome.Control { Key: 's', Cursor: var at })
            return at >= 0 && at < items.Count ? (items[at].Id, false) : null;
        return outcome.PickedRow is { } chosen ? (items[chosen].Id, true) : null;
    }





    //the gist: control characters stripped, whitespace collapsed and the text cut to 80 cells
    public static string MakeGist(string raw)
    {
        var collapsed = new System.Text.StringBuilder();
        var pendingSpace = false;
        foreach (var r in raw.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(r) || Rune.IsControl(r))
            {
                if (collapsed.Length > 0) pendingSpace = true;   //the collapsed text never starts with a space
            }
            else
            {
                if (pendingSpace) { collapsed.Append(' '); pendingSpace = false; }
                collapsed.Append(r.ToString());
            }
        }

        var outSb = new System.Text.StringBuilder();
        var cells = 0;
        foreach (var r in collapsed.ToString().EnumerateRunes())
        {
            var w = UnicodeWidth.OfRune(r);
            if (cells + w > 80) break;
            outSb.Append(r.ToString());
            cells += w;
        }
        return outSb.ToString();
    }
}
