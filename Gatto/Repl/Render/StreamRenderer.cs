using System.Linq;
using System.Text;
using System.Text.Json;
using Gatto.Core.Client;
using Gatto.Core.Home;
using Gatto.Core.Loop;
using Gatto.Core.Tools;
using Gatto.Repl.Input;
using Gatto.Repl.Render.Chrome;
using Gatto.Repl.Render.Syntax;
using Gatto.Terminal;

namespace Gatto.Repl.Render;

//the transcript's view of a permission answer. the Core PermissionAnswer says what the gate did. the AutoApproved arm names the scope the grant covers
public enum PermissionOutcomeKind
{
    //allowed this once, so the tool ran and the result row is the normal one
    Allowed,
    //a standing grant was made, so a dim line after the result row names the scope
    AutoApproved,
    //refused, the row reads ⎿ ✗ denied with a red ●
    Denied,
    //withdrawn with Esc, so the row reads ⎿ ✗ cancelled with a red ●. the gate keeps this apart from a refusal
    Cancelled,
}

//never writes to the surface, a delta updates the chrome tail. a finished line commits through ChromePainter, and a throw falls back to plain writes
public sealed class StreamRenderer : ITurnObserver
{
    private readonly ChromePainter _painter;
    private readonly ITermSurface _surface;   //used only by the degraded fallback, the painter owns the surface otherwise
    private readonly Theme _theme;
    //the run's glyph set, handed down from the session
    private readonly GlyphSet _glyphs;
    private readonly string _role;
    private readonly ChromeTicker _ticker;
    private readonly object _gate;
    private readonly Action<Usage>? _onUsage;
    private readonly ReasoningMode _reasoning;      //collapsed streams then closes the block, expanded keeps it open
    private readonly Func<DateTime> _now;           //the wall clock the reasoning block's elapsed time reads (settable for tests)
    private DateTime _reasoningStart;               //stamped when the reasoning block opens, its difference from now is the elapsed time
    private readonly StringBuilder _tail = new();   //the logical line being built

    //every commit also appends to the model, which must render exactly the rows Commit received. both fields stay null on the plain path
    private readonly TranscriptModel? _model;
    private readonly Func<ChatMessage?>? _convoTail;
    //test-only tap with the exact rows handed to Commit, so the model's render can be compared against them
    internal Action<IReadOnlyList<string>>? CommitTap;
    private ChatMessage? After() => _convoTail?.Invoke();

    //the role for committed tool blocks, read from the model so tool and prose share one source. the live paint keeps the renderer's own role
    private string StampRole => _model!.Role;

    private BlockState _block = BlockState.Normal;
    private bool _inReasoning;
    private bool _blockOpen;      //the ● of a prose run stays for the whole run, every later row hangs under it
                                  //a paragraph blank doesn't reset it, only a tool, system, user or reasoning block does
    private bool _blankPending;   //a block closed, so the next commit starts with a blank row
    private bool _degraded;
    //an answered prompt held while the tool bullet is still chrome, so the answer can't commit above the line that announces the tool. freed by OnToolResult
    private List<string>? _pendingPrompt;
    //system lines held while a saved call is unanswered, so a line cannot commit above the block it is about
    private List<(string Text, bool Transient)>? _pendingSystem;
    //between BeginTurn and EndTurn, so a line typed at rest never waits for a turn
    private bool _inTurn;

    //the calls of the message being streamed, by index, each holding its own fragments
    private readonly SortedDictionary<int, (string Name, string? Partial)> _written = new();
    //the round's saved calls not yet answered, the only calls a stopped turn commits as cancelled
    private List<ToolCall>? _recorded;

    //the answer for the running call, consumed by the next OnToolResult. it clears at the start of a turn, so an aborted turn can't mark the next one's tool row
    private PermissionOutcomeKind? _permissionOutcome;
    private string? _permissionScope;

    private enum LastWrite { None, Text, Tool }
    private LastWrite _last;

    //while true, a compaction summary streams in full with the dim styling. clearing it seals any open block first, the collapse decision happens at close
    public bool PassthroughReasoning
    {
        get => _passthroughReasoning;
        set
        {
            //seal before the field changes, CloseReasoningBlock reads this property to decide
            if (_passthroughReasoning && !value)
                Guarded(() => { if (_inReasoning) CloseReasoningBlock(); }, () => { });
            _passthroughReasoning = value;
        }
    }
    private bool _passthroughReasoning;

    //fires once when the painter dies, so the loop can stop painting into it and restore an inline echo. without that the user types into a frame nobody draws
    public Action? OnDegraded { get; set; }

    //raised when a tool result changes the context figure, so the footer's ctx% repaints instead of freezing. null on the plain path, re-pointed on a /role swap
    public Action? OnContextChanged { get; set; }

    //every visible byte goes through the painter, the surface serves only the degraded fallback, and the gate is the lock they all share
    public StreamRenderer(
        ChromePainter painter, ITermSurface surface, Theme theme, string roleName,
        ChromeTicker ticker, object gate, Action<Usage>? onUsage = null,
        ReasoningMode reasoning = ReasoningMode.Collapsed, Func<DateTime>? now = null,
        TranscriptModel? model = null, Func<ChatMessage?>? convoTail = null,
        GlyphSet? glyphs = null, Gatto.Core.Trace? trace = null)
    {
        _painter = painter; _surface = surface; _theme = theme; _role = roleName;
        _ticker = ticker; _gate = gate; _onUsage = onUsage;
        _reasoning = reasoning; _now = now ?? (() => DateTime.UtcNow);
        _model = model; _convoTail = convoTail; _glyphs = glyphs ?? GlyphSet.Unicode;
        //detects a dead painter here, a /role swap hands the new renderer the same instance. otherwise every later commit writes nowhere
        _degraded = !painter.Alive;
        _trace = trace ?? Gatto.Core.Trace.Off;
        _counter = _trace.Enabled ? surface as CountingSurface : null;   //a traced session wraps the surface the painter writes to
    }

