using System.Text.RegularExpressions;
using Gatto.Core.Client;
using Gatto.Core.Tools;
using Gatto.Terminal;

namespace Gatto.Repl.Render;

//never cache rows, render from the logical inputs at any width, keep the one blank row in LeadingBlank and leave painted rows painted
public abstract record TranscriptItem
{
    private bool _collapsed;

    //the setter bumps Rev so a selection invalidates, but only on a real change, since the auto-collapse sets the default even when it matches
    public bool Collapsed { get => _collapsed; set { if (_collapsed != value) { _collapsed = value; Rev++; } } }

    //a per-item revision that only grows, bumped by Collapsed and by an in-place content change. a live selection clears when a Rev it snapshotted diverges
    public int Rev { get; private set; }

    //bump Rev for an in-place content change, such as the open item gaining a streamed line. the extend branch of AppendOpenLine calls it
    internal void MarkContentChanged() => Rev++;

    //set at append time, true for every item but the first, and it renders as one blank row above the item
    public bool LeadingBlank { get; set; }

    //the item's rows, width-reentrant and self-contained with their own SGR codes, the blank separator row first when LeadingBlank is set
    public IReadOnlyList<string> Render(int width, Theme theme, GlyphSet? glyphs)
    {
        var body = RenderBody(width, theme, glyphs);
        if (!LeadingBlank) return body;
        var rows = new List<string>(body.Count + 1) { "" };
        rows.AddRange(body);
        return rows;
    }

    //each kind draws its own rows here, the blank separator above them comes from Render
    protected abstract IReadOnlyList<string> RenderBody(int width, Theme theme, GlyphSet? glyphs);

    //one entry per rendered row, Continuation marks a row that continues the line above and PrefixCells counts the wrap chrome a rejoin drops
    public IReadOnlyList<RowWrap> RowWraps(int width, Theme theme, GlyphSet? glyphs)
    {
        var body = RowWrapsBody(width, theme, glyphs);
        if (!LeadingBlank) return body;
        var wraps = new List<RowWrap>(body.Count + 1) { new RowWrap(false, 0) };   //the separator row is its own line
        wraps.AddRange(body);
        return wraps;
    }

    //the default, every body row is its own logical line. a wrapping item overrides it from the same tagged primitive, so rows and wrap metadata can't drift
    protected virtual IReadOnlyList<RowWrap> RowWrapsBody(int width, Theme theme, GlyphSet? glyphs) =>
        RenderBody(width, theme, glyphs).Select(_ => new RowWrap(false, 0)).ToList();
}

//an item no message can derive, saved as its own event record right after its After anchor. a null After puts it before any message
public abstract record EventItem(ChatMessage? After) : TranscriptItem;

//derivable kinds, no linkage and re-derived from chat records on replay

public sealed record AssistantBlockItem(IReadOnlyList<string> RawLines, string Role) : TranscriptItem
{
    protected override IReadOnlyList<string> RenderBody(int width, Theme theme, GlyphSet? glyphs) =>
        ItemRender.ProseRun(RawLines, theme, Role, width, glyphs);

    protected override IReadOnlyList<RowWrap> RowWrapsBody(int width, Theme theme, GlyphSet? glyphs) =>
        ItemRender.ProseRunTagged(RawLines, theme, Role, width, glyphs).Select(r => new RowWrap(r.Continuation, r.PrefixCells)).ToList();
}

//never saved and omitted on replay, a live-session item. it streams full while open, and a collapsed one renders a 1-row summary with the Elapsed at close
public sealed record ReasoningItem(IReadOnlyList<string> RawLines) : TranscriptItem
{
    //how long the reasoning block ran, stamped when the block closes
    public TimeSpan Elapsed { get; set; }

    //true while the block streams and false once it closes. a collapsed block shows the capped preview while streaming and the 1-row summary once it is closed
    public bool Streaming { get; set; } = true;

    //set when the user toggles the block by hand, so the config default is not applied at close and the choice sticks
    public bool UserToggled { get; set; }

    //the first reasoning blocks of a session show a click teaser on the summary. it is off for later blocks, the user has learned the click by then
    public bool ClickHint { get; set; }

