using Gatto.Core;
using Gatto.Core.Client;
using Gatto.Core.Loop;
using Gatto.Core.Memory;
using Gatto.Core.Session;
using Gatto.Repl.Commands;
using Gatto.Repl.Input;
using Gatto.Repl.Prompts;
using Gatto.Repl.Render;
using Gatto.Repl.Render.Chrome;
using Gatto.Terminal;

namespace Gatto.Repl;

//the slash command handlers, Repl members because each reads or changes the session
public sealed partial class Repl
{
    //the /context rows at a width, built from what the last request sent, so the composer thread may build them mid-turn
    internal async Task<IReadOnlyList<ContextReport.Row>?> ContextRowsAsync(int width, Theme theme, CancellationToken ct) =>
        await ContextFiguresAsync(ct) is { } figures ? ContextReport.Rows(figures, width, theme, _glyphs) : null;

    //the figures alone, width-free, for a screen that is drawn again at each width
    private async Task<ContextFigures?> ContextFiguresAsync(CancellationToken ct) =>
        contextInputs is null ? null : await ContextCount.BuildAsync(contextInputs(_usageState), tokenCounter, ct) with { Folder = cwd };

    internal const string ContextUnavailable = "context figures are unavailable in this session";

    //the keys of an open info panel: the page keys and the bare arrows scroll it, Enter closes it, any other key closes it and goes on to the composer
    internal static Func<ConsoleKeyInfo, KeyDisposition> BuildInfoPanelSink(ChromePainter painter, object gate) => k =>
    {
        lock (gate) { if (!painter.State.PanelInfo) return KeyDisposition.PassThrough; }
        switch (k.Key)
        {
            case ConsoleKey.PageUp: painter.ScrollPanel(InfoPanelPage); return KeyDisposition.Consumed;
            case ConsoleKey.PageDown: painter.ScrollPanel(-InfoPanelPage); return KeyDisposition.Consumed;
            case ConsoleKey.UpArrow when k.Modifiers == 0: painter.ScrollPanel(1); return KeyDisposition.Consumed;
            case ConsoleKey.DownArrow when k.Modifiers == 0: painter.ScrollPanel(-1); return KeyDisposition.Consumed;
            case ConsoleKey.Enter: painter.CloseInfoPanel(); return KeyDisposition.Consumed;   //consumed, so an empty composer is never submitted
        }
        //a copy chord is the cancel sink's, it already looked and passed it on
        if (k.Key == ConsoleKey.C && k.Modifiers.HasFlag(ConsoleModifiers.Control)) return KeyDisposition.PassThrough;
        painter.CloseInfoPanel();
        return KeyDisposition.PassThrough;
    };

    //rows a PgUp or PgDn moves an info panel by
    private const int InfoPanelPage = 10;

    //opens the answer once its rows are ready, and never after teardown. a fault leaves the composer as it was, the command was read-only
    private static async Task OpenInfoPanelAsync(Task<Func<int, IReadOnlyList<string>>> answer, RichState s, Func<bool> stopped)
    {
        try
        {
            var rows = await answer;
            lock (s.Gate) { if (!stopped()) s.Painter.SetInfoPanel((w, _) => rows(w)); }
        }
        catch (Exception) { }
    }

    //counted once from the last request, each call bounded by the probes' deadline, then drawn again at every width
    private async Task<Func<int, IReadOnlyList<string>>> ContextAnswerAsync(RichState s)
    {
        if (contextInputs is null) return _ => ["  " + ContextUnavailable];
        var figures = await ContextCount.BuildAsync(contextInputs(_usageState), tokenCounter, CancellationToken.None) with { Folder = cwd };
        return w => [.. ContextReport.Rows(figures, w, s.Theme, _glyphs).Select(r => r.Rendered)];
    }

    internal const string NoBackgroundTasks = "no background tasks";