    private readonly Gatto.Core.Trace _trace;
    private readonly CountingSurface? _counter;
    private long _turnSpan;

    public void OnTurnSpan(long id) => _turnSpan = id;

    //the counters before a delta, zeros when untraced, so the span reads what the surface took during it
    private (long Bytes, long Writes, long Ticks) Counts() =>
        _counter is { } c ? (c.Bytes, c.Writes, c.WriteTicks) : default;

    private void CloseDelta(Gatto.Core.Span span, string kind, int chars, (long Bytes, long Writes, long Ticks) before)
    {
        if (!_trace.Enabled) return;
        var after = Counts();
        span.Set("kind", kind);
        span.Set("chars", chars);
        span.Set("repaints", after.Writes - before.Writes);
        span.Set("bytes", after.Bytes - before.Bytes);
        span.Set("write_us", (after.Ticks - before.Ticks) * 1_000_000 / System.Diagnostics.Stopwatch.Frequency);
        span.Dispose();
    }

    //turn bracket

    //per-turn reset. it does not start the ticker, the dispatch bracket owns the purr
    public void BeginTurn()
    {
        lock (_gate)
        {
            _tail.Clear();
            _block = BlockState.Normal;
            _inTurn = true;
            _inReasoning = false;
            _blockOpen = false;   //the blank flag and the last kind are transcript state and survive the turn boundary, or the blank row after the user echo is lost
            //an aborted turn leaves the prompt buffer filled, so it is cleared here or the block surfaces under the next turn's first tool bullet
            _pendingPrompt = null;
            _pendingSystem = null;
            _permissionOutcome = null;   //cleared for the same reason, a decision from an earlier turn must not mark the next one
            _permissionScope = null;
            _written.Clear();
            _recorded = null;
            _model?.CloseOpen();   //no open model item may cross the turn boundary
            if (_degraded) return;
            try
            {
                _painter.State.Thinking = false;
                _painter.State.Waiting = [];
                _painter.State.WaitingAbove = 0;
                ClearTail();
                _painter.Repaint();
            }
            catch (Exception) { Degrade(); }
        }
    }

    //a /role swap hands the new renderer the old transcript state, so the blank row before the first block survives. per-turn state stays behind
    internal void AdoptTranscriptState(StreamRenderer previous)
    {
        lock (_gate) { _last = previous._last; _blankPending = previous._blankPending; }
    }

    //commits the open tail line and every saved call left unanswered as cancelled, so the screen holds what a resume draws. the dispatch bracket calls this before StopTurn
    public void EndTurn()
    {
        lock (_gate)
        {
            _inTurn = false;
            if (_degraded) return;
            try
            {
                //no turn may end while a reasoning block is open, close it here or the collapse is skipped
                if (_inReasoning) CloseReasoningBlock();
                else FlushOpenLine();
                _model?.CloseOpen();   //the open model item closes at turn end, a second close here is harmless
                ClearLiveTool();
                //a written call that was never saved commits nothing, the record holds none of it
                foreach (var call in _recorded ?? []) CommitCancelled(call);
                _recorded = null;
                _written.Clear();
                _painter.State.Waiting = [];
                _painter.State.WaitingAbove = 0;
                FlushPendingSystem();             //flush the held system lines now, after the tool blocks
                _painter.State.Thinking = false;
                ClearTail();
                _painter.Repaint();
            }
            catch (Exception) { Degrade(); }
        }
    }

    //a saved call the turn answered as cancelled, red and closed as its resume draws it
    private void CommitCancelled(ToolCall call)
    {
        var cancelled = _theme.Paint(_glyphs.Bad + " cancelled", Theme.Err);
        var item = new ToolBlockItem(TermText.Sanitize(call.Name), TermText.Sanitize(HeaderArgs(call.Name, call.ArgumentsJson)), cancelled, HasResult: true, StampRole)
            { Collapsed = true, FullResult = "", FullArgs = ItemRender.FullArgsOf(call.Name, call.ArgumentsJson), BulletTint = Theme.Err };
        _model?.Append(item);
        CommitRows(TapRows(item));
        _last = LastWrite.Tool;
        _blockOpen = false;
        _blankPending = true;         //the tool block has closed, so the next commit leads with a blank row
    }

    //the loop names each call it cancelled before its line, so the blocks and then the line are in the model when the turn is saved
    public void OnToolCallCancelled(ToolCall call) => Guarded(
        () =>
        {
            ClearLiveTool();
            _recorded?.RemoveAll(c => c.Id == call.Id);
            _painter.State.Waiting = [.. (_recorded ?? []).Select(c => WaitingRow(TermText.Sanitize(c.Name), c.ArgumentsJson))];
            _painter.State.WaitingAbove = 0;
            CommitCancelled(call);
            FlushPendingSystem();   //a line raised while this call ran follows its block, anchored after its answer
        },
        () => { });

