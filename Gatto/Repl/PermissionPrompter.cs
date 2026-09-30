using Gatto.Core;
using Gatto.Core.Loop.Permissions;
using Gatto.Repl.Render;
using Gatto.Terminal;

namespace Gatto.Repl;

//sanitize every request-supplied string before either prompter renders it, since the summary and the grant offer are model-controlled
internal static class PermissionPromptText
{
    //drop every control byte, including ESC and newlines, so a stripped escape shows as inert text
    public static string Sanitize(string s) => TermText.Sanitize(s);

    //char-based truncation for a display cap, no cell or width awareness
    public static string Truncate(string s, int max, GlyphSet? glyphs) =>
        s.Length <= max ? s : s[..max] + (glyphs ?? GlyphSet.Unicode).Ellipsis;

    //render a write offer as a directory with a trailing * so it reads as a scope covering everything under it
    public static string FormatOffer(PermissionRequest request, string? cwd = null,
        GlyphSet? glyphs = null)
    {
        var offer = Sanitize(request.GrantOffer!);
        if (request.Tool is "write_file" or "edit_file")
        {
            var display = ProjectRelativeDir(offer, cwd);
            var dir = display.EndsWith(Path.DirectorySeparatorChar) ? display : display + Path.DirectorySeparatorChar;
            return Truncate(dir, 60, glyphs: glyphs) + "*";
        }
        return "\"" + Truncate(offer, 50, glyphs: glyphs) + "\"";
    }

    //fall back to the absolute directory on any failure, since the display must never show more than was granted
    private static string ProjectRelativeDir(string dir, string? cwd)
    {
        try
        {
            var root = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(cwd ?? Environment.CurrentDirectory));
            var name = Path.GetFileName(root);
            if (name.Length == 0) return dir;   //a drive root has no name to prefix the display with
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
            if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase)) return name;
            var rel = ItemRender.RelativePath(full, root);
            //the call returns the input unchanged for a path outside the root
            return string.Equals(rel, full, StringComparison.OrdinalIgnoreCase) ? dir : Path.Combine(name, rel);
        }
        catch (Exception) { return dir; }
    }
}