    protected override IReadOnlyList<string> RenderBody(int width, Theme theme, GlyphSet? glyphs) =>
        (Streaming, Collapsed) switch
        {
            (true, true) => ItemRender.ReasoningCapped(RawLines, theme, width, glyphs),                          //capped first-N preview while thinking
            (false, true) => ItemRender.ReasoningCollapsedRow(Elapsed, theme, width, ClickHint, glyphs),         //the 1-row summary of a finished block
            (true, false) => ItemRender.ReasoningRows(RawLines, theme, width),                           //streaming and expanded, the plain full rows following live
            (false, false) => ItemRender.ReasoningExpanded(RawLines, Elapsed, theme, width, ClickHint, glyphs),   //expanded and closed, the ▾ thought line over the full rows
        };

    protected override IReadOnlyList<RowWrap> RowWrapsBody(int width, Theme theme, GlyphSet? glyphs) =>
        ((Streaming, Collapsed) switch
        {
            (true, true) => ItemRender.ReasoningCappedTagged(RawLines, theme, width, glyphs),
            //the 1-row summary can never wrap, so it is a single false entry and needs no tagged primitive
            (false, true) => new[] { new RenderedRow("", false) },
            (true, false) => ItemRender.ReasoningRowsTagged(RawLines, theme, width),
            (false, false) => ItemRender.ReasoningExpandedTagged(RawLines, Elapsed, theme, width, ClickHint, glyphs),
        }).Select(r => new RowWrap(r.Continuation, r.PrefixCells, r.LeadCells)).ToList();
}

//a false HasResult marks a torn-turn call with no ⎿ gloss row. the gloss arrives already painted, while Name and Args are untrusted and sanitized in Render
public sealed record ToolBlockItem(string Name, string Args, string Gloss, bool HasResult, string Role) : TranscriptItem
{
    //the full tool result text, from the live result or from the tool record on replay. its rail shows only when the block is open and the text is not empty
    public string FullResult { get; init; } = "";

    //a denied or cancelled call tints its ● with Theme.Err, an auto-approved one gets a dim follow-up row. the live path sets both, they stay null on replay
    public RgbColor? BulletTint { get; init; }

    //the dim row naming the scope an auto-approved call was granted
    public string? FollowUp { get; init; }

    //the full shell command a block opens to show, with the line breaks kept. the call supplies it, nothing is saved, and it is empty for any other tool
    public string FullArgs { get; init; } = "";

    //the unpainted gloss the tool returned, which the shell row reads its exit code from. null keeps the painted gloss as given
    public string? RawGloss { get; init; }

    private ShellView _view;
    private int? _outputTop;
    private int _commandFromTail;
    private ShellOutput? _output;

    //the open view, read only while the block is open
    public ShellView View { get => _view; set { if (_view != value) { _view = value; MarkContentChanged(); } } }

    //the first output line the window shows, the block's rest position until the user moves it
    public int OutputTop { get => _outputTop ?? RestTop; set { if (OutputTop != value) { _outputTop = value; MarkContentChanged(); } } }   //a value equal to what shows changes nothing, the rest position stays unset

    //an edit rests on its first change that adds a row, every other block at its first row
    private int RestTop => Name == "edit_file" && HasResult ? ToolBody.RestTop(Body.Rows, ToolWindow.Height) : 0;

    //back to the rest position, so a closed block or a show less opens where the block first rested
    public void ResetTop() { if (_outputTop is not null) { _outputTop = null; MarkContentChanged(); } }

    //how many rows above the tail the command window rests, so a resize keeps the tail in view
    public int CommandFromTail { get => _commandFromTail; set { if (_commandFromTail != value) { _commandFromTail = value; MarkContentChanged(); } } }

    public bool IsShell => Name == "shell" && FullArgs.Length > 0;

    //the result row's pieces, so the link sits between the words and the token count. null keeps the painted gloss with no link
    public ToolGlossParts? Parts { get; init; }

    //the link shows when opening shows text the row does not hold. a write or an edit opens to its own rows, and a search with no match opens to its own words
    private bool Linkable => !Refused && (Name is "write_file" or "edit_file" ? Body.Rows.Count > 0
        : !string.IsNullOrWhiteSpace(FullResult) && !(Name is "grep" or "glob" && FullResult == Globbing.NoMatches));

    public ToolRowLayout LinkLayout(int width, Theme theme, GlyphSet? glyphs) => LinkLayout(width, theme, glyphs, !Collapsed);

