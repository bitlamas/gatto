namespace Gatto.Cli;

//one line of honest motion, rewritten in place and erased before what comes next. a tick never commits a line, and the erase counts display columns
internal sealed class TickLine(
    TextWriter output,
    bool rich,
    Func<string, string>? paint = null,
    Func<int>? windowWidth = null,
    bool blankBeforeFirst = false) : IDisposable
{
    //the live line's width as displayed, counted in columns rather than painted characters
    private int _live;

    //a caller that already framed its own header leaves this false. a tick that can appear right under the typed command needs one blank row first
    private bool _leadPending = blankBeforeFirst;

    private void Lead()
    {
        if (!_leadPending) return;
        _leadPending = false;
        output.WriteLine();
    }

    //whether motion is actually visible. callers branch their plain-mode policy on it, since the type never invents one
    public bool Rich => rich;

    //replace the live line. a no-op when Rich is false
    public void Live(string plain)
    {
        if (!rich) return;
        Lead();
        Erase();

        //clamp to the window so the line can't wrap into two rows. the carriage return only reaches the last row, so the erase would leave the first one behind
        var width = Width();
        if (width > 1 && plain.Length > width - 1) plain = plain[..(width - 1)];

        _live = plain.Length;
        output.Write(paint is null ? plain : paint(plain));
        output.Flush();
    }

    //a row whose runs have their own inks: the width is measured on the plain text and the cut keeps the escapes whole
    public void LivePainted(string painted, Gatto.Terminal.GlyphSet glyphs)
    {
        if (!rich) return;
        Lead();
        Erase();

        var width = Width();
        if (width > 1) painted = Gatto.Terminal.TermText.TruncateCells(painted, width - 1, glyphs);
        _live = Gatto.Terminal.UnicodeWidth.Of(Gatto.Terminal.TermText.StripAnsiForWidth(painted));
        output.Write(painted);
        output.Flush();
    }

    //commit a line the user keeps, taking the live line down first so the two never collide on one row
    public void Note(string text)
    {
        Lead();
        Erase();
        output.WriteLine(text);
        Noted = true;
    }

    //whether Note committed a line, so a caller can close it with one empty line and write nothing when it said nothing
    public bool Noted { get; private set; }

    //take the live line down. it is idempotent, so a caller can always call it before writing anything
    public void Finish() => Erase();

    //so an exception between the last tick and the next output can't leave half a progress line above an error
    public void Dispose() => Erase();

    private void Erase()
    {
        if (_live == 0) return;
        output.Write("\r" + new string(' ', _live) + "\r");
        output.Flush();
        _live = 0;
    }

    //console width, or 0 when there is no console to ask, because a host without a window must not throw out of a progress callback
    private int Width()
    {
        if (windowWidth is not null) return windowWidth();
        try { return Console.WindowWidth; }
        catch (Exception) { return 0; }
    }
}
