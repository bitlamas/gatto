using Gatto.Core.Acquire;
using Gatto.Repl;
using Gatto.Repl.Input;
using Gatto.Repl.Render;
using Gatto.Terminal;

namespace Gatto.Cli.Setup.Tui;

//ink goes on after the clamp, or an inked row would be cut by escape bytes. paint through ITermSurface so a test can render the face
internal sealed class TuiWizardSurface : IWizardSurface, IDisposable
{
    private readonly ITermSurface _surface;
    private readonly IKeySource _keys;
    private readonly Theme _theme;
    private readonly Func<long> _nowMs;
    private readonly ArmedChord _chord = new();
    private readonly string _version;
    private readonly string _build;
    private readonly string _command;

    //named so the source shows no invisible byte, a raw DEL cannot be reviewed
    private const char Del = '\u007f';

    //version has no default, a literal version would be printed rather than reported. pass a clock that resolves at once, otherwise a scripted cancel hangs the run

    public TuiWizardSurface(ITermSurface surface, IKeySource keys, Theme theme, string version, string build,
        Func<long>? nowMs = null, Func<bool, IPollClock>? clock = null,
        Func<IPurrPulse>? pulse = null,
        GlyphSet? glyphs = null,
        Gatto.Repl.Render.PurrFrames? fullPurr = null,
        string command = ScreenPainter.DefaultCommand)
    {
        _command = command;
        //the purr is chosen once when the face is built, and null keeps the frame every golden pins
        _fullPurr = fullPurr ?? Gatto.Repl.Render.PurrFrames.Full;
        //the set is handed in rather than read from a static, and the Unicode default keeps every call site and golden unchanged
        _glyphs = glyphs ?? GlyphSet.Unicode;
        _pulse = pulse;
        _surface = surface;
        _keys = keys;
        _theme = theme;
        //injectable so a chord's expiry is testable without waiting two seconds. a frozen one plus a null clock hangs the run
        _nowMs = nowMs ?? (() => Environment.TickCount64);
        _clock = clock;
        _version = version;
        _build = build;
        _notes = new NoteSink(this);
    }

    //the bool is true for a watching screen and false for an armed chord's wait, a scripted queue can't tell the two apart
    private readonly Func<bool, IPollClock>? _clock;

    //the set this face composes with, handed in at construction so no static decides it
    private readonly GlyphSet _glyphs;

    //the full purr for this face, chosen once. the short set stays the same for hours, so only the check's set varies
    private readonly Gatto.Repl.Render.PurrFrames _fullPurr;

    //the set this face speaks, for the runner to hand the flow (it holds a face and a flow at once)
    public GlyphSet Glyphs => _glyphs;

    //null means no pulse, since a defaulted timer arms a repeating write on a pool thread after the caller is gone
    private readonly Func<IPurrPulse>? _pulse;

    //the pulse running now, null between screens. read and written under _writeGate, so a late tick writes nothing
    private IPurrPulse? _live;
    private long _pulseBeganMs;

    //one lock over every write, since the pulse's tick writes from a pool thread. take it before the painter's own gate on both paths, so the two can't deadlock
    private readonly object _writeGate = new();

    //the last painted screen's composer, kept so a pulse can republish it with a working row
    private Func<int, int, Screen>? _lastComposer;

    //when the last question was answered, so the drain can judge the wait. 0 would read as answered, since every fake clock starts at 0
    private long? _answeredAtMs;

    //the writer the probes get, captured so probe lines never reach the painted screen (they print on the way out)
    public TextWriter Notes => _notes;
    private readonly NoteSink _notes;

    //everything the probes said, oldest first. the caller reads it after the alt screen is restored
    internal IReadOnlyList<string> CapturedNotes => _captured;
    private readonly List<string> _captured = [];

    //a second is the shortest wait worth drawing a purr for, so an instant transition draws none
    private const long PurrAfterMs = 1000;

    //one line from a probe, kept, and the purr shows gatto working. the frame stays byte-identical, the next Paint clears the purr
    private void Note(string line)
    {
        //a second raw write lives here, fine while this face owns the screen and corrupting a CUP-addressed frame when it doesn't. no write after the record prints
        _captured.Add(line);

        //notes don't draw, the pulse owns that row. a second note must not push the purr deadline out, or a probe every 900ms never reaches it
        if (_live is null) StartPulse();
    }

    //the wait that says nothing is still a wait, started on an answer and stopped at the top of Paint
    private void StartPulse()
    {
        lock (_writeGate)
        {
            //stop the previous one first, or a pulse whose answer never reached a Paint accumulates one per question
            _live?.Stop();
            _live?.Dispose();

            if (_pulse is not { } source) return;

            _pulseBeganMs = _nowMs();
            _live = source();
            _live.Start(PurrTick, TimeSpan.FromMilliseconds(PurrAfterMs), PurrEvery);
        }
    }

    //test seam, whether a pulse is armed. absence of output would also mean a pulse that has not ticked yet, so ask the pulse itself
    internal bool PulseArmed { get { lock (_writeGate) return _live is not null; } }

    //dispose stops the pulse, or the last answer's timer keeps firing after the wizard closed. safe on a face that never started one
    public void Dispose() => StopPulse();

    //clearing _live under the gate is what disarms a tick in flight, a timer cannot promise its callback returned
    private void StopPulse()
    {
        lock (_writeGate)
        {
            _live?.Stop();
            _live?.Dispose();
            _live = null;
        }
    }

    //one beat of the silent-wait purr, on the pulse's thread, so read and write under _writeGate
    private void PurrTick()
    {
        lock (_writeGate)
        {
            //a tick that arrived after the stop, the frame it would draw on is already being replaced
            if (_live is null) return;

            var elapsed = _nowMs() - _pulseBeganMs;

            //the setup face republishes the frame with the purr in the title row, through Widget.Purr so the spelling stays one
            if (_lastComposer is not { } setupComposer) return;
            PaintCore((w, h) => setupComposer(w, h) with
            {
                Working = Widget.Purr(elapsed, PurrFrames.Short, _glyphs),
            });
        }
    }

    //the one purr line, composed here so the setup face and the in-session face can't look different
    private PaintedRow PurrRow(long elapsedMs) =>
        PaintedRow.Of("  " + ChromeTicker.PurrHead(Cats.Face(_glyphs), elapsedMs, PurrFrames.Short),
            RunInk.Accent);

    private static readonly TimeSpan PurrEvery = TimeSpan.FromMilliseconds(120);

    //drop keys typed before this screen existed, but only after a wait long enough to have drawn a purr
    private void DrainIfTheWaitWasLong()
    {
        if (_answeredAtMs is not { } answered || _nowMs() - answered < PurrAfterMs) return;
        try { while (_keys.KeyAvailable) _keys.ReadKey(); }
        catch (Exception) { } //redirected stdin answers nothing
    }

    //the stamp and the pulse start in one place, reached from a finally, Choose has sixteen returns
    private void Answered()
    {
        _answeredAtMs = _nowMs();
        StartPulse();
    }

    //the writer's newline is \n, set once here, since a CR hits the arm below and clears the line
    private sealed class NoteSink : TextWriter
    {
        private readonly TuiWizardSurface _face;
        private readonly System.Text.StringBuilder _line = new();

        public NoteSink(TuiWizardSurface face)
        {
            _face = face;
            CoreNewLine = ['\n'];
        }

        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

        public override void Write(char value)
        {
            switch (value)
            {
                case '\n':
                    _face.Note(_line.ToString());
                    _line.Clear();
                    break;
                //a CR is the ticker redrawing one line, so what preceded it was a frame of motion
                case '\r':
                    _line.Clear();
                    break;
                default:
                    _line.Append(value);
                    break;
            }
        }