    private ToolRowLayout LinkLayout(int width, Theme theme, GlyphSet? glyphs, bool open)
    {
        //a row with no pieces has no room for a link, so what opens is left to the body
        if (Parts is null || !HasResult) return new ToolRowLayout(Gloss, HasResult ? 1 : -1, 0, 0, Opens: true);
        var g = glyphs ?? GlyphSet.Unicode;
        var parts = Grep is { } gp ? Parts with { Words = GrepRowsRender.Words(gp, theme, g), Tok = gp.Matches.Count == 0 ? "" : Parts.Tok }
            : Name == "write_file" && !Failed && WriteContent is not null ? Parts with { Words = WriteWords(Body.Rows.Count, RawGloss, theme, g) }
            : Name == "edit_file" && !Failed && Body.Diff is { } diff ? Parts with { Words = EditWords(diff, theme, g) }
            : Parts;
        return ToolResultRow.Layout(parts, Linkable, open, width, theme, g);
    }

    //the pattern of a grep call, so the open rows can mark what it matched. the call supplies it, nothing is saved
    public string? GrepPattern { get; init; }

    //the file line the first row of a read shows. the call supplies it, nothing is saved
    public int BodyStart { get; init; } = 1;

    //an edit's two strings, a write's content and the file's language, read from the call's arguments on both paths and never saved
    public string? EditOld { get; init; }
    public string? EditNew { get; init; }
    public string? WriteContent { get; init; }
    public CodeLanguage FileLanguage { get; init; }

    //where an edit landed in its file, from the result on the live path and from the record on replay
    public EditView? EditAt { get; init; }

    //the call failed, was denied or was cancelled, from the result on the live path and from the record on replay
    public bool IsError { get; init; }

    //a write or an edit that did not happen shows its result text, never the change it asked for
    private bool Failed => IsError || Parts?.Error is not null;

    private sealed record BodyKey(string Name, string FullResult, string? RawGloss, int BodyStart, string? GrepPattern, bool HasResult, bool Error,
        bool Failed, string? EditOld, string? EditNew, string? WriteContent, CodeLanguage Language, EditView? EditAt);

    //the grep parse, the body rows, the marks and a write's roles, built together from the inputs they are keyed on
    private sealed record BodyCache(BodyKey Key, GrepParse? Grep, IReadOnlyList<ToolBodyRow> Rows, Regex? Marks, SpanRole[]? Roles, int[]? Offsets, EditDiff? Diff);

    private BodyCache? _body;

    //built once and assigned in one statement, a copy made with a changed result rebuilds since the key differs
    private BodyCache Body
    {
        get
        {
            var key = new BodyKey(Name, FullResult, RawGloss, BodyStart, GrepPattern, HasResult, Parts?.Error is not null,
                Failed, EditOld, EditNew, WriteContent, FileLanguage, EditAt);
            if (_body is { } b && b.Key == key) return b;
            var grep = Name == "grep" && HasResult && Parts is { Error: null } ? GrepRows.Parse(FullResult) : null;
            var diff = Name == "edit_file" && !Failed && EditOld is { } o && EditNew is { } n ? Diff(o, n, EditAt) : null;
            IReadOnlyList<ToolBodyRow> rows = diff is not null ? ToolBody.Edit(diff, EditAt)
                : Name is "write_file" or "edit_file" ? ChangeBody()
                : Parts?.Error is not null ? ToolBody.Plain(FullResult, error: true)
                : grep is not null ? ToolBody.Grep(grep)
                : Name is "grep" or "glob" && FullResult == Globbing.NoMatches ? Array.Empty<ToolBodyRow>()
                : Name == "read_file" ? ToolBody.ReadFile(FullResult, RawGloss, BodyStart) ?? ToolBody.Plain(FullResult)
                : ToolBody.Plain(FullResult);
            SpanRole[]? roles = null;
            int[]? offsets = null;
            if (Name == "write_file" && !Failed && FileLanguage != CodeLanguage.None && rows.Count > 0)
            {
                //classified whole, so a string that spans two rows keeps its colour on both
                var clean = rows.Select(r => TermText.Sanitize(r.Text)).ToList();
                roles = SyntaxHighlight.Roles(FileLanguage, string.Join("\n", clean));
                offsets = new int[clean.Count];
                for (var i = 1; i < clean.Count; i++) offsets[i] = offsets[i - 1] + clean[i - 1].Length + 1;
            }
            var built = new BodyCache(key, grep, rows, grep is null ? null : GrepRowsRender.Marks(GrepPattern), roles, offsets, diff);
            _body = built;
            return built;
        }
    }