//a thin binding onto SelectPrompt: it picks the words and options and keeps no keypress loop, rendering or echo
public sealed class RichPermissionPrompter(
    ITermSurface surface, Theme theme, IKeySource keys, InputPump? pump = null,
    ChromeHandle? chrome = null, bool denyReason = false, string? webSearchProvider = null,
    TurnAbortHandle? abort = null, WarningSink? warn = null,
    GlyphSet? glyphs = null) : IPermissionPrompter
{
    private readonly GlyphSet _glyphs = glyphs ?? GlyphSet.Unicode;

    //only a turn still live when the prompt opens can end it, so an earlier esc leaves the rest of the round asking
    private CancellationToken TokenLiveAtOpen() => abort?.Token is { IsCancellationRequested: false } live ? live : default;

    //test seam for the widget call, since the fail-closed arm can't be reached through the real widget
    internal Func<SelectSpec, SelectOutcome>? ShowForTest;

    //the widget's text column, which a pre-wrapped row must budget for and the deny panel must reproduce
    private const int PanelIndent = 2;

    //name the model-side cap in the prompt so the number can't drift from the one PrepareReason enforces at submit
    private static readonly string ReasonPrefix =
        $"reason (optional, {GlyphSet.Unicode.AtMost}{PermissionGate.MaxReasonLength} chars, Enter to skip): ";

    //raised out of the reason read when the input pump is dead, callers above depend on this wording
    private const string DeadPumpMessage = "input pump closed while awaiting an answer";

    //the notice for a reason the model receives none of (all control characters, or the cap inside leading spaces), don't reuse the truncation text
    internal const string NothingReachedModel = "(none of that reached the model)";

    //no reason is read on this path, CheckpointGate is the only caller and discards it
    public PermissionAnswer Ask(PermissionRequest request) => AskCore(request, offerReasonOnDeny: false).Answer;

    //a Deny is followed by one optional line that steers the model. deny_reason is read at construction, so being asked stays a session preference
    public PermissionDecision AskWithReason(PermissionRequest request) => AskCore(request, offerReasonOnDeny: denyReason);

    private PermissionDecision AskCore(PermissionRequest request, bool offerReasonOnDeny)
    {
        //the painter must be alive, SetPanel is a no-op after Teardown so a dead one would swallow the keypress and show nothing
        var painter = chrome?.Painter is { Alive: true } p ? p : null;

        //push one focus scope for the options and the reason line, and hand the widget pump: null, its own closes when Show returns
        using var focus = pump?.PushFocus();
        var k = focus?.Keys ?? keys;

        var hasOffer = !string.IsNullOrEmpty(request.GrantOffer);
        var offerText = hasOffer ? PermissionPromptText.FormatOffer(request) : null;
        var (title, question, detail) = PromptTitles.For(request, webSearchProvider, glyphs: _glyphs);

        //the agent label leads the title block, so who is asking reads before what for
        var titleRows = new List<TitleRow>();
        if (request.Agent is not null) titleRows.Add("[" + PermissionPromptText.Sanitize(request.Agent) + "]");
        titleRows.Add(title);

        var options = new List<SelectOption> { new("Yes") };
        //the scope goes in LabelCode so it reads as inline code off the cursor and takes one accent run when selected
        if (hasOffer) options.Add(new SelectOption(
            "Yes, always allow ", LabelCode: offerText, LabelAfter: " on this project"));
        options.Add(new("No"));

        //detail lines go through raw, the widget wraps them at each paint width so a resize mid-prompt re-wraps instead of clips
        var spec = new SelectSpec(titleRows, question, options, detail,
            FreeTextLabel: null, MultiSelect: false, FooterHint: "Esc to cancel", Cancel: TokenLiveAtOpen(),
            DetailAt: PromptTitles.DetailAt(request, _glyphs));
        var outcome = ShowForTest is { } showForTest
            ? showForTest(spec)
            : new SelectPrompt(surface, theme, k, pump: null, chrome, _glyphs).Show(spec);

        //a subagent prompt notes no outcome at all, the mark would show on the outer run_agent's result row
        var renderer = request.Agent is null ? chrome?.Renderer : null;
        switch (outcome)
        {
            case SelectOutcome.Chosen chosen when chosen.Index == 0:
                renderer?.NotePermissionOutcome(PermissionOutcomeKind.Allowed);
                return new PermissionDecision(PermissionAnswer.Once);

            case SelectOutcome.Chosen chosen when hasOffer && chosen.Index == 1:
                //the note shows the same scope text the option row offered, so what the user read can't drift from what the row reports
                renderer?.NotePermissionOutcome(PermissionOutcomeKind.AutoApproved, offerText);
                return new PermissionDecision(PermissionAnswer.Always);

            case SelectOutcome.Cancelled:
                //esc cancels the call instead of denying it, the gate reports "cancelled by user" and the turn ends
                renderer?.NotePermissionOutcome(PermissionOutcomeKind.Cancelled);
                //abort the turn here and don't throw OperationCanceledException out of this arm, the loop would write a bare cancelled and lose the message
                abort?.RequestAbort();
                return new PermissionDecision(PermissionAnswer.Cancel);

            //the No arm is explicit so an unrecognised outcome can't pass as the user's denial
            case SelectOutcome.Chosen chosen when chosen.Index == options.Count - 1:
                return Deny();

            default:
                //warn through the session sink, a failed Debug.Assert would kill the whole session over one denied call
                warn?.Warn($"permission prompt got an unrecognised {outcome.GetType().Name} response - denied");
                return Deny();
        }

        //the refusal both arms share so the fail-closed path can't drift, and the reason is captured before the note
        PermissionDecision Deny()
        {
            var reason = offerReasonOnDeny ? CaptureReason() : null;
            //the gloss shows what the model got, so it uses PrepareReason's text and not the raw buffer
            var detail = reason is null ? null : PermissionGate.PrepareReason(reason).Text;
            renderer?.NotePermissionOutcome(PermissionOutcomeKind.Denied, detail);
            return new PermissionDecision(PermissionAnswer.Deny, reason);
        }

        //the widget doesn't compose the deny-reason rows, this class indents and truncates them like any panel row
        List<string> WrapReasonLine(string text)
        {
            var w = surface.Width;
            var budget = w <= 0 ? 0 : Math.Max(1, w - PanelIndent);
            var indent = new string(' ', PanelIndent);
            var rows = new List<string>();
            foreach (var seg in SoftWrap.Wrap(text, budget, budget))
            {
                var row = indent + seg.Text;
                rows.Add(w > 0 ? TermText.TruncateCells(row, w, glyphs: _glyphs) : row);
            }
            return rows;
        }

        //esc skips the reason without undoing the deny, and a dead pump keeps raising an OperationCanceledException with its message
        string? CaptureReason()
        {
            //a reason the model would receive none of is refused and re-asked, an empty Enter still skips and a truncated one still closes
            if (painter is null)
            {
                while (true)
                {
                    surface.Write("  " + ReasonPrefix);
                    var raw = RichPrompter.ReadLine(k, surface, CancellationToken.None);
                    if (raw is null) throw new OperationCanceledException(DeadPumpMessage);
                    if (string.IsNullOrEmpty(raw)) return null;

                    var model = PermissionGate.PrepareReason(raw);
                    if (model.LostEntirely)
                    {
                        //the raw text is already echoed above, so write the notice and ask again
                        surface.Write("  " + NothingReachedModel + "\n");
                        continue;
                    }
                    //report the truncation and close anyway, the verdict comes from the PrepareReason call and not a re-measure
                    if (model.Truncated)
                        surface.Write("  (only the first " + PermissionGate.MaxReasonLength + " chars reach the model)\n");
                    return raw;
                }
            }

            try
            {
                //head must return a fresh list per render, empty until a refusal puts the notice above the input line
                var notice = new List<string>();
                while (true)
                {
                    var line = SelectPrompt.ReadLinePanel(
                        k, () => new List<string>(notice), ReasonPrefix, WrapReasonLine, painter.SetPanel, TokenLiveAtOpen());
                    switch (line.Status)
                    {
                        case PanelLineStatus.Dead: throw new OperationCanceledException(DeadPumpMessage);
                        case PanelLineStatus.BackedOut: return null;
                    }
                    if (string.IsNullOrEmpty(line.Text)) return null;
                    if (PermissionGate.PrepareReason(line.Text).LostEntirely)
                    {
                        //the panel stays open with the notice above a cleared input line, and both esc and a plain Enter still work
                        notice = WrapReasonLine(NothingReachedModel);
                        continue;
                    }
                    return line.Text;
                }
            }
            finally
            {
                //clear the panel whatever happens, an aborted read must not pin a dead panel over the composer
                painter.SetPanel(null);
            }
        }
    }
}

