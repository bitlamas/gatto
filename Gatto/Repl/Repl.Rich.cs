using Gatto.Core.Client;
using Gatto.Core.Home;
using Gatto.Core.Loop;
using Gatto.Repl.Input;
using Gatto.Repl.Prompts;
using Gatto.Repl.Render;
using Gatto.Repl.Render.Chrome;
using Gatto.Terminal;

namespace Gatto.Repl;

//the rich face: its loop, the key decisions and the cancel sink
public sealed partial class Repl
{
    //rich loop, the chrome is always the buffer suffix owned by ChromePainter, each finished row goes to scrollback above it. internal so the harness can pass in a synthetic rich TermCaps, Detect() answers plain whenever stdout is redirected
    internal async Task<int> RunRichAsync(TermCaps caps, CancellationToken appCt)
    {
        //the pump starts here and only here, the composer needs it running continuously, and the fallback covers a directly built Repl
        var keys = pump ?? new InputPump(new KeyInputSource(new ConsoleKeySource()));
        keys.Start();
        ITermSurface surface = termSurface ?? new ConsoleSurface();
        //only a traced session counts its writes, so an untraced one paints through the surface it always had
        if (loop.Trace.Enabled) surface = new CountingSurface(surface);
        var theme = new Theme(caps, themeMode);
        if (roleTint is { } launchTint) theme.SetRoleTint(roleName, launchTint.Dark, launchTint.Light);   //told before anything paints
        var ctx = new CtxState { ContextWindow = configContext ?? await client.TryGetContextLengthAsync(appCt) };

        //every OnUsage feeds CtxState.RecordUsage and adds to the running up and down totals the status line draws
        long tokensUp = 0, tokensDown = 0;
        void OnUsageAccumulate(Usage u)
        {
            ctx.RecordUsage(u);
            //don't record the prompt tokens here, the same Usage already reaches _usageState through AgentLoop and the footer reads its figure from there
            tokensUp += u.PromptTokens;
            tokensDown += u.CompletionTokens;
        }

        //one lock for the session, the painter, ticker and renderer all serialize on it, and every live pixel goes through the painter under it
        var gate = new object();
        //the ChromePainter drives the ViewportCompositor and pins the chrome to the bottom, and StreamRenderer fills the transcript model
        var model = new TranscriptModel(roleName);
        var queue = new DispatchQueue(QueueCap);
        //the picker is retired, so the painter needs no queue reader and no pick sink
        var painter = new ChromePainter(surface, theme, gate, model, wheelLines,
            glyphs: _glyphs,
            terminalTitle: _terminalTitle,
            //a mouse composer gesture goes down the same channel as keys, this is the only caller of EnqueueComposerGesture and it runs under the gate
            onComposerSelect: (line, col, kind) => keys.EnqueueComposerGesture(new ComposerInput.Select(line, col, kind)))
            { RoleForTint = roleName, ChromeScrolls = mouseEnabled };   //the wheel takes the chrome away only where a wheel reaches gatto
        var scroll = painter.Scroll;
        var altScreen = painter.AltScreen;
        //the mouse-capture console mode, entered with the alt screen and restored on every path it goes away, null means no capture
        var inputMode = mouseEnabled
            ? new Gatto.Terminal.ConsoleInputMode(modeControl ?? new Gatto.Terminal.Win32ConsoleModeControl())
            : null;
        var ticker = new ChromeTicker(painter, gate, slotsReader);
        //the turn clock stops while a prompt is up, hooked at the pump where every prompt reads its key through PushFocus
        keys.SetModalWaitSink(open =>
        {
            if (open) { ticker.BeginUserWait(); deafWatch?.Arm(); }
            else { ticker.EndUserWait(); deafWatch?.Disarm(); }
        });
        var renderer = BuildSessionRenderer(painter, surface, theme, roleName, ticker, gate,
            OnUsageAccumulate, reasoning, () => convo.Messages.Count > 0 ? convo.Messages[^1] : null,
            _glyphs, loop.Trace);
        //scroll keys move the viewport and the pump consumes them even while a modal prompt holds focus, each paint runs under the gate
        keys.SetScrollSink(sk =>
        {
            lock (gate)
            {
                var w = surface.Width;
                var h = painter.ViewportRows();   //transcript height above the pinned chrome, so Home and PageUp reach the banner
                switch (sk)
                {
                    case ScrollKey.PageUp: scroll.PageUp(w, h); break;
                    case ScrollKey.PageDown: scroll.PageDown(w, h); break;
                    case ScrollKey.Top: scroll.Home(w, h); break;
                    case ScrollKey.Bottom: scroll.End(); break;
                }
                painter.Repaint();
            }
        });
        //the focus sink registers below, after s.Refresh is set, it must call s.Refresh() so the footer hint follows focus at once
        var history = new History(Path.Combine(GattoHome.Resolve(), "history.txt"));
        var editor = new LineEditor(keys.Composer, history, () => surface.Width, KnownCommands);
        //the count question is armed here, and its standing yes lives with the grants in the home, so a project folder can't bring one
        _attachManyGranted = () => permissions?.AttachMany == true;
        _grantAttachMany = () =>
        {
            try { permissions?.GrantAttachMany(); }
            catch (Exception) { }   //a failed write keeps the yes in memory for this session, and the question comes back next time
        };
        _confirmAttach = (count, _) =>
        {
            var spec = ConfirmAttach.Spec(count);
            var outcome = ConfirmAttachForTest is { } forTest
                ? forTest(spec)
                : new SelectPrompt(surface, theme, new ConsoleKeySource(), keys, _chrome, _glyphs).Show(spec);
            return ConfirmAttach.Interpret(outcome);
        };
        //the two-press controller behind Esc Esc and Ctrl+C Ctrl+C, declared ahead of RefreshStatus because it threads its live hint onto the top rule
        var chords = new ArmedChord();
        //a toggle-capable model's footer reads thinking on or off, so take the effective toggle-ness from GattoApp rather than the capability alone
        var status = new StatusInfo(cwd, modelName, roleName, ctx,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Wild: wild?.On == true,
            Thinking: thinkingName, ThinkingToggle: thinkingToggle, Cloud: cloud, SessionName: sessionName);
        var frame = new InputFrame(surface, theme, DisplayRole(), status, _glyphs, KnownCommands);
        var focusTaught = false;   //the one-shot teaser for the first collapsed reasoning block, set once it has fired

        //feeds the ctx estimate and rebuilds the status frame, and the painter is retargeted here so it never composes against a stale one
        void RefreshStatus()
        {
            //the one context figure, the same expression the compactor evaluates, and it must precede the estimate fallback
            ctx.RecordUsedTokens(ContextBudget.UsedTokens(convo, _usageState));
            ctx.RecordEstimate(ContextBudget.Estimate(convo.Messages), contextBudget);
            //the Alt+V hint appears while the clipboard holds an image, takes precedence over the focus hint, and the check is safe on every refresh
            var pasteHint = _clipboardImages?.Available() is not null
                and not Gatto.Terminal.ClipboardImageKind.None
                ? "Alt+V to paste the image on your clipboard"
                : null;
            var focusHint = pasteHint ?? ComputeFocusHint(painter.Focus.Focused is not null,
                painter.Model.Items.OfType<ReasoningItem>().Any(r => r.Collapsed), ref focusTaught, _glyphs);
            var usage = readUsage?.Invoke() ?? (null, null);
            status = status with
            {
                Model = modelName, Role = roleName, Branch = GitBranch.Read(cwd), SessionName = sessionName,
                TokensUp = tokensUp, TokensDown = tokensDown, Wild = wild?.On == true, Thinking = thinkingName,
                ThinkingToggle = thinkingToggle, FocusHint = focusHint,
                //an expired arm yields null at repaint so the chord hint vanishes on its own, and the paste hint wins over it
                ChordHint = LivePasteHint() ?? chords.Hint,
                Quota = usage.Quota, Cost = usage.Cost,
            };
            frame = new InputFrame(surface, theme, DisplayRole(), status, _glyphs, KnownCommands);
            //the retarget takes the gate too, the ticker repaints from its own thread while this runs on the dispatcher's
            lock (gate) { painter.Frame = frame; painter.RoleForTint = roleName; }
        }

        //boxed so every dispatch path sees the current renderer and frame, a loop holding a stale local would paint the old role's tint
        var s = new RichState
        {
            Surface = surface, Theme = theme, Glyphs = _glyphs, Renderer = renderer, Frame = frame,
            OnUsage = OnUsageAccumulate, Gate = gate, Painter = painter, Ticker = ticker, Ctx = ctx,
        };
        //bound through s.Renderer so a /role swap can't orphan it, the sink is a holder because the tool is built before any renderer exists
        if (subagentProgressSink is { Length: > 0 })
            subagentProgressSink[0] = m => s.Renderer.OnSubagentProgress(m);
        s.Refresh = () => { RefreshStatus(); s.Frame = frame; };
        s.Refresh();
        //stashed where this local and s can both reach it, idempotent and null when mouse is off
        s.RestoreMouseMode = () => inputMode?.Restore();

        //global like scroll, registered once after s.Refresh so the sink can call it, and the painter owns focus so a /role swap keeps it
        keys.SetFocusSink(fk =>
        {
            lock (gate)
            {
                var w = surface.Width;
                var h = painter.ViewportRows();
                switch (fk)
                {
                    case FocusKey.Prev: painter.Focus.Prev(w, h); break;
                    case FocusKey.Next: painter.Focus.Next(w, h); break;
                    case FocusKey.Toggle: painter.Focus.ToggleFocused(w, h); break;
                }
                s.Refresh();          //rebuild the status and frame so the focus hint follows at once, the gate is re-entrant
                painter.Repaint();    //the refresh above retargets the frame without painting, so paint here
            }
        });

        //the Shift+Tab key runs the same HandleWild("") the slash command does so the two can't drift, and a keystroke never writes the grants file
        keys.SetWildSink(() =>
        {
            lock (gate)
            {
                //one CommitSystem call per line, a newline inside one call is stripped and two rows mash together
                foreach (var line in HandleWild("").Split('\n'))
                    s.Renderer.CommitSystem(line);
                s.Refresh();          //the footer's wild chip must reflect the flip
                painter.Repaint();
            }
        });

        //the one row a deaf reader prints, committed under the gate like every other row a background thread writes.
        deafWatch?.SetDeafSink(row =>
        {
            lock (gate)
            {
                s.Renderer.CommitSystem(row);
                painter.Repaint();
            }
        });

        //a failed copy leaves the selection alone, Win32Clipboard.TrySet never throws. the selection needs no wiring, the compositor reads the painter's SelectionController each paint and clears itself on reflow
        var clip = clipboard ?? new Gatto.Terminal.Win32Clipboard();
        void Copy()
        {
            lock (gate)
            {
                //pass the painted chrome rows every time so a composer or queue drag copies too, harmless when the live span is the transcript
                var text = painter.Selection.CopyText(surface.Width, theme, painter.ChromeRowInfo, _glyphs);
                if (text is { Length: > 0 }) clip.TrySet(text);
            }
        }

        //mouse events are global and work under a modal, only a Press refreshes since each refresh reads the git branch, and every event repaints
        if (mouseEnabled)
            keys.SetMouseSink(me =>
            {
                lock (gate)
                {
                    painter.Mouse.Handle(me, surface.Width, painter.ViewportRows());
                    if (me.Kind == MouseKind.Press) s.Refresh();
                    painter.Repaint();
                }
                //the HasSelection flag is the one predicate that covers both the transcript and the chrome domains, don't test them separately
                if (copyOnSelect && me.Kind == MouseKind.Release && painter.Selection.HasSelection) Copy();
            });

        //registered unconditionally, Esc needs this path in a mouse: false session, and the copy arms are no-ops without a mouse
        keys.SetCancelSink(BuildCancelSink(painter, gate, () => s.TurnCts, Copy,
            () => s.ComposerEmpty?.Invoke() ?? true));
        //an open info panel looks at every key first, the panel's own keys never reach the transcript's scroll or the composer
        keys.SetOverlaySink(BuildInfoPanelSink(painter, gate));
        //off the bottom a key returns the view first, and a key meant for a prompt nobody can see is swallowed
        if (mouseEnabled)
            keys.SetAwaySink(k =>
            {
                lock (gate)
                {
                    var copy = k.Key == ConsoleKey.C && k.Modifiers.HasFlag(ConsoleModifiers.Control) && !k.Modifiers.HasFlag(ConsoleModifiers.Alt);
                    if (DecideAway(painter.Away, painter.PromptHidden(), copy && painter.Selection.HasSelection) is not { } d) return KeyDisposition.PassThrough;
                    painter.Scroll.End();
                    painter.Repaint();
                    return d;
                }
            });

        //use the live read () => s.TurnCts here and in the cancel sink, the field is replaced at every turn boundary
        if (turnAbort is not null) turnAbort.Current = () => s.TurnCts;


        //a prompt draws as chrome above a dimmed composer from here on, the teardown nulls both handles again
        if (_chrome is not null) { _chrome.Painter = painter; _chrome.Renderer = renderer; }

        //exit hooks register before either entry so a crash there still restores, and from here every visible row is drawn by the compositor
        inputMode?.RegisterExitHooks();
        inputMode?.Enter();
        altScreen.RegisterExitHooks();
        altScreen.QuietWheel = !mouseEnabled;   //no mouse capture, so the wheel must not arrive as the composer's arrow keys
        //the tab title is composed from the same roleName the banner prints, so the two cannot drift
        altScreen.Enter(TerminalTitle.For(roleName));
        if (sessionName is { } resumedName) altScreen.Name(TerminalTitle.Named(resumedName));   //a resumed session opens with its own name on the tab

        //the banner is the transcript's first item
        model.Append(BannerItem(theme, roleName, roleCat, modelName, versionLine, _glyphs));

        //the memory-truncation notice, a system row right after the banner and above any resumed transcript
        if (memoryWarning is not null) renderer.CommitSystem(memoryWarning);
        if (cloudNotice is not null) renderer.CommitSystem(cloudNotice);
        if (servedNotice is not null) renderer.CommitSystem(servedNotice, transient: true);   //what this launch found serving, which the next resume says for itself
        //the list as it stands now, a warning raised between here and the route below still reaches stderr alone
        foreach (var w in LaunchWarningsNow()) renderer.CommitSystem(w);
        if (sandboxNotice is not null) renderer.CommitSystem(sandboxNotice, transient: true);   //this launch's sandbox state, never saved, so a resume shows only its own
        if (wildNotice is not null) renderer.CommitSystem(wildNotice, transient: true);   //this launch's wild state, never saved, so a resume shows only its own

        //fire on resumedFrom alone, a compact-then-quit session has no chat turns even though its context was loaded
        if (resumedFrom is not null)
        {
            //re-seed the live convo with the rebuilt instances so the model's anchors point at the same messages the next save writes
            if (resumedPath is { } priorPath)
            {
                var (rebuiltConvo, rebuiltModel) =
                    TranscriptStore.Rebuild(File.ReadLines(priorPath), theme, roleName, _glyphs);
                var sysCount = convo.Count > 0 && convo.Messages[0].Role == "system" ? 1 : 0;
                convo.TruncateTo(sysCount);   //drop the chat-only instances GattoApp loaded and keep the fresh system message
                //keep the fresh system message, exclude the rebuilt record, and don't simplify BuildLines to a reference check, a system-role event still leads
                convo.Load(rebuiltConvo.Messages.Where(m => m.Role != "system"));   //the same instances the model's anchors point at
                foreach (var item in rebuiltModel.Items) model.Append(item);
            }
            else   //resumedFrom without a resumedPath, so replay the chat records only
            {
                ReplayTranscript(convo.Messages, renderer, u => renderer.CommitUser(u.Split('\n')), renderer.CommitBackground);
                renderer.EndTurn();
            }
            _usageState.SeedFrom(convo.Messages);   //the last reply's cut, so the first request keeps the prefix the server last read
            //the divider goes after the restored transcript so it sits just above the composer, and being transient it never serializes
            model.Append(new CommandEchoItem(new[] { theme.Paint($"— resumed from {resumedFrom} —", Theme.Dim) }, null) { Transient = true });
            if (resumeLine is not null)
                model.Append(new CommandEchoItem(Array.Empty<string>(), null) { Transient = true, DimText = resumeLine });
        }
        else if (SeedFirstMessage(convo, firstMessage))
        {
            ReplayTranscript(convo.Messages, renderer, u => renderer.CommitUser(u.Split('\n')), renderer.CommitBackground);
            renderer.EndTurn();
        }
        painter.Repaint();   //first paint, so the banner and empty composer are up before the loop starts

        //on the mouse-on path this handler is dormant for Ctrl+C and only catches Ctrl+Break, so don't delete it, the plain path still needs it
        var lastAtRestCtrlC = 0L;
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            bool composerEmpty;
            lock (s.Gate) { composerEmpty = s.ComposerEmpty?.Invoke() ?? true; }   //read before _ctsLock so the two locks never nest
            lock (_ctsLock)
            {
                var now = Environment.TickCount64;
                switch (DecideCtrlC(s.TurnCts is not null, composerEmpty, now, lastAtRestCtrlC))
                {
                    case CtrlCAction.AbortTurn:
                        try { s.TurnCts!.Cancel(); } catch (ObjectDisposedException) { }
                        break;
                    case CtrlCAction.Quit:
                        s.CancelLoop?.Invoke();
                        break;
                    case CtrlCAction.None:
                        break;
                    default:   //the HintExit rung, arm the 2s window and show the transient rule hint
                        lastAtRestCtrlC = now;                    //keep this, the next press reads it to decide a quit
                        chords.Fire(Chord.Quit, now);            //armed only for its hint string, the quit path is DecideCtrlC's
                        s.OnHintChanged?.Invoke();               //repaint the rule and schedule the expiry, the same machinery the mouse path uses
                        break;
                }
            }
        };