    //a write's content, or the result text when the call did not happen, or nothing when the arguments hold no change
    private IReadOnlyList<ToolBodyRow> ChangeBody() =>
        Failed ? ToolBody.Plain(FullResult, error: true)
        : Name == "write_file" && WriteContent is { } c ? ToolBody.Write(c)
        : Array.Empty<ToolBodyRow>();

    private static EditDiff Diff(string oldS, string newS, EditView? at)
    {
        var (o, n) = EditLocate.Lines(oldS, newS, at);
        return LineDiff.Of(o, n);
    }

    //the words of a successful edit's row, the counts taken from its diff
    private static string EditWords(EditDiff d, Theme theme, GlyphSet g) =>
        theme.Paint(g.Ok, Theme.Ok) + " " + theme.Paint($"edited {g.Dot} +{d.Added} -{d.Removed}"
            + (d.Changes > 1 ? $" {g.Dot} {Gatto.Core.Plural.Of(d.Changes, "change")}" : ""), Theme.Dim);

    //the footer's count of the changes the window does not hold whole, a change cut by its edge counted
    private static Func<int, int, string?> OtherChanges(IReadOnlyList<ToolBodyRow> body)
    {
        var runs = new List<(int Start, int End)>();
        for (var i = 0; i < body.Count; i++)
        {
            if (body[i].Kind is not (BodyKind.Added or BodyKind.Removed) || (i > 0 && body[i - 1].Kind is BodyKind.Added or BodyKind.Removed)) continue;
            var end = i;
            while (end + 1 < body.Count && body[end + 1].Kind is BodyKind.Added or BodyKind.Removed) end++;
            runs.Add((i, end));
        }
        return (top, shown) =>
        {
            var others = runs.Count(r => r.Start < top || r.End >= top + shown);
            return others > 0 ? Gatto.Core.Plural.Of(others, "other change") : null;
        };
    }

    //the painter of a write's rows, each row painted from the roles of the whole content at its own offset
    private Func<int, string, int, int, string>? WritePainter(BodyCache body, Theme theme)
    {
        if (body.Roles is not { } roles || body.Offsets is not { } offsets || !theme.TrueColor) return null;
        var language = FileLanguage;
        return (i, text, start, length) =>
            i < offsets.Length && offsets[i] + text.Length <= roles.Length
                ? theme.PaintRuns(SyntaxHighlight.Runs(text, new ArraySegment<SpanRole>(roles, offsets[i], text.Length), start, length), Theme.CodeBlockFg, language)
                : theme.Paint(text.Substring(start, length), Theme.CodeBlockFg);
    }

    //the words of a successful write's row, the count taken from the rows the window shows and the verb from the tool's gloss. a record from before the gloss said overwrote reads wrote
    private static string WriteWords(int lines, string? gloss, Theme theme, GlyphSet g) =>
        theme.Paint(g.Ok, Theme.Ok) + " " + theme.Paint(
            $"{(gloss?.StartsWith("overwrote ", StringComparison.Ordinal) == true ? "overwrote" : "wrote")} {g.Dot} {Gatto.Core.Plural.Of(lines, "line")}", Theme.Dim);

    //null for any other tool, a failed call, or text that is not the tool's
    private GrepParse? Grep => Body.Grep;

    //every tool block but a shell opens to the window
    public bool Windowed => !IsShell;

    //something to open, the window's rows for a windowed block and the result or the command for the others
    public bool HasBody => Windowed ? Body.Rows.Count > 0 : FullResult.Length > 0 || FullArgs.Length > 0;

    //a denied or a cancelled call, the two outcomes that paint the bullet red
    public bool Refused => BulletTint == Theme.Err;

    //opening must show what the closed block's result row does not hold at this width, and a refused call never opens
    public bool Opens(int width, Theme theme, GlyphSet? glyphs) =>
        !Refused && HasBody && (IsShell ? ShellLayout(width, theme, glyphs).Opens : LinkLayout(width, theme, glyphs, open: false).Opens);

