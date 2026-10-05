namespace Gatto.Terminal;

//the alt buffer lifecycle: Enter switches once and Restore puts the main screen back on every exit path, both idempotent, every write through ITermSurface
public sealed class AltScreen(ITermSurface surface, ITerminalTitle? title = null)
{
    private bool _entered;
    private bool _restored;
    private bool _final;   //once the folder title is applied, every later Retitle (a /role swap) is ignored

    //null when this session has no tab worth titling (a redirected stream, or a console that refuses). the alt screen defaults the title, it knows every leave path
    private readonly ITerminalTitle? _title = title ?? TerminalTitle.Deed(Console.IsOutputRedirected);

    //true keeps the wheel from arriving as arrow keys while the alt buffer is up, for a session with no mouse capture. set before Enter
    public bool QuietWheel { get; set; }

    //true between a successful Enter and the first Restore
    public bool Active => _entered && !_restored;

    //switch to the alt buffer once and say so on the tab (a second call writes nothing). only the wizard hides the cursor, the REPL's compositor sets it every paint
    public void Enter(string? windowTitle = null, bool hideCursor = false)
    {
        if (_entered) return;
        _entered = true;
        surface.Write(Ansi.AltScreenEnter + (QuietWheel ? Ansi.AlternateScrollOff : "") + (hideCursor ? Ansi.HideCursor : ""));
        if (windowTitle is not null) _title?.Apply(windowTitle);
    }

    //change the tab mid-session, called when the role changes. a no-op outside the alt screen, so a plain-mode session never touches a title it never set
    public void Retitle(string windowTitle)
    {
        if (Active && !_final) _title?.Apply(windowTitle);
    }

    //applies the title once and latches the tab, so every later Retitle is ignored. a no-op outside the alt screen, like Retitle
    public void SetFinalTitle(string windowTitle)
    {
        if (!Active || _final) return;
        _final = true;
        _title?.Apply(windowTitle);
    }

    //dismiss the alt buffer and restore the main screen, showing the cursor in case a compositor left it hidden. idempotent, and a no-op if Enter never ran
    public void Restore()
    {
        if (!_entered || _restored) return;
        _restored = true;
        surface.Write(Ansi.AltScreenExit + Ansi.ShowCursor + (QuietWheel ? Ansi.AlternateScrollOn : ""));
        //the tab goes back with the buffer, in the same guarded breath, so no abnormal exit leaves a stranded title behind
        _title?.Restore();
    }

    //wire Restore to ProcessExit and UnhandledException before Enter, so a crash during entry is covered. a hard kill or a closed window stays out of reach
    public void RegisterExitHooks()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Restore();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => Restore();
    }
}
