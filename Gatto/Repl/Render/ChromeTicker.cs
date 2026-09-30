using System.Diagnostics;
using Gatto.Core.Client;
using Gatto.Terminal;

namespace Gatto.Repl.Render;

//one timer owns the purr row and the tool dot's blink, run by the dispatch bracket alone. a paint throw reports once and stops the ticker
public sealed class ChromeTicker(ChromePainter painter, object gate, ISlotsReader? slotsReader = null, Func<string?>? timeOfDay = null)
{
    public const int GraceMs = 250;
    //how often the /slots reader polls, only while a turn is live and no delta has arrived, since deltas are the decode signal
    private const int PollIntervalMs = 3000;
    //only a leak ceiling, so it sits far above any real batch wait. an abort sends a cancel task the server has died on
    private const int PollTimeoutMs = 300_000;
    //the default purr set, so a caller that names no set and every golden sees the standard animation
    private static PurrFrames Purr => PurrFrames.Full;
    private PurrFrames _purr = PurrFrames.Full;

    private CancellationTokenSource? _cts;
    private Stopwatch? _clock;
    //read when the completion line is built, so it shows the time the turn ended
    private readonly Func<string?> _timeOfDay = timeOfDay ?? Gatto.Core.LocalClock.Now;
    //the turn's time blocked on prompts, subtracted from the displayed elapsed and read under the gate. the blink and the /slots poll run on the wall clock
    private UserWait _wait;
    private string _kaomoji = "";
    private long _tokenChars;
    //tool-result characters the model has read this turn, counted as a quarter of the decode shown in the purr
    private long _toolChars;
    private bool _faulted;
    //the last /slots answer, and the highest decode count shown so the committed purred-for line matches the last live one
    private SlotProgress? _slots;
    private long _lastPollMs = -1;
    private long _promptEstimate;   //gatto's own estimate of the request, the denominator of the prefill row
    private long _maxDown;
    private int _polling;   //0/1 latch so at most one poll is in flight
    //set by any streamed delta and by MarkStreaming on a tool call, so the prefill row ends when the model starts emitting
    private int _emitted;

    //the last prompt_progress chunk of the request, and 0 means none arrived so the prefill row keeps using /slots
    private long _streamTotal;
    private long _streamProcessed;

    public Action<Exception>? OnFault { get; set; }

    //the purr head, kaomoji, frame and elapsed, with the frame set as a parameter. the download watch takes the short set since it can run for hours
    public static string PurrHead(string kaomoji, long elapsedMs, PurrFrames frames) =>
        kaomoji + " " + frames.At(elapsedMs) + " " + FormatElapsed(elapsedMs);

    //the one elapsed formatter the purr and the completion row share, spelled by ElapsedText
    public static string FormatElapsed(long ms) =>
        Gatto.Core.ElapsedText.Of(TimeSpan.FromMilliseconds(Math.Max(0, ms)));

    private static string K(long n) => Gatto.Repl.Input.InputFrame.KFormat(n);

    //the turn total shown only when tool results added something, so a pure-chat turn keeps its bare count, and one source for every row
    private static string Sigma(long decodeTokens, long toolTokens, GlyphSet? glyphs)
    {
        if (toolTokens <= 0) return "";
        var g = glyphs ?? GlyphSet.Unicode;
        return $" {g.Dot} {g.Sum} " + K(decodeTokens + toolTokens);
    }

    //the purr row, keyed off the /slots counters so prefill reads as a percentage and decode as the current count
    public static string PurrRow(string kaomoji, long elapsedMs, long tokens, long toolTokens,
        SlotProgress? slots = null, bool prefilling = false, long promptEstimate = 0,
        GlyphSet? glyphs = null, PurrFrames? frames = null, long promptTotal = 0)
    {
        //the dot comes from the glyph table, the arrow and sigma do not yet, and the mix is deliberate
        var g = glyphs ?? GlyphSet.Unicode;
        var head = PurrHead(kaomoji, elapsedMs, frames ?? Purr);
        var sigma = Sigma(tokens, toolTokens, g);

        //the prefill phase comes from nothing having streamed this turn, so it holds on every endpoint. a poll's numbers are added over gatto's estimate
        if (prefilling)
        {
            //same column discipline as the purr head, and server numbers come after the timer so a poll never moves a number being read
            var showTotal = promptEstimate > 0 && (slots is null || slots.Value.PromptProcessed <= promptEstimate);
            //a stream chunk gives the server's real denominator, so it prints with no ~ and the estimate stays on the /slots path
            var progress = promptTotal > 0 && slots is { } streamed
                ? " " + g.Dot + " " + K(streamed.PromptProcessed) + "/" + K(promptTotal)
                : slots is { PromptProcessed: > 0 } r
                    ? " " + g.Dot + " " + K(r.PromptProcessed) + (showTotal ? "/~" + K(promptEstimate) : "")
                    : "";
            return kaomoji + " " + ReadingContext + Ellipsis(elapsedMs).PadRight(EllipsisWidth)
                + " " + FormatElapsed(elapsedMs) + progress + Sigma(0, toolTokens, g);
        }

        //the server's decoded count when a slot has one, else the chars÷4 estimate from the stream
        var down = slots is { } s ? s.Decoded : tokens;
        return head + " " + g.Dot + $" {g.Down} " + K(down) + sigma;
    }