    public void ResetView() { View = ShellView.Window; ResetTop(); CommandFromTail = 0; }

    //moves the output window by n notch units, a positive n toward the first row, and returns the units it used
    public int ScrollWindow(int n)
    {
        var (top, used) = ToolWindow.Step(IsShell ? ShellBlockRender.Entries(Output) : Body.Rows, OutputTop, n);
        OutputTop = top;
        return used;
    }

    //the rows and their map for every tool block, the one layout the paint, the mouse and the keys read
    public ShellBlockLayout Layout(int width, Theme theme, GlyphSet? glyphs)
    {
        if (IsShell) return ShellLayout(width, theme, glyphs);
        var g = glyphs ?? GlyphSet.Unicode;
        var link = LinkLayout(width, theme, glyphs);
        //the head rows are cut to the window here, so the layout the mouse reads is the row the frame draws
        var rows = ItemRender.ToolRows(Name, Args, link.Gloss, HasResult, theme, Role, BulletTint, FollowUp, glyphs)
            .Select(r => new RenderedRow(width > 0 ? TermText.TruncateCells(r, width, glyphs: g) : r, false)).ToList();
        var body = Body;
        int outputFirst = -1, shown = 0, footerRow = -1, actionStart = 0, actionEnd = 0;
        var action = ShellAction.None;
        var hides = body.Rows.Count > ToolWindow.Height;
        if (!Collapsed && body.Rows.Count > 0)
        {
            var unit = body.Rows.Any(r => r.Kind is BodyKind.Heading or BodyKind.Note) ? "row" : "line";
            var marks = body.Grep is not null ? GrepRowsRender.Painter(body.Marks, theme) : WritePainter(body, theme);
            var part = body.Diff is not null ? OtherChanges(body.Rows) : null;
            var window = ToolWindow.Layout(new WindowInput(body.Rows, NumbersAlways: true, unit, Marks: marks, FooterPart: part), new WindowState(View, OutputTop), width, theme, g);
            outputFirst = rows.Count;
            shown = window.Shown;
            if (window.FooterRow >= 0) footerRow = outputFirst + window.FooterRow;
            (actionStart, actionEnd, action, hides) = (window.ActionStart, window.ActionEnd, window.Action, window.Hides);
            rows.AddRange(window.Rows);
        }
        return new ShellBlockLayout(rows, -1, 0, 0, link.ResultRow, link.LinkStart, link.LinkEnd,
            outputFirst, shown, body.Rows.Count, footerRow, actionStart, actionEnd, action, hides);
    }

    //the parse is width-free and the result never changes, so it is read once
    private ShellOutput Output => _output ??= ShellOutput.Parse(FullResult);

    protected override IReadOnlyList<string> RenderBody(int width, Theme theme, GlyphSet? glyphs) =>
        Layout(width, theme, glyphs).Rows.Select(r => r.Text).ToList();

    protected override IReadOnlyList<RowWrap> RowWrapsBody(int width, Theme theme, GlyphSet? glyphs) =>
        Layout(width, theme, glyphs).Rows.Select(r => new RowWrap(r.Continuation, r.PrefixCells, r.LeadCells)).ToList();

    public ShellBlockLayout ShellLayout(int width, Theme theme, GlyphSet? glyphs) =>
        ShellBlockRender.Layout(
            new ShellBlockInput(FullArgs, Output, FullResult.Length, RawGloss, Gloss, HasResult, Role, BulletTint, FollowUp),
            new ShellBlockState(Collapsed, View, OutputTop, CommandFromTail), width, theme, glyphs ?? GlyphSet.Unicode);

}

public sealed record UserEchoItem(IReadOnlyList<string> RawLines) : TranscriptItem
{
    protected override IReadOnlyList<string> RenderBody(int width, Theme theme, GlyphSet? glyphs) =>
        ItemRender.UserEchoRows(RawLines, theme, width, glyphs);

    protected override IReadOnlyList<RowWrap> RowWrapsBody(int width, Theme theme, GlyphSet? glyphs) =>
        ItemRender.UserEchoRowsTagged(RawLines, theme, width, glyphs).Select(r => new RowWrap(r.Continuation, r.PrefixCells)).ToList();
}

