using Gatto.Core.Commands;
using Gatto.Repl.Prompts;
using Gatto.Terminal;

namespace Gatto.Repl.Commands;

//a command's listing as the panel draws it and as the plain terminal prints it, pure in the listing so a test reads both without a session
internal static class ListingPanel
{
    //one option per row, keyed by its index since two rows may read alike, the label as its text
    public static IReadOnlyList<SelectOption> Options(Listing listing) =>
        [.. listing.Rows.Select((r, i) => new SelectOption(r.Label, Key: i.ToString(System.Globalization.CultureInfo.InvariantCulture), Columns: r.Columns))];

    //the title is the command's name, the count and the Enter verb the listing's, a digit moves the cursor since the host can't know the act is undoable
    public static PanelFields Fields(string commandName, Listing listing, string? role)
    {
        var rows = listing.Rows;
        return new PanelFields(commandName.TrimStart('/'), [new("Enter", TermText.Sanitize(listing.Verb)), new("Esc", "close")],
            Count: Gatto.Core.Plural.Of(rows.Count, TermText.Sanitize(listing.Unit)),
            Header: listing.Header is { } h ? [.. h.Skip(1).Select(TermText.Sanitize)] : null,
            LabelHeader: listing.Header is { } l ? TermText.Sanitize(l[0]) : null,
            Role: role, Under: new PanelUnder.Sentence(i => i >= 0 && i < rows.Count ? rows[i].Sentence : null), DigitsMove: true);
    }

    //the plain face's lines: the title, a table numbered from 1 with every column at its widest member, and the typed form that acts
    public static IReadOnlyList<string> Plain(string commandName, Listing listing, GlyphSet g)
    {
        var name = commandName.TrimStart('/');
        var rows = listing.Rows;
        var lines = new List<string> { $"{name} {g.Dot} {Gatto.Core.Plural.Of(rows.Count, TermText.Sanitize(listing.Unit))}" };
        var cells = rows.Select(r => (IReadOnlyList<string>)[.. new[] { r.Label }.Concat(r.Columns ?? []).Select(TermText.Sanitize)]).ToList();
        var header = listing.Header?.Select(TermText.Sanitize).ToList();
        var count = Math.Max(cells.Max(c => c.Count), header?.Count ?? 0);
        var widths = new int[count];
        foreach (var c in header is null ? cells : cells.Append(header))
            for (var k = 0; k < c.Count; k++) widths[k] = Math.Max(widths[k], UnicodeWidth.Of(c[k]));
        var numberWidth = rows.Count.ToString(System.Globalization.CultureInfo.InvariantCulture).Length + 1;
        if (header is not null) lines.Add(Row(new string(' ', numberWidth), header, widths));
        for (var i = 0; i < cells.Count; i++)
            lines.Add(Row($"{i + 1}.".PadLeft(numberWidth), cells[i], widths));
        lines.Add($"type /{name} <n> to {TermText.Sanitize(listing.Verb)}");
        return lines;
    }

    //each cell padded to its column by display width, two spaces apart, and the row's end trimmed so a short row leaves no trail
    private static string Row(string number, IReadOnlyList<string> cells, int[] widths)
    {
        var sb = new System.Text.StringBuilder(number);
        for (var k = 0; k < cells.Count; k++)
            sb.Append("  ").Append(cells[k]).Append(' ', widths[k] - UnicodeWidth.Of(cells[k]));
        return sb.ToString().TrimEnd();
    }
}
