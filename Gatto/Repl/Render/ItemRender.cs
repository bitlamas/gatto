using System.Text;
using Gatto.Core;
using Gatto.Core.Tools;
using Gatto.Repl.Input;
using Gatto.Terminal;

namespace Gatto.Repl.Render;

//the transcript render primitives, shared by the live commit and a re-render. every row is SGR-self-contained so the compositor can clip a partial item
public static class ItemRender
{
    //prose is filled, the model speaking, and a tool call is hollow, the model acting. only the glyph differs, since both still take the role tint
    public static string ProseMarkerOf(GlyphSet? glyphs) => (glyphs ?? GlyphSet.Unicode).Loaded;

    //the open circle against the prose marker's filled one. both take the set's glyph, so the pair stays tellable apart in the fallback
    public static string ToolMarkerOf(GlyphSet? glyphs) => (glyphs ?? GlyphSet.Unicode).Ring;

    //only a shell command is PowerShell, so it gets syntax colour on a truecolour terminal
    public static string PaintToolArgs(string name, string args, Theme theme) =>
        name == "shell" && args.Length > 0 && theme.TrueColor
            ? theme.PaintRuns(SyntaxHighlight.Runs(args, SyntaxHighlight.Roles(CodeLanguage.PowerShell, args), 0, args.Length), Theme.ToolArgs, CodeLanguage.PowerShell)
            : theme.Paint(args, Theme.ToolArgs);

    //a bound on an argument preview far past any window width. the row is cut at the window edge, and this only stops a runaway argument
    public const int ArgsPreviewCells = 2000;

    //the command text a shell block shows, paths relativized and the profile abbreviated, one transform for the live and the committed rows
    public static string ShellCommandText(string command, string? cwd = null, string? profile = null) =>
        TermText.SanitizeProse(AbbreviateProfileEmbedded(RelativizeEmbedded(command, cwd ?? SafeCwd()), profile ?? SafeProfile()));