        public override void Write(string? value)
        {
            if (value is null) return;
            foreach (var c in value) Write(c);
        }

        //override every overload that can take content, since WriteLine(string) never reaches Write(char) and the line arrives empty
        public override void Write(char[] buffer, int index, int count)
        {
            for (var i = 0; i < count; i++) Write(buffer[index + i]);
        }

        public override void Write(ReadOnlySpan<char> buffer)
        {
            foreach (var c in buffer) Write(c);
        }

        public override void WriteLine(string? value)
        {
            Write(value);
            Write('\n');
        }

        public override void WriteLine(ReadOnlySpan<char> buffer)
        {
            Write(buffer);
            Write('\n');
        }

        public override void WriteLine() => Write('\n');
    }


    //the live width, one answer for wrapping and clamping so the two cannot disagree at the screen's edge
    private int Width => _surface.Width > 0 ? _surface.Width : 100;

    //how many model rows this face may show, read from the live height each time it is asked
    public int RowBudget => Shelf.RowBudget(RowsAvailable);

    //the terminal's height, since this face owns the screen, read when asked rather than cached
    private int RowsAvailable => _surface.Height;

    //drop blank rows to fit the terminal, from the body only, and say nothing when even that is not enough
    private static IReadOnlyList<PaintedRow> Fit(IReadOnlyList<PaintedRow> rows, int height)
    {
        if (height <= 0 || rows.Count <= height) return rows;

        //header, strip, rule at the top and rule, keys at the bottom. neither bound can bind any more, RowFit.Fixed keeps the fit off the chrome
        const int Head = 3;
        const int Tail = 2;
        var kept = new List<PaintedRow>(rows);

        //structural blanks go first, then paragraph breaks, then optional rows (a fact row stays). one class at a time, or a break goes while blanks remain above it
        foreach (var cls in (ReadOnlySpan<RowFit>)[RowFit.Structural, RowFit.Paragraph, RowFit.Optional])
            for (var i = kept.Count - Tail - 1; i >= Head && kept.Count > height; i--)
                if (kept[i].Fit == cls) kept.RemoveAt(i);

        return kept;
    }

    //this face binds m, so the key the shelf advertises and the answer here must change in the same edit
    public bool CanSwitchSource => true;

    //this face paints a Choice's body rows, so the flow has no reason to narrate the next screen
    public bool ShowsChoiceBodyRows => true;

    //the rows on screen, plain, kept so a repaint can be a diff in place
    private IReadOnlyList<PaintedRow> _painted = [];

    //the width _painted was composed at, since a diff is only valid against rows of the same width
    private int _paintedWidth;

    //the height the last frame was painted at, since a resize moves the absolutely addressed rows and the text still matches
    private int _paintedHeight;

    //the composer is pure, runs on the painter's thread, reads nothing off this face, and takes the width and the rows
    public void Paint(Func<int, int, Screen> compose)
    {
        //the flow has something to show, so stop the purr before anything is written
        StopPulse();
        PaintCore(compose);
    }

    //the paint without the stop, so a purr tick can repaint the frame it purrs on (the public entry would disarm the pulse)
    private void PaintCore(Func<int, int, Screen> compose)
    {
        var screen = compose(Width, RowsAvailable);
        //a new screen is a new wait, so a probe that answers at once draws no purr from an earlier slow one
        var whole = ScreenPainter.Paint(screen, Width, _version, _build, _glyphs, out var spot, _command);
        var next = Fit(whole, RowsAvailable);

        lock (_writeGate)
        {
            //kept for the in-session pulse, which republishes this screen with a working row, and assigned on every paint so the field is always current
            _lastComposer = compose;

            //repaint by diff, and either dimension change invalidates it (a width change makes the rows incomparable, a height change moves their addresses)
            var diffed = _painted.Count > 0 && Width == _paintedWidth && _surface.Height == _paintedHeight;
            if (diffed)
            {
                for (var i = 0; i < next.Count; i++)
                {
                    var ink = Ink(next[i]);
                    if (i < _painted.Count && Ink(_painted[i]) == ink) continue;
                    //clear to end of line before the content, or a row that exactly fills the width loses its last character
                    _surface.Write(Ansi.Cup(i + 1, 1) + Ansi.ClearToEol + ink);
                }
            }
            else
            {
                //clear and home, then write the frame in place rather than appending. no newline after the last row, or the frame scrolls up and the diff writes below it
                _surface.Write(Ansi.CursorHome + Ansi.ClearBelow);
                for (var i = 0; i < next.Count; i++)
                    _surface.Write((i == 0 ? "" : "\n") + Ink(next[i]));
            }

            //park the cursor after the frame on both paths, so a diff and a full paint leave it in one place. erase below only for a diff whose row count shrank
            _surface.Write(Ansi.Cup(next.Count + 1, 1)
                + (diffed && next.Count < _painted.Count ? Ansi.ClearBelow : ""));

            //hide the terminal cursor and place it on the caret cell only on a screen with a typed door, after the park
            _surface.Write(CursorAt(spot, next.Count));

            _painted = next;
            _paintedWidth = Width;
            _paintedHeight = _surface.Height;
        }
    }

    //the frame's caret, or the cursor stays hidden. the row comes from the distance to the bottom and the fitted count, since the fit drops blanks above the door
    private static string CursorAt(CaretSpot? spot, int painted)
    {
        if (spot is not { } s) return Ansi.HideCursor;

        var row = painted - s.FromEnd;
        //a door the fit pushed off the frame takes no cursor (unreachable while the fit drops blanks only)
        if (row < 0) return Ansi.HideCursor;

        return Ansi.Cup(row + 1, s.Column + 1) + Ansi.ShowCursor;
    }

    //paint the already clamped row, or the clamp counts escape bytes and loses visible cells
    private string Ink(PaintedRow row) =>
        string.Concat(row.Runs.Select(r => r.Ink switch
        {
            RunInk.Bright => _theme.Paint(r.Text, Theme.Bright),
            RunInk.Dim => _theme.Paint(r.Text, Theme.Dim),
            RunInk.Accent => _theme.Paint(r.Text, Theme.Accent),
            RunInk.Ok => _theme.Paint(r.Text, Theme.Ok),
            RunInk.Warn => _theme.Paint(r.Text, Theme.Warn),
            _ => r.Text,
        }));

    //what the last Paint put on screen, plain, the oracle a render test diffs against its golden

    //derived from the painted rows, so the record and the screen cannot disagree about what a row says
    internal IReadOnlyList<string> LastPainted => [.. _painted.Select(r => r.Text)];

    //the IWizardSurface members below

    public string? Choose(WizardScreen.Choice c, Func<bool>? watch = null, Func<FetchTick?>? tick = null,
        Func<CheckTick?>? check = null)
    {
        DrainIfTheWaitWasLong();
        try { return ChooseCore(c, watch, tick, check); }
        finally { Answered(); }
    }