    //a message the loop delivered at a round boundary, the same block a resume draws from its record, between the round's results and the next reply
    public void OnQueuedDelivered(ChatMessage record)
    {
        if (record.Watch is not null) CommitLetter(record);
        else CommitUser((record.Content ?? "").Split('\n'));
    }

    //stream events

    public void OnTextDelta(string text)
    {
        //opened first, so the span covers the whole call, the sanitize and the gate's wait included
        var span = _trace.Open(Gatto.Core.Trace.Delta, _turnSpan);
        var before = Counts();
        //strip ESC, CR and the other C0 bytes before anything measures or paints them. a newline survives, it commits a line
        text = TermText.SanitizeProse(text);
        _ticker.AddTokenChars(text.Length);
        Guarded(
            () =>
            {
                FlushHeldAtReplyStart();
                if (_inReasoning) CloseReasoningBlock();
                _painter.State.Thinking = false;
                _last = LastWrite.Text;
                Feed(text);
                PaintTail();
            },
            () => _surface.Write(text));
        CloseDelta(span, "text", text.Length, before);
    }

    public void OnReasoningDelta(string text)
    {
        var span = _trace.Open(Gatto.Core.Trace.Delta, _turnSpan);
        var before = Counts();
        //sanitize the reasoning delta too, so ESC and CR reach neither the painter nor the fallback
        text = TermText.SanitizeProse(text);
        _ticker.AddTokenChars(text.Length);
        //reasoning always streams, the collapse happens when the block closes
        Guarded(
            () =>
            {
                FlushHeldAtReplyStart();
                if (!_inReasoning) OpenReasoningBlock();
                _painter.State.Thinking = false;
                _last = LastWrite.Text;
                Feed(text);
                PaintTail();
            },
            () => _surface.Write(text));
        CloseDelta(span, "reasoning", text.Length, before);
    }

    //nothing to paint here (the purr is the ticker's), but an open prose line is finished once a tool call starts, so commit it
    public void OnToolCallDelta(string? name = null, string? partialArguments = null, int index = 0)
    {
        var span = _trace.Open(Gatto.Core.Trace.Delta, _turnSpan);
        var before = Counts();
        ToolCallDelta(name, partialArguments, index);
        CloseDelta(span, "tool_call", partialArguments?.Length ?? 0, before);   //the arguments streamed so far, which each chunk resends whole
    }

    private void ToolCallDelta(string? name, string? partialArguments, int index) => Guarded(
        () =>
        {
            //the model is emitting, so the purr must not say it is reading context while tool arguments stream
            _ticker.MarkStreaming();
            FlushHeldAtReplyStart();
            if (_inReasoning) CloseReasoningBlock();
            else FlushOpenLine();
            _painter.State.Thinking = false;

            //the row is chrome, and a stream that ends unsaved commits nothing of it
            if (name is { Length: > 0 }) _written[index] = (TermText.Sanitize(name), partialArguments);
            if (_written.Count > 0) ShowWritten();

            PaintTail();
        },
        () => { });

    //the newest call is the live row, its name and preview showing from the first fragments (shell decodes its command), and every earlier call waits above it
    private void ShowWritten()
    {
        var st = _painter.State;
        var live = _written.Keys.Last();
        var (name, partial) = _written[live];
        var shell = name == "shell";
        st.Tool = (name, shell ? "" : TermText.Sanitize(ItemRender.EarlyArgsPreview(partial)));
        st.ToolInput = shell ? ItemRender.ShellCommandText(PartialJson.StringValue(partial, "command") ?? "") : null;
        st.Waiting = [.. _written.Where(e => e.Key != live).Select(e => WaitingRow(e.Value.Name, e.Value.Partial))];
        st.WaitingAbove = st.Waiting.Count;
    }

    //the saved calls after the running one wait under it
    private void ShowWaiting(ToolCall running)
    {
        var st = _painter.State;
        st.Waiting = [.. (_recorded ?? []).Where(c => c.Id != running.Id).Select(c => WaitingRow(TermText.Sanitize(c.Name), c.ArgumentsJson))];
        st.WaitingAbove = 0;
    }

    //a waiting call's row: a shell call's command for its head row, any other the header its block commits with once its arguments parse, the early preview before
    private (string Name, string Args) WaitingRow(string name, string? json) =>
        (name, name == "shell"
            ? ItemRender.ShellCommandText(PartialJson.StringValue(json, "command") ?? "")
            : TermText.Sanitize(Parses(json) ? HeaderArgs(name, json!) : ItemRender.EarlyArgsPreview(json)));

    private static bool Parses(string? json)
    {
        if (json is not { Length: > 0 }) return false;
        try { using var _ = JsonDocument.Parse(json); return true; }
        catch (JsonException) { return false; }
    }

    //the round's calls once saved: the first is the next to run, so it takes the live row and the rest wait under it
    public void OnToolCallsRecorded(IReadOnlyList<ToolCall> calls) => Guarded(
        () =>
        {
            _recorded = [.. calls];
            _written.Clear();
            if (calls.Count == 0) return;
            var first = calls[0];
            _painter.State.Tool = (TermText.Sanitize(first.Name), TermText.Sanitize(HeaderArgs(first.Name, first.ArgumentsJson)));
            _painter.State.ToolInput = ItemRender.FullArgsOf(first.Name, first.ArgumentsJson);
            ShowWaiting(first);
            _painter.Repaint();
        },
        () => { });