    private const string ReadingContext = "reading context";

    //the prefill row's liveness cue, cycling on the purr's cadence so the row moves while the server is silent
    private static string Ellipsis(long elapsedMs) => new('.', 1 + (int)(elapsedMs / 400 % 3));
    private const int EllipsisWidth = 3;   //pads to the longest frame so the timer never moves

    //the waiting twin of the purr row, no animation, same live timer and down/sigma suffix, drawn on the tool row
    public static string WaitingRow(long elapsedMs, long tokens, long toolTokens,
        GlyphSet? glyphs)
    {
        //the cat and dot come from the glyph table, the arrow, ellipsis and sigma do not yet, and the mix is deliberate
        var g = glyphs ?? GlyphSet.Unicode;
        return Cats.WaitingOf(g) + $" waiting for your input {g.Ellipsis} " + FormatElapsed(elapsedMs)
            + $" {g.Dot} {g.Down} " + K(tokens) + Sigma(tokens, toolTokens, g);
    }

    //the committed turn-cost line, the same face as the live purr in past tense, with the same down and sigma shape
    public static string CompletionText(string kaomoji, long elapsedMs, long tokens, long toolTokens,
        GlyphSet? glyphs, string? timeOfDay = null) =>
        Completion(kaomoji, elapsedMs, tokens, toolTokens, glyphs ?? GlyphSet.Unicode, timeOfDay);

    private static string Completion(string kaomoji, long elapsedMs, long tokens, long toolTokens,
        GlyphSet g, string? timeOfDay) =>
        kaomoji + " purred for " + FormatElapsed(elapsedMs)
            + (timeOfDay is null ? "" : " (" + timeOfDay + ")")
            + $" {g.Dot} {g.Down} " + K(tokens)
            + Sigma(tokens, toolTokens, g);

    //the turn's completion line for scrollback, null under a second, matching the live row's elapsed and chars/4 estimate. call it before StopTurn stops the clock
    public string? CompletionRow()
    {
        lock (gate)
        {
            if (_clock is not { } c) return null;
            var shown = ShownMs(c.ElapsedMilliseconds);
            if (shown < 1000) return null;
            var tokens = Math.Max(Interlocked.Read(ref _tokenChars) / 4, _maxDown);
            var toolTokens = Interlocked.Read(ref _toolChars) / 4;
            return CompletionText(_kaomoji, shown, tokens, toolTokens, painter.Glyphs, _timeOfDay());
        }
    }

    //the displayed clock pauses while a prompt holds modal focus. every prompt fires the sink, so nesting counts as one wait. safe outside a turn, the reading is 0
    public void BeginUserWait()
    {
        lock (gate) _wait.Begin(_clock?.ElapsedMilliseconds ?? 0);
    }

    //the modal closed, so the displayed clock resumes
    public void EndUserWait()
    {
        lock (gate) _wait.End(_clock?.ElapsedMilliseconds ?? 0);
    }

    //wall clock minus the waits on the user minus the grace, so the live purr and the committed line can't drift. callers floor it at 0, CompletionRow doesn't
    private long ShownMs(long wallMs) => wallMs - _wait.TotalAt(wallMs) - GraceMs;

    public void AddTokenChars(long chars)
    {
        Interlocked.Add(ref _tokenChars, chars);
        if (chars > 0) Interlocked.Exchange(ref _emitted, 1);
    }

    //a streaming tool call ends the prefill phase here, without moving the token count, since its argument fragments are not decoded prose
    public void MarkStreaming() => Interlocked.Exchange(ref _emitted, 1);

    //a prompt_progress chunk gives the row the server's real total, with no ~. a chunk means nothing has decoded yet, so the row goes back to prefill
    public void SetPromptProgress(long total, long processed)
    {
        Interlocked.Exchange(ref _streamTotal, total);
        Interlocked.Exchange(ref _streamProcessed, processed);
        Interlocked.Exchange(ref _emitted, 0);
    }
    //feeds the purr's Σ
    public void AddToolResultChars(long chars) => Interlocked.Add(ref _toolChars, chars);

