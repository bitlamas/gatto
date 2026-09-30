namespace Gatto.Terminal;

//the window and tab title as a deed, so a test can drive it with no console. the first Apply saves the title it found, and Restore puts that one back
public interface ITerminalTitle
{
    void Apply(string title);
    void Restore();
}

//a title is a decoration, the getter and setter both throw on hosts that have none. wrap every call, a session must not die for it
public sealed class TerminalTitle : ITerminalTitle
{
    //two hieroglyphs written as escapes (no editor or pipe can mangle a surrogate pair). worst case on a stripped install is two boxes
    public const string Glyph = "𓏲𓎨";

    public static string For(string? role) =>
        string.IsNullOrWhiteSpace(role) || role.Trim() == "generalist"
            ? "gatto " + Glyph
            : "gatto " + Glyph + " " + role.Trim();

    //the tab names the folder it works in, glyph first. a drive root has no file name, so fall back to the trimmed root rather than an empty tab
    public static string Project(string cwd)
    {
        var trimmed = cwd.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var folder = Path.GetFileName(trimmed);
        return Glyph + " " + (folder.Length > 0 ? folder : trimmed);
    }

    //only outputRedirected decides whether there's a tab to title, keep it a pure function so a test can assert it directly
    public static ITerminalTitle? Deed(bool outputRedirected) => outputRedirected ? null : new TerminalTitle();

    private string? _saved;
    private bool _applied;

    public void Apply(string title)
    {
        //check IsWindows() at runtime instead of the attribute (the analyzer sees it too, so callers need no annotation)
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            if (!_applied) { _saved = Console.Title; _applied = true; }
            Console.Title = title;
        }
        catch
        {
            //a host that won't take a title is no reason to kill the session
        }
    }

    public void Restore()
    {
        if (!_applied) return;
        _applied = false;
        if (!OperatingSystem.IsWindows() || _saved is null) return;
        try { Console.Title = _saved; } catch { }
    }
}