    private string? ChooseCore(WizardScreen.Choice c, Func<bool>? watch, Func<FetchTick?>? tick,
        Func<CheckTick?>? check)
    {
        var regions = RegionsFor(c);
        //the ring opens on the shelf's region when there is one, in both builders. name Region.List, regions[0] is the strip on every screen that draws one
        var ring = new FocusRing(regions,
            c.Shelf is { } opening ? Shelf.Opening(opening) : Region.List);
        var cursor = c.NoDefault ? -1 : 0;
        var at = ShelfCursors.Start;
        //where the keys sit on the strip, null until they move so the painter rests them on the section
        int? stripAt = null;
        //what the user has typed into the door, which wins over the screen's composed draft while they are in it
        var typed = new System.Text.StringBuilder();
        //the watch's start, since the face owns the clock and a browser watch has no tick to measure it
        var began = _nowMs();

        while (true)
        {
            //read the tick once per frame, one moment per screen. freeze the ring into the composer, or a resize mid-Tab composes a region the frame never showed
            var frozen = ring.Frozen();
            var shown = typed.Length > 0 ? c with { Draft = typed.ToString() } : c;
            var elapsedMs = c.Watching ? Math.Max(0, _nowMs() - began) : (long?)null;
            var tickNow = tick?.Invoke();
            var checkNow = check?.Invoke();
            Paint((w, h) => ScreenFor(shown, frozen, cursor, tickNow, at, w, h,
                elapsedMs, checkNow, stripAt));

            //on a watching screen the thing can turn up with no key pressed, so look for it between key checks
            var next = NextKey(c, watch);
            //the length of the watch just ended, which the flow has no clock to measure for the next screen
            if (next.Arrived) { _watchedMs = Math.Max(0, _nowMs() - began); return SetupFlow.Landed; }

            //go round and repaint on the watch's own clock, since repainting per chunk would strobe on a fast line
            if (next.Key is not { } key) continue;

            //one Ctrl+C leaves, a single press costs nothing while nothing is on disk. on a watching screen it arms Esc's chord and answers the last option
            if (IsCtrlC(key))
            {
                if (!c.Watching) return null;
                if (_chord.ArmedAt(_nowMs()) == Chord.Quit)
                    return c.KeysOnly ? c.Options[^1].Key : null;
                _chord.Fire(Chord.Quit, _nowMs());
                continue;
            }

            //one Esc in the list arms, a second within the window leaves (anywhere else Esc returns the keys to the list first)
            if (key.Key == ConsoleKey.Escape)
            {
                if (ring.EscReturnsToList()) { _chord.Disarm(); continue; }

                //on a watching screen Esc is a chord that names the price, and its second press answers the last option
                if (c.Watching && c.KeysOnly)
                {
                    if (_chord.ArmedAt(_nowMs()) == Chord.Quit) return c.Options[^1].Key;
                    _chord.Fire(Chord.Quit, _nowMs());
                    continue;
                }

                //on a keys-only screen Esc is the second option, one press with no chord
                if (c.KeysOnly) return c.Options[1].Key;

                //a numbered screen can make Esc an answer instead of a leave. this comes before the in-session branch, which returns null and would drop a decided answer
                if (EscAnswer(c) is { } answered) return answered.Key;

                //on a shelf with no list in the ring, Esc takes the flow's back key, which the hint and footer already name
                if (c.Shelf is not null && c.AllowBack && !ring.Has(Region.List)) return SetupFlow.BackKey;

                if (_chord.ArmedAt(_nowMs()) == Chord.Quit) return null;
                _chord.Fire(Chord.Quit, _nowMs());
                continue;
            }

            //any other key disarms, or a warning would outlive the state it describes
            _chord.Disarm();

            //a watching screen's own keys are read from the same field as the footer and taken above the switch
            if (c.Watching && c.KeysOnly)
                foreach (var o in c.Options)
                    if (o.Press is { Length: > 0 } press && Presses(key, press))
                        return o.Key;

            //the door takes the keys first, but on a shelf the list keeps what the draft can't use. both Tab and Up leave the door, Enter on an empty one returns the keys
            if (ring.Current == Region.Search && c.Door is not null
                && key.Key is not (ConsoleKey.Tab or ConsoleKey.UpArrow)
                && (c.Shelf is null || key.Key is ConsoleKey.Enter or ConsoleKey.Backspace
                    || (key.KeyChar >= ' ' && key.KeyChar != Del)))
            {
                if (key.Key == ConsoleKey.Enter)
                {
                    //wrap the typed text, or a bare key a face sends could start a multi-gigabyte fetch
                    if (typed.Length > 0) return ShelfControls.TypedAnswer(typed.ToString());
                    ring.Focus(Region.List);
                    continue;
                }
                if (key.Key == ConsoleKey.Backspace)
                {
                    if (typed.Length > 0) typed.Length--;
                    continue;
                }
                Edit(typed, key);
                continue;
            }

            switch (key.Key)
            {
                case ConsoleKey.Tab:
                    ring.Next();
                    //the strip forgets where the keys were, a remembered section is a screen the user has moved on from
                    stripAt = null;
                    continue;
                //the strip takes no keys, and going back is b. left and right move the chips while the keys are there, and the files only in a folded frame
                case ConsoleKey.LeftArrow when ring.Current == Region.Files && Folded(c):
                    at = at with { File = Math.Max(0, Pane.FileAt(Files(c, cursor), at.File) - 1) };
                    continue;
                case ConsoleKey.RightArrow when ring.Current == Region.Files && Folded(c):
                    at = at with
                    {
                        File = Math.Min(Math.Max(0, Files(c, cursor).Count - 1),
                            Pane.FileAt(Files(c, cursor), at.File) + 1),
                    };
                    continue;
                case ConsoleKey.LeftArrow when ring.Current == Region.Families && at.Chip > 0:
                    at = at with { Chip = at.Chip - 1 };
                    continue;
                case ConsoleKey.RightArrow when ring.Current == Region.Families
                        && c.Shelf is { Families: { } fam } && at.Chip < fam.Count - 1:
                    at = at with { Chip = at.Chip + 1 };
                    continue;
                //the vertical arrows step through the pane's files, since one shared index would let Tab move the model
                case ConsoleKey.UpArrow when ring.Current == Region.Files:
                    at = at with { File = Math.Max(0, Pane.FileAt(Files(c, cursor), at.File) - 1) };
                    continue;
                case ConsoleKey.DownArrow when ring.Current == Region.Files:
                    at = at with
                    {
                        File = Math.Min(Math.Max(0, Files(c, cursor).Count - 1),
                            Pane.FileAt(Files(c, cursor), at.File) + 1),
                    };
                    continue;
                //the vertical arrows step through the builds here, since the pane is two Tab stops with their own cursors
                case ConsoleKey.UpArrow when ring.Current == Region.Builds:
                    at = at with { Build = Math.Max(0, Build(c, cursor, at) - 1) };
                    continue;
                case ConsoleKey.DownArrow when ring.Current == Region.Builds:
                    at = at with
                    {
                        Build = Math.Min(Math.Max(0, Builds(c, cursor).Count - 1), Build(c, cursor, at) + 1),
                    };
                    continue;
                //down past the last option enters the door, and up brings the keys back. plain door screens only, since a shelf's arrows step through the table
                case ConsoleKey.UpArrow when c.Shelf is null && ring.Current == Region.Search
                        && ring.Has(Region.List):
                    ring.EscReturnsToList();
                    continue;
                case ConsoleKey.DownArrow when c.Shelf is null && ring.Current != Region.Search
                        && c.Door is not null && ring.Has(Region.Search) && Step(c, cursor, +1) is null:
                    ring.Focus(Region.Search);
                    continue;
                //the vertical arrows do nothing in the slot or the chips row, each zone holds one value moved with Tab or the arrows
                case ConsoleKey.UpArrow or ConsoleKey.DownArrow
                        when ring.Current is Region.Publisher or Region.Families:
                    continue;
                //skip the folder door's index rather than stopping at it, since its row is gone. a ceiling would strand the row the flow adds after it
                case ConsoleKey.UpArrow when Step(c, cursor, -1) is { } up:
                    cursor = up;
                    at = at.OnANewRow();
                    ring = RingFor(c, cursor) ?? ring;
                    continue;
                case ConsoleKey.DownArrow when Step(c, cursor, +1) is { } down:
                    cursor = down;
                    at = at.OnANewRow();
                    ring = RingFor(c, cursor) ?? ring;
                    continue;
                //inert exactly when the screen only advances on the watch, the same answer the plain face uses
                case ConsoleKey.Enter when c.OnlyTheWatchAdvances:
                    continue;
                //the Enter key belongs to the region the keys are in, and the pane's own Enter answers with the file it shows
                case ConsoleKey.Enter when ring.Current == Region.Files:
                    at = at with
                    {
                        File = Pane.FileAt(Files(c, cursor), at.File),
                        PickedQuant = Pane.QuantAt(Files(c, cursor), at.File),
                    };
                    //reaching this arm proves cursor indexes an option, since the Files zone is in the ring only when the row had files
                    return at.PickedQuant is { Length: > 0 } paneQuant
                        ? ShelfControls.PickAnswer(c.Options[cursor].Key, paneQuant)
                        : c.Options[cursor].Key;
                case ConsoleKey.Enter when ring.Current == Region.Builds:
                    at = at with { Build = Build(c, cursor, at) };
                    continue;
                case ConsoleKey.Enter when ring.Current == Region.Families
                        && c.Shelf is { Families: { Count: > 0 } fams } && at.Chip >= 0 && at.Chip < fams.Count:
                    return ShelfControls.FamilyAnswer(fams[at.Chip]);
                //the slot's Enter answers the publisher rather than the list's cursor
                case ConsoleKey.Enter when ring.Current == Region.Publisher:
                    return ShelfControls.PublisherAnswer();
                //bound the cursor by the option count, since a search that matched nothing still draws chips and a door
                case ConsoleKey.Enter when cursor >= 0 && cursor < c.Options.Count:
                    //the pane's pick travels with the key, or the footer's pick would promise a deed nothing did
                    return at.PickedQuant is { Length: > 0 } pickedQuant
                        ? ShelfControls.PickAnswer(c.Options[cursor].Key, pickedQuant)
                        : c.Options[cursor].Key;
                case ConsoleKey.Enter:
                    continue;   //no cursor, so Enter answers nothing
            }

            //the slash key takes the keys to the door, and d does the same where the shelf offers it
            if ((key.KeyChar == '/' || (key.KeyChar == 'd' && ShelfControls.OffersD(c)))
                && c.Shelf is not null && ring.Focus(Region.Search)) continue;

            //m switches source on a shelf, and where a screen offers the switch, since elsewhere it is a typed letter
            if (key.KeyChar == 'm' && (c.Shelf is not null || c.SwitchesSource)) return ShelfControls.SourceAnswer();
            //a lifts the fit filter that the count line advertises, and only past the door block where it is a typed letter
            if (key.KeyChar == 'a' && ShelfControls.OffersLift(c)) return ShelfControls.LiftAnswer();
            //b is back, guarded on the same flag the footer reads so it works exactly where it is drawn
            if (key.KeyChar == 'b' && c.AllowBack) return SetupFlow.BackKey;
            //where the screen allows no back, b answers the option Esc answers
            if (key.KeyChar == 'b' && !c.AllowBack && EscAnswer(c) is { } escAnswered) return escAnswered.Key;
            //neither f nor s is bound here, since this face's footer names neither (a key is named or not bound). s leaves a real gap, there is no sort on this face
            if ((key.KeyChar == 'd' && ShelfControls.OffersD(c)) || (key.KeyChar == '/' && c.Shelf is not null))
                return SetupFlow.CtlSearch;

            //a digit lights its row first, then answers, where the screen draws numbers. inert on a render-and-wait screen, where one press would stop the fetch
            if (key.KeyChar is >= '1' and <= '9' && !c.Unnumbered && !c.OnlyTheWatchAdvances)
            {
                var i = key.KeyChar - '1';
                if (i >= c.Options.Count) continue;
                //skip a digit whose row is not painted, since the folder door's index would answer in its place
                if (ShelfControls.IsFolderDoor(c, i)) continue;
                if (cursor < 0) { cursor = i; continue; }
                return c.Options[i].Key;
            }
        }
    }