    //prefill progress goes to the ticker, which owns the purr row
    public void OnPromptProgress(long total, long processed) => _ticker.SetPromptProgress(total, processed);

    public void OnToolCallStart(ToolCall call) => Guarded(
        () =>
        {
            if (_inReasoning) CloseReasoningBlock();
            else FlushOpenLine();       //a tool call means the open prose line is finished for the transcript
            _model?.CloseOpen();        //the prose run ends here, the tool block appends at OnToolResult
            if (_last != LastWrite.None) _blankPending = true;
            CommitRows(new List<string>());   //commits nothing, which flushes a pending blank separator
            //the name and args are untrusted and sit right above the permission prompt, so strip control bytes that could forge an allow line
            _painter.State.Thinking = false;
            _painter.State.Tool = (TermText.Sanitize(call.Name), TermText.Sanitize(HeaderArgs(call.Name, call.ArgumentsJson)));
            _painter.State.ToolInput = ItemRender.FullArgsOf(call.Name, call.ArgumentsJson);
            if (_recorded?.Any(c => c.Id == call.Id) != true) (_recorded ??= []).Add(call);   //a replayed call was never recorded, and it leaves at its result
            ShowWaiting(call);
            _painter.Repaint();          //repaint, the bullet is chrome with a blinking dot while the tool runs
            _last = LastWrite.Tool;
            _blockOpen = false;
        },
        () => _surface.Write("\n[tool] " + TermText.Sanitize(call.Name) + " "
            + TermText.Sanitize(TruncateChars(call.ArgumentsJson, 80, _glyphs)) + "\n"));

    //the prompter records it before the tool runs, consumed by the next OnToolResult. a refusal paints ✗ denied with a red ● rather than the gate's note to the model
    public void NotePermissionOutcome(PermissionOutcomeKind kind, string? detail = null)
    {
        lock (_gate)
        {
            _permissionOutcome = kind;
            _permissionScope = detail;
        }
    }

    //the follow-up line an auto-approved call leaves under the result row, one source so the row and the test can't drift
    internal const string AutoApprovedPrefix = "auto-approved on this project: ";

    public void OnToolResult(ToolCall call, ToolResult result) => Guarded(
        () =>
        {
            //read and clear it first, even a throw below must not leave it armed for the next result
            var outcome = _permissionOutcome;
            var outcomeScope = _permissionScope;
            _permissionOutcome = null;
            _permissionScope = null;
            //count this result's text toward the turn total, errors included, so the purr's Σ matches the ⎿ glosses
            _ticker.AddToolResultChars(result.Text.Length);
            //sanitize the result text and gloss before truncation, and send a multi-line error through ErrorPreview so its newlines survive
            var name = TermText.Sanitize(call.Name);
            var args = TermText.Sanitize(HeaderArgs(call.Name, call.ArgumentsJson));

            //a refusal's result text is the gate's nudge to the model, so the row shows the fact. the red ● keeps a refusal visible in a long turn
            var bulletTint = outcome is PermissionOutcomeKind.Denied or PermissionOutcomeKind.Cancelled
                ? Theme.Err : (RgbColor?)null;
            var outcomeGloss = Refusal.Gloss(outcome, outcomeScope, _theme, _glyphs);
            var followUp = outcome == PermissionOutcomeKind.AutoApproved && outcomeScope is { Length: > 0 }
                ? AutoApprovedPrefix + outcomeScope : null;

            ClearLiveTool();   //drops the live bullet now that the pair is committed
            _recorded?.RemoveAll(c => c.Id == call.Id);   //the waiting rows already leave the running call out
            //the gloss is computed once here, the item and the tap rows read it, so the two cannot drift
            var gloss = outcomeGloss ?? ItemRender.ToolGloss(result, _theme, _glyphs);
            //append the whole tool block as one item before the paint. a write's content and an edit's strings come from the call args, so live and resumed blocks match
            var (editOld, editNew, writeContent) = EditArgs.Of(call.Name, call.ArgumentsJson);
            var happened = outcomeGloss is null && !result.IsError;
            var item = new ToolBlockItem(name, args, gloss, true, StampRole)
            {
                Collapsed = !(happened && call.Name is "write_file" or "edit_file"),   //a write or an edit that happened commits open on its change, every other block closed
                FullResult = result.Text,
                RawGloss = outcomeGloss is null ? result.Gloss : null,   //a denied or cancelled call has no exit code to read, its painted gloss stands as given
                Parts = outcomeGloss is null ? ItemRender.ToolGlossParts(result, _theme, _glyphs) : new ToolGlossParts(outcomeGloss, "", null, false),
                BulletTint = bulletTint,
                FollowUp = followUp,   //the dim row naming the scope an auto-approved call was granted
                FullArgs = ItemRender.FullArgsOf(call.Name, call.ArgumentsJson),
                GrepPattern = GrepRows.PatternOf(call.Name, call.ArgumentsJson),
                BodyStart = ToolBody.StartOf(call.Name, call.ArgumentsJson),
                AskArgs = call.Name == "ask_user" ? call.ArgumentsJson : null,
                EditOld = editOld, EditNew = editNew, WriteContent = writeContent,
                FileLanguage = SyntaxHighlight.LanguageOfToolCall(call.Name, call.ArgumentsJson),
                EditAt = result.View,
                IsError = !happened,
            };
            _model?.Append(item);
            CommitRows(TapRows(item));   //paint only, a second repaint here would be the same frame twice
            _last = LastWrite.Tool;
            _blockOpen = false;
            _blankPending = true;         //the tool block has closed, so the next commit leads with a blank row
            FlushPendingSystem();         //the ♯ lines raised mid-tool are flushed after the block, in order
            //a tool result changes the context figure, so the footer refreshes here. it stays last in the body so the repaint sees the finished block
            OnContextChanged?.Invoke();
        },
        () => _surface.Write(PlainRenderer.ResultLine(call, result, _glyphs) + "\n"));

