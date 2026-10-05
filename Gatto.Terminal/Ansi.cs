namespace Gatto.Terminal;

public readonly record struct RgbColor(byte R, byte G, byte B, byte Index256);

public static class Ansi
{
    public const string Reset = "\x1b[0m";
    public const string Bold = "\x1b[1m";
    public const string Italic = "\x1b[3m";
    public const string Strike = "\x1b[9m";
    public const string Underline = "\x1b[4m";
    public const string ClearLine = "\x1b[2K";
    public const string ClearToEol = "\x1b[K";
    public const string ClearBelow = "\x1b[0J";
    //the cursor to row 1, column 1, so the wizard repaints in place (home then clear below) instead of appending to the transcript
    public const string CursorHome = "\x1b[H";

    //the synchronized update mode: output between Start and End is buffered and presented in one frame, and a terminal without the mode ignores it
    public const string SyncStart = "\x1b[?2026h";
    public const string SyncEnd   = "\x1b[?2026l";

    //the alternate screen buffer: gatto owns the viewport, its scroll, and draws with absolute Cup. entering clears and switches, exiting restores the main screen
    public const string AltScreenEnter = "\x1b[?1049h";
    public const string AltScreenExit  = "\x1b[?1049l";
    //a terminal's alternate scroll mode turns the wheel into arrow keys on the alt buffer. off for a session that reads no mouse, back on as it leaves
    public const string AlternateScrollOff = "\x1b[?1007l";
    public const string AlternateScrollOn  = "\x1b[?1007h";
    public const string HideCursor = "\x1b[?25l";
    public const string ShowCursor = "\x1b[?25h";

    public static string LinkOpen(string url) => "\x1b]8;;" + url + "\x1b\\";
    public const string LinkClose = "\x1b]8;;\x1b\\";

    public static string Up(int n) => n <= 0 ? "" : $"\x1b[{n}A";

    //cursor down n rows, the terminal clamps at the screen edge, and n of zero or less sends nothing
    public static string Down(int n) => n <= 0 ? "" : $"\x1b[{n}B";

    public static string Col(int n) => $"\x1b[{n}G";

    //absolute cursor position, row and column counted from one. the alt-screen compositor draws the frame top-to-bottom and pins the composer to the bottom row
    public static string Cup(int row, int col) => $"\x1b[{row};{col}H";

    //an escape byte followed straight by a hex digit needs the fixed four-digit \u form. hex escapes are greedy in C#, so escape-then-7 collapses into U+01B7

    public static string Fg(RgbColor c, bool trueColor) =>
        trueColor ? $"\x1b[38;2;{c.R};{c.G};{c.B}m" : $"\x1b[38;5;{c.Index256}m";
    public static string Bg(RgbColor c, bool trueColor) =>
        trueColor ? $"\x1b[48;2;{c.R};{c.G};{c.B}m" : $"\x1b[48;5;{c.Index256}m";
}