    //the typed ask, with the same door row every other screen draws. a rejected answer repaints with the message and keeps the draft
    public string? Ask(WizardScreen.Ask a)
    {
        DrainIfTheWaitWasLong();
        try { return AskCore(a); }
        finally { Answered(); }
    }

    private string? AskCore(WizardScreen.Ask a)
    {
        //an offer is a row to pick, so the keys start in the list. with no offer the door is the only region and keeps the focus
        var offer = a.Offer;
        var ring = offer is null
            ? new FocusRing([Region.Search])
            : new FocusRing([Region.List, Region.Search]);
        var draft = new System.Text.StringBuilder();
        string? problem = null;

        while (true)
        {
            var askRing = ring.Frozen();
            Paint((w, _) => new Screen(
                Sections: a.Strip, Focused: askRing.Current, Title: a.Label,
                Body: [
                    .. Rows(a.BodyRows, w),
                    .. problem is { Length: > 0 } p
                        ? new[] { PaintedRow.Of(""), PaintedRow.Of("  " + p, RunInk.Accent) } : [],
                    //the cursor sits on the offer row only while the keys are in the list, so a Tab to the door is visible
                    .. offer is null
                        ? (IReadOnlyList<PaintedRow>)[]
                        : [PaintedRow.Of(""),
                           Option(_glyphs, new ChoiceOption(offer.Value, offer.Label), 0,
                               ring.Current == Region.List ? 0 : -1)],
                ],
                //the door is idle while the keys are in the list, and its hint is how the user learns Tab reaches it. with no offer the door has the keys, so it draws no hint
                Door: new DoorRow(a.Placeholder ?? "", draft.ToString(),
                    ring.HasSecondArea ? DoorHints.AnywhereOf(_glyphs) : ""),
                //the Esc word follows the region. two areas word it leave, one area takes back when the ask allows it, so the drawn verb can't lie
                Keys: ring.HasSecondArea
                    ? [new("Tab", "area"), new("Enter", a.EnterVerb),
                       new("Esc", ring.EscVerb("leave"))]
                    : [new("Enter", a.EnterVerb),
                       new("Esc", ring.EscVerb(a.AllowBack ? "back" : "leave"))],
                //the armed row is the only sign the chord is armed, otherwise the first Esc looks like it did nothing
                Armed: offer is not null && _chord.ArmedAt(_nowMs()) == Chord.Quit
                    ? "Esc again to leave"
                    : null));

            //a resize comes back false, so the loop goes round and repaints at the new size
            if (!KeyOrResize()) continue;
            var key = _keys.ReadKey();
            //one Ctrl+C leaves a typed screen, where no work is running for a press to interrupt
            if (IsCtrlC(key)) return null;
            switch (key.Key)
            {
                case ConsoleKey.Escape:
                    //the first Esc returns the keys to the list and is spent doing that. the ring answers it, so nothing is assumed
                    if (ring.EscReturnsToList()) { _chord.Disarm(); continue; }

                    //a door-only ask has no list, so one press leaves. the chord belongs to the screen that has a list
                    if (offer is null) return null;

                    if (_chord.ArmedAt(_nowMs()) == Chord.Quit) return null;
                    _chord.Fire(Chord.Quit, _nowMs());
                    continue;
                //the keys move between the offer row and the door on Tab, when the ask has both
                case ConsoleKey.Tab:
                    ring.Next();
                    continue;

                //an offer is accepted only when the row was shown. on the list the row is the answer, and on an empty door Enter takes it
                case ConsoleKey.Enter when offer is not null
                        && (ring.Current == Region.List || draft.Length == 0):
                    return offer.Value;

                case ConsoleKey.Enter:
                    problem = a.Validate(draft.ToString());
                    if (problem is null) return draft.ToString();
                    continue;
                case ConsoleKey.Backspace when draft.Length > 0:
                    draft.Length--;
                    continue;
            }

            Edit(draft, key);
        }
    }