    //drops the live row once its call has a block, or once the turn that ran it ends
    private void ClearLiveTool()
    {
        _painter.State.Tool = null;
        _painter.State.ToolWait = null;
        _painter.State.ToolProgress = null;
        _painter.State.ToolInput = null;
    }

    //a liveness line for a long tool run, drawn as a dim chrome row that replaces itself in place. committing it would put it above the live bullet
    public void OnSubagentProgress(string message) => Guarded(
        () =>
        {
            _painter.State.ToolProgress = TermText.Sanitize(message);
            _painter.Repaint();
        },
        () => _surface.Write("  " + TermText.Sanitize(message) + "\n"));

    //warnings go through the ♯ system line, and their text can come from the endpoint so it is sanitized first
    public void OnWarning(string message) => CommitSystem(message);

    //the ♯ system line: a blank-separated, Warn-painted gutter block. while a saved call is unanswered the line is buffered and flushed after the block
    public void CommitSystem(string message) => CommitSystem(message, transient: false);

    //a transient row is shown and never saved, for a fact about this launch alone
    public void CommitSystem(string message, bool transient)
    {
        message = TermText.Sanitize(message);
        Guarded(
            () =>
            {
                if (_recorded is { Count: > 0 })   //a running call is a saved one, and a row the deltas set may never be saved, so it holds no line
                {
                    (_pendingSystem ??= []).Add((message, false));
                    return;
                }
                CommitSystemNow(message, transient);
            },
            () => _surface.Write("\n! " + message + "\n"));
    }

    //a line a command answers from the composer while a turn may stream. it waits while a prose or reasoning block is open, so it never cuts the reply in two
    public void CommitSystemBetweenBlocks(string message, bool transient = false)
    {
        message = TermText.Sanitize(message);
        Guarded(
            () =>
            {
                if (_inTurn && (_inReasoning || _blockOpen || _tail.Length > 0 || _recorded is { Count: > 0 }))
                {
                    (_pendingSystem ??= []).Add((message, transient));
                    return;
                }
                CommitSystemNow(message, transient);
            },
            () => _surface.Write("\n! " + message + "\n"));
    }

    //a line raised at a round boundary, before the loop draws what it delivers there. it follows those blocks: it waits for the next reply to begin or the turn to end
    public void CommitSystemBeforeNextReply(string message)
    {
        message = TermText.Sanitize(message);
        Guarded(
            () =>
            {
                if (_inTurn) (_pendingSystem ??= []).Add((message, false));
                else CommitSystemNow(message, transient: false);
            },
            () => _surface.Write("\n! " + message + "\n"));
    }

    //a reply begins: the lines held for it commit first, unless a block is still open, which keeps them for its own close
    private void FlushHeldAtReplyStart()
    {
        if (!_inReasoning && !_blockOpen && _tail.Length == 0 && _recorded is not { Count: > 0 }) FlushPendingSystem();
    }

    //the caller holds the gate and has decided the line commits here, so it is never held again
    private void CommitSystemNow(string message, bool transient)
    {
        if (_inReasoning) CloseReasoningBlock();
        else FlushOpenLine();
        if (_last != LastWrite.None) _blankPending = true;
        _model?.Append(new SystemLineItem(message, After()) { Transient = transient });   //append before the paint
        CommitRows(ItemRender.SystemRows(message, _theme, _surface.Width, _glyphs));
        _last = LastWrite.Text;
        _blockOpen = false;
        _blankPending = true;   //the system block has closed, so the next commit leads with a blank row
    }

    //a background task's end as a closed shell block under its head line, live and on restore alike, appended before the paint
    public void CommitBackground(ChatMessage record) => Guarded(
        () =>
        {
            if (_inReasoning) CloseReasoningBlock();
            else FlushOpenLine();
            if (_last != LastWrite.None) _blankPending = true;
            var item = BackgroundEndItem.From(record, _theme, _glyphs, StampRole);
            _model?.Append(item);
            CommitRows(TapRows(item));
            _last = LastWrite.Tool;
            _blockOpen = false;
            _blankPending = true;   //the block has closed, so the next commit leads with a blank row
        },
        () => _surface.Write("\n! " + TermText.Sanitize(Gatto.Core.Tools.BackgroundTasks.HeadOf(record.Content)) + "\n"));

    //a watched letter as its folded block on its own band, live and on restore alike, appended before the paint
    public void CommitLetter(ChatMessage record) => Guarded(
        () =>
        {
            if (_inReasoning) CloseReasoningBlock();
            else FlushOpenLine();
            if (_last != LastWrite.None) _blankPending = true;
            var item = LetterItem.From(record);
            _model?.Append(item);
            CommitRows([.. item.Render(_surface.Width, _theme, _glyphs).Skip(item.LeadingBlank ? 1 : 0)]);
            _last = LastWrite.Tool;
            _blockOpen = false;
            _blankPending = true;   //the block has closed, so the next commit leads with a blank row
        },
        () => _surface.Write("\n" + _glyphs.Sharp + " " + TermText.Sanitize(LetterText.Head(record.Watch ?? "")) + "\n"));