//numbered menu on Console.ReadLine, no esc bytes so -p output stays byte-pure. a closed stdin denies instead of throwing
public sealed class PlainPermissionPrompter(bool denyReason = false) : IPermissionPrompter
{
    //no reason is read on this path, CheckpointGate is the only caller and discards it
    public PermissionAnswer Ask(PermissionRequest request) => AskCore(request, offerReasonOnDeny: false).Answer;

    //same menu and parsing, a Deny reads one optional line the model sees verbatim. deny_reason is read at construction, both prompters honour the same switch
    public PermissionDecision AskWithReason(PermissionRequest request) => AskCore(request, offerReasonOnDeny: denyReason);

    private static PermissionDecision AskCore(PermissionRequest request, bool offerReasonOnDeny)
    {
        var hasOffer = !string.IsNullOrEmpty(request.GrantOffer);
        var offerText = hasOffer ? PermissionPromptText.FormatOffer(request) : null;

        Console.WriteLine();
        var agentPrefix = request.Agent is not null
            ? $"[{PermissionPromptText.Sanitize(request.Agent)}] "
            : "";
        //the tool name is model-controlled, it can hold ESC before the unknown-tool check runs, so sanitize it like the Summary
        Console.WriteLine($"[permission] {agentPrefix}{PermissionPromptText.Sanitize(request.Tool)}  {PermissionPromptText.Sanitize(request.Summary)}");
        Console.WriteLine("  1. once");
        if (hasOffer) Console.WriteLine($"  2. always (this project) {offerText}");
        Console.WriteLine("  0. deny");

        while (true)
        {
            Console.Write("> ");
            var raw = Console.ReadLine();
            if (raw is null)
            {
                Console.WriteLine("denied (stdin closed)");
                return new PermissionDecision(PermissionAnswer.Deny);
            }
            switch (raw.Trim())
            {
                case "1": Console.WriteLine("allowed once"); return new PermissionDecision(PermissionAnswer.Once);
                case "2" when hasOffer:
                    Console.WriteLine($"always (this project) {offerText}");
                    return new PermissionDecision(PermissionAnswer.Always);
                case "0":
                    Console.WriteLine("denied");
                    return new PermissionDecision(PermissionAnswer.Deny, offerReasonOnDeny ? ReadReason() : null);
                default: Console.WriteLine(hasOffer ? "pick 1, 2, or 0" : "pick 1 or 0"); break;
            }
        }
    }

    //no reason on empty input or a closed stdin, and no retry loop keeps the fast deny path fast
    private static string? ReadReason()
    {
        Console.Write("reason (optional, Enter to skip): ");
        var raw = Console.ReadLine();
        return string.IsNullOrEmpty(raw) ? null : raw;
    }
}