    public void Show(WizardScreen.Info i) =>
        Paint((w, _) => new Screen(i.Strip, Region.List, null, [.. Rows(i.Rows, w)], null, []));

    //the option Esc answers on a numbered screen, or null when Esc leaves. the Esc branch and the footer both read it, so the drawn verb and the key can't disagree
    private static ChoiceOption? EscAnswer(WizardScreen.Choice c)
    {
        if (c.KeysOnly) return null;
        ChoiceOption? found = null;
        foreach (var o in c.Options)
        {
            if (o.EscVerb is not { Length: > 0 }) continue;
            if (found is not null)
                throw new InvalidOperationException(
                    $"Esc answers exactly one option; '{c.Key}' declares an EscVerb on more than one");
            found = o;
        }
        return found;
    }

    //both key loops ask this instead of testing the modifier mask, so they can't leave on different keys
    private static bool IsCtrlC(ConsoleKeyInfo key) =>
        key.Key == ConsoleKey.C && (key.Modifiers & ConsoleModifiers.Control) != 0;

    //what counts as typing for the draft: printable characters append, nothing else does (a control byte would reach the answer and never show on screen)
    private static void Edit(System.Text.StringBuilder draft, ConsoleKeyInfo key)
    {
        if (key.KeyChar >= ' ' && key.KeyChar != Del) draft.Append(key.KeyChar);
    }

    //one numbered option row, plus the recommended tail when the flow asked for one. lowercase, two spaces, dim, and on every row, cursor or not
    private static PaintedRow Option(GlyphSet g, ChoiceOption o, int index, int cursor,
        bool unnumbered = false)
    {
        //no digit when the screen has no digit keys, a number beside a row promises that typing it picks the row
        var text = unnumbered ? o.Label : $"{index + 1}. {o.Label}";
        List<Run> runs = index == cursor
            ? [new Run(g.Prompt + " ", RunInk.Accent), new Run(text, RunInk.Bright)]
            : [new Run("  " + text, RunInk.Plain)];

        //recommended is a claim gatto stands behind, so a picker that only marks the current row gives its own word
        if (o.Recommended) runs.Add(new Run("  " + (o.MarkWord ?? "recommended"), RunInk.Dim));
        return new PaintedRow(runs);
    }

    //the terminal screen renders like any other, and the caller prints its rows to scrollback once the alt screen is restored. otherwise they vanish with the buffer
    public void End(WizardScreen.Terminal t) =>
        Paint((w, _) => new Screen(t.Strip, Region.List, null,
            [
                .. Rows(t.Rows, w),
                //draw NextStep after the rows, with a blank between only when rows are above. on a leave that wrote nothing the line stands alone
                .. t.NextStep is null ? []
                    : t.Rows.Count > 0 ? Rows([new WizardRow(""), t.NextStep], w) : Rows([t.NextStep], w),
            ],
            null, []));

    //the next key, or null when a watching screen's thing arrived first. it waits one interval, hands back for a repaint, and asks the watch first
    private (bool Arrived, ConsoleKeyInfo? Key) NextKey(WizardScreen.Choice c, Func<bool>? watch)
    {
        var isWatch = c.Watching && watch is not null;
        TimeSpan budget;
        if (isWatch)
        {
            if (watch!()) return (true, null);

            //wait for the shorter of the watch interval and the armed chord, so a press after the window stops instead of re-arming
            budget = _chord.RemainingMs(_nowMs()) is { } armed
                ? ArmWait(Math.Min(Watch.Interval.TotalMilliseconds, armed))
                : Watch.Interval;
        }
        else
        {
            //an armed chord expires by time, so wait at most its window or the hint stays drawn until some other key arrives
            if (_chord.RemainingMs(_nowMs()) is not { } left)
                return KeyOrResize() ? (false, _keys.ReadKey()) : (false, null);
            budget = ArmWait(left);
        }

        var clock = _clock?.Invoke(isWatch) ?? new ConsolePollClock(_keys);
        return clock.WaitForKey(budget) ? (false, _keys.ReadKey()) : (false, null);
    }

    //true when a key is waiting, false when the window changed size first, so the caller repaints without a keystroke. only surfaces that report resize poll here
    private bool KeyOrResize()
    {
        if (!_surface.ReportsResize) return true;
        var poll = _clock?.Invoke(false) ?? new ConsolePollClock(_keys);
        while (!poll.WaitForKey(ResizePoll))
            if (Width != _paintedWidth || _surface.Height != _paintedHeight) return false;
        return true;
    }

    //how often a screen waiting for a key checks the window size
    private static readonly TimeSpan ResizePoll = TimeSpan.FromMilliseconds(100);

    //never wait zero: a zero budget makes the poll clock return at once and the caller repaints in a burst
    private static TimeSpan ArmWait(double remainingMs) =>
        TimeSpan.FromMilliseconds(Math.Max(1, remainingMs));

    //composition

    //does this keystroke press the key the option names, over the same strings the footer draws. the only spellings are Space and one character
    private static bool Presses(ConsoleKeyInfo key, string press) =>
        press == "Space"
            ? key.Key == ConsoleKey.Spacebar
            : press.Length == 1
              && char.ToLowerInvariant(key.KeyChar) == char.ToLowerInvariant(press[0]);

    //a watching screen's keys in option order, each under the key it names and the last under Esc
    private static IReadOnlyList<FooterKey> WatchKeys(WizardScreen.Choice c)
    {
        var keys = new List<FooterKey>(c.Options.Count);
        for (var i = 0; i < c.Options.Count; i++)
        {
            var o = c.Options[i];
            var verb = o.Label;
            keys.Add(new FooterKey(i == c.Options.Count - 1 ? "Esc" : o.Press!, verb));
        }
        return keys;
    }

