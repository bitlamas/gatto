using System.Text.Json;
using System.Text.RegularExpressions;
using Gatto.Core;
using Gatto.Core.Loop.Permissions;
using Gatto.Repl.Render;
using Gatto.Terminal;

namespace Gatto.Repl;

//the words of the permission prompt, sanitized here too so the unit oracles pin it. a long detail keeps head and tail, a payload hides at the end
internal static class PromptTitles
{
    //past this many detail lines the block shows the head and tail with a marker between them
    private const int DetailLineCap = 14;
    private const int DetailHeadLines = 10;
    private const int DetailTailLines = 3;

    //the cap applies to shell as well, its exemption is from elision only, a heredoc would otherwise push the options off the panel
    private const int SummaryLineCap = 20;

    internal const string GenericQuestion = "Do you want to proceed?";

    //the sizing suffix a summary may end with, anchored so a path with parentheses survives, and optional, Core may emit a bare path
    private static readonly Regex SizingSuffix =
        new(@" \(\+?\d+ (?:lines|bytes)\)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    //title, question and detail for one request. a blank search provider gives the bare Web search title, and a null cwd means the process working directory
    public static (TitleRow Title, PromptQuestion Question, IReadOnlyList<DetailRow> Detail) For(
        PermissionRequest r, string? webSearchProvider, string? cwd = null, GlyphSet? glyphs = null)
    {
        var g = glyphs ?? GlyphSet.Unicode;
        switch (r.Tool)
        {
            case "write_file" or "edit_file":
            {
                var path = SizingSuffix.Replace(r.Summary, "");
                var rel = S(ItemRender.RelativePath(path, cwd));
                var name = S(SafeFileName(path));
                var write = r.Tool == "write_file";
                var over = write && r.Existing is not null;
                //an edit shows its change, a write with no preview falls back to the generic detail block, don't invent one
                IReadOnlyList<DetailRow> detail = !write && r.EditOld is { } oldS && r.EditNew is { } newS ? ChangeRows.At(oldS, newS, r.View, g)(ChangeRows.Cap)
                    : r.PreviewLines is { Count: > 0 } preview
                        ? WithRoles(NumberedPreview(g, preview, r.PreviewTotalLines), SyntaxHighlight.LanguageOfPath(path))
                        : Detail(r, g);
                return (
                    //the path and the name go in Code spans in both rows, so the widget paints them in the inline-code style
                    new TitleRow(over ? "Overwrite file " : write ? "Write new file " : "Edit file ", Code: rel),
                    new PromptQuestion(
                        over ? "Do you want to overwrite " : write ? "Do you want to write new file " : "Do you want to edit ",
                        Code: name, After: "?"),
                    Replaces(r.Existing) is { } replaced ? [new DetailRow(replaced, Warn: true), .. detail] : detail);
            }

            //the shell command goes in the detail unelided, and past the line cap a row counts what it does not show
            case "shell":
                return ("shell command", GenericQuestion, WithRoles(Detail(r, g), CodeLanguage.PowerShell));

            case "web_search":
            {
                var parsed = TryWebSearch(r.Summary, g);
                if (parsed is null) break;
                var provider = string.IsNullOrWhiteSpace(webSearchProvider) ? null : S(webSearchProvider!);
                return (provider is null ? "Web search" : $"Web search using {provider}",
                    GenericQuestion, new DetailRow[] { parsed });
            }

            case "web_fetch":
            {
                if (TryStringArg(r.Summary, "url") is not { } url) break;
                return ("Web fetch", GenericQuestion, new DetailRow[] { S(url) });
            }
        }

        //everything else, extension tools and any web_search or web_fetch that didn't parse
        return (S(r.Tool), GenericQuestion, Detail(r, g));
    }

    //the detail at a smaller cap for a short window. an edit cuts its own change rows, every other tool cuts from the end and counts what it hides
    public static Func<int, IReadOnlyList<DetailRow>> DetailAt(PermissionRequest r, IReadOnlyList<DetailRow> detail, GlyphSet? glyphs = null)
    {
        var g = glyphs ?? GlyphSet.Unicode;
        return r is { Tool: "edit_file", EditOld: { } oldS, EditNew: { } newS } ? ChangeRows.At(oldS, newS, r.View, g) : cap => Cut(detail, cap, g);
    }

    //a warn row stays on top, then as many rows as the cap leaves, then one dim row counting every line the cut hides
    private static IReadOnlyList<DetailRow> Cut(IReadOnlyList<DetailRow> detail, int cap, GlyphSet g)
    {
        if (detail.Count <= cap) return detail;
        var pinned = detail.TakeWhile(d => d.Warn).ToList();
        if (pinned.Count >= cap) return pinned.Take(cap).ToList();   //the warning outranks the count of what is hidden
        var rest = detail.Skip(pinned.Count).ToList();
        var keep = Math.Max(0, cap - pinned.Count - 1);
        var hidden = rest.Skip(keep).Sum(d => d.Lines);
        return [.. pinned, .. rest.Take(keep), new DetailRow($"{g.Ellipsis} +{Plural.Of(hidden, "line")}", Dim: true, Lines: hidden)];
    }

    //what a write over a file replaces, null when nothing was measured, and the plain prompter says the same words
    internal static string? Replaces(ExistingFile? existing) => existing switch
    {
        { Lines: { } lines } => "replaces " + Plural.Of(lines, "line"),
        { Bytes: { } bytes } => "replaces " + Plural.Of(bytes, "byte"),
        _ => null,
    };

    //numbered preview rows, then +N lines for the rest. the count is exact, Core ends a line at its trailing newline
    private static List<DetailRow> NumberedPreview(GlyphSet g, IReadOnlyList<string> preview, int total)
    {
        var rows = new List<DetailRow>(preview.Count + 1);
        for (var i = 0; i < preview.Count; i++)
            rows.Add(new DetailRow(S(preview[i]), Gutter: $"{i + 1}  "));
        var hidden = total - preview.Count;
        if (hidden > 0) rows.Add(new DetailRow($"{g.Ellipsis} +{Plural.Of(hidden, "line")}", Dim: true, Lines: hidden));
        return rows;
    }

    //one sanitized detail row per summary line, head+tail elided past the cap unless the tool is shell
    private static List<DetailRow> Detail(PermissionRequest r, GlyphSet g)
    {
        var lines = r.Summary.Split('\n');
        var shown = Math.Min(lines.Length, SummaryLineCap);
        var rows = new List<DetailRow>(shown + 1);
        for (var i = 0; i < shown; i++) rows.Add(S(lines[i]));
        //past the cap one row counts the rest, a command is never cut silently
        if (lines.Length > SummaryLineCap)
            rows.Add(new DetailRow($"{g.Ellipsis} +{Plural.Of(lines.Length - SummaryLineCap, "line")}", Dim: true, Lines: lines.Length - SummaryLineCap));
        if (r.Tool == "shell" || rows.Count <= DetailLineCap) return rows;

        var elided = new List<DetailRow>(DetailHeadLines + 1 + DetailTailLines);
        elided.AddRange(rows.Take(DetailHeadLines));
        var middle = rows.Skip(DetailHeadLines).Take(rows.Count - DetailHeadLines - DetailTailLines).Sum(d => d.Lines);
        elided.Add(new DetailRow($"{g.MidEllipsis} +" + middle + $" lines {g.MidEllipsis}", Dim: true, Lines: middle));
        elided.AddRange(rows.Skip(rows.Count - DetailTailLines));
        return elided;
    }

    //the query and its result count, or null when the summary isn't that JSON, a failed parse falls back to the generic shape
    private static string? TryWebSearch(string summary, GlyphSet g)
    {
        if (TryStringArg(summary, "query") is not { } query) return null;
        var count = TryIntArg(summary, "count");
        return "\"" + S(query) + "\"" + (count is { } n ? $" {g.Dot} {Plural.Of(n, "result")}" : "");
    }

    private static string? TryStringArg(string json, string key)
    {
        try
        {
            using var doc = JsonDocument.Parse(json.Length > 0 ? json : "{}");
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            return doc.RootElement.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.String
                ? el.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    private static int? TryIntArg(string json, string key)
    {
        try
        {
            using var doc = JsonDocument.Parse(json.Length > 0 ? json : "{}");
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            return doc.RootElement.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.Number
                && el.TryGetInt32(out var n) ? n : null;
        }
        catch (JsonException) { return null; }
    }

    //get the file name through Path.GetFileName but guard the throw, a model-supplied path must not stop a prompt rendering
    private static string SafeFileName(string path)
    {
        try { return Path.GetFileName(path) is { Length: > 0 } n ? n : path; }
        catch (Exception) { return path; }
    }

    //one classification over the whole block, so a string that spans two rows keeps its colour on both. a dim hint row is not code
    private static List<DetailRow> WithRoles(List<DetailRow> rows, CodeLanguage language)
    {
        if (language == CodeLanguage.None) return rows;
        var text = string.Join("\n", rows.Where(row => !row.Dim).Select(row => row.Text));
        var roles = SyntaxHighlight.Roles(language, text);
        if (roles.Length != text.Length) return rows;
        var offset = 0;
        var coded = new List<DetailRow>(rows.Count);
        foreach (var row in rows)
        {
            if (row.Dim) { coded.Add(row); continue; }
            coded.Add(row with { Roles = new ArraySegment<SpanRole>(roles, offset, row.Text.Length), Language = language });
            offset += row.Text.Length + 1;
        }
        return coded;
    }

    private static string S(string s) => TermText.Sanitize(s);
}
