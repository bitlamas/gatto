using System.Runtime.ExceptionServices;
using Gatto.Core.Client;
using Gatto.Core.Home;
using Gatto.Core.Loop;
using Gatto.Repl.Commands;
using Gatto.Repl.Input;
using Gatto.Repl.Prompts;
using Gatto.Repl.Render;
using Gatto.Repl.Render.Chrome;
using Gatto.Repl.Render.Pointer;
using Gatto.Terminal;

namespace Gatto.Repl;

//the rich face's composer loop and the dispatch of each line it queues
public sealed partial class Repl
{
    //the composer's own task feeds a FIFO the dispatcher consumes, and every paint takes the gate while editor.Read waits outside it
    private async Task<int> RunLinearLoopAsync(RichState s, LineEditor editor, DispatchQueue queue,
        ArmedChord chords, Gatto.Terminal.IClipboard clip, CancellationToken appCt)
    {
        Ready();
        var idleView = new EditorView(new List<string> { "" }, 0, 0);
        var lastView = idleView;         //written and read only under the gate, see RedrawLocked
        //reads lastView by reference so it sees the current composer contents, and it is set before the first dispatch
        s.ComposerEmpty = () => lastView.Lines.Count == 1 && lastView.Lines[0].Length == 0;
        var stopped = false;             //teardown latch, written and read only under the gate
        var degraded = false;            //renderer degrade latch, written and read only under the gate
        //the pump died, console gone or a dispatch that never recovered, read once at the exit epilogue so the session can say why it ended
        var pumpDied = false;
        Exception? fault = null;         //the first fault wins, from the composer task, the ticker or the resize poller

        //route diagnostics to the current renderer's system line, stderr would corrupt the alt screen, and drop them once stopped is set
        using var warnScope = warn?.Route(m => { lock (s.Gate) { if (!stopped) s.Renderer.OnWarning(m); } });

        //ends the session when the pump dies or Ctrl+C is pressed at rest, otherwise the dispatcher parks on the queue forever
        using var loopCts = CancellationTokenSource.CreateLinkedTokenSource(appCt);
        var loopCt = loopCts.Token;
        s.CancelLoop = () => { try { loopCts.Cancel(); } catch (ObjectDisposedException) { } };

        //caller holds the gate, which is the ordering for lastView, and every repaint is unconditional
        void RedrawLocked(EditorView v)
        {
            lastView = v;
            if (stopped) return;     //skip the repaint once teardown has run
            if (degraded)
            {
                //after a degrade Repaint() is a no-op, so echo the composer inline or the user types blind while Enter still sends
                EchoComposerInline(s.Surface, v, s.Glyphs);
                return;
            }
            s.Painter.State.Composer = v;
            s.Painter.Repaint();
        }

        //a degrade ends the alt screen but not the session, so give the shell's mouse mode back now, and the call is idempotent
        s.OnDegraded = () => { degraded = true; s.RestoreMouseMode?.Invoke(); };
        s.Renderer.OnDegraded = s.OnDegraded;
        //the Alt+V key pastes an image and names Ctrl+V when there is nothing to paste, Ctrl+V itself belongs to the terminal
        var clipboardImages = clipboardImageSource ?? new Win32ClipboardImageNative();
        _clipboardImages = clipboardImages;   //the footer's Alt+V hint reads this field too
        //this runs on the editor thread mid-turn, so never read convo here, dedupe and caps happen at submit
        editor.OnPasteImage = () =>
        {
            var r = ImagePaste.Paste(clipboardImages, Path.Combine(GattoHome.Resolve(), "blobs"));
            lock (s.Gate)
            {
                //nothing stashed and nothing painted once the session is stopped
                if (stopped) return;
                foreach (var (path, bytes) in r.Unsaved ?? new Dictionary<string, byte[]>()) _unsavedPaste[path] = bytes;
                foreach (var c in r.Candidates)
                {
                    var n = ++_pastedCount;
                    //the display name is the placeholder the user sees, one naming scheme for the picture
                    _pendingPaste[n] = c.DisplayName is null
                        ? c with { DisplayName = ImageAttach.Placeholder(n) }
                        : c;
                    //the placeholder in the text is the attachment, where it can be seen, moved and deleted
                    editor.InsertAtCursor(ImageAttach.Placeholder(n));
                }
                //feedback goes on the composer header, and nothing is said on success since the placeholders already show what is attached
                if (r.Candidates.Count == 0)
                {
                    _pasteHint = r.Notice;
                    _pasteHintUntil = DateTime.UtcNow.AddSeconds(PasteHintSeconds);
                    //the frame needs a wake-up when the hint expires, RefreshStatus only runs on events, a timer that repaints is enough
                    _armHintExpiry?.Invoke(PasteHintSeconds * 1000 + 100);
                }
                s.Refresh();
                s.Painter.Repaint();
            }
        };

        //a tool result arriving moves the context figure, so the footer repaints through the loop's existing shared repaint
        s.OnContextChanged = RepaintChrome;
        s.Renderer.OnContextChanged = s.OnContextChanged;

        void Redraw(EditorView v) { lock (s.Gate) RedrawLocked(v); }
        void RedrawLast() { lock (s.Gate) RedrawLocked(lastView); }

        //armed before the first dispatch, where the editor and Redraw both live, so the first refusal can restore the text
        _restoreComposer = BuildRestoreComposer(s.Gate, editor, Redraw, () => stopped);

        //the repaint any thread can call, take the gate, rebuild the frame, repaint, and skip it once stopped
        void RepaintChrome() { lock (s.Gate) { if (!stopped) { s.Refresh(); s.Painter.Repaint(); } } }
        onUsageChanged?.Invoke(RepaintChrome);
        //one reusable timer re-armed on each press so a hint vanishes by itself after the window, disposed at teardown
        var hintTimer = new System.Threading.Timer(_ => RepaintChrome(), null, Timeout.Infinite, Timeout.Infinite);
        //the timer only wakes the frame, and each hint's own staleness check drops it when RefreshStatus runs
        _armHintExpiry = ms => hintTimer.Change(ms, Timeout.Infinite);
        void OnHintChanged()
        {
            RepaintChrome();
            //re-arm only when a press left a chord armed, and the extra margin makes the shot fire after expiry
            if (chords.Armed != Chord.None)
                hintTimer.Change(CtrlCDoubleTapMs + 50, Timeout.Infinite);
        }
        //exposed through s so the mouse-off Ctrl+C handler on another thread can show the same hint
        s.OnHintChanged = OnHintChanged;

        //the one pusher, called when the queue changes, so the chrome and the queue cannot part
        void PublishQueueLocked() => s.Painter.State.QueueTexts = queue.Texts();

        //one fault slot shared with the composer and resize tasks, the first fault wins, and the cancel is punted off the timer thread
        void OnChromeFault(Exception ex)
        {
            Interlocked.CompareExchange(ref fault, ex, null);
            _ = Task.Run(() => { try { loopCts.Cancel(); } catch (Exception) { } }, CancellationToken.None);
        }
        s.Ticker.OnFault = OnChromeFault;

        //resize poller, nothing else notices a resize while the composer parks on the pump, so it repaints once the drag settles
        var resize = new ResizeWatch(s.Surface.Width, s.Surface.Height);
        _ = Task.Run(async () =>
        {
            try
            {
                while (!loopCt.IsCancellationRequested)
                {
                    await Task.Delay(ResizePollMs, loopCt);
                    if (resize.Sample(s.Surface.Width, s.Surface.Height)) RedrawLast();
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { OnChromeFault(ex); }
        }, CancellationToken.None);

        //the composer task
        _ = Task.Run(() =>
        {
            string? rejected = null;   //only this task writes and reads it
            try
            {
                while (!loopCt.IsCancellationRequested)
                {
                    string text;
                    try
                    {
                        text = editor.Read(Redraw, rejected,
                            //the whole queue comes back at once with its permits, and an empty answer lets the editor recall history
                            pullQueue: () =>
                            {
                                lock (s.Gate)
                                {
                                    var taken = queue.DrainAll();
                                    if (taken.Count > 0 && !stopped) PublishQueueLocked();
                                    return taken;
                                }
                            },
                            chords: chords,
                            onQuit: () => s.CancelLoop?.Invoke(),
                            onHintChanged: OnHintChanged,
                            //a Ctrl+C with Ctrl+A's selection live copies through the same clipboard instance as a drag, no gate needed
                            onCopyText: text => clip.TrySet(text),
                            //every composer-selection set-site clears any live transcript or chrome span, so one selection domain is live at most
                            onComposerSelectionSet: BuildComposerSelectionSetHook(s.Painter, s.Gate, () => stopped));
                    }
                    catch (ComposerPaintException) { throw; }   //a paint bug, rethrown so it is not mistaken for pump death
                    catch (Exception)
                    {
                        //nothing can be typed again, so end the session rather than leave the dispatcher blocked on a queue nobody fills
                        pumpDied = true;
                        return;
                    }
                    rejected = null;
                    if (text.Trim().Length == 0) continue;
                    var typed = text;
                    //the placeholders beside a command seed the next Read here, where no restore can race the reseed
                    string? carry = null;
                    if (CommandAmongPlaceholders(text, KnownCommands()) is { } split) (text, carry) = split;
                    //a command that only reads answers at once while a turn runs, drawn where the composer was, and never waits in the queue
                    if (BypassNow(text, s) is { } answer)
                    {
                        lock (s.Gate) { if (!stopped) RedrawLocked(idleView); }
                        //the counts are awaited off this thread, so the composer keeps reading keys while a server answers
                        _ = OpenInfoPanelAsync(answer, s, () => stopped);
                        rejected = carry;
                        continue;
                    }
                    if (queue.TryEnqueue(text))
                    {
                        //one repaint clears the composer and shows the new queued row
                        lock (s.Gate) { PublishQueueLocked(); RedrawLocked(idleView); }
                        rejected = carry;
                    }
                    else
                    {
                        rejected = typed;           //handed back to the next Read as typed, which reseeds the composer
                        //cap hit, beep and keep the text, guarded by stopped so no bell is written into an erased chrome
                        lock (s.Gate) { if (!stopped) s.Surface.Write("\a"); }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                //a clean shutdown is not a fault
            }
            catch (Exception ex)
            {
                //a fault out of this body is stashed and re-raised on the dispatcher instead of dying unobserved, and the first fault wins
                Interlocked.CompareExchange(ref fault, ex, null);
            }
            finally
            {
                //any exit of this task ends the session, the composer is the only producer and the dispatcher would park forever otherwise
                try { loopCts.Cancel(); } catch (ObjectDisposedException) { }
            }
        }, CancellationToken.None);

        //the dispatcher paints too, and no exit from this loop skips chrome teardown
        try
        {
            while (!loopCt.IsCancellationRequested)
            {
                string? input;
                try { input = await queue.TakeAsync(loopCt); }
                catch (OperationCanceledException) { break; }
                //null means Esc dropped this message after its permit, so the counts agree again and the loop just repaints
                if (input is null) { lock (s.Gate) { PublishQueueLocked(); RedrawLocked(lastView); } continue; }

                if (await DispatchLinearAsync(input, s, queue, appCt)) break;
                lock (s.Gate) { PublishQueueLocked(); RedrawLocked(lastView); }   //fresh status and ctx after the turn
            }
        }
        finally
        {
            //cancel the loop before loopCts is disposed, a /quit exit breaks the while without cancelling and the resize poller's next Task.Delay would throw
            try { loopCts.Cancel(); } catch (ObjectDisposedException) { }
            hintTimer.Dispose();            //stop a pending composer-hint expiry shot
            TeardownChrome(s.Gate, s.Painter, s.Ticker, _chrome, () => stopped = true, Volatile.Read(ref fault));
            s.RestoreMouseMode?.Invoke();   //restore the shell's mouse and QuickEdit mode (idempotent, the degrade path and the exit hooks call it too)
        }
        //the alt screen is gone with the transcript, so Ended tells the exit block whether to reprint it or offer a resume
        Ended = EndOf(degraded, dumpOnExit, sessions?.CountForCwd() > 0, pumpDied);
        if (!degraded) ExitEpilogue(s.Surface, s.Painter.Model, s.Theme, dumpOnExit, pumpDied, s.Glyphs);
        return 0;
    }

    //how the session ended, for the exit block the launcher prints once this loop has returned. it stays null on the plain loop, which keeps its own exit as it was.
    public SessionEnd? Ended { get; private set; }

    //the resume line needs a session to resume and a transcript that was not reprinted (a reprinted one is already the record)
    internal static SessionEnd EndOf(bool degraded, bool dumpOnExit, bool hasSessions, bool inputDied) =>
        new(Farewell: !degraded && !inputDied, Resume: !degraded && !dumpOnExit && hasSessions);

    //the transcript's exit on the restored main screen, it reports an input death or reprints the whole transcript on request
    internal static void ExitEpilogue(ITermSurface surface, TranscriptModel model, Theme theme, bool dumpOnExit,
        bool inputDied = false, Gatto.Terminal.GlyphSet? glyphs = null)
    {
        var width = surface.Width > 0 ? surface.Width : 80;
        if (inputDied)
            surface.Write(theme.Paint("gatto hit an input error, and your session is saved.", Theme.Warn) + "\n");
        if (!dumpOnExit) return;

        var sb = new System.Text.StringBuilder();
        foreach (var row in model.Items.SelectMany(i => i.Render(width, theme, glyphs)))
            sb.Append(row).Append(Ansi.Reset).Append('\n');
        if (sb.Length > 0) surface.Write(sb.ToString());
    }

    //this bracket starts and stops the purr, the body cannot end it early. the renderer is a Func, a /role swap mid-turn must still close the current turn
    internal static async Task<bool> DispatchBracketAsync(
        object gate, ChromePainter painter, ChromeTicker ticker, Func<StreamRenderer> renderer,
        IReadOnlyList<string> queued, IReadOnlyList<string> userLines, string kaomoji,
        IReadOnlyList<SlashCommands.Cmd> known, Func<Task<bool>> body,
        long promptEstimate = 0, Func<Render.Chrome.PurrFrames>? purrSet = null, string? classifyAs = null)
    {
        lock (gate)
        {
            painter.State.QueueTexts = queued;     //the dispatched message has left the queue
            renderer().CommitUser(userLines);      //the ❯ echo becomes scrollback
            renderer().BeginTurn();
            //only a submission that reaches the model purrs. classify the typed text, a typed //help reaches the model while the echo reads /help
            if (SlashCommands.ReachesModel(classifyAs ?? string.Join("\n", userLines), known))
                //the purr is drawn once per turn, the accordion must not change word mid-breath. the chooser is injected, a session without one draws the same frames
                ticker.StartTurn(kaomoji, promptEstimate,
                    purrSet is null ? Render.Chrome.PurrFrames.Full : purrSet());
        }
        try { return await body(); }
        finally
        {
            //call EndTurn before StopTurn, a Ctrl+C mid-tool leaves the tool row and its permission answer in the state and StopTurn would erase them
            renderer().EndTurn();
            //read the completion row before StopTurn stops the clock, and repaint there only when there is none so the composer stays put
            var purred = ticker.CompletionRow();
            ticker.StopTurn(repaint: purred is null);
            if (purred is not null) renderer().CommitCompletion(purred);
        }
    }


    //retire the ticker under the gate before the painter erases anything, a tick after that would paint onto a cleaned screen
    internal static void TeardownChrome(
        object gate, ChromePainter painter, ChromeTicker ticker, ChromeHandle? chrome,
        Action markStopped, Exception? fault)
    {
        try
        {
            lock (gate)
            {
                markStopped();
                ticker.StopTurn();          //idempotent, and its own paint faults go to OnFault
                ticker.OnFault = null;
                if (chrome is not null) { chrome.Painter = null; chrome.Renderer = null; }
                painter.Teardown();          //idempotent, it erases the chrome and never paints again
            }
        }
        catch when (fault is not null) { } //the console is sick, the fault below is the real story
        if (fault is not null) ExceptionDispatchInfo.Capture(fault).Throw();
    }

    //rewrites the current composer line at the cursor row when the painter is dead, a one-line fallback rather than a frame
    internal static void EchoComposerInline(ITermSurface surface, EditorView v, Gatto.Terminal.GlyphSet? glyphs)
    {
        var line = v.Lines.Count > 0 ? v.Lines[Math.Clamp(v.CursorLine, 0, v.Lines.Count - 1)] : "";
        surface.Write("\r" + Ansi.ClearLine + (glyphs ?? Gatto.Terminal.GlyphSet.Unicode).Prompt + " "
            + TermText.Sanitize(line));
    }

    //the rich session's mutable wiring, the reference types a /role swap or a status rebuild replaces live here so no caller holds a stale local
    private sealed class RichState
    {
        //the interface, so the session state does not name the concrete surface
        public ITermSurface Surface = null!;
        public Theme Theme = null!;

        //the one glyph set for the session, a rebuild hands the new painter the same set rather than resolving one again
        public Gatto.Terminal.GlyphSet Glyphs = Gatto.Terminal.GlyphSet.Unicode;
        public StreamRenderer Renderer = null!;
        public InputFrame Frame = null!;
        public Action Refresh = null!;
        public Action<Usage> OnUsage = null!;
        public CancellationTokenSource? TurnCts;

        //the one CtxState for the session, fed by RefreshStatus every turn so the compact offer reads the same instance instead of rebuilding one
        public CtxState? Ctx;

        //the one lock every painter serializes on, set once and handed to the /role replacement renderer so a swap keeps the same object
        public object Gate = null!;

        //the session's one painter and one turn ticker, both outlive a /role swap since the replacement renderer is built around them
        public ChromePainter Painter = null!;
        public ChromeTicker Ticker = null!;

        //cancels the linear loop on Ctrl+C at rest, set by RunLinearLoopAsync which owns the loop's token source
        public Action? CancelLoop;

        //the loop's degrade latch, a /role swap must re-point it at the replacement renderer or a degrade after the swap never reaches the loop
        public Action? OnDegraded;

        //the mid-turn footer refresh, a /role swap must re-point it at the replacement renderer or the footer stops moving after the change
        public Action? OnContextChanged;

        //restores the shell's mouse and QuickEdit mode, held here since the inputMode local is out of scope at the degrade latch and teardown
        public Action? RestoreMouseMode;

        //the composer hint repaint, held here so the Ctrl+C handler outside the loop can run the same repaint and expiry
        public Action? OnHintChanged;

        //the live composer-empty predicate, a closure over the published view so every read sees the current contents (null means empty until the loop starts)
        public Func<bool>? ComposerEmpty;
    }

    //a line of image placeholders and one command, in any order, is that command, and the placeholders go back to the composer as typed. null for any other line, a // escape included
    internal static (string Command, string Carry)? CommandAmongPlaceholders(string text, IReadOnlyList<SlashCommands.Cmd> known)
    {
        var (held, rest) = ImageAttach.SplitPlaceholders(text);
        if (held.Count == 0 || rest.Trim().Length == 0) return null;
        var gate = SlashGate.GateSlash(rest, known);
        if (gate.Literal || !(gate.Refused || SlashGate.IsSlashCommand(rest, known))) return null;
        return (rest.Trim(), string.Concat(held.Select(p => p + " ")));
    }

    //takes the attach decision above the turn bracket, a refusal from inside the body comes after the echo and the purr
    private async Task<bool> DispatchLinearAsync(string input, RichState s, DispatchQueue queue, CancellationToken appCt)
    {
        //refuses an unknown command above the bracket, and clears the composer instead of handing the text back since a restore races the composer's own paint
        var submitted = input;   //what was typed, the bracket classifies this rather than the stripped text
        var gate = SlashGate.GateSlash(input, KnownCommands());
        if (gate.Refused)
        {
            s.Renderer.CommitSystem(gate.Refusal!);
            return false;
        }
        input = gate.Text;   //a typed // has lost its first slash, everything below sees the stripped text

        var attach = AttachDecision.None;
        //the one place attachments are acquired, and the literal flag keeps a //help message on this arm since the stripped text reads as a command
        if (gate.Literal || !SlashGate.IsSlashCommand(input, KnownCommands()))
        {
            attach = PrepareAttachments(input, s);
            if (attach.Refused)
            {
                //commit only the cause's own sentence, the user still has to resend this message
                foreach (var line in attach.RefusalLines!) s.Renderer.CommitSystem(line);
                _restoreComposer?.Invoke(input);
                return false;
            }
            //clear the pending pastes only on the path that sends, so a command typed after an image keeps the image attached
            lock (s.Gate)
            {
                _pendingPaste.Clear();
                _unsavedPaste.Clear();   //a paste whose placeholder was deleted goes with its bytes, never written
            }
        }
        return await DispatchBracketAsync(s.Gate, s.Painter, s.Ticker, () => s.Renderer,
            queue.Texts(), input.Split('\n'), Cats.Face(_glyphs), KnownCommands(),
            () => DispatchLinearBodyAsync(input, s, attach, appCt, gate.Literal),
            //what the model is about to re-read, measured before the turn appends anything
            promptEstimate: ScaledPromptEstimate(ContextBudget.Estimate(convo.Messages), _usageState.Ratio),
            purrSet: purrSet, classifyAs: submitted);
    }

    //what a message attaches and why it may not send, the notices commit inside the turn and a refusal line stops the message
    private readonly record struct AttachDecision(
        IReadOnlyList<ImageRef>? Images,
        IReadOnlyList<ImageAttach.Notice> Notices,
        IReadOnlyList<string>? RefusalLines)
    {
        //nothing to attach and nothing to say, a slash command or a message with no images
        internal static AttachDecision None { get; } = new(null, Array.Empty<ImageAttach.Notice>(), null);

        internal static AttachDecision Send(IReadOnlyList<ImageRef>? images, IReadOnlyList<ImageAttach.Notice> notices) =>
            new(images, notices, null);

        internal static AttachDecision Refuse(params string[] lines) =>
            new(null, Array.Empty<ImageAttach.Notice>(), lines);

        internal bool Refused => RefusalLines is not null;
    }

    //decides what this message attaches and commits nothing, so the caller owns the user-visible consequences and the pastes survive a refusal
    private AttachDecision PrepareAttachments(string input, RichState s)
    {
        //only images whose placeholder is still in the text attach, the placeholders stay in the message so the model knows where each picture sits
        IReadOnlyList<ImageRef> pasted;
        lock (s.Gate)
        {
            pasted = ImageAttach.ReferencedIn(input, _pendingPaste);
            _pasteHint = null;
            _pasteHintUntil = null;
        }
        //the attached set comes from the live conversation, an accumulated one duplicates after a restore. the notices merge in one place, ImageAttach
        var guarded = ImageAttach.Detect(input, cwd, pasted, ImageAttach.ShasIn(convo.Messages),
            AttachLimits.Default);
        var notices = guarded.Notices;

        //block the notices by kind rather than by their wording, and report all of them since a message can name two ambiguous files
        var blocked = notices.Where(n => n.Kind == ImageAttach.NoticeKind.Blocked)
                             .Select(n => n.Text).ToArray();
        if (blocked.Length > 0) return AttachDecision.Refuse(blocked);

        var collected = guarded.Attached;
        //refuse the attach when the model cannot see, a server without the projector answers HTTP 500 and the whole turn would die
        if (collected.Count > 0 && VisionUnavailable() is string why) return AttachDecision.Refuse(why);

        //the count question is checked last and a session with nobody to ask sends the batch, it is a courtesy rather than a wall
        if (guarded.NeedsConfirmation && !(_attachManyGranted?.Invoke() ?? false) && _confirmAttach is { } ask)
        {
            var answer = ask(collected.Count, collected.Count * ImageAttach.EstimatedTokensPerImage);
            //a No answer commits nothing, the count question was the sentence and a row about it would only repeat it
            if (answer == ConfirmAttachAnswer.No) return AttachDecision.Refuse();
            if (answer == ConfirmAttachAnswer.YesAlways) _grantAttachMany?.Invoke();
        }

        //a pasted image reaches the home only now that it is sent, and a write that fails keeps the message unsent with the reason
        try
        {
            lock (s.Gate) ImagePaste.SaveSent(collected, _unsavedPaste);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return AttachDecision.Refuse($"could not save the pasted image: {ex.Message}");
        }

        //null when there are no images, an empty list would be a second way of saying nothing
        return AttachDecision.Send(collected.Count > 0 ? collected : null, notices);
    }

    //the reason an image cannot go to the answering model, or null when it can. only a definite false blocks an attach, unknown means no probe answered
    private string? VisionUnavailable() =>
        //a definite false only, a probe that could not answer leaves the bit null and an unknown must not block an attach
        probeServing?.Invoke() is { Vision: false }
            //on a session gatto does not serve the tail says nothing about switching models, /model refuses there
            ? unmanagedVisionNotice
              //when the encoder is already beside the model the tail names the one-key fix, otherwise the generic ending stands
              ?? "this model can't see images — message not sent. "
                 + (visionFixHint ?? "Remove the image tokens to send it anyway, or /model to one that can.")
            : null;

    //the command chain and the model turn, true ends the session. every status line is a ♯ row, the bracket owns the echo, turn and purr
    private async Task<bool> DispatchLinearBodyAsync(string input, RichState s, AttachDecision attach,
        CancellationToken appCt, bool literal = false)
    {
        var trimmed = literal ? "" : input.Trim();

        if (trimmed == "/quit") return true;
        if (trimmed == "/help")
        {
            //the help block lists the commands you type and then the keys you press, gutter rows with no ♯ marker
            s.Renderer.CommitPlain(SlashCommands.RenderRich(s.Theme, KnownCommands()) + "\n\n" + Shortcuts.RenderRich(s.Theme, glyphs: _glyphs, KnownCommands()));
            return false;
        }
        if (trimmed == "/new")
        {
            //hold the warning sink first, the recompose below and any extension hook can both raise a diagnostic
            using var held = warn?.Hold();
            //a ♯ row committed here would be erased by the rotation below, which resets the display model
            var carry = new List<string>();
            _lastBankFailure = null;   //only this rotation's failure may be carried, BankMemory may not run at all
            //the recompose, the bookkeeping and the display reset are one seam, a separate reset would discard the ♯ rows the recompose just committed
            RotateForNew(recomposeSystem, held,
                adopt: fresh =>
                {
                    fresh.SetGattoRole(roleName);          //every record after a reset carries the running role, as the launch's do
                    convo = fresh;
                    sessions?.StartNew();
                    sessionName = null;                    //the name belongs to the session /new ends
                    s.Painter.AltScreen.Name(null);        //and the tab goes back to what the unnamed sequence holds
                    _offered = false;                      //a fresh session, so the 85% offer may fire again
                    _usageState.Clear();                   //the old reading describes a conversation that is gone
                    loop.ResetStripWarning();              //a fresh session reports its own first leak
                    loop.ResetAutoCompact();               //a fresh session may auto-compact again
                    s.Ctx?.RecordUsedTokens(null);         //the same for the context figure
                    s.Refresh();                           //a fresh conversation, so the ctx estimate resets too
                },
                s.Painter.Model, s.Renderer, BannerItem(s.Theme, roleName, roleCat, modelName, versionLine, s.Glyphs),
                NewConversationLine, carry);
            if (SeedFirstMessage(convo, firstMessage))
            {
                ReplayTranscript(convo.Messages, s.Renderer, u => s.Renderer.CommitUser(u.Split('\n')));
                s.Renderer.EndTurn();
            }
            s.Painter.Repaint();
            return false;
        }
        if (SlashGate.TryMatchCommand(trimmed, "/wild", out var wildArg))
        {
            //one ♯ row per line, CommitSystem strips a newline so two sentences in one call would mash together
            foreach (var line in HandleWild(wildArg).Split('\n'))
                s.Renderer.CommitSystem(line);
            s.Refresh();                                   //the wild toggle changed, the footer chip must follow
            return false;
        }
        if (SlashGate.TryMatchCommand(trimmed, "/sandbox", out var sandboxArg))
        {
            s.Renderer.CommitSystem(HandleSandbox(sandboxArg));
            return false;
        }
        if (trimmed == "/safe")
        {
            s.Renderer.CommitSystem(HandleSafe());
            s.Refresh();
            return false;
        }
        if (SlashGate.TryMatchCommand(trimmed, "/permissions", out var permArg))
        {
            //one ♯ row per line again, the listing has a row per grant plus the wild row
            foreach (var line in HandlePermissions(permArg).Split('\n'))
                s.Renderer.CommitSystem(line);
            return false;
        }
        if (MatchRegistered(trimmed) is { } reg)
        {
            //one ♯ row per line, as /wild commits its lines
            foreach (var row in await RunRegisteredAsync(reg.Row, reg.Arg, appCt))
                s.Renderer.CommitSystem(row);
            return false;
        }
        if (trimmed == "/context")
        {
            var figures = await ContextFiguresAsync(appCt);
            if (figures is null) { s.Renderer.CommitSystem(ContextUnavailable); return false; }
            //the transcript hangs gatto's rows under its gutter, which stands in for the screen's own two-cell margin
            s.Renderer.CommitContext(figures);
            return false;
        }
        if (trimmed == "/tools")
        {
            if (listTools is null) { s.Renderer.CommitSystem(ToolsUnavailable); return false; }
            var tools = listTools();
            var policy = PolicyRows();
            //read both lists, a pure-policy extension is armed and calling it no tools would deny it
            if (tools.Count == 0 && policy.Count == 0)
            { s.Renderer.CommitSystem("no tools are armed this session"); return false; }
            s.Renderer.CommitSystem(CommandText.ToolsHeadline(tools.Count));
            s.Renderer.CommitListing(CommandText.ToolsSpec(tools, policy, s.Glyphs));
            return false;
        }
        if (trimmed == "/compact")
        {
            lock (_ctsLock) { s.TurnCts = CancellationTokenSource.CreateLinkedTokenSource(appCt); }
            var ok = false;
            //the same hold /new takes, opened around the whole handler since the recompose sits deep inside CompactAsync
            using var held = warn?.Hold();
            try { ok = await HandleCompactAsync(s.Renderer, s.TurnCts!.Token, s.Ctx); }
            finally { lock (_ctsLock) { s.TurnCts?.Dispose(); s.TurnCts = null; } }
            s.Refresh();                                   //tokens moved either way, summarizing is a real turn
            if (ok)
            {
                //reset the display model so it matches the fresh conversation, otherwise the pre-compact transcript stays on screen and the screen and the file diverge
                RotateDisplay(s.Painter.Model, s.Renderer,
                    _lastCompactSummary is { Length: > 0 } summary
                        ? new SessionLeadItem(summary, After: null) { Memory = _lastCompactOutcome }
                        : null,
                    CompactedLine(s.Ctx), held,
                    _lastBankFailure is { } bankFailure ? new[] { bankFailure } : Array.Empty<string>());
                SaveSession(sessions, s.Painter.Model, convo);
                s.Painter.Repaint();
            }
            return false;
        }
        if (SlashGate.TryMatchCommand(trimmed, "/name", out var nameArg))
        {
            if (NameCommand(nameArg) is { } answer) s.Renderer.CommitSystem(answer);
            //the tab and the composer read the name now, past the folder latch
            if (sessionName is { } named) s.Painter.AltScreen.Name(TerminalTitle.Named(named));
            s.Refresh();
            return false;
        }
        if (trimmed == "/role" || trimmed.StartsWith("/role ", StringComparison.Ordinal))
        {
            var arg = trimmed.Length > 5 ? trimmed[5..].Trim() : "";
            var result = ApplyRoleSwitch(convo, switchRole, arg, roleName);
            //a bare /role says the running role and touches nothing, since its result carries no model to rebuild with
            if (result.Success && result.ModelName is null)
            {
                s.Renderer.CommitSystem(CommandText.RoleLine(roleName));
            }
            else if (result.Success)
            {
                roleName = result.RoleName;
                modelName = result.ModelName!;
                thinkingName = result.ThinkingName;   //null when the new role has no map, the chip hides
                roleCat = result.RoleCat;
                firstMessage = result.FirstMessage;
                //a role without a tint takes the accent, so a switch away from a tinted role drops its colour
                var tint = result.RoleTint ?? (Theme.Accent, null);
                s.Theme.SetRoleTint(roleName, tint.Dark, tint.Light);
                //future assistant blocks take the new role's tint, committed ones read the accent
                s.Painter.Model.Role = roleName;
                //the tab follows the role too, until the folder title is in
                s.Painter.AltScreen.Retitle(TerminalTitle.For(roleName));
                //rebuild the objects that captured roleName and modelName at construction, and reuse the painter and ticker so the purr never blinks across the swap
                s.Refresh();                              //this also retargets painter.Frame and RoleForTint
                //build the renderer through the launch builder, and let the tail closure read the convo field since /new and /compact reassign it
                var next = BuildSessionRenderer(s.Painter, s.Surface, s.Theme, roleName, s.Ticker, s.Gate,
                    s.OnUsage, reasoning, () => convo.Messages.Count > 0 ? convo.Messages[^1] : null,
                    s.Glyphs, loop.Trace);
                next.AdoptTranscriptState(s.Renderer);     //hand the new renderer what the scrollback last ended with
                next.OnDegraded = s.OnDegraded;            //the loop's degrade latch too (both or neither)
                next.OnContextChanged = s.OnContextChanged;   //the mid-turn footer refresh too (both or neither)
                s.Renderer = next;
                //re-point the chrome handle at the new renderer, a stale one would leave the prompters orphaned
                if (_chrome is not null) { _chrome.Painter = s.Painter; _chrome.Renderer = s.Renderer; }
                s.Renderer.CommitSystem(CommandText.RoleLine(roleName));
                //the switch seeded the new role's opening, painted through the rebuilt renderer in the new tint
                if (result.Opened)
                {
                    ReplayTranscript(convo.Messages, s.Renderer, u => s.Renderer.CommitUser(u.Split('\n')));
                    s.Renderer.EndTurn();
                }
            }
            else
            {
                s.Renderer.CommitSystem(result.Message ?? RoleSwitchFailed);
            }
            return false;
        }
        if (trimmed == "/model" || trimmed.StartsWith("/model ", StringComparison.Ordinal))
        {
            //one body for both entries, what the two sites used to duplicate lives in HandleModelCommand and what stays here only this site can do
            if (HandleModelCommand(trimmed, s.Renderer, s.Renderer.CommitSystem, out var notice))
            {
                s.Ctx?.RecordUsedTokens(null);   //the old figure describes a pairing that is gone
                //no renderer swap here, a model change never touches roleName or RoleForTint and Refresh alone repaints the mismatch chip
                s.Refresh();
                s.Renderer.CommitSystem(notice ?? CommandText.ModelLine(modelName));
                //check the context fit at the switch, an over-budget history should be compactable before the user sends a message
                MaybeWarnContextFit(s.Renderer);
            }
            return false;
        }
        if (trimmed == "/effort" || trimmed.StartsWith("/effort ", StringComparison.Ordinal))
        {
            var arg = trimmed.Length > 7 ? trimmed[7..].Trim() : "";
            if (HandleEffortCommand(arg) is { } line)
            {
                s.Renderer.CommitSystem(line);
                s.Refresh();   //the footer's level chip must reflect the switch at once
            }
            return false;
        }
        if (SlashGate.TryMatchCommand(trimmed, "/remember", out var rememberArg))
        {
            if (rememberArg.Length == 0)
            {
                s.Renderer.CommitSystem(RememberUsage);
                return false;
            }
            input = CommandText.RememberPrompt(rememberArg);   //sent through the normal turn below, as if the user had typed it
        }
        if (trimmed == "/init") input = InitPrompt.Text;   //sent through the normal turn below, as if the user had typed it

        RefreshServingBeforeTurn(s.Renderer);   //re-probe and check the context fit before the request goes out
        s.Refresh();   //the chip (or its clearing) must reach the footer before the turn streams

        //announce the notices here inside the turn so they read under the message, a refusal never reaches this body at all
        foreach (var notice in attach.Notices)
            s.Renderer.CommitSystem(notice.Text);

        lock (_ctsLock) { s.TurnCts = CancellationTokenSource.CreateLinkedTokenSource(appCt); }
        var turnCt = s.TurnCts.Token;
        bool turnCancelled;
        deafWatch?.Arm();
        try
        {
            await RunTurnGuardedAsync(loop, convo, input, s.Renderer, sessions, turnCt, s.Painter.Model, attach.Images, serverGone);
            turnCancelled = turnCt.IsCancellationRequested;
        }
        finally
        {
            deafWatch?.Disarm();
            lock (_ctsLock) { s.TurnCts?.Dispose(); s.TurnCts = null; }
        }
        //name the session's folder in the tab once a turn ran to its end, a cancelled turn is not that boundary
        if (!turnCancelled) s.Painter.AltScreen.SetFinalTitle(TerminalTitle.Project(cwd));
        s.Refresh();                                   //a turn may have committed or switched branches, and tokens moved
        MaybeOfferCompact(s.Renderer, s.Ctx);           //the 85% offer goes through the warning sink and becomes a ♯ row
        return false;
    }
}