    //the footer's keys, composed from the options on a keys-only screen so the words and the deeds are one fact. the label is the flow's own copy, drawn verbatim
    private static IReadOnlyList<FooterKey> KeysFor(GlyphSet g, WizardScreen.Choice c, FocusRing ring,
        int width)
    {
        //the shelf names its own keys, the arrows and m, and its Enter says next since picking a model is the step's answer
        if (c.Shelf is { } keysShelf)
            return Shelf.Keys(ring, keysShelf.Source,
                //with no list in the ring, Esc words as back when the flow has one. b is a letter in the field, so it is not offered there
                c.AllowBack && !ring.Has(Region.List) ? "back" : "leave",
                //one predicate for the folder door, read by the footer, the key arm and the row painter, so they cannot disagree
                ShelfControls.OffersD(c), g, ShelfControls.OffersLift(c), c.AllowBack && ring.Has(Region.List),
                //with no list a door takes every letter, so m is typed there instead of answered
                sourceKey: ring.Has(Region.List) || c.Door is null);

        //a no-default screen words Enter as confirm, since with no cursor yet the key answers nothing
        var enter = c.NoDefault
            ? new FooterKey("Enter", "confirm")
            : new FooterKey("Enter", c.Options.Count == 1 ? "next" : "choose");   //one option is the next step and more are a choice, so a screen that gains an option changes its verb

        //an unnumbered list moves with the arrows, so the footer names them, and a no-default screen borrows the row to word them choose
        var move = c.NoDefault
            ? (IReadOnlyList<FooterKey>)[new FooterKey(g.ArrowsKey, "choose")]
            : c.Unnumbered
                ? [new FooterKey(g.ArrowsKey, "move")]
                : [];

        //the Esc verb is leave on a numbered screen, unless an option declares that Esc answers it. it comes from the same field the key branch reads
        if (!c.KeysOnly)
        {
            var escOption = EscAnswer(c);
            var escVerb = escOption?.EscVerb ?? "leave";
            //with no back key the b key answers what Esc answers, so the two share one label. that only holds while the keys are on the list
            var escKey = escOption is not null && !c.AllowBack && ring.EscVerb(escVerb) == escVerb
                ? "b / Esc" : "Esc";
            //every screen before a write offers the b back key, read off AllowBack
            IReadOnlyList<FooterKey> back = c.AllowBack ? [new("b", "back")] : [];
            //the m key appears where the screen offers the source switch, the flag the key arm reads
            IReadOnlyList<FooterKey> source = c.SwitchesSource ? [new("m", "local")] : [];
            return ring.HasSecondArea
                ? [new("Tab", "area"), .. move, enter, .. source, .. back, new(escKey, ring.EscVerb(escVerb))]   //the Tab key leads, read off the ring so the row and the ring agree on a second area
                : [.. move, enter, .. source, .. back, new(escKey, ring.EscVerb(escVerb))];
        }

        //a watching screen answers no Enter, and each key is drawn with the option's own label, so a verb can't outlive the deed behind it
        if (c.Watching)
        {
            //the last option is Esc's and every option ahead of it names a key the loop answers. that is what keeps an unanswerable verb off the footer
            if (c.Options.Count == 0)
                throw new InvalidOperationException(
                    $"a watching keys-only screen needs at least Esc's option; '{c.Key}' has none");

            //an option ahead of Esc's must name its Press, or the row shows a verb with no key beside it
            for (var i = 0; i < c.Options.Count - 1; i++)
            {
                if (c.Options[i].Press is not { Length: > 0 } press)
                    throw new InvalidOperationException(
                        $"every option before Esc's on a watching screen names its key; "
                        + $"'{c.Key}' option '{c.Options[i].Key}' has no Press");
                //a press name must be one Presses recognises, refused here so the footer never draws a key no press can answer
                if (press != "Space" && press.Length != 1)
                    throw new InvalidOperationException(
                        $"a watching screen's key is 'Space' or one character; "
                        + $"'{c.Key}' option '{c.Options[i].Key}' names '{press}'");
            }

            //the long row wherever it fits, with no progressive fold, since the corpus and the product both draw two shapes
            return WatchKeys(c);
        }

        //a keys-only screen has exactly two options, Enter for the first and Esc for the second, since a third would be unreachable
        if (c.Options.Count != 2)
            throw new InvalidOperationException(
                $"a KeysOnly screen has exactly two options (Enter and Esc); '{c.Key}' has {c.Options.Count}");

        //a keys-only screen can hold a strip stop, so its footer is read off the ring. the Esc word there is back while the keys are on the strip
        return ring.HasSecondArea
            ? [new("Tab", "area"), new("Enter", c.Options[0].Label),
               new("Esc", ring.EscVerb(c.Options[1].Label))]
            : [new("Enter", c.Options[0].Label), new("Esc", c.Options[1].Label)];
    }

    //body rows wrapped at the live width, in the tone the flow set. this is the one place RowTone meets RunInk

    //the fact rows inked: label dim, value in its own tone, GB accent. the label splits at MachineFacts.Column, so a value that starts with a space keeps its ink
    private IReadOnlyList<PaintedRow> MachineRows(MachineView m, int width) =>
    [
        //fact rows take the frame margin, since DeWidow is gated on it. without it a row keeps the full width and orphans its last word
        .. WizardRows.Toned(MachineFacts.Rows(m.Hardware, m.Names, width), width, WizardRows.FrameMargin,
            _glyphs).Select(r =>
        {
            var lead = 2 + MachineFacts.Column;
            if (r.Text.Length <= lead) return PaintedRow.Of(r.Text, RunInk.Dim);

            var tail = r.Text[lead..];
            var value = Marked(tail, SpansOf(r, tail), RunInk.Dim);
            return new PaintedRow([new Run(r.Text[..lead], RunInk.Dim), .. value.Runs]);
        })
    ];

    //a row's right-aligned note, as runs, since the row and the tail take different inks. when it does not fit it is dropped
    private static PaintedRow Tailed(PaintedRow row, string? tail, int width)
    {
        if (tail is not { Length: > 0 }) return row;
        //the tail is right-aligned content, so the gap is measured inside the margin
        var gap = Margins.Inside(width) - Gatto.Terminal.UnicodeWidth.Of(row.Text)
            - Gatto.Terminal.UnicodeWidth.Of(tail);
        return gap > 0
            ? new PaintedRow([.. row.Runs, new Run(new string(' ', gap), RunInk.Dim), new Run(tail, RunInk.Dim)])
            : row;
    }

    //rows at the full width with no right margin, these facts fold rather than wrap and a margin would only break a fitted row
    private IReadOnlyList<PaintedRow> RowsUnwrapped(int width, IReadOnlyList<WizardRow>? rows) =>
    [
        .. WizardRows.Toned(rows, width, glyphs: _glyphs).Select(r => Tailed(Marked(r.Text, SpansOf(r, r.Text),
            r.Tone == RowTone.Aside ? RunInk.Dim : r.Tone == RowTone.Subject ? RunInk.Accent : RunInk.Plain),
            r.Tail, width))
    ];

    //the body decides a row's fit class: optional stays optional through wrapping, and a blank row is a paragraph break
    private static PaintedRow Classify(WizardRow src, PaintedRow painted) =>
        src.Optional ? painted with { Fit = RowFit.Optional }
        : painted.Text.Trim().Length == 0 ? painted with { Fit = RowFit.Paragraph }
        : painted;

    private IReadOnlyList<PaintedRow> Rows(IReadOnlyList<WizardRow>? rows, int width) =>
    [
        .. WizardRows.Toned(rows, width, WizardRows.FrameMargin, _glyphs).Select(r =>
        {
            var ink = r.Tone switch
            {
                RowTone.Aside => RunInk.Dim,
                RowTone.Subject => RunInk.Accent,
                _ => RunInk.Plain,
            };
            //the status mark is its own run, so a coloured glyph can open a sentence the rest of the row paints plain
            if (r.Glyph is not { } g)
                return Classify(r, Tailed(Marked(r.Text, SpansOf(r, r.Text), ink), r.Tail, width));
            var mark = Gatto.Cli.Setup.Glyphs.Of(g, _glyphs);
            //find the mark's own position in the text, since Toned folds it in behind the row's indent. a fixed prefix would draw it twice
            var at = r.Text.IndexOf(mark, StringComparison.Ordinal);
            if (at < 0)
                return Classify(r, Tailed(Marked(r.Text, SpansOf(r, r.Text), ink), r.Tail, width));
            var said = r.Text[(at + mark.Length)..];
            return Classify(r, Tailed(new PaintedRow([
                .. at > 0 ? (IReadOnlyList<Run>)[new Run(r.Text[..at])] : [],
                new Run(mark, GlyphInk(g)),
                .. Marked(said, SpansOf(r, said), ink).Runs,
            ]), r.Tail, width));
        })
    ];

