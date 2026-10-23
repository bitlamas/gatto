using Gatto.Core.Session;
using Gatto.Repl.Commands;
using Gatto.Repl.Prompts;
using Gatto.Repl.Render;
using Gatto.Repl.Render.Chrome;
using Gatto.Terminal;

namespace Gatto.Repl;

//the plain face, for a terminal the rich surface cannot drive
public sealed partial class Repl
{
    //plain loop
    private async Task<int> RunPlainAsync(CancellationToken appCt)
    {
        Console.WriteLine(Cats.For(roleCat, glyphs: _glyphs));
        //the plain banner joins with Dot, the same separator as every other row
        var sep = $" {_glyphs.Dot} ";
        Console.WriteLine($"gatto{sep}{roleName}{sep}{Gatto.Core.Models.ServedModelName.Display(modelName)}{sep}v{Gatto.Core.GattoVersion.String}");
        Console.WriteLine("(/quit to exit, /new to reset, Ctrl+C aborts a turn)");
        //the same system line the rich path commits, printed literally here, plain has no renderer to hang the marker on
        if (memoryWarning is not null) Console.WriteLine($"{_glyphs.Sharp} " + memoryWarning);
        if (cloudNotice is not null) Console.WriteLine($"{_glyphs.Sharp} " + cloudNotice);
        if (servedNotice is not null) Console.WriteLine($"{_glyphs.Sharp} " + servedNotice);
        foreach (var w in LaunchWarningsNow()) Console.WriteLine($"{_glyphs.Sharp} " + TermText.Sanitize(w));   //an extension's error text reaches this list, so strip it like OnWarning does
        if (sandboxNotice is not null) Console.WriteLine($"{_glyphs.Sharp} " + sandboxNotice);
        if (wildNotice is not null) Console.WriteLine($"{_glyphs.Sharp} " + wildNotice);

        var renderer = new PlainRenderer(reasoning, _glyphs);
        //the resumedFrom flag on its own still prints the marker, a compact-then-quit session has no turns to replay
        if (resumedFrom is not null)
        {
            Console.WriteLine($"(resumed from {resumedFrom})");
            if (resumeLine is not null) Console.WriteLine(resumeLine);
            _usageState.SeedFrom(convo.Messages);   //the last reply's cut, so the first request keeps the prefix the server last read
            ReplayTranscript(convo.Messages, renderer, u => Console.WriteLine("\n> " + u));
            Console.WriteLine();
        }
        else if (SeedFirstMessage(convo, firstMessage))
            ReplayTranscript(convo.Messages, renderer, u => Console.WriteLine("\n> " + u));
        //plain prints subagent progress as a bracketed line like its [tool] rows, and the sink is a one-slot holder filled once a renderer exists
        if (subagentProgressSink is { Length: > 0 })
            subagentProgressSink[0] = m => Console.WriteLine("[agent] " + m);
        CancellationTokenSource? turnCts = null;
        Console.CancelKeyPress += (_, e) =>
        {
            lock (_ctsLock)
            {
                if (turnCts is not null)
                {
                    e.Cancel = true;            //first Ctrl+C aborts the turn and keeps the REPL
                    try { turnCts.Cancel(); } catch (ObjectDisposedException) { }
                }
            }
            //with no turn in flight Ctrl+C keeps its default, the process exits
        };

        var firstLine = true;
        while (!appCt.IsCancellationRequested)
        {
            if (firstLine) Ready();
            Console.Write("\n> ");
            var line = Console.ReadLine();
            if (line is null) break;
            //a byte order mark that opens piped stdin is the encoder's and not the user's, so it never reaches the model or hides a command
            if (firstLine && line.Length > 0 && line[0] == (char)0xFEFF) line = line[1..];
            firstLine = false;
            if (line.Trim().Length == 0) continue;

            //both faces call this gate before the command chain and before the send, so a mistyped command is refused in one place
            var gate = SlashGate.GateSlash(line, KnownCommands());
            if (gate.Refused)
            {
                Console.WriteLine(gate.Refusal);
                continue;
            }
            line = gate.Text;
            var trimmedLine = gate.Literal ? "" : line.Trim();

            if (trimmedLine == "/quit") break;
            if (trimmedLine == "/help")
            {
                Console.WriteLine(SlashCommands.RenderPlain(KnownCommands()));
                continue;
            }
            if (trimmedLine == "/new")
            {
                var composed = recomposeSystem();              //recomposeSystem rereads the context files, so a reset picks up any edits
                convo = new Conversation(composed.Text, baseline: composed.Baseline);
                convo.SetGattoRole(roleName);          //every record after a reset carries the running role, as the launch's do
                sessions?.StartNew();                  //the next save starts a new session file and leaves the old transcript alone
                sessionName = null;                    //the name belongs to the session /new ends
                _offered = false;                      //a fresh session, so the 85% offer can fire again
                _usageState.Clear();                   //the old usage reading describes the conversation that just ended
                loop.ResetStripWarning();              //a fresh session reports its own first leak
                loop.ResetAutoCompact();               //a fresh session may auto-compact again
                Console.WriteLine(NewConversationLine);
                if (SeedFirstMessage(convo, firstMessage))
                    ReplayTranscript(convo.Messages, renderer, u => Console.WriteLine("\n> " + u));
                continue;
            }
            if (SlashGate.TryMatchCommand(trimmedLine, "/wild", out var wildArg))
            {
                Console.WriteLine(HandleWild(wildArg));
                continue;
            }
            if (SlashGate.TryMatchCommand(trimmedLine, "/sandbox", out var sandboxArg))
            {
                Console.WriteLine(HandleSandbox(sandboxArg));
                continue;
            }
            if (trimmedLine == "/safe")
            {
                Console.WriteLine(HandleSafe());
                continue;
            }
            if (trimmedLine == "/context")
            {
                var width = 80;
                try { if (Console.WindowWidth > 20) width = Console.WindowWidth; } catch { } //no console, so the 80 default stands
                var rows = await ContextRowsAsync(width, new Theme(TermCaps.Plain), appCt);
                if (rows is null) Console.WriteLine(ContextUnavailable);
                else foreach (var row in rows) Console.WriteLine(row.Visible);
                continue;
            }
            if (trimmedLine == "/tools")
            {
                //sanitize each row, a tool name and description can come from an extension script, which is not gatto's text
                foreach (var row in RenderToolsList().Split('\n'))
                    Console.WriteLine(TermText.Sanitize(row));
                continue;
            }
            if (SlashGate.TryMatchCommand(trimmedLine, "/permissions", out var permArg))
            {
                //sanitizing drops newlines, so split the rows first, and the listing re-emits stored grant text from model-authored arguments
                foreach (var l in HandlePermissions(permArg).Split('\n'))
                    Console.WriteLine(TermText.Sanitize(l));
                continue;
            }
            if (MatchRegistered(trimmedLine) is { } reg)
            {
                foreach (var row in await RunRegisteredAsync(reg.Row, reg.Arg, appCt))
                    Console.WriteLine(row);
                continue;
            }
            if (trimmedLine == "/compact")
            {
                lock (_ctsLock) { turnCts = CancellationTokenSource.CreateLinkedTokenSource(appCt); }
                var ok = false;
                try { ok = await HandleCompactAsync(renderer, turnCts.Token); }
                finally
                {
                    lock (_ctsLock) { turnCts?.Dispose(); turnCts = null; }
                    Console.WriteLine();
                }
                if (ok) Console.WriteLine(CompactedLine());
                continue;
            }
            if (SlashGate.TryMatchCommand(trimmedLine, "/name", out var nameArg))
            {
                if (NameCommand(nameArg) is { } answer) Console.WriteLine($"{_glyphs.Sharp} " + answer);
                continue;
            }
            if (trimmedLine == "/role" || trimmedLine.StartsWith("/role ", StringComparison.Ordinal))
            {
                var arg = trimmedLine.Length > 5 ? trimmedLine[5..].Trim() : "";
                var result = ApplyRoleSwitch(convo, switchRole, arg, roleName);
                if (result.Success && result.ModelName is not null)
                {
                    roleName = result.RoleName;
                    modelName = result.ModelName!;
                    thinkingName = result.ThinkingName;   //null when the new role has no map, which hides the chip
                    roleCat = result.RoleCat;
                    firstMessage = result.FirstMessage;
                }
                Console.WriteLine(result.Success ? CommandText.RoleLine(roleName) : result.Message);
                if (result.Opened)   //the switch seeded the new role's opening, so it alone is painted
                    ReplayTranscript(convo.Messages, renderer, u => Console.WriteLine("\n> " + u));
                continue;
            }
            if (trimmedLine == "/model" || trimmedLine.StartsWith("/model ", StringComparison.Ordinal))
            {
                //the same body the rich dispatch runs, this site keeps its own sink and control-flow keyword, there is no chrome to refresh here
                if (HandleModelCommand(trimmedLine, renderer, Console.WriteLine, out var notice))
                {
                    Console.WriteLine(notice ?? CommandText.ModelLine(modelName));
                    //the context-fit check runs at the switch itself, so the warning appears before the next send
                    MaybeWarnContextFit(renderer);
                }
                continue;
            }
            if (trimmedLine == "/effort" || trimmedLine.StartsWith("/effort ", StringComparison.Ordinal))
            {
                var arg = trimmedLine.Length > 7 ? trimmedLine[7..].Trim() : "";
                if (HandleEffortCommand(arg) is { } effortLine) Console.WriteLine(effortLine);
                continue;
            }
            if (SlashGate.TryMatchCommand(trimmedLine, "/remember", out var rememberArg))
            {
                if (rememberArg.Length == 0)
                {
                    Console.WriteLine(RememberUsage);
                    continue;
                }
                line = CommandText.RememberPrompt(rememberArg);   //the prompt goes through the normal turn below, as if typed
            }
            if (trimmedLine == "/init") line = InitPrompt.Text;   //the prompt goes through the normal turn below, as if typed

            RefreshServingBeforeTurn(renderer);   //re-probe the server and check the context fit before the request is sent

            lock (_ctsLock)
            {
                turnCts = CancellationTokenSource.CreateLinkedTokenSource(appCt);
            }
            renderer.BeginTurn();   //reset the hide-reasoning state, a Ctrl+C mid-reasoning must not leave it set
            try
            {
                await RunTurnGuardedAsync(loop, convo, line, renderer, sessions, turnCts.Token, serverGone: serverGone);
            }
            finally
            {
                lock (_ctsLock)
                {
                    turnCts?.Dispose();
                    turnCts = null;
                }
                Console.WriteLine();
            }
            MaybeOfferCompact(renderer);
        }
        return 0;
    }
}