    //the estimate is gatto's chars/4 count of the request, the prefill row's only denominator. frames picks the turn's purr once, and null keeps pool index 0
    public void StartTurn(string kaomoji, long promptTokensEstimate = 0, PurrFrames? frames = null)
    {
        lock (gate)
        {
            if (_cts is not null || _faulted) return;
            _purr = frames ?? PurrFrames.Full;
            _kaomoji = kaomoji;
            _tokenChars = 0;
            _emitted = 0;
            _streamTotal = 0;
            _streamProcessed = 0;
            _toolChars = 0;
            _slots = null;
            _maxDown = 0;
            _promptEstimate = promptTokensEstimate;
            _lastPollMs = -1;
            _wait.Restart();
            _clock = Stopwatch.StartNew();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _ = Task.Run(() => LoopAsync(ct), ct);
        }
    }

    //repaint false clears the state but skips the paint. pass it when the completion commit follows, so the composer stays at the viewport bottom
    public void StopTurn(bool repaint = true)
    {
        lock (gate)
        {
            if (_cts is null) return;
            _cts.Cancel(); _cts.Dispose(); _cts = null;
            //the Stopwatch keeps running once started, so null it here. otherwise CompletionRow recommits a purred-for line whose elapsed keeps growing off an earlier turn
            _clock = null;
            _slots = null;   //no stale /slots answer reaches the next turn
            painter.State.PurrText = null;
            painter.State.Tool = null;
            painter.State.ToolInput = null;
            painter.State.ToolWait = null;
            painter.State.ToolProgress = null;
            if (!repaint) return;   //the caller's commit paints this change right after
            try { painter.Repaint(); }
            catch (Exception ex) { _faulted = true; Report(ex); }
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        Exception? fault = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(100, ct);
                var ms = _clock!.ElapsedMilliseconds;
                if (ms < GraceMs) continue;
                if (!Monitor.TryEnter(gate)) continue;   //don't block the timer waiting for the gate, a long hold would freeze the tick
                try
                {
                    if (ct.IsCancellationRequested || _faulted) return;
                    //floor the estimate at the highest count shown, so a failed poll cannot dip the ↓ back below what the row just showed
                    var tokens = Math.Max(Interlocked.Read(ref _tokenChars) / 4, _maxDown);
                    var toolTokens = Interlocked.Read(ref _toolChars) / 4;   //the turn's tool-result tokens (Σ)
                    //the tick keeps ms as the wall clock for the grace gate, the poll cadence and the blink. the user reads only shown, which is the work clock
                    var shown = Math.Max(0, ShownMs(ms));
                    if (painter.State.PanelUp)
                    {
                        //with the panel up the tool row shows the waiting suffix and the dot goes solid, since nothing is executing while gatto waits
                        painter.State.PurrText = null;
                        //the waiting row counts the prompt's own age, since the turn clock is frozen while it is up
                        painter.State.ToolWait = WaitingRow(_wait.OpenAt(ms), tokens, toolTokens,
                            painter.Glyphs);
                        painter.State.BlinkOn = true;
                    }
                    else
                    {
                        //poll /slots for the whole turn, since its count is the truth and the estimate is only the fallback with no reader wired
                        if (slotsReader is not null && (_lastPollMs < 0 || ms - _lastPollMs >= PollIntervalMs))
                        {
                            _lastPollMs = ms;
                            _ = PollSlotsAsync(ct);   //fire and forget, so the paint never waits on this poll
                        }
                        //the row shows reading context only while nothing has streamed and no tool runs. the tool clause matters, since a tool round streams no text either
                        var prefilling = Interlocked.CompareExchange(ref _emitted, 0, 0) == 0
                            && painter.State.Tool is null;
                        //the highest decode count is latched only when not prefilling, since the slot still holds the previous task's n_decoded
                        if (!prefilling && _slots is { } s) _maxDown = Math.Max(_maxDown, s.Decoded);

                        //the thinking suffix is added here, keeping PurrRow a pure static. a stream progress chunk makes the row read the stream's numbers only, one source per row
                        var streamTotal = Interlocked.Read(ref _streamTotal);
                        var rowSlots = prefilling && streamTotal > 0
                            ? new SlotProgress(streamTotal, Interlocked.Read(ref _streamProcessed), 0, true)
                            : _slots;
                        var row = PurrRow(_kaomoji, shown, tokens, toolTokens, rowSlots,
                            prefilling, _promptEstimate, frames: _purr,
                            promptTotal: prefilling ? streamTotal : 0);
                        if (painter.State.Thinking) row += " ~ thinking";
                        painter.State.PurrText = row;
                        painter.State.ToolWait = null;
                        painter.State.BlinkOn = ms / 500 % 2 == 0;
                    }
                    try { painter.Repaint(); }
                    catch (Exception ex) { _faulted = true; fault = ex; return; }
                }
                finally { Monitor.Exit(gate); }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        finally { if (fault is not null) Report(fault); }
    }

    //one /slots poll at a time, a failed or empty answer keeps the last one, and a dead reader never faults the ticker
    private async Task PollSlotsAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _polling, 1) == 1) return;
        try
        {
            //the poll runs on its own timeout. an aborted /slots request makes llama-server log a cancel task, and the turn token still gates applying the answer
            using var timeout = new CancellationTokenSource(PollTimeoutMs);
            var p = await slotsReader!.ReadAsync(timeout.Token).ConfigureAwait(false);
            //a null answer means no information, so keep the last count, since it only ever moves forward
            lock (gate) { if (p is not null && !ct.IsCancellationRequested) _slots = p; }
        }
        //a failed poll keeps the last answer, since the server only answers between batches and polls fail most when the wait is long
        catch (Exception) { }
        finally { Interlocked.Exchange(ref _polling, 0); }
    }

    private void Report(Exception ex)
    {
        try { OnFault?.Invoke(ex); } catch (Exception) { }
    }
}