    //the ink a status mark draws in, the frame's half of RowGlyph, since the plain face wants the character and has no ink
    internal static RunInk GlyphInk(RowGlyph glyph) => glyph switch
    {
        RowGlyph.Good => RunInk.Ok,
        RowGlyph.Bad => RunInk.Warn,
        //the Warn ink matches Bad on purpose, and the mark is what says nothing was measured or the attempt failed
        RowGlyph.Warn => RunInk.Warn,
        RowGlyph.NotRun => RunInk.Dim,
        _ => throw new ArgumentOutOfRangeException(nameof(glyph), glyph, "no ink is defined for this row glyph"),
    };

    //one row split around the spans it asked to mark, first occurrence only and in the given order, with a missing span skipped
    private static PaintedRow Highlighted(string text, IReadOnlyList<string>? spans, RunInk baseInk) =>
        Marked(text, [.. (spans ?? []).Select(s => (s, RunInk.Accent, false))], baseInk);

    //the row's spans in the order they sit in the text, since the split runs left to right

    //the three span inks: accent for what the user acts on, plain for what is named, bright for a key. don't add a fourth
    private static IReadOnlyList<(string Span, RunInk Ink, bool Word)> SpansOf(WizardRow row, string text) =>
    [
        .. (row.Highlight ?? []).Select(s => (Span: s, Ink: RunInk.Accent, Word: false))
            .Concat((row.Lift ?? []).Select(s => (Span: s, Ink: RunInk.Plain, Word: false)))
            //a key inside a sentence takes the same bright ink the footer's keys use
            .Concat((row.Keys ?? []).Select(s => (Span: s, Ink: RunInk.Bright, Word: true)))
            .Where(p => p.Span.Length > 0 && At(text, p.Span, p.Word) >= 0)
            .OrderBy(p => At(text, p.Span, p.Word))
    ];

