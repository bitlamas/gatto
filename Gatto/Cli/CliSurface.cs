using Gatto.Terminal;

namespace Gatto.Cli;

//the command layer's writer. the same calls give gatto's grammar on a terminal and plain text through a pipe, so no call site asks which one it has
internal sealed class CliSurface(TextWriter output, Theme? theme, Gatto.Terminal.GlyphSet? glyphs)
{
    //two spaces with one meaning: the row is subordinate to the row above it. the Under method is the only member that writes them
    internal const string Gutter = "  ";

    internal Theme? Theme => theme;

    public void Blank() => output.WriteLine();

    //the blank row that ends a command's frame, paired with the header that opens it. a command sits mid-scrollback, so both ends need the gap
    public void Close() => output.WriteLine();

    //a plain sentence at column zero, the layer's default voice (a command's body belongs to the command itself)
    public void Say(string text) => output.WriteLine(TermText.Sanitize(text));

    //a row that belongs under the row above it (a log tail under a failure line), and the only indent the layer writes
    public void Under(string text) => output.WriteLine(Gutter + TermText.Sanitize(text));

    //a row that replaces itself, so the caller ends its loop with Blank or the next write goes onto this row
    public void Live(string text) => output.Write("\r" + TermText.Sanitize(text));

    //the affirmative row: mark, subject in model ink, machinery in dim parentheses the form adds, both arguments sanitized (the subject is a server-reported id)
    public void Ok(string subject, string? machinery = null)
    {
        var row = Paint((glyphs ?? Gatto.Terminal.GlyphSet.Unicode).Ok, Theme.Ok) + " "
            + Paint(TermText.Sanitize(subject), Style(CliInk.Model));
        if (machinery is { Length: > 0 })
            row += " " + Paint("(" + TermText.Sanitize(machinery) + ")", Theme.Dim);
        output.WriteLine(row);
    }

    //a command a person can type takes one ink and is never quoted or backticked (a terminal renders no markup), the run is un-painted
    public static (string Text, CliInk Ink) Command(string name) => (name, CliInk.Command);

    //a row of differently-inked runs, sanitized once each, for the lines the fixed forms above do not fit.
    public void Row(params (string Text, CliInk Ink)[] parts) =>
        output.WriteLine(Compose(parts.Select(p => (p.Text, Style(p.Ink)))));

    //neighbouring runs of one colour are painted once, a reset between them costs bytes and can break a row mid-colour
    private string Compose(IEnumerable<(string Text, RgbColor Ink)> parts)
    {
        var row = new System.Text.StringBuilder();
        var run = new System.Text.StringBuilder();
        RgbColor? open = null;
        foreach (var (text, ink) in parts)
        {
            if (open is { } was && !was.Equals(ink)) { row.Append(Paint(run.ToString(), was)); run.Clear(); }
            open = ink;
            run.Append(TermText.Sanitize(text));
        }
        if (open is { } last) row.Append(Paint(run.ToString(), last));
        return row.ToString();
    }

    //the same row painted from the palette directly, one caller needs the removed and kept register. a second caller is a design decision
    public void Row(params (string Text, RgbColor Ink)[] parts) => output.WriteLine(Compose(parts));

    //sanitizing is the caller's here and deliberately not repeated. doing it twice would measure one string and paint another.
    private string Paint(string text, RgbColor style) => theme is null ? text : theme.Paint(text, style);

    internal static RgbColor Style(CliInk ink) => ink switch
    {
        CliInk.Accent => Theme.Accent,
        CliInk.Dim => Theme.Dim,
        CliInk.Model => Theme.CodeInlineFg,
        CliInk.Command => Theme.RoleCoder,
        //the Bright ink is the body colour (there is no Body), painting it keeps a row one sequence so an accent can't bleed in
        _ => Theme.Bright,
    };
}

//the inks are named for meaning, so a call site cannot pick a colour it cannot see. red and green stay out, that register belongs to the uninstall map
internal enum CliInk
{
    Plain,
    Accent,
    Dim,
    Model,
    Command,
}