        return await RunLinearLoopAsync(s, editor, queue, chords, clip, appCt);
    }

    //a turn in flight aborts, composer text makes it a no-op, at rest a single Ctrl+C only hints and a second within CtrlCDoubleTapMs quits
    internal const long CtrlCDoubleTapMs = 2000;
    internal enum CtrlCAction { None, AbortTurn, HintExit, Quit }
    internal static CtrlCAction DecideCtrlC(bool turnInFlight, bool composerEmpty, long nowMs, long lastAtRestMs) =>
        turnInFlight ? CtrlCAction.AbortTurn
        : !composerEmpty ? CtrlCAction.None
        : lastAtRestMs > 0 && nowMs - lastAtRestMs <= CtrlCDoubleTapMs ? CtrlCAction.Quit
        : CtrlCAction.HintExit;

    //off the bottom a key returns the view and goes on, a key a hidden prompt would take is swallowed, and a copy leaves the view alone
    internal static KeyDisposition? DecideAway(bool away, bool promptHidden, bool copyWithSelection) =>
        !away || copyWithSelection ? null : promptHidden ? KeyDisposition.Consumed : KeyDisposition.PassThrough;

    //the Esc rung clears a live selection, a non-empty composer passes through ahead of a turn abort. the Ctrl+C rung copies the selection and keeps it
    internal enum CancelAction { PassThrough, ClearSelection, AbortTurn, Copy, CloseInfo }
    internal static CancelAction DecideCancel(ConsoleKey key, bool hasSelection, bool turnInFlight,
        bool composerEmpty, bool infoUp = false) =>
        key == ConsoleKey.Escape
            //an open info panel takes the first Esc, so a second one is needed to stop the turn
            ? (infoUp ? CancelAction.CloseInfo
               : hasSelection ? CancelAction.ClearSelection
               : !composerEmpty ? CancelAction.PassThrough   //a composer with text passes through so the editor's Esc Esc can clear it, ahead of a turn abort
               : turnInFlight ? CancelAction.AbortTurn
               : CancelAction.PassThrough)                    //the ladder ends here, an empty composer's Esc drops nothing
            //the Ctrl+C rung copies a live selection and leaves it, else aborts a turn in flight, else passes through to the composer's quit chord
            : (hasSelection ? CancelAction.Copy
               : turnInFlight ? CancelAction.AbortTurn
               : CancelAction.PassThrough);

    //turnCts and composerEmpty are read live at each invocation so a /role swap or a keystroke sees the current state, registered unconditionally
    internal static Func<ConsoleKeyInfo, KeyDisposition> BuildCancelSink(
        ChromePainter painter,
        object gate, Func<CancellationTokenSource?> turnCts, Action copy, Func<bool> composerEmpty) => k =>
    {
        bool hasSel, composerBlank, infoUp;
        lock (gate) { hasSel = painter.Selection.HasSelection; composerBlank = composerEmpty(); infoUp = painter.State.PanelInfo; }
        switch (DecideCancel(k.Key, hasSel, turnCts() is not null, composerBlank, infoUp))
        {
            case CancelAction.CloseInfo:
                painter.CloseInfoPanel();
                return KeyDisposition.Consumed;
            case CancelAction.ClearSelection:
                lock (gate) { painter.Selection.Clear(); painter.Repaint(); }
                return KeyDisposition.Consumed;
            case CancelAction.AbortTurn:
                try { turnCts()?.Cancel(); } catch (ObjectDisposedException) { }
                return KeyDisposition.Consumed;
            case CancelAction.Copy:
                copy();
                return KeyDisposition.Consumed;
            default:
                return KeyDisposition.PassThrough;
        }
    };

    //clears the transcript and chrome selection under the same gate every mouse write takes, and does nothing once teardown has run
    internal static Action BuildComposerSelectionSetHook(ChromePainter painter, object gate, Func<bool> stopped) => () =>
    {
        lock (gate) { if (!stopped()) { painter.Selection.Clear(); painter.Repaint(); } }
    };

    //the insert alone writes a buffer nothing paints while the composer task is parked, so redraw, and do nothing after teardown
    internal static Action<string> BuildRestoreComposer(
        object gate, LineEditor editor, Action<EditorView> redraw, Func<bool> stopped) => text =>
    {
        lock (gate)
        {
            if (stopped()) return;
            editor.InsertAtCursor(text);
            redraw(editor.Snapshot());
        }
    };
}