    //where a span starts, or -1. a whole-word match needs a non-alphanumeric on both sides, so a one-letter key stays out of a word
    private static int At(string text, string span, bool word)
    {
        if (!word) return text.IndexOf(span, StringComparison.Ordinal);

        for (var i = text.IndexOf(span, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(span, i + 1, StringComparison.Ordinal))
        {
            var before = i == 0 || !char.IsLetterOrDigit(text[i - 1]);
            var after = i + span.Length >= text.Length || !char.IsLetterOrDigit(text[i + span.Length]);
            if (before && after) return i;
        }
        return -1;
    }

    private static PaintedRow Marked(
        string text, IReadOnlyList<(string Span, RunInk Ink, bool Word)> spans, RunInk baseInk)
    {
        if (spans.Count == 0) return PaintedRow.Of(text, baseInk);

        var runs = new List<Run>();
        var rest = text;
        foreach (var (span, ink, word) in spans)
        {
            if (span.Length == 0) continue;
            //resolve the span the same way the sort did, or the row would paint it where the sort did not put it
            var at = At(rest, span, word);
            if (at < 0) continue;
            if (at > 0) runs.Add(new Run(rest[..at], baseInk));
            runs.Add(new Run(span, ink));
            rest = rest[(at + span.Length)..];
        }
        if (rest.Length > 0) runs.Add(new Run(rest, baseInk));
        return runs.Count == 0 ? PaintedRow.Of(text, baseInk) : new PaintedRow(runs);
    }

    //the files the pane lists for the row under the cursor, or none
    private static IReadOnlyList<PaneFile> Files(WizardScreen.Choice c, int cursor) =>
        c.Shelf is { Facts: { } facts } && cursor >= 0 && cursor < facts.Count
            ? facts[cursor].Files ?? []
            : [];

    //true when the shelf's facts sit under the table at this width. ask Shelf.PaneWidth, so the answer can't drift from what draws
    private bool Folded(WizardScreen.Choice c) =>
        c.Shelf is { } sv && Shelf.PaneWidth(sv, Width, _glyphs) == 0;

    private static IReadOnlyList<PaneBuild> Builds(WizardScreen.Choice c, int cursor) =>
        c.Shelf is { Facts: { } facts } && cursor >= 0 && cursor < facts.Count
            ? facts[cursor].Builds ?? []
            : [];

    //where the build cursor sits, with -1 resolved to the build gatto would take
    private static int Build(WizardScreen.Choice c, int cursor, ShelfCursors at)
    {
        var builds = Builds(c, cursor);
        if (at.Build >= 0 && at.Build < builds.Count) return at.Build;
        var picked = builds.ToList().FindIndex(b => b.Picked);
        return picked >= 0 ? picked : 0;
    }

    //the next index the arrows may rest on, skipping any option the face does not paint. the cursor can never rest on a row the user cannot see
    private static int? Step(WizardScreen.Choice c, int cursor, int delta)
    {
        for (var i = cursor + delta; i >= 0 && i < c.Options.Count; i += delta)
            if (!ShelfControls.IsFolderDoor(c, i)) return i;
        return null;
    }

    //the ring after the cursor moved, rebuilt since a row with builds has one more Tab stop. it always opens on the list, the only reachable state
    private FocusRing RingFor(WizardScreen.Choice c, int cursor) =>
        c.Shelf is { } sv
            ? new FocusRing(Shelf.Regions(sv, Width, cursor, c.Door is not null), Shelf.Opening(sv))
            : null!;

    //the row window recomputed from the painter's allowance. an allowance of 0 means unknown, and the cursor is never windowed out
    private static ShelfView Windowed(ShelfView v, int cursor, int allowance, int outside)
    {
        if (allowance <= 0) return v;
        //at least 1, an allowance of 0 means an unknown height
        var window = Math.Max(Shelf.RowBudget(Math.Max(1, allowance - outside)), cursor + 1);
        if (v.Rows.Count <= window) return v;
        return v with
        {
            Rows = [.. v.Rows.Take(window)],
            Facts = v.Facts is { } f ? [.. f.Take(window)] : null,
            MoreBelow = v.MoreBelow + (v.Rows.Count - window),
        };
    }

    //the flow's rows past the ones the shelf drew, painted so the cursor can never rest on an invisible row. they draw no numbers
    private static IReadOnlyList<PaintedRow> Escapes(GlyphSet g, WizardScreen.Choice c, ShelfView shelf,
        int cursor)
    {
        if (c.Options.Count <= shelf.Rows.Count) return [];
        //a shelf that fetched nothing keeps its keys in the door, so no key reaches a row below it. the options stay in c.Options
        if (shelf.NothingFetched) return [];
        //one affordance may not sit in two homes, so the folder door keeps its key and gives up its row. the option stays in c.Options, which d answers
        var door = ShelfControls.OffersFolderDoor(c);
        var rows = new List<PaintedRow> { PaintedRow.Of("") };
        for (var i = shelf.Rows.Count; i < c.Options.Count; i++)
            if (!door || !string.Equals(c.Options[i].Key, SetupFlow.Elsewhere, StringComparison.Ordinal))
                rows.Add(i == cursor
                    ? new PaintedRow([new Run(g.Prompt + " ", RunInk.Accent),
                        new Run(c.Options[i].Label, RunInk.Bright)])
                    : PaintedRow.Of("  " + c.Options[i].Label));
        //only the blank is left when the door was the only row, so return no rows at all
        return rows.Count > 1 ? rows : [];
    }

    //the screen's Tab ring: the strip is a stop only where a done section sits behind it. a watching keys-only screen is the pair that has no back
    private IReadOnlyList<Region> RegionsFor(WizardScreen.Choice c) =>
        //the shelf declares its own ring, which depends on the width, since a folded frame has no pane for Tab to stop at
        c.Shelf is { } shelf ? Shelf.Regions(shelf, Width, c.NoDefault ? -1 : 0, c.Door is not null)
        : [
            //the strip is never a Tab stop here, so the ring holds only the list and the door's search region
            Region.List,
            .. c.Door is not null ? (IReadOnlyList<Region>)[Region.Search] : [],
          ];

    //where the cursors are on a shelf: one per zone, built with Start so the pane pick never lands on file zero
    internal readonly record struct ShelfCursors(int Chip, int File, int Build, string? PickedQuant)
    {
        public static ShelfCursors Start => new(0, -1, -1, null);

        //a new model brings a new file list and builds, so both pane cursors and the quant pick reset
        public ShelfCursors OnANewRow() => this with { File = -1, Build = -1, PickedQuant = null };
    }

    //how long the most recently resolved watch ran, kept on the face since only the loop can time it
    private long _watchedMs;

    private Screen ScreenFor(WizardScreen.Choice c, FocusRing ring, int cursor, FetchTick? t,
        ShelfCursors at, int width, int allowance, long? watching = null, CheckTick? k = null,
        int? stripAt = null) => new(
        StripAt: stripAt,
        Sections: c.Strip,
        Focused: ring.Current,
        Title: c.Question,
        //the prose rows come first, since the options alone leave a screen with its questions and none of its words
        Body: [
            .. BodyAbove(c, t, k, width),
            //the shelf replaces the option rows, since the models are the options, and its escape rows follow as ordinary options
            .. c.Shelf is { } sv
                ? [.. Shelf.Body(Windowed(sv, cursor, allowance, Outside(c, t, k, width, sv, cursor)), cursor, at.Chip, at.File,
                       ring.Current, width, at.Build, _glyphs),
                   //the escape rows index the full shelf, so the window must not move their option index. a cursor is drawn only while the ring holds a list
                   .. Escapes(_glyphs, c, sv, ring.Has(Region.List) ? cursor : -1)]
                : c.KeysOnly ? [] : c.Options.Select((o, i) => Option(_glyphs, o, i, cursor, c.Unnumbered)),
            //an option list never sits on the footer rule, so a screen with options but no door or shelf adds the blank itself
            .. !c.KeysOnly && c.Door is null && c.Shelf is null && c.Options.Count > 0
                ? [PaintedRow.Of("")]
                : (IReadOnlyList<PaintedRow>)[],
        ],
        //the shelf's door names the key that opens it and shows what was typed. the face keeps that as view data, since only Ask holds a draft
        Door: c.Door is { } d
            ? new DoorRow(d, c.Draft ?? "",
                Hint: c.Shelf is not { } hintShelf ? DoorHints.AnywhereOf(_glyphs)
                    : hintShelf.Source == ShelfSource.Local ? DoorHints.SearchLocal
                    : DoorHints.Search,
                //a door with no list beside it is one the user types in, and the local shelf's empty state says so
                Focused: c.Shelf is not { } f ? null
                    : f.Rows.Count == 0 && f.Source == ShelfSource.Local ? DoorHints.FocusedLookOf(_glyphs)
                    : DoorHints.FocusedOf(_glyphs))
            : null,
        Keys: KeysFor(_glyphs, c, ring, width),
        //what sits right of the keys: a screen's own sentence, which is drawn whole or not at all, or the shelf's marks
        Legend: c.Legend is { Length: > 0 } said ? new Legend(LegendKind.Sentence, said)
            //a shelf that fetched nothing draws no legend either, since there are no marks to explain, while an empty search keeps one
            : c.Shelf is { NothingFetched: false } lv ? FitMarks.LegendFor(lv.Shape, _glyphs) : null,
        //the armed sentence belongs to the screen: leaving setup costs nothing, and a stopped fetch prices what has landed
        Armed: _chord.ArmedAt(_nowMs()) != Chord.Quit ? null
            //a screen's own declared price comes first in the ladder, so a screen that sets one is never silently overruled
            : c.ArmedCost is { Length: > 0 } declared ? declared
            //a paused screen has a live tick and no live fetch, so it prices Esc with CostOfKept. the live arm would say it stops a fetch already stopped
            : c.Paused && t is { } held ? Widget.CostOfKept(held)
            : t is { } armed ? Widget.Cost(armed)
            //a screen whose fetch already stopped prices Esc off what it kept, so the armed row cannot deny the kept figures above it
            : c.Kept is { } kept ? Widget.CostOfKept(kept)
            : "Esc again to leave",
        Hero: c.Hero is { Count: > 0 } cat ? [.. cat.Select(l => PaintedRow.Of(l, RunInk.Accent))] : null,
        //the purr in its four states: a live tick wins, a bare watch times on the face's clock, then the two settled forms
        Working: t is { } working ? Widget.Purr(working, _glyphs)
            //the frame set is the screen's, since it says how long the wait can run
            : watching is { } waiting
                ? Widget.Purr(waiting, c.FullPurr ? _fullPurr : PurrFrames.Short, _glyphs)
            : c.PurredMs is { } purred ? Widget.Settled(purred, _glyphs)
            : c.PurredSinceWatch ? Widget.Settled(_watchedMs, _glyphs)
            : null);

    //the body rows above the shelf or the options, composed in one place so the row window can count them
    private IReadOnlyList<PaintedRow> BodyAbove(WizardScreen.Choice c, FetchTick? t, CheckTick? k, int width) =>
    [
        .. Rows(c.BodyRows, width),
        //the machine's fact rows are composed here, since they fold at the live width and the flow has none
        .. c.Machine is { } m
            //the fact rows take no right margin, since they fold at the width and wrapping one would break the label column
            ? [PaintedRow.Of(""), .. MachineRows(m, width), PaintedRow.Of("")]
            : (IReadOnlyList<PaintedRow>)[],
        //the engine's fetch facts, unwrapped like the machine block. a tick with the screen decides the live composer
        .. c.Engine is { } ev
            ? [PaintedRow.Of(""), .. RowsUnwrapped(width, t is { } live
                ? Widget.Rows(ev, live, width, glyphs: _glyphs)
                : EngineFetchView.Rows(ev, width, _glyphs))]
            : (IReadOnlyList<PaintedRow>)[],
        //the model fetch's block, composed here for the same reason as the engine block. a tick decides whether the live composer draws it
        .. c.Model is { } mv
            //the screen says whether the fetch is paused, since a paused fetch's last tick looks exactly like a live one
            ? [PaintedRow.Of(""), .. RowsUnwrapped(width, t is { } running
                ? Widget.Rows(mv, running, width, _glyphs, c.Paused)
                : ModelFetchView.Rows(mv, width, _glyphs))]
            : (IReadOnlyList<PaintedRow>)[],
        //the check's live rows, unwrapped like the other fact blocks, and its closing blank is drawn even before the first moment arrives
        .. c.Checking
            //a null tick is a state, since CheckRows.Of has words for the seconds before the server answers
            ? [.. RowsUnwrapped(width, CheckRows.Of(k, _glyphs)),
               //the block's prose is wrapped through Rows, since a sentence takes the two-cell margin the fact rows do not
               .. Rows(CheckRows.Aside(k), width) is { Count: > 0 } aside
                   ? (IReadOnlyList<PaintedRow>)[PaintedRow.Of(""), .. aside]
                   : [],
               PaintedRow.Of("")]
            : (IReadOnlyList<PaintedRow>)[],
        //a keys-only screen ends at its prose, since its answers are already in the footer
        .. c.KeysOnly ? [] : c.BodyRows is { Count: > 0 } ? new[] { PaintedRow.Of("") } : [],
    ];

    //the shelf frame's non-model rows, the body above and the escapes below, which the row window pays for with model rows
    private int Outside(WizardScreen.Choice c, FetchTick? t, CheckTick? k, int width, ShelfView sv, int cursor) =>
        BodyAbove(c, t, k, width).Count + Escapes(_glyphs, c, sv, cursor).Count();
}