    //the turn's dim completion line, one blank row under the output and truncated to one row. the bracket calls it after EndTurn so the timer is still live
    public void CommitCompletion(string line) => Guarded(
        () =>
        {
            if (_inReasoning) CloseReasoningBlock();
            else FlushOpenLine();
            if (_last != LastWrite.None) _blankPending = true;
            var fit = _surface.Width > 0 ? TermText.TruncateCells(line, _surface.Width, glyphs: _glyphs) : line;
            _model?.Append(new CompletionItem(line, After()));   //the model keeps the full line, it re-truncates at render width
            CommitRows(new List<string> { _theme.Paint(fit, Theme.Dim) });
            _last = LastWrite.Text;
            _blockOpen = false;
            _blankPending = true;   //the block has closed, so the next commit leads with a blank row
        },
        () => _surface.Write(line + "\n"));

    //gatto's own painted rows committed as they are, hang-indented, with no ♯ marker and no sanitize that would strip the paint. never send model or tool text here
    public void CommitPlain(string text) => CommitHung(text.TrimEnd('\n').Split('\n'), null, () => _surface.Write(text.TrimEnd('\n') + "\n"));

    //a /context report drawn now at this width, its figures kept on the item so a repaint or a resumed session draws it at its own width
    public void CommitContext(ContextFigures figures) =>
        CommitHung(Gatto.Repl.Render.ContextReport.Unhung(figures, _surface.Width, _theme, _glyphs), figures,
            () => _surface.Write(string.Join("\n", Gatto.Repl.Render.ContextReport.Unhung(figures, _surface.Width, _theme, _glyphs)) + "\n"));

    private void CommitHung(IReadOnlyList<string> lines, ContextFigures? context, Action degraded) => Guarded(
        () =>
        {
            if (_inReasoning) CloseReasoningBlock();
            else FlushOpenLine();
            if (_last != LastWrite.None) _blankPending = true;
            var rows = lines.Select(line => GutterWrap.Hang + line).ToList();
            var modelRows = rows.ToList();   //copy first, CommitRows may add a leading blank row to the list
            _model?.Append(new CommandEchoItem(modelRows, After()) { Context = context });   //append before the paint
            CommitRows(rows);
            _last = LastWrite.Text;
            _blockOpen = false;
            _blankPending = true;   //the block has closed, so the next commit leads with a blank row
        },
        degraded);