    //the full input a tool block opens to show. only a shell command has one, and its line breaks survive
    public static string FullArgsOf(string tool, string argumentsJson, string? cwd = null, string? profile = null)
    {
        if (tool != "shell") return "";
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(argumentsJson.Length > 0 ? argumentsJson : "{}");
            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                && doc.RootElement.TryGetProperty("command", out var command)
                && command.ValueKind == System.Text.Json.JsonValueKind.String)
                return ShellCommandText(command.GetString() ?? "", cwd, profile);
        }
        catch (System.Text.Json.JsonException) { }
        return "";
    }

    //one prose line into its tagged rows, threading the block state. a blank line opens a paragraph break, and only the wrap rows are continuations
    public static IReadOnlyList<RenderedRow> ProseLineTagged(
        string raw, Theme theme, string role, int width,
        ref BlockState block, ref bool blockOpen, ref bool pendingBlank, GlyphSet? glyphs, IReadOnlyList<SpanRole>? codeRoles = null, CodeLanguage codeLanguage = CodeLanguage.None)
    {
        if (block == BlockState.Normal && raw.Trim().Length == 0)
        {
            if (blockOpen) pendingBlank = true;
            return System.Array.Empty<RenderedRow>();
        }
        var textWidth = width - 2;
        var accent = theme.RoleTint(role);   //the output text takes the role's colour
        var r = MarkdownLine.RenderLine(raw, block, theme, textWidth, accent, glyphs, block == BlockState.Fence ? codeRoles : null, codeLanguage);
        block = r.Next;
        if (r.FenceClose) return System.Array.Empty<RenderedRow>();

        var rows = new List<RenderedRow>();
        if (pendingBlank) { rows.Add(new RenderedRow("", false)); pendingBlank = false; }   //the blank row is its own logical line
        var marker = blockOpen ? GutterWrap.Hang : theme.Paint(ProseMarkerOf(glyphs), accent) + " ";
        if (r.Spans is null)
        {
            //a horizontal rule or a fence line arrives already wrapped to the text budget. it takes the same marker and hang as prose, so a copy rejoins it the same way
            var noSpan = r.Rows ?? new[] { r.Styled ?? "" };
            for (var i = 0; i < noSpan.Count; i++)
                rows.Add(new RenderedRow((i == 0 ? marker : GutterWrap.Hang) + noSpan[i], i > 0,
                    i > 0 ? GutterWrap.Hang.Length : 0));
        }
        else
        {
            var wrapped = SpanWrap.Wrap(r.Spans, textWidth - r.FirstPrefixCells, textWidth - r.ContPrefixCells);
            for (var i = 0; i < wrapped.Count; i++)
            {
                var sb = new StringBuilder(i == 0 ? marker : GutterWrap.Hang);
                sb.Append(i == 0 ? r.FirstPrefix : r.ContPrefix)
                  .Append(r.LineStyle)
                  .Append(theme.Paint(wrapped[i], r.LineStyle, accent));
                if (r.LineStyle.Length > 0) sb.Append(Ansi.Reset);
                //a soft-wrap continuation row holds the hang plus the block's own continuation indent, and a rejoin must drop both
                rows.Add(new RenderedRow(sb.ToString(), i > 0,
                    i > 0 ? GutterWrap.Hang.Length + r.ContPrefixCells : 0));
            }
        }
        blockOpen = true;
        return rows;
    }

    //a whole prose run, one tagged line after another
    public static List<RenderedRow> ProseRunTagged(IReadOnlyList<string> rawLines, Theme theme, string role, int width,
        GlyphSet? glyphs)
    {
        var rows = new List<RenderedRow>();
        var block = BlockState.Normal;
        var blockOpen = false;
        var pendingBlank = false;

        //table regions have to be found before the per-line pass, since their columns need every row, and only their start lines are kept
        var regionStarts = new HashSet<int>();
        foreach (var (start, _) in TableParse.Regions(rawLines)) regionStarts.Add(start);
        FenceRoles? fence = null;

        for (var i = 0; i < rawLines.Count; i++)
        {
            //entering a region is gated on the fence state, since the pre-scan is fence-blind. the start set is only an optimization, the gate is what keeps it safe
            if (block == BlockState.Normal && regionStarts.Contains(i)
                && TableParse.TryParse(rawLines, i, out var spec, out var end) && spec is not null)
            {
                if (pendingBlank) { rows.Add(new RenderedRow("", false)); pendingBlank = false; }
                //a table is content, so it opens the run with the message marker like a fence, or a table-first message has no bullet
                var marker = blockOpen ? GutterWrap.Hang : theme.Paint(ProseMarkerOf(glyphs), theme.RoleTint(role)) + " ";
                var first = true;
                //the table sits under the block's hang, so it lays out at the width minus the two-cell gutter
                foreach (var r in TableLayout.Rows(spec, theme, width - GutterWrap.Hang.Length, glyphs))
                {
                    rows.Add(new RenderedRow(
                        (first ? marker : GutterWrap.Hang) + r.Text,
                        r.Continuation, GutterWrap.Hang.Length + r.PrefixCells));
                    first = false;
                }
                //prose after the table hangs under the same run, so no second marker appears
                blockOpen = true;
                i = end - 1;
                continue;
            }
            var inFence = block == BlockState.Fence;
            rows.AddRange(ProseLineTagged(rawLines[i], theme, role, width, ref block, ref blockOpen,
                ref pendingBlank, glyphs, inFence ? fence?.Line(i, rawLines[i]) : null, fence?.Language ?? CodeLanguage.None));
            if (!inFence && block == BlockState.Fence) fence = FenceRoles.Open(rawLines, i, theme);
        }
        return rows;
    }

    //the roles of one fenced block, classified once from its opening line to its closing line and cut per source line
    private sealed class FenceRoles(SpanRole[] roles, int firstLine, int[] offsets, CodeLanguage codeLanguage)
    {
        public CodeLanguage Language => codeLanguage;

        public IReadOnlyList<SpanRole>? Line(int index, string raw)
        {
            var k = index - firstLine;
            if (k < 0 || k >= offsets.Length) return null;
            return new ArraySegment<SpanRole>(roles, offsets[k], Math.Min(raw.Length, roles.Length - offsets[k]));
        }

        //null when the fence names no highlighted language or the terminal has no truecolour, so the fence renders literal
        public static FenceRoles? Open(IReadOnlyList<string> lines, int opening, Theme theme)
        {
            var language = SyntaxHighlight.LanguageOfFence(lines[opening]);
            if (language == CodeLanguage.None || !theme.TrueColor) return null;
            var close = opening + 1;
            while (close < lines.Count && lines[close].TrimEnd().Trim() != "```") close++;
            var offsets = new int[close - opening - 1];
            var at = 0;
            for (var k = opening + 1; k < close; k++)
            {
                offsets[k - opening - 1] = at;
                at += lines[k].Length + 1;
            }
            var body = string.Join("\n", Enumerable.Range(opening + 1, close - opening - 1).Select(k => lines[k]));
            return new FenceRoles(SyntaxHighlight.Roles(language, body), opening + 1, offsets, language);
        }
    }

    //the text form of the tagged run, one string per row
    public static List<string> ProseRun(IReadOnlyList<string> rawLines, Theme theme, string role, int width,
        GlyphSet? glyphs)
        => ProseRunTagged(rawLines, theme, role, width, glyphs).Select(r => r.Text).ToList();

    //the reasoning rows: dim-italic, hang-indented and unmarked
    public static List<RenderedRow> ReasoningRowsTagged(IReadOnlyList<string> rawLines, Theme theme, int width)
    {
        var rows = new List<RenderedRow>();
        foreach (var raw in rawLines)
            rows.AddRange(GutterWrap.RowsTagged(GutterWrap.Hang, GutterWrap.Hang, raw, width,
                s => theme.Paint(s, Theme.Thought, italic: true)));
        return rows;
    }

    //no glyph set here, since a reasoning row is dim-italic and unmarked, while the head and the collapsed summary are the marked rows
    public static List<string> ReasoningRows(IReadOnlyList<string> rawLines, Theme theme, int width)
        => ReasoningRowsTagged(rawLines, theme, width).Select(r => r.Text).ToList();

    //the bullet row, plus the ⎿ gloss row once a result arrives. a refused call tints the bullet with the error colour, and an approved one adds a scope row
    public static IReadOnlyList<string> ToolRows(string name, string args, string gloss, bool hasResult,
        Theme theme, string role, RgbColor? bulletTint = null, string? followUp = null,
        GlyphSet? glyphs = null)
    {
        var g = glyphs ?? GlyphSet.Unicode;
        //sanitize the tool name and its args, since both are model-chosen. the gloss is already painted, so sanitizing it here would strip its colour
        name = TermText.Sanitize(name);
        args = TermText.Sanitize(args);
        var bullet = theme.Paint(ToolMarkerOf(glyphs), bulletTint ?? theme.RoleTint(role)) + " " + theme.Paint(name, Theme.ToolName) + " " + PaintToolArgs(name, args, theme);
        var rows = new List<string> { bullet };
        if (hasResult)
            rows.Add(GutterWrap.Hang + theme.Paint($"{g.Elbow}", Theme.Dim) + " " + gloss);
        //the scope text is gatto's own, but it is built from a model-chosen path, so sanitize it too
        if (followUp is not null)
            rows.Add(GutterWrap.Hang + theme.Paint(TermText.Sanitize(followUp), Theme.Dim));
        return rows;
    }

    //how many rows the capped reasoning preview shows
    public const int ReasoningCap = 3;

    //the first few wrapped reasoning rows while it thinks, dim, with an affordance row below naming the expand key
    public static IReadOnlyList<RenderedRow> ReasoningCappedTagged(IReadOnlyList<string> rawLines, Theme theme, int width,
        GlyphSet? glyphs)
    {
        var rows = ReasoningRowsTagged(rawLines, theme, width).Take(ReasoningCap).ToList();
        var n = rawLines.Count;
        var g = glyphs ?? GlyphSet.Unicode;
        //the affordance names the real key, read from the keymap so the two cannot drift
        var affordance = $"  {g.MidEllipsis} {Plural.Of(n, "line")} {g.Dot} {FocusKeys.ToggleKeyName} or click to expand";
        rows.Add(new RenderedRow(theme.Paint(width > 0
            ? TermText.TruncateCells(affordance, width, g) : affordance, Theme.Thought), false));
        return rows;
    }

    public static IReadOnlyList<string> ReasoningCapped(IReadOnlyList<string> rawLines, Theme theme, int width,
        GlyphSet? glyphs)
        => ReasoningCappedTagged(rawLines, theme, width, glyphs).Select(r => r.Text).ToList();

    //the one dim row of a closed reasoning block, a chevron and the elapsed time. the chevron reads as closed, click to open
    public static IReadOnlyList<string> ReasoningCollapsedRow(TimeSpan elapsed, Theme theme, int width,
        bool clickHint = false, GlyphSet? glyphs = null)
    {
        var g = glyphs ?? GlyphSet.Unicode;
        var text = $"{g.Triangle} thought for {FormatElapsed(elapsed)}" + (clickHint ? $" {g.Dot} click to expand" : "");
        var fit = width > 0 ? TermText.TruncateCells(text, width, g) : text;
        return new[] { theme.Paint(fit, Theme.Thought) };
    }

    //the open reasoning block: a chevron header row, then the full reasoning rows. a null elapsed means the block is still streaming
    public static IReadOnlyList<RenderedRow> ReasoningExpandedTagged(IReadOnlyList<string> rawLines, TimeSpan? elapsed,
        Theme theme, int width, bool clickHint = false, GlyphSet? glyphs = null)
    {
        var g = glyphs ?? GlyphSet.Unicode;
        var head = (elapsed is { } e ? $"{g.Caret} thought for {FormatElapsed(e)}" : $"{g.Caret} thinking{g.Ellipsis}") + (clickHint ? $" {g.Dot} click to collapse" : "");
        var fit = width > 0 ? TermText.TruncateCells(head, width, g) : head;
        var rows = new List<RenderedRow> { new RenderedRow(theme.Paint(fit, Theme.Thought), false) };   //the header is its own logical line
        //the full reasoning sits under the same dim rail an expanded tool uses, which marks the open block and gives a click target
        var gutter = $"{g.Box.Vertical} ";
        rows.Add(new RenderedRow(theme.Paint(gutter, Theme.Dim), false, 0, GutterWrap.Hang.Length));
        foreach (var raw in rawLines)
            rows.AddRange(GutterWrap.RowsTagged(theme.Paint(gutter, Theme.Dim), theme.Paint(gutter, Theme.Dim),
                raw, width, s => theme.Paint(s, Theme.Thought, italic: true), theme.Paint(gutter, Theme.Dim)));
        return rows;
    }

    public static IReadOnlyList<string> ReasoningExpanded(IReadOnlyList<string> rawLines, TimeSpan? elapsed, Theme theme,
        int width, bool clickHint = false, GlyphSet? glyphs = null)
        => ReasoningExpandedTagged(rawLines, elapsed, theme, width, clickHint, glyphs).Select(r => r.Text).ToList();

    //the elapsed text in the purr's grammar, from the one shared formatter
    public static string FormatElapsed(TimeSpan t) => Gatto.Core.ElapsedText.Of(t);

    //a user's fence stays literal, as do an untagged fence and tool output, so only an assistant fence with a tag is classified
    public static List<RenderedRow> UserEchoRowsTagged(IReadOnlyList<string> rawLines, Theme theme, int width,
        GlyphSet? glyphs)
    {
        var g = glyphs ?? GlyphSet.Unicode;
        var budget = width > 2 ? width - 2 : 0;
        var band = Ansi.Bg(theme.Map(Theme.UserInputBg), theme.TrueColor);
        var textOpen = Ansi.Fg(theme.Map(Theme.Bright), theme.TrueColor);
        var dimOpen = Ansi.Fg(theme.Map(Theme.Dim), theme.TrueColor);
        var baseStyle = band + textOpen;
        var rows = new List<RenderedRow>();
        var first = true;
        var inFence = false;

        void Marker(StringBuilder sb)
        {
            sb.Append(band).Append(Ansi.ClearToEol);
            if (first) { sb.Append(theme.Paint((glyphs ?? GlyphSet.Unicode).Prompt, Theme.Accent, bold: true)).Append(band).Append(' '); first = false; }
            else sb.Append(GutterWrap.Hang);
        }

        foreach (var line in rawLines)
        {
            var isFence = FenceMarker(line, out var lang);
            if (isFence && !inFence)   //the fence that opens a block
            {
                inFence = true;
                if (lang.Length > 0)   //a tagged fence gets a dim tag row on its own line
                {
                    var sb = new StringBuilder();
                    Marker(sb);
                    rows.Add(new RenderedRow(sb.Append(dimOpen).Append($"{g.Box.Vertical} ").Append(TermText.Sanitize(lang)).Append(Ansi.Reset).ToString(), false));
                }
                continue;
            }
            if (isFence && inFence && lang.Length == 0)   //the closing fence is consumed and shows nothing
            {
                inFence = false;
                continue;
            }
            if (inFence)   //a code line gets the dim rail and stays literal, soft-wrapped under it
            {
                var text = TermText.Sanitize(line);
                var segs = SoftWrap.Wrap(text, budget > 2 ? budget - 2 : 0, budget > 2 ? budget - 2 : 0);
                for (var i = 0; i < segs.Count; i++)
                {
                    var sb = new StringBuilder();
                    Marker(sb);
                    //a continuation row holds the hang and the code rail, so a rejoin drops four cells and leaves no rail behind
                    rows.Add(new RenderedRow(sb.Append(dimOpen).Append($"{g.Box.Vertical} ").Append(textOpen).Append(segs[i].Text).Append(Ansi.Reset).ToString(),
                        i > 0, i > 0 ? GutterWrap.Hang.Length + 2 : 0));
                }
                continue;
            }
            //a prose line takes inline markdown over the user band
            var spans = InlineStyler.Parse(TermText.Sanitize(line));
            var wrapped = SpanWrap.Wrap(spans, budget, budget);
            for (var i = 0; i < wrapped.Count; i++)
            {
                var sb = new StringBuilder();
                Marker(sb);
                sb.Append(textOpen).Append(theme.Paint(wrapped[i], baseStyle)).Append(Ansi.Reset);
                rows.Add(new RenderedRow(sb.ToString(), i > 0, i > 0 ? GutterWrap.Hang.Length : 0));   //the wrap chrome is the marker's own hang
            }
        }
        return rows;
    }

    public static List<string> UserEchoRows(IReadOnlyList<string> rawLines, Theme theme, int width,
        GlyphSet? glyphs)
        => UserEchoRowsTagged(rawLines, theme, width, glyphs).Select(r => r.Text).ToList();

    //a fence line starts with three or more backticks after any indent, and lang is the first word after them
    private static bool FenceMarker(string line, out string lang)
    {
        lang = "";
        var i = 0;
        while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) i++;
        var ticks = 0;
        while (i < line.Length && line[i] == '`') { ticks++; i++; }
        if (ticks < 3) return false;
        var info = line[i..].Trim();
        var sp = info.IndexOfAny(new[] { ' ', '\t' });
        lang = sp < 0 ? info : info[..sp];
        return true;
    }

    //a system line in the warn colour, with its text sanitized since it comes from the endpoint
    public static List<RenderedRow> SystemRowsTagged(string text, Theme theme, int width,
        GlyphSet? glyphs) =>
        SystemRowsCore(text, theme, width, glyphs ?? GlyphSet.Unicode);

    private static List<RenderedRow> SystemRowsCore(string text, Theme theme, int width, GlyphSet g) =>
        GutterWrap.RowsTagged(theme.Paint(g.Sharp, Theme.Warn) + " ", g.Sharp + " ",
            TermText.Sanitize(text), width, s => theme.Paint(s, Theme.Warn)).ToList();

    public static List<string> SystemRows(string text, Theme theme, int width,
        GlyphSet? glyphs) =>
        SystemRowsTagged(text, theme, width, glyphs).Select(r => r.Text).ToList();

    //the grey purred-for line, truncated to one row at the window width
    public static IReadOnlyList<string> CompletionRows(string text, Theme theme, int width,
        GlyphSet? glyphs = null)
    {
        var fit = width > 0 ? TermText.TruncateCells(text, width, glyphs) : text;
        return new[] { theme.Paint(fit, Theme.Dim) };
    }

    //already-painted rows gatto wrote, committed as they are since a re-wrap would cut their ANSI
    public static IReadOnlyList<string> Verbatim(IReadOnlyList<string> rows, int width) => rows;

    //the ⎿ gloss for a tool result, shared by the live path and replay so both build one row. an error shows ✗ and a preview, a success shows ✓ and the token count
    public static string ToolGloss(ToolResult result, Theme theme, GlyphSet? glyphs) =>
        ToolResultRow.Plain(ToolGlossParts(result, theme, glyphs), theme, glyphs ?? GlyphSet.Unicode);

    //the same row in pieces, so the link can sit between the words and the token count
    public static ToolGlossParts ToolGlossParts(ToolResult result, Theme theme, GlyphSet? glyphs)
    {
        var g = glyphs ?? GlyphSet.Unicode;
        if (result.IsError)
        {
            var lines = result.Text.Split('\n').Count(l => l.Trim().Length > 0 && l.Trim() != ShellTool.StderrMarker);
            return new ToolGlossParts("", "", ErrorPreview(result.Text), lines == 1);
        }
        var words = theme.Paint(g.Ok, Theme.Ok) + (result.Gloss is not null ? " " + theme.Paint(TermText.Sanitize(result.Gloss), Theme.Dim) : "");
        var tok = result.Text.Length > 0 ? theme.Paint(" " + g.Dot + " ~" + InputFrame.KFormat(result.Text.Length / 4) + " tok", Theme.Dim) : "";
        return new ToolGlossParts(words, tok, null, false);
    }

    //the one-line argument preview after a tool's name, driven by the keys: bodies are dropped, one identifier leads, and the rest is labelled
    public static string CompactArgs(string json, string? cwd = null, GlyphSet? glyphs = null, string? profile = null)
    {
        string flat;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json.Length > 0 ? json : "{}");
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
                return TermText.TruncateCells(json, ArgsPreviewCells, glyphs: glyphs);
            flat = Summarize(glyphs ?? GlyphSet.Unicode, doc.RootElement, cwd ?? SafeCwd(), profile ?? SafeProfile());
        }
        catch (System.Text.Json.JsonException) { flat = json; }
        return TermText.TruncateCells(flat, ArgsPreviewCells, glyphs: glyphs);
    }

    //the preview while a tool call is still streaming its arguments. it shows the path once that one property is complete, and nothing otherwise
    public static string EarlyArgsPreview(string? partialArguments, GlyphSet? glyphs = null, string? profile = null)
    {
        if (partialArguments is not { Length: > 0 }) return "";
        try
        {
            const string key = "\"path\":";
            var at = partialArguments.IndexOf(key, StringComparison.Ordinal);
            if (at < 0) return "";
            var i = at + key.Length;
            while (i < partialArguments.Length && char.IsWhiteSpace(partialArguments[i])) i++;
            if (i >= partialArguments.Length || partialArguments[i] != '"') return "";
            i++;

            var sb = new StringBuilder();
            while (i < partialArguments.Length)
            {
                var ch = partialArguments[i];
                if (ch == '\\')
                {
                    if (i + 1 >= partialArguments.Length) return "";      //the escape sequence is cut off mid-stream
                    var esc = partialArguments[i + 1];
                    sb.Append(esc switch { 'n' => '\n', 't' => '\t', 'r' => '\r', _ => esc });
                    i += 2;
                    continue;
                }
                if (ch == '"') return TermText.TruncateCells(AbbreviateProfile(Relativize(sb.ToString(), SafeCwd()), profile ?? SafeProfile()), ArgsPreviewCells, glyphs);
                sb.Append(ch);
                i++;
            }
            return "";   //the path string never closed, so the value is still arriving
        }
        catch (Exception) { return ""; }
    }

    private static string SafeCwd()
    {
        try { return Environment.CurrentDirectory; } catch (Exception) { return ""; }
    }

    private static string SafeProfile()
    {
        try { return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); } catch (Exception) { return ""; }
    }

    //file text, which never reads as a useful preview
    private static readonly HashSet<string> BodyKeys =
        new(StringComparer.OrdinalIgnoreCase) { "content", "old_string", "new_string" };

    //the argument keys that say what a call is about, where the first one seen in the JSON leads
    private static readonly HashSet<string> IdentifierKeys =
        new(StringComparer.OrdinalIgnoreCase) { "path", "file", "url", "query", "command", "pattern" };

    private static readonly HashSet<string> PathKeys =
        new(StringComparer.OrdinalIgnoreCase) { "path", "file", "root" };

    private static string Summarize(GlyphSet g, System.Text.Json.JsonElement root, string cwd, string profile)
    {
        string? identifier = null;
        string? firstScalarFallback = null;
        var fallbackAt = -1;                 //the index of the fallback in modifiers, tracked as it is added instead of searched for
                                             //a label such as timeout_ms does not end with its own value, so a search would miss and RemoveAt(-1) would throw
        int? offset = null, limit = null;
        var modifiers = new List<string>();

        foreach (var prop in root.EnumerateObject())
        {
            if (BodyKeys.Contains(prop.Name)) continue;

            if (prop.Value.ValueKind == System.Text.Json.JsonValueKind.Number)
            {
                if (prop.Name.Equals("offset", StringComparison.OrdinalIgnoreCase)
                    && prop.Value.TryGetInt32(out var o)) { offset = o; continue; }
                if (prop.Name.Equals("limit", StringComparison.OrdinalIgnoreCase)
                    && prop.Value.TryGetInt32(out var l)) { limit = l; continue; }
            }

            var text = Scalar(prop.Value);
            if (text is null) continue;                      //an object or an array has no one-line form
            if (PathKeys.Contains(prop.Name)) text = AbbreviateProfile(Relativize(text, cwd), profile);
            //a command is free text with paths inside it, so it takes the embedded rule. this is display only, since the raw record is what runs
            else if (prop.Name.Equals("command", StringComparison.OrdinalIgnoreCase))
                text = AbbreviateProfileEmbedded(RelativizeEmbedded(text, cwd), profile);

            if (identifier is null && IdentifierKeys.Contains(prop.Name)) { identifier = text; continue; }
            if (firstScalarFallback is null) { firstScalarFallback = text; fallbackAt = modifiers.Count; }
            modifiers.Add(LabelModifier(g, prop.Name, text));
        }

        //an extension tool has its own vocabulary, so promote its first scalar to lead the row
        if (identifier is null && firstScalarFallback is not null)
        {
            identifier = firstScalarFallback;
            modifiers.RemoveAt(fallbackAt);
        }

        if (LineWindow(g, offset, limit) is { } window) modifiers.Insert(0, window);

        var parts = new List<string>();
        if (identifier is { Length: > 0 }) parts.Add(identifier);
        if (modifiers.Count > 0) parts.Add("(" + string.Join(", ", modifiers) + ")");
        return string.Join(" ", parts);
    }

    //the read_file window is a 1-based inclusive line range, so an offset of 240 with a limit of 50 means lines 240 to 289
    private static string? LineWindow(GlyphSet g, int? offset, int? limit) => (offset, limit) switch
    {
        //the Range glyph member, since both sets draw a dash and only the writer knows which one a site means
        (int o, int l) when l > 0 => $"lines {o}{g.Range}{o + l - 1}",
        (int o, _) => $"from line {o}",
        (null, int l) => $"first {Plural.Of(l, "line")}",
        _ => null,
    };

    private static string LabelModifier(GlyphSet g, string key, string value) => key.ToLowerInvariant() switch
    {
        "count" => $"{g.Times}" + value,
        "timeout_ms" => value + "ms",
        "root" => "in " + value,
        _ => key + " " + value,
    };

    private static string? Scalar(System.Text.Json.JsonElement v) => v.ValueKind switch
    {
        System.Text.Json.JsonValueKind.String => v.GetString() ?? "",
        System.Text.Json.JsonValueKind.Number or System.Text.Json.JsonValueKind.True
            or System.Text.Json.JsonValueKind.False => v.GetRawText(),
        _ => null,
    };

    //the one path-shortening rule for every surface that shows a file path, so no second copy of it appears
    public static string RelativePath(string path, string? cwd = null) => Relativize(path, cwd ?? SafeCwd());

    //a path under the working directory renders relative to it, and anything else stays absolute. the match ignores case, and the result is display only
    private static string Relativize(string path, string cwd)
    {
        if (cwd.Length == 0) return path;
        try
        {
            if (!Path.IsPathRooted(path)) return path;
            var full = Path.GetFullPath(path);
            var root = Path.GetFullPath(cwd);
            if (root.Length == 0) return path;
            if (root[^1] != Path.DirectorySeparatorChar) root += Path.DirectorySeparatorChar;
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full[root.Length..] : path;
        }
        catch (Exception) { return path; }   //an unparseable path is still worth showing verbatim
    }

    //the Relativize rule for text with paths inside it, where the root drops only where a path starts and a name follows
    internal static string RelativizeEmbedded(string text, string cwd) => ReplaceEmbeddedRoot(text, cwd, null);

    //a path under the profile reads from ~, and this runs after the working-directory rule so that rule wins first
    internal static string AbbreviateProfile(string path, string profile)
    {
        var root = profile.TrimEnd('\\', '/');
        if (root.Length == 0 || path.Length < root.Length || !SameRoot(path.AsSpan(0, root.Length), root)) return path;
        if (path.Length == root.Length) return "~";
        return path[root.Length] is '\\' or '/' ? "~" + path[root.Length..] : path;
    }

    internal static string AbbreviateProfileEmbedded(string text, string profile) => ReplaceEmbeddedRoot(text, profile, "~");

    //a null replacement drops the root and its separator, and any other stands in for the root
    private static string ReplaceEmbeddedRoot(string text, string root, string? replacement)
    {
        root = root.TrimEnd('\\', '/');
        if (root.Length == 0) return text;   //a root of "" would strip every leading separator

        var sb = new System.Text.StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            var sep = i + root.Length;
            var bounded = sep <= text.Length && (i == 0 || !IsPathNameChar(text[i - 1]));
            var named = bounded && sep + 1 < text.Length && (text[sep] is '\\' or '/') && StartsPathName(text[sep + 1]);
            var bare = bounded && replacement is not null && (sep == text.Length || EndsBareRoot(text[sep]));
            if ((named || bare) && SameRoot(text.AsSpan(i, root.Length), root))
            {
                if (replacement is null) i = sep + 1;
                else { sb.Append(replacement); i = sep; }
                continue;
            }
            sb.Append(text[i++]);
        }
        return sb.ToString();
    }

    private static bool IsPathNameChar(char c) =>
        char.IsLetterOrDigit(c) || c is '\\' or '/' or '.' or '_' or '-' or ':' or '?' or '~';

    //a shell delimiter such as a pipe ends the root, so only a name character lets it drop
    private static bool StartsPathName(char c) =>
        char.IsLetterOrDigit(c) || c is '.' or '_' or '-' or '~' or '*' or '$' or '%' or '@';

    //a root on its own ends at a space, a quote or a shell delimiter, so a sibling folder never reads as the profile
    private static bool EndsBareRoot(char c) => c is ' ' or '\t' or '\r' or '\n' or '"' or '\'' or ';' or '|' or ')' or ',';

    private static bool SameRoot(ReadOnlySpan<char> candidate, string root)
    {
        for (var k = 0; k < root.Length; k++)
        {
            bool same;
            if (candidate[k] is '\\' or '/') same = root[k] is '\\' or '/';
            else same = char.ToUpperInvariant(candidate[k]) == char.ToUpperInvariant(root[k]);
            if (!same) return false;
        }
        return true;
    }

    //first visible line of a tool error with control characters stripped, skipping the --- stderr --- marker a silent command starts with
    internal static string ErrorPreview(string s)
    {
        string? line = null;
        foreach (var part in s.Split('\n'))
            if (part.Trim().Length > 0 && part.Trim() != ShellTool.StderrMarker) { line = part; break; }
        if (line is null) return "";
        var sb = new StringBuilder(line.Length);
        foreach (var ch in line)
        {
            if (ch == '\t') sb.Append(' ');
            else if (ch >= 0x20 && ch != 0x7F && (ch < 0x80 || ch > 0x9F)) sb.Append(ch);
        }
        return sb.ToString();
    }
}