//the animation is the text accordion behind the cat face, with no braille. a set's grammar: the r's grow to a peak, the ellipsis builds, the word retracts
public sealed record PurrFrames(IReadOnlyList<string> Frames, int? PadTo = null)
{
    //each frame pads to its set's widest, so the column after the accordion never shifts. a pool member pads to the pool's width, keeping the timer column still
    public int Width { get; } = PadTo ?? Frames.Max(f => f.Length);

    //the width every pool member pads to. it stays a fixed number, so a set widened by mistake cannot move the column for the others
    public const int PoolWidth = 12;

    //how long one frame is held, one speed for every set since the set is the parameter
    private const int FrameMs = 140;

    //the set the REPL and the audition use, so the audition shows the same purr as every turn after it
    public static readonly PurrFrames Full = new([
        "purr", "purrr", "purrrr", "purrrrr", "purrrrrr", "purrrrrrr",
        "purrrrrrr.", "purrrrrrr..", "purrrrrrr...",
        "purrrrrrr..", "purrrrrrr.", "purrrrrrr", "purrrrrr", "purrrrr", "purrrr", "purrr",
    ], PoolWidth);

    //two purrs, the second word growing a letter at a time. the full stretch holds for three frames, since a one-frame hold would change only a trailing space
    public static readonly PurrFrames Pair = new([
        "purr", "purr p", "purr pu", "purr pur", "purr purr",
        "purr purr.", "purr purr..", "purr purr...",
        "purr purr...", "purr purr...",
        "purr purr..", "purr purr.", "purr purr", "purr pur", "purr pu", "purr p",
    ], PoolWidth);

    //the same two-word shape with a tilde in place of the space, so the sound reads as one long purr
    public static readonly PurrFrames Tilde = new([
        "purr", "purr~", "purr~p", "purr~pu", "purr~pur", "purr~purr",
        "purr~purr~", "purr~purr~.", "purr~purr~..",
        "purr~purr~.", "purr~purr~", "purr~purr", "purr~pur", "purr~pu", "purr~p", "purr~",
    ], PoolWidth);

    //the roll leaves out the u, so its r's make the whole word and it is the most different of the four
    public static readonly PurrFrames Roll = new([
        "prrr", "prrrr", "prrrrr", "prrrrrr", "prrrrrrr", "prrrrrrrr",
        "prrrrrrrr.", "prrrrrrrr..", "prrrrrrrr...",
        "prrrrrrrr..", "prrrrrrrr.", "prrrrrrrr", "prrrrrrr", "prrrrrr", "prrrrr", "prrrr",
    ], PoolWidth);

    //the purr varies in sound, and Full stays index 0 because every consumer defaults to it
    public static readonly IReadOnlyList<PurrFrames> Pool = [Full, Pair, Tilde, Roll];

    //the pool member at index, wrapping, since a bounds exception in a chrome row would fail a turn over an animation
    public static PurrFrames FromPool(int index) =>
        Pool[(int)(((long)index % Pool.Count + Pool.Count) % Pool.Count)];

    //the only place randomness enters, called once and handed down. a chooser read from a clock would make every golden depend on when it ran
    public static PurrFrames RandomFromPool() => FromPool(System.Random.Shared.Next(Pool.Count));

    //the download watch's set, calmer because that watch may run for hours. don't narrow it to match a quoted cell count
    public static readonly PurrFrames Short = new([
        "purr", "purr.", "purr..", "purr...", "purr..", "purr.",
    ]);

    //the frame for this moment, padded, read from the elapsed clock so the animation and the timer cannot part
    public string At(long elapsedMs) =>
        Frames[(int)(Math.Max(0, elapsedMs) / FrameMs % Frames.Count)].PadRight(Width);
}