    //one line per task, running and ended-but-undelivered alike, and no pid, since under the sandbox the child gatto holds is the wrapper
    private IReadOnlyList<string> TasksLines() =>
        [.. (_tasks?.Snapshot ?? []).Select(t => $"{t.Task.Number}  "
            + (t.Gloss is null ? $"running {Gatto.Core.ElapsedText.Of(t.Elapsed)}" : $"ended {_glyphs.Dot} {t.Gloss}, waiting for the model")
            + "  " + TermText.Sanitize(t.Task.Command.Split('\n')[0].Trim()))];

    //the kill acts at once on the composer thread, during a turn too, and reaches no model. a successful kill says nothing, the queue row shows the end at once
    private bool KillNow(string text, RichState s)
    {
        if (!SlashGate.TryMatchCommand(text.Trim(), "/tasks", out var arg)) return false;
        var words = arg.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0 || words[0] != "kill") return false;
        var which = words.Length > 1 ? words[1] : "";
        if (!(int.TryParse(which, out var number) && _tasks?.Kill(number, Gatto.Core.Tools.BackgroundTasks.ByTasks) == true))
            s.Renderer.CommitSystemBetweenBlocks($"no background task {which}", transient: true);
        return true;
    }

    //the watch acts at once on the composer thread, at rest and during a turn, reaches no model and never enters the queue. its line waits for a streaming block to close
    private bool WatchNow(string text, RichState s, DispatchQueue queue)
    {
        if (!SlashGate.TryMatchCommand(text.Trim(), "/watch", out var arg)) return false;
        arg = arg.Trim();
        string line;
        if (arg == "off")
        {
            line = _watch is null ? "not watching" : "watch off";
            Disarm(s, queue);
        }
        else if (arg.Length == 0 && _watch is { } armed)
        {
            if (armed.Paused) armed.Lift();
            line = $"watching {Shown(armed.Folder)}";
        }
        else
        {
            var folder = arg.Length == 0
                ? Path.Combine(cwd, ".gatto", "mailbox", MailboxName(Gatto.Core.Models.ServedModelName.Display(modelName)))
                : Path.Combine(cwd, arg);
            try
            {
                //a failed arm leaves the watch that was armed as it was
                var w = Watch.Arm(folder, _watchSink!, row => s.Renderer.CommitSystemBeforeNextReply(row));
                Disarm(s, queue);
                lock (s.Gate) _watch = w;
                w.Start();
                line = $"watching {Shown(w.Folder)}";
            }
            catch (Exception ex) { line = $"watch failed: {ex.Message}"; }
        }
        s.Renderer.CommitSystemBetweenBlocks(line);
        return true;
    }

    //the folder as the screen shows it, the profile read as ~, while the watch and every letter keep the absolute path
    private static string Shown(string folder)
    {
        string profile;
        try { profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); }
        catch (Exception) { return folder; }
        return HomePath.AbbreviateProfile(folder, profile);
    }

    //the watch goes, and the letters it queued with it, which stay in the folder for the next arm
    private void Disarm(RichState s, DispatchQueue queue)
    {
        lock (s.Gate)
        {
            _watch?.Dispose();
            _watch = null;
            queue.DropWatch();
            s.Painter.State.QueueTexts = queue.Texts();
        }
    }

    //a model id as a folder name, each character a path cannot hold, a cloud id's slash or colon, turned into a hyphen
    internal static string MailboxName(string modelId) =>
        string.Concat(modelId.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c));

    //the answers that are text already, /help, /tools and the list view of /permissions. /tasks opens the background panel before the bypass is asked
    private Func<int, IReadOnlyList<string>> ReadOnlyAnswer(string trimmed, RichState s)
    {
        if (trimmed == "/help")
        {
            var known = KnownCommands();
            var help = (SlashCommands.RenderRich(s.Theme, known) + "\n\n" + Shortcuts.RenderRich(s.Theme, glyphs: _glyphs, known)).Split('\n');
            return _ => [.. help.Select(r => "  " + r)];
        }
        if (trimmed == "/tools")
            return w => [.. (listTools is null ? ToolsUnavailable
                : CommandText.RenderToolsListFor(listTools(), PolicyRows(), new Theme(TermCaps.Plain), Math.Max(20, w - 4), _glyphs))
                .Split('\n').Select(r => "  " + TermText.Sanitize(r))];
        SlashGate.TryMatchCommand(trimmed, "/permissions", out var arg);
        var grants = HandlePermissions(arg).Split('\n');
        return _ => [.. grants.Select(r => "  " + TermText.Sanitize(r))];
    }

    //built-ins first, then the registered rows in commit order. every site that reads the command table reads this, so none can disagree
    internal IReadOnlyList<SlashCommands.Cmd> KnownCommands() =>
        [.. SlashCommands.All, .. _commands.Rows.Select(r => new SlashCommands.Cmd(r.Name, r.Args, r.Summary, Source: r.SourceFile))];

    //a registered handler's text, one row per line and each line sanitised, since it is an extension's code. a throw becomes its message alone
    private async Task<IReadOnlyList<string>> RunRegisteredAsync(Gatto.Core.Commands.CommandRow row, string arg, CancellationToken appCt)
    {
        string text;
        try { text = await row.Handler(arg, appCt) ?? ""; }
        catch (Exception ex) { text = ex.Message; }
        return [.. text.Split('\n').Select(TermText.Sanitize)];
    }

    //the registered row a trimmed line names, with its argument text, or null when the first token is not a registered name
    private (Gatto.Core.Commands.CommandRow Row, string Arg)? MatchRegistered(string trimmed)
    {
        if (_commands.TryGet(SlashGate.FirstToken(trimmed), out var row) && SlashGate.TryMatchCommand(trimmed, row.Name, out var arg))
            return (row, arg);
        return null;
    }

    //the usage line a bare /remember prints, no turn is dispatched
    internal const string RememberUsage =
        "usage: /remember <text> — bank a note to project memory, in the model's own words";

    //the last assistant message with text and the nearest user text before it, null when the model has not answered in text
    public static (string User, string Assistant)? LastTextExchange(Conversation convo)
    {
        var msgs = convo.Messages;
        var aIdx = -1;
        for (var i = msgs.Count - 1; i >= 0; i--)
            if (msgs[i].Role == "assistant" && msgs[i].Content is { Length: > 0 }) { aIdx = i; break; }
        if (aIdx < 0) return null;
        for (var i = aIdx - 1; i >= 0; i--)
            if (msgs[i].Role == "user" && msgs[i].Content is { Length: > 0 } u)
                //a background end is not the user's words, so its head line names it and its output stays out of the summary
                return (msgs[i].Background is null ? u : Gatto.Core.Tools.BackgroundTasks.HeadOf(u), msgs[aIdx].Content!);
        return null;
    }

    //the shared /compact body, and a null summary leaves the conversation and its session file untouched
    public static async Task<Conversation?> CompactAsync(
        Compactor compactor, Conversation convo, SessionStore? sessions,
        Func<ComposedSystem> recomposeSystem, ITurnObserver observer, CancellationToken ct,
        int? contextBudget = null, Action<string>? onSummary = null,
        Action<MemoryPiggyback.Extraction>? bankCandidates = null, string? doNotRepeat = null)
    {
        var oldPath = sessions?.CurrentPath;          //read before StartSuccessor, which clears the path
        var lastExchange = LastTextExchange(convo);
        var rawSummary = await compactor.SummarizeAsync(
            convo, observer, ct, windowTokens: contextBudget, doNotRepeat: doNotRepeat);
        if (rawSummary is null)
        {
            observer.OnWarning("compact failed — session unchanged");
            return null;                              //null tells the caller to abort, the old session stays live
        }
        //project memory banks here because the user chose this boundary, and the stripped text is what the rest of the method sees
        var extraction = MemoryPiggyback.Extract(rawSummary);
        var summary = extraction.StrippedSummary;
        if (extraction.Candidates.Count > 0 || extraction.Dropped > 0)
            bankCandidates?.Invoke(extraction);
        //the session_summary hook fires here with no extra model call, before the rotation so the caller emits on the current conversation
        onSummary?.Invoke(summary);
        //the compact block stays final, a later block would be swallowed into the resume graft --continue builds
        var composed = recomposeSystem();             //a fresh read of the context files on disk, so the prompt matches what is there now
        var systemText = composed.Text + "\n\n" + Compactor.BuildContext(summary, oldPath ?? "(unsaved session)", lastExchange);
        sessions?.StartSuccessor();                   //later saves open a new file of the same session, so the old transcript is left alone
        var fresh = new Conversation(systemText, baseline: composed.Baseline);
        sessions?.Save(fresh);
        return fresh;
    }

    //runs CompactAsync and, on success, adopts the fresh conversation and lets the 85% offer fire again
    internal async Task<bool> HandleCompactAsync(ITurnObserver renderer, CancellationToken ct, CtxState? ctx = null)
    {
        _lastCompactBanked = (0, 0);   //reset per compact, so a later confirmation cannot repeat an earlier count
        _lastCompactBeforePercent = ctx?.Percent;   //the percent the footer showed, read before anything here mutates
        _lastCompactImagesDropped = convo.Messages.Sum(m => m.Images?.Count ?? 0);   //counted before the rebuild, when the images are still in history
        _lastCompactOutcome = null;    //reset per compact, like the counts above
        _lastBankFailure = null;       //reset per compact, so a failure from an earlier one cannot leak
        _lastSuppressionUnfed = 0;     //how many index lines were dropped for budget, set below and reset per compact

        //the index is composed from disk because the cached system message misses facts written mid-session, and Compose is total so no try/catch is needed
        string? doNotRepeat = null;
        if (piggyback is not null)
        {
            var index = piggyback.ComposeIndex();
            doNotRepeat = Compactor.SuppressionAddendum(index);
            _lastSuppressionUnfed = index.TruncatedLines;
        }
        //build the Compactor here, /role can change modelName and a captured one would keep summarizing with the stale launch model
        string? capturedSummary = null;
        Conversation? fresh;
        SetReasoningPassthrough(renderer, true);   //the dimmed summary streams as reasoning, so the preview filter must pass it through
        try
        {
            fresh = await CompactAsync(new Compactor(client, modelName, loop.RequestShape, loop.Trace), convo, sessions, recomposeSystem, renderer, ct,
                contextBudget: contextBudget, onSummary: s => capturedSummary = s,
                doNotRepeat: doNotRepeat,
                bankCandidates: ex =>
                {
                    var banked = BankMemory(ex.Candidates, renderer).Banked;
                    _lastCompactBanked = (banked, ex.Dropped);
                    //recorded even when nothing banked, a zero with heading_found:true is the case that matters
                    _lastCompactOutcome = new MemoryBankOutcome(
                        ex.HeadingFound, ex.Candidates.Count, banked, ex.Dropped);
                });
        }
        finally { SetReasoningPassthrough(renderer, false); }
        if (fresh is null) return false;
        _lastCompactSummary = capturedSummary;   //the rich loop shows it as the new session's lead
        //the session_summary hook fires here with the summary just produced and no extra model call, before convo rotates
        if (capturedSummary is not null && hooks is not null)
            await hooks.EmitSessionSummaryAsync(new HookPayload(
                Summary: new SummaryInfo(capturedSummary, DateTimeOffset.Now, modelName, GistOfConversation())));
        convo = fresh;
        _offered = false;   //a fresh session may cross 85% again
        loop.ResetAutoCompact();   //the user's own compaction worked, so the automatic one may run again
        _usageState.Clear();   //the old reading describes a conversation that no longer exists
        ctx?.RecordUsedTokens(null);   //clears the context figure too, there is none on the plain path
        return true;
    }

    internal const string WildUsage =
        "usage: /wild [always|never] — toggle wild mode; always/never also persists for this project";

    //shared /wild dispatch, the persistence variants return two lines and the rich caller commits one row per line. every string is a literal but the forbidding file's path, which goes through Sanitize, and Shift+Tab comes through here too
    private string HandleWild(string arg)
    {
        if (wild is null) return "wild mode is unavailable in this session";
        if (wild.ForbiddenBy is { } by && (arg == "always" || (arg == "" && !wild.On)))
            return $"wild mode stays off: {TermText.Sanitize(by)} sets \"wild\": false for this project";
        switch (arg)
        {
            case "":
                wild.On = !wild.On;
                return CommandText.WildLine(wild.On, _glyphs);
            case "always":
                wild.On = true;
                persistWild?.Invoke(true);
                return CommandText.WildLine(true, _glyphs) + "\npersisted for this project in your gatto home — /wild never removes it";
            case "never":
                wild.On = false;
                var hadGrant = wildPersisted?.Invoke() == true;   //read before the persist below clears the grant
                persistWild?.Invoke(false);
                //the next-launch row only where gatto.json's wild would really start the next launch, a project veto keeps it off
                return (wildFromHome && wild.ForbiddenBy is null
                        ? "wild mode off for this session, gatto.json sets \"wild\": true, so it is on again next launch"
                        : CommandText.WildLine(false, _glyphs))
                    + (hadGrant ? "\npersistence removed from your gatto home" : "");
            default:
                return WildUsage;
        }
    }

    //the mode is for the process: /sandbox says its state, on turns it on, and off names what keeps it on
    internal string HandleSandbox(string arg)
    {
        if (sandbox is null || !OperatingSystem.IsWindows()) return "sandbox off";
        switch (arg)
        {
            case "":
                return TermText.Sanitize(sandbox.Line);
            case "on":
                try { sandbox.TurnOn("/sandbox on"); }
                catch (GattoConfigException ex) { return TermText.Sanitize(ex.Message); }
                return TermText.Sanitize(sandbox.Line);
            case "off":
                return TermText.Sanitize(sandbox.On ? sandbox.OffRefusal : sandbox.Line);
            default:
                return SandboxUsage;
        }
    }

    internal const string SandboxUsage = "usage: /sandbox [on]";

    //the exact inverse of a bare /wild that turned wild on, for this session only
    private string HandleSafe()
    {
        if (wild is null) return "wild mode is unavailable in this session";
        if (!wild.On) return "wild mode is off";
        wild.On = false;
        return CommandText.WildLine(false, _glyphs);
    }

    internal const string ToolsUnavailable = "the tool list is unavailable in this session";

    //the policy rows both /tools sites read, one method so the two renders cannot differ on what they were handed
    private IReadOnlyList<(string Extension, string Line)> PolicyRows() =>
        listPolicy?.Invoke() ?? Array.Empty<(string, string)>();

    //the plain path never paints, so a plain theme is the right one to measure with
    private string RenderToolsList()
    {
        if (listTools is null) return ToolsUnavailable;
        var width = 80;
        try { if (Console.WindowWidth > 20) width = Console.WindowWidth - 4; } catch { } //no console, so the 80 default stands
        return CommandText.RenderToolsListFor(listTools(), PolicyRows(), new Theme(TermCaps.Plain), width, _glyphs);
    }

    internal const string PermissionsUsage =
        "usage: /permissions [all | revoke <n>] — list or revoke standing grants";

    internal const string PermissionsUnavailable = "permissions are unavailable in this session";

    //shared /permissions dispatch, a bad revoke argument is a friendly error that changes nothing
    private string HandlePermissions(string arg)
    {
        if (permissions is null) return PermissionsUnavailable;
        if (arg.Length == 0) return CommandText.RenderPermissionsList(permissions, glyphs: _glyphs, wildOn: wild?.On);
        if (string.Equals(arg, "all", StringComparison.Ordinal))
            return CommandText.RenderPermissionsList(permissions, all: true, glyphs: _glyphs, wildOn: wild?.On);

        var parts = arg.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !string.Equals(parts[0], "revoke", StringComparison.Ordinal))
            return PermissionsUsage;

        var numberText = parts[1].Trim();
        if (!int.TryParse(numberText, out var n))
            //int.TryParse fails the same way for overflow and for garbage, so a numeric value that does not fit is answered as an out-of-range index
            return LooksNumeric(numberText)
                ? $"no grant numbered {numberText} — /permissions lists the current grants"
                : $"'{numberText}' is not a number — {PermissionsUsage}";

        var removed = permissions.RevokeAt(n - 1);   //the listing numbers grants from 1, the store counts from 0
        if (removed is null)
            return $"no grant numbered {n} — /permissions lists the current grants";

        return "revoked: " + CommandText.FormatKindAndText(removed.Kind, removed.Text);
    }

    //an optionally signed run of ASCII digits, which is a different question from whether the value fits in an int
    private static bool LooksNumeric(string s)
    {
        var i = s.Length > 0 && (s[0] == '+' || s[0] == '-') ? 1 : 0;
        if (i >= s.Length) return false;
        for (; i < s.Length; i++)
            if (!char.IsAsciiDigit(s[i])) return false;
        return true;
    }

    //the chars÷4 percent of the live conversation, the plain path's stand-in for the footer. it is only ever compared with itself
    private int? EstimatedPercent() =>
        contextBudget is int b && b > 0
            ? (int)Math.Round(100.0 * ContextBudget.Estimate(convo.Messages) / b)
            : null;

    //the /compact confirmation line, silent about zero banked and always reporting a non-zero drop count
    internal string CompactedLine(CtxState? ctx = null)
    {
        //both figures come from the footer's own CtxState, sampled before and after, so the number told is the number shown
        var after = ctx?.Percent ?? EstimatedPercent();
        var line = (_lastCompactBeforePercent, after) switch
        {
            (int before, int a) => $"compacted {_glyphs.Dot} ctx {before}% {_glyphs.Right} {a}%",
            (_, int a) => $"compacted {_glyphs.Dot} ctx {a}%",
            _ => "compacted",
        };
        //a compacted history is text, so report how many images went with it
        if (_lastCompactImagesDropped > 0)
            line += $" {_glyphs.Dot} {_lastCompactImagesDropped} image{(_lastCompactImagesDropped == 1 ? "" : "s")} dropped";
        var (appended, dropped) = _lastCompactBanked;
        if (appended > 0) line += $" {_glyphs.Dot} {appended} memory candidates banked";
        if (dropped > 0)
            line += appended > 0
                ? $", {dropped} dropped over cap"
                : $" {_glyphs.Dot} {dropped} memory candidates dropped over cap";
        //a partial suppression is never silent, the line says how many facts were not fed
        if (_lastSuppressionUnfed > 0)
            line += $" {_glyphs.Dot} {Plural.Of(_lastSuppressionUnfed, "fact")} over budget not fed to the summarizer";
        return line;
    }

    //a failed bank warns and reports zero instead of taking down the compact boundary it hangs off
    private (int Banked, int Skipped) BankMemory(IReadOnlyList<string> candidates, ITurnObserver observer)
    {
        _lastBankFailure = null;   //cleared per attempt, so a rotation cannot repeat an earlier failure
        if (piggyback is null || candidates.Count == 0) return (0, 0);
        try { return piggyback.Bank(candidates); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            //recorded as well as warned, the observer channel alone loses this on a rotation
            _lastBankFailure = $"memory not updated — {ex.Message}";
            observer.OnWarning(_lastBankFailure);
            return (0, 0);
        }
    }

    internal const string NewConversationLine = "new conversation";

    internal const string RoleSwitchFailed = "role switch failed";
    internal const string EffortUnavailable = "effort switching is unavailable in this session";

    //the effort is written above the tools, so a change moves the prompt's first bytes and the server cannot reuse what it read. no timing number, the cost is the server's
    internal const string EffortRereadsLine = "the next request reads the whole conversation again";
    internal const string ModelSwitchFailed = "model switch failed";
    internal const string ModelSwitchUnavailable = "model switching is unavailable in this session";
    //the word a user types to add a model, one const so the dispatch and the help row agree
    internal const string AddModelArg = "add";

    //new answers exactly as add does, so the word is not read as a model id
    internal const string NewModelArg = "new";

    //the sentence that sends the user to another terminal and back, all three clauses are needed
    internal const string AddMovedText =
        "adding a model needs its own terminal — run gatto model there, then /model to switch to it";

    //the word that names a model's files, chosen so it cannot collide with a model id
    internal const string QuantArg = "quant";

    //the word that forgets a file, a const beside QuantArg so nothing spells it differently
    internal const string RemoveArg = "rm ";

    //bare /model shows the list and returns the picked id, null when cancelled, with Answered set only by the add flow's question
    private (string? Id, bool Answered) ShowModelPicker(ITurnObserver renderer)
    {
        if (listModels is null || picker is null) return (null, false);

        //d writes the default and the rows are rebuilt from what the write left, so the word cannot outlive a failed write
        int? openAt = null;
        while (true)
        {
            var items = listModels().ToList();
            if (items.Count == 0)
            {
                //send the user to gatto model, which is where a model is added now
                renderer.OnWarning("no models — run gatto model to add one");
                return (null, false);
            }

            //the d key is declared only when there is somewhere to write it, a picker cannot report a key it never got
            var outcome = picker.Pick("select a model", items,
                markLegend: setDefault is null ? modelMarkLegend : modelMarkLegend + "   d set as default",
                controlKeys: setDefault is null ? null : DefaultKey,
                openAt: openAt);

            if (outcome is PickOutcome.Control { Key: 'd', Cursor: var at })
            {
                //every row is a model, so the only guard is the cursor's range, the picker can name any row it was on
                if (at >= 0 && at < items.Count && setDefault!(items[at].Id) is { } problem)
                    renderer.OnWarning(problem);
                openAt = at;
                continue;
            }

            if (outcome.PickedRow is not { } chosen)
            {
                //a plain terminal could not select from the list it printed, so tell the user how to switch
                if (!IsRichTerminal()) renderer.OnWarning("type /model <name> to switch");
                return (null, false);
            }

            //a pick is always a switch, the add row is gone and gatto model is the way to add one
            return (items[chosen].Id, false);
        }
    }

    //the key that writes the default, allocated once rather than per repaint
    private static readonly char[] DefaultKey = ['d'];

    //s applies the row for this session only and writes no profile, Enter is the persisting pick
    private static readonly char[] EffortSessionOnlyKey = ['s'];

    //the /effort body both call sites share, a typed level always persists and the picker's s key is the only session-only way
    private string? HandleEffortCommand(string arg)
    {
        if (arg.Length == 0)
        {
            if (IsRichTerminal() && listEfforts is not null && picker is not null)
            {
                var items = listEfforts().ToList();
                if (items.Count > 0)
                {
                    var outcome = picker.Pick("select an effort level", items,
                        markLegend: "current   s session only", controlKeys: EffortSessionOnlyKey);
                    var picked = CommandText.InterpretEffortPick(items, outcome);
                    return picked is { } p ? ApplyEffort(p.LevelArg, p.Persist) : null;
                }
            }
            return CommandText.EffortUsage(thinkingName, thinkingToggle, thinkingUnavailable);
        }
        return ApplyEffort(arg, persist: true);
    }

    //the one seam all three effort changes go through, so a typed level and a picked one cannot resolve differently
    private string ApplyEffort(string levelArg, bool persist)
    {
        if (setEffort is null) return EffortUnavailable;
        var before = thinkingName;
        var result = setEffort(levelArg, persist);
        if (result.Success) thinkingName = result.LevelName;
        if (result.Success && result.Thinking is { } thinking) convo.SetThinking(thinking);
        //only a level that moved, in a session that has talked, costs a full read, the first request reads everything anyway
        var rereads = result.Success && !string.Equals(before, result.LevelName, StringComparison.Ordinal)
            && convo.Messages.Any(m => m.Role != "system");
        return rereads ? result.Message + ", " + EffortRereadsLine : result.Message;
    }

    //a short label for the conversation, its first user turn read from the top of the transcript
    private string GistOfConversation()
    {
        for (var i = 0; i < convo.Count; i++)
            if (convo.Messages[i].Role == "user" && convo.Messages[i].Content is { } c)
                return CommandText.MakeGist(c);
        return "";
    }


    //the one body behind /model, it returns true only when a switch happened and hands the caller the line to say in its place
    private bool HandleModelCommand(string trimmed, ITurnObserver picker, Action<string> say,
        out string? switchNotice)
    {
        switchNotice = null;
        //refuse before the picker, a connect endpoint does not swap models and a picker there could arm one against a server gatto does not manage
        if (unmanagedNotice is { Length: > 0 } unmanaged)
        {
            say(unmanaged);
            return false;
        }

        var arg = trimmed.Length > 6 ? trimmed[6..].Trim() : "";

        //set only when the wizard's done step answered, in the same branch as the id so the two cannot part
        var answered = false;

        //both verbs parse and answer with a line since the shelf moved to gatto model, and nothing about the session changed so they answer false
        if (arg == AddModelArg || arg == NewModelArg)
        {
            say(AddMovedText);
            return false;
        }


        //the bare /model quant lists the files the model holds, it is read-only and answers false so the caller must not refresh the chip
        if (arg == QuantArg)
        {
            say(CommandText.QuantLines(modelFiles, _glyphs));
            return false;
        }

        //the quant switch answers false so the caller does not recompose, and quant rm is matched before it or rm reads as a file name
        if (arg.StartsWith(QuantArg + " " + RemoveArg, StringComparison.Ordinal))
        {
            var name = arg[(QuantArg.Length + RemoveArg.Length + 1)..].Trim();
            say(name.Length == 0 ? $"which file? /model {QuantArg} {RemoveArg} <name>"
                : removeQuant is null ? "removing a file isn't available in this session"
                : removeQuant(name));
            return false;
        }

        if (arg.StartsWith(QuantArg + " ", StringComparison.Ordinal))
        {
            var name = arg[(QuantArg.Length + 1)..].Trim();
            say(name.Length == 0 ? CommandText.QuantLines(modelFiles, _glyphs)
                : switchQuant is null ? "switching files isn't available in this session"
                : switchQuant(name));
            return false;
        }

        if (arg.Length == 0 && listModels is null && cloudSwitchHint is { } hint)
        {
            say(hint);
            return false;
        }

        if (arg.Length == 0)
        {
            var (chosen, chosenWasAnswered) = ShowModelPicker(picker);
            if (chosen is null) return false;   //cancelled, or a plain terminal which printed the list
            arg = chosen;
            answered = chosenWasAnswered;
        }
        if (switchModel is null)
        {
            say(ModelSwitchUnavailable);
            return false;
        }
        var result = switchModel(arg, answered);
        if (!result.Success)
        {
            say(result.Message ?? ModelSwitchFailed);
            return false;
        }

        switchNotice = result.Message;
        modelName = result.ModelId;
        contextBudget = result.ContextBudget;
        probedNCtx = result.ProbedNCtx;
        thinkingName = result.ThinkingName;   //null when the new model has no map, the chip hides
        thinkingToggle = result.ThinkingIsToggle;   //a /model switch can change the reasoning shape (binary or levels)
        thinkingUnavailable = result.ThinkingIsUnavailable;
        if (result.SystemText is not null) convo.ReplaceSystem(result.SystemText, result.Baseline);
        else if (result.Baseline is not null && convo.Messages[0].Content is { } sameText) convo.ReplaceSystem(sameText, result.Baseline);   //same text, new baseline, so the record names the model now in use
        _usageState.Clear();   //the old reading describes a conversation and model pairing that is gone
        return true;
    }
}