//event kinds, anchored and saved as event records

//one of gatto's own listings, holding its TableSpec so the columns are measured again at any width. no chat record can rebuild one, so it is saved as an event
public sealed record ListingItem(TableSpec Spec, ChatMessage? After) : EventItem(After)
{
    //the name stays Aligned so a private Render of the same signature can't hide the base member, the two return different things
    private IReadOnlyList<RenderedRow> Aligned(int width, Theme theme) =>
        TableLayout.AlignedRows(Spec, theme, width);

    protected override IReadOnlyList<string> RenderBody(int width, Theme theme, GlyphSet? glyphs) =>
        Aligned(width, theme).Select(r => r.Text).ToList();

    protected override IReadOnlyList<RowWrap> RowWrapsBody(int width, Theme theme, GlyphSet? glyphs) =>
        Aligned(width, theme).Select(r => new RowWrap(r.Continuation, r.PrefixCells)).ToList();
}

public sealed record SystemLineItem(string Text, ChatMessage? After) : EventItem(After)
{
    protected override IReadOnlyList<string> RenderBody(int width, Theme theme, GlyphSet? glyphs) =>
        ItemRender.SystemRows(Text, theme, width, glyphs);

    protected override IReadOnlyList<RowWrap> RowWrapsBody(int width, Theme theme, GlyphSet? glyphs) =>
        ItemRender.SystemRowsTagged(Text, theme, width, glyphs).Select(r => new RowWrap(r.Continuation, r.PrefixCells)).ToList();
}

public sealed record CompletionItem(string Text, ChatMessage? After) : EventItem(After)
{
    protected override IReadOnlyList<string> RenderBody(int width, Theme theme, GlyphSet? glyphs) =>
        ItemRender.CompletionRows(Text, theme, width);
}

//the lead of a /compact run, the previous session summary, drawn as a dim block
public sealed record SessionLeadItem(string Text, ChatMessage? After) : EventItem(After)
{
    //what the memory piggyback did, saved beside the summary and never drawn. it is null on a lead from a resume or an auto-compact
    public Gatto.Core.Memory.MemoryBankOutcome? Memory { get; init; }

    protected override IReadOnlyList<string> RenderBody(int width, Theme theme, GlyphSet? glyphs) =>
        ItemRender.ReasoningRows(Text.Split('\n'), theme, width);

    protected override IReadOnlyList<RowWrap> RowWrapsBody(int width, Theme theme, GlyphSet? glyphs) =>
        ItemRender.ReasoningRowsTagged(Text.Split('\n'), theme, width).Select(r => new RowWrap(r.Continuation, r.PrefixCells)).ToList();
}

//gatto's own painted content, already render-ready, so Render emits the rows verbatim
public sealed record CommandEchoItem(IReadOnlyList<string> LogicalRows, ChatMessage? After) : EventItem(After)
{
    //chrome that regenerates every launch, so the store skips it and it is never saved, otherwise it piles up on every resume. a /help echo stays false
    public bool Transient { get; init; }

    //a dim line wrapped at the width it is drawn at, so a narrow window keeps every word and a resize wraps it again
    public string? DimText { get; init; }

    //the figures of a committed /context report, so the item draws again at any width and the session file can carry them
    public Gatto.Core.Loop.ContextFigures? Context { get; init; }

    protected override IReadOnlyList<string> RenderBody(int width, Theme theme, GlyphSet? glyphs) =>
        DimText is { } text
            ? SoftWrap.Wrap(TermText.Sanitize(text), width, width).Select(s => theme.Paint(s.Text, Theme.Dim)).ToList()
            : Context is { } figures
                ? [.. Gatto.Repl.ContextReport.Unhung(figures, width, theme, glyphs ?? GlyphSet.Unicode).Select(r => GutterWrap.Hang + r)]
                : ItemRender.Verbatim(LogicalRows, width);

    protected override IReadOnlyList<RowWrap> RowWrapsBody(int width, Theme theme, GlyphSet? glyphs) =>
        DimText is { } text
            ? SoftWrap.Wrap(TermText.Sanitize(text), width, width).Select((_, i) => new RowWrap(i > 0, 0)).ToList()
            : base.RowWrapsBody(width, theme, glyphs);
}
