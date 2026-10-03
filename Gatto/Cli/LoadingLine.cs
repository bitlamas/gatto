using Gatto.Terminal;

namespace Gatto.Cli;

//the live line when the host repaints, one committed line per rung when it cannot
internal sealed class LoadingLine(bool rich, Action<string> live, Action<string> note, GlyphSet glyphs, Theme? theme)
{
    private int _frame;
    private string? _lastWords;

    public void Draw(TimeSpan elapsed, string modelId, string? size)
    {
        if (rich)
        {
            live(LoadingRow.Render(elapsed, modelId, size, glyphs, theme, _frame++));
            return;
        }

        //a pipe cannot repaint, so one line is committed each time the words change
        var words = LoadingRow.Words(elapsed, modelId, size, glyphs);
        if (words == _lastWords) return;
        _lastWords = words;
        note(words);
    }

    //wrap a tick line as a loading row (the tick's paint arm draws the live row, its note arm commits the plain one)
    internal static LoadingLine Over(TickLine line, GlyphSet glyphs, Theme? theme) =>
        new(line.Rich, painted => line.LivePainted(painted, glyphs), line.Note, glyphs, theme);
}