    //the user echo: banded ❯ rows with a two-space hang on every continuation, committed like any other block
    public void CommitUser(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0) return;
        Guarded(
            () =>
            {
                var width = _surface.Width;
                var budget = width > 2 ? width - 2 : 0;
                var band = Ansi.Bg(_theme.Map(Theme.UserInputBg), _theme.TrueColor);
                var textOpen = Ansi.Fg(_theme.Map(Theme.Bright), _theme.TrueColor);
                var baseStyle = band + textOpen;   //the band is reopened after a styled span's own Reset
                var rows = new List<string>();
                var first = true;
                foreach (var line in lines)
                {
                    //sanitize the typed line before parsing spans, the same contract every other commit path keeps
                    var spans = InlineStyler.Parse(TermText.Sanitize(line));
                    foreach (var row in SpanWrap.Wrap(spans, budget, budget))
                    {
                        var sb = new StringBuilder();
                        sb.Append(band).Append(Ansi.ClearToEol);   //the band goes down first so the whole row is filled
                        if (first)
                            //the Paint() call ends with Reset, so the band is reopened before the text
                            sb.Append(_theme.Paint(_glyphs.Prompt, Theme.Accent, bold: true)).Append(band).Append(' ');
                        else
                            sb.Append(GutterWrap.Hang);
                        sb.Append(textOpen).Append(_theme.Paint(row, baseStyle)).Append(Ansi.Reset);
                        rows.Add(sb.ToString());
                        first = false;
                    }
                }
                if (_last != LastWrite.None) _blankPending = true;
                _model?.Append(new UserEchoItem(lines.ToList()));   //the model gets the lines, it re-renders the band itself
                CommitRows(rows);
                _last = LastWrite.Text;
                _blockOpen = false;
                _blankPending = true;   //one blank row between the sent message and its output
            },
            () =>
            {
                foreach (var line in lines) _surface.Write(_glyphs.Prompt + " " + TermText.Sanitize(line) + "\n");
            });
    }

    //the rows arrive sanitized and painted, so only the blank-row discipline is added here, and a block raised during a tool bullet waits for OnToolResult
    public void CommitPrompt(IReadOnlyList<string> rows)
    {
        if (rows.Count == 0) return;
        Guarded(
            () =>
            {
                if (_painter.State.Tool is not null)
                {
                    if (_pendingPrompt is null) _pendingPrompt = rows.ToList();
                    else { _pendingPrompt.Add(""); _pendingPrompt.AddRange(rows); }
                    return;
                }
                if (_inReasoning) CloseReasoningBlock();
                else FlushOpenLine();
                if (_last != LastWrite.None) _blankPending = true;
                //no model append here (an answered prompt is not transcript content)
                CommitRows(rows.ToList());
                _last = LastWrite.Text;
                _blockOpen = false;
                _blankPending = true;
            },
            () => { foreach (var r in rows) _surface.Write(r + "\n"); });
    }

    public void OnUsage(Usage usage)
    {
        try { _onUsage?.Invoke(usage); } catch (Exception) { }
    }

    //internals

    //run the body under the session gate, or the plain fallback once degraded. render never throws, a fault degrades the session and tears the painter down
    private void Guarded(Action body, Action degraded)
    {
        lock (_gate)
        {
            if (_degraded) { try { degraded(); } catch (Exception) { } return; }
            try { body(); }
            catch (Exception) { Degrade(); }
        }
    }

    //first fault wins, the painter is torn down once so plain bytes can't interleave with a live chrome block. every later event goes to the plain fallback
    private void Degrade()
    {
        _degraded = true;
        try { _painter.Teardown(); } catch (Exception) { }   //stops the compositor and restores the main screen
        try { DumpTail(); } catch (Exception) { }            //otherwise the user stares at a blank prompt with no idea what was said
        try { OnDegraded?.Invoke(); } catch (Exception) { }
    }

    //the alt buffer goes the instant Teardown restores the main screen, so reprint the tail of the model plain, before the degraded fallback starts writing
    private void DumpTail()
    {
        if (_model is null) return;
        var width = _surface.Width > 0 ? _surface.Width : 80;
        var rows = _model.Items.SelectMany(i => i.Render(width, _theme, _glyphs)).ToList();
        const int cap = 40;
        var sb = new StringBuilder();
        for (var i = Math.Max(0, rows.Count - cap); i < rows.Count; i++)
            sb.Append(rows[i]).Append(Ansi.Reset).Append('\n');
        if (sb.Length > 0) _surface.Write(sb.ToString());
    }

    //split the delta on newlines, each segment extends the open line and each newline commits it. the leftover goes to the tail chrome
    private void Feed(string text)
    {
        var parts = text.Split('\n');
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length > 0) _tail.Append(parts[i]);
            if (i < parts.Length - 1) CommitLine();
        }
    }

    //commit the open line and clear the tail chrome here, the text has moved to scrollback and keeping both draws it twice
    private void FlushOpenLine()
    {
        if (_tail.Length > 0) { CommitLine(); ClearTail(); }
        FlushTableBuffer();   //an aborted or unclosed turn still emits its partial table
    }

    //linear mode only, rich mode re-renders the open item so its table grows in place. the append-only surface takes the block whole at close
    private List<string>? _tableBuffer;

    //true when the line went into the table buffer, false when the caller must commit it
    private bool BufferTableLine(string raw)
    {
        if (_model is not null) return false;   //rich mode, the model owns table rendering

        if (_tableBuffer is null)
        {
            if (!TableParse.IsRowShaped(raw)) return false;
            _tableBuffer = new List<string> { raw };
            return true;
        }

        if (TableParse.IsRowShaped(raw)) { _tableBuffer.Add(raw); return true; }
        FlushTableBuffer();
        return false;
    }

    private void FlushTableBuffer()
    {
        if (_tableBuffer is not { } buf) return;
        _tableBuffer = null;
        if (TableParse.TryParse(buf, 0, out var spec, out _) && spec is not null)
        {
            //text lives at column 2, so lay out at width − 2 and prefix the hang. otherwise the grid starts at column 0, two cells out of line with the prose rows
            var rows = TableLayout.Rows(spec, _theme,
                Math.Max(1, _surface.Width - GutterWrap.Hang.Length), _glyphs);
            CommitRows(rows.Select(r => GutterWrap.Hang + r.Text).ToList());
            _blockOpen = true;
        }
        else
        {
            //these lines go back through the normal styling path, otherwise a prose '|' line that isn't a table shows up unstyled at column 0
            foreach (var line in buf) CommitParsedLine(line);
        }
    }

    private void CommitLine()
    {
        var raw = _tail.ToString();
        _tail.Clear();

        //the model re-renders from the raw line, and ProseRun derives the same blanks, fence swallow and single ● per run as the commit path below
        _model?.AppendOpenLine(raw, _inReasoning);

        //check the block state here, the guard sits before the fence handling so a '|' line inside a fence would look like a table
        if (!_inReasoning && _block == BlockState.Normal && BufferTableLine(raw)) return;

        CommitParsedLine(raw);
    }

    private void CommitParsedLine(string raw)
    {
        var width = _surface.Width;

        if (_inReasoning)
        {
            CommitRows(GutterWrap.Rows(GutterWrap.Hang, GutterWrap.Hang, raw, width,
                s => _theme.Paint(s, Theme.Dim, italic: true)).ToList());
            return;
        }

        //a blank source line ends the paragraph, one blank row flushed with the next block. the _blockOpen flag survives so the next paragraph hangs and no new ●
        if (_block == BlockState.Normal && raw.Trim().Length == 0)
        {
            if (_last != LastWrite.None) _blankPending = true;
            return;
        }

        //style once, here at commit time, there is no repaint, and the text starts at column 2 with the gutter owning columns 0–1
        var textWidth = width - 2;
        var accent = _theme.RoleTint(_role);   //the live output uses the role's colour
        var r = MarkdownLine.RenderLine(raw, _block, _theme, textWidth, accent, _glyphs);
        _block = r.Next;
        if (r.FenceClose) return;   //the closing fence is structure, so no row is committed for it

        var marker = _blockOpen ? GutterWrap.Hang : _theme.Paint(ItemRender.ProseMarkerOf(_glyphs), accent) + " ";
        var rows = new List<string>();
        if (r.Spans is null)
        {   //hr and fence lines, already wrapped by MarkdownLine
            var noSpan = r.Rows ?? new[] { r.Styled ?? "" };
            for (var i = 0; i < noSpan.Count; i++)
                rows.Add((i == 0 ? marker : GutterWrap.Hang) + noSpan[i]);
        }
        else
        {
            var wrapped = SpanWrap.Wrap(r.Spans, textWidth - r.FirstPrefixCells, textWidth - r.ContPrefixCells);
            for (var i = 0; i < wrapped.Count; i++)
            {
                var sb = new StringBuilder(i == 0 ? marker : GutterWrap.Hang);
                sb.Append(i == 0 ? r.FirstPrefix : r.ContPrefix)
                  .Append(r.LineStyle)
                  .Append(_theme.Paint(wrapped[i], r.LineStyle, accent));
                if (r.LineStyle.Length > 0) sb.Append(Ansi.Reset);
                rows.Add(sb.ToString());
            }
        }
        CommitRows(rows);
        _blockOpen = true;
    }

    //every commit goes through here, so the blank-row rule has one home. an empty list still clears the pending blank (the tool bullet's separator)
    private void CommitRows(List<string> rows)
    {
        if (_blankPending) { rows.Insert(0, ""); _blankPending = false; }
        if (rows.Count == 0) return;
        CommitTap?.Invoke(rows);   //the test tap gets the rows exactly as they were committed
        _painter.NotifyCommitted();
    }

    //the committed item's own rows at the surface width, the decision rows after its header, so a test reading the tap reads the paint
    private List<string> TapRows(ToolBlockItem item)
    {
        var body = item.Render(_surface.Width, _theme, _glyphs).Skip(item.LeadingBlank ? 1 : 0).ToList();
        if (_pendingPrompt is { } pending)
        {
            body.InsertRange(Math.Min(1, body.Count), pending);
            _pendingPrompt = null;
        }
        return body;
    }

    //commit the ♯ lines buffered while a tool row was chrome, right after the block they were raised under, never past the blocks after it
    private void FlushPendingSystem()
    {
        if (_pendingSystem is not { } pending) return;
        _pendingSystem = null;
        foreach (var (text, transient) in pending) CommitSystemNow(text, transient);
    }

    private void OpenReasoningBlock()
    {
        FlushOpenLine();
        if (_last != LastWrite.None) _blankPending = true;
        _inReasoning = true;
        _blockOpen = false;
        _reasoningStart = _now();   //stamp the block's start, the collapsed summary reports the elapsed
        //a new reasoning item starts capped under collapsed mode and full under expanded, and a passthrough summary is always full
        if (_model is not null)
            _model.ReasoningCollapseDefault = _reasoning == ReasoningMode.Collapsed && !PassthroughReasoning;
    }

    private void CloseReasoningBlock()
    {
        FlushOpenLine();
        //seal the item at the block boundary with the collapse decision, a passthrough summary is styled reasoning and must never auto-collapse
        var collapse = _reasoning == ReasoningMode.Collapsed && !PassthroughReasoning;
        _model?.CloseOpenReasoning(_now() - _reasoningStart, collapse);
        _inReasoning = false;
        _blockOpen = false;
        if (_last != LastWrite.None) _blankPending = true;
    }

    //publish the open line as the chrome tail and repaint. a reasoning tail paints dim italic with no marker, and a tail that opens a text block gets the ●
    private void PaintTail()
    {
        SetTail();
        _painter.Repaint();
    }

    private void SetTail()
    {
        var st = _painter.State;
        //the in-progress line stays out of the tail while reasoning is capped, otherwise it rotates past the preview. the open item decides, so Ctrl+R brings it back
        var reasoningCapped = _inReasoning && (
            _model?.OpenItem is ReasoningItem ri ? ri.Collapsed
            : _reasoning == ReasoningMode.Collapsed && !PassthroughReasoning);
        st.TailRaw = (_tail.Length > 0 && !reasoningCapped) ? _tail.ToString() : null;
        st.TailReasoning = _inReasoning;
        st.TailFence = !_inReasoning && _block == BlockState.Fence;
        st.TailMarker = _inReasoning || _blockOpen
            ? null
            : _theme.Paint(ItemRender.ProseMarkerOf(_glyphs), _theme.RoleTint(_role)) + " ";
        //mirror the pending blank in the live tail, otherwise it appears a second late and shoves the transcript down
        st.TailBlank = _blankPending && st.TailRaw is not null;
    }

    private void ClearTail()
    {
        var st = _painter.State;
        st.TailRaw = null;
        st.TailReasoning = false;
        st.TailFence = false;
        st.TailMarker = null;
        st.TailBlank = false;
    }

    //the live argument preview comes from ItemRender.HeaderArgs, keep it a one-line delegation so there is one formatter
    private string HeaderArgs(string name, string json) => ItemRender.HeaderArgs(name, json, _glyphs);

    //the degraded [tool] line truncates by char count, the rich path measures cells
    private static string TruncateChars(string s, int max, GlyphSet g) =>
        s.Length <= max ? s : s[..max] + g.Ellipsis;
}
