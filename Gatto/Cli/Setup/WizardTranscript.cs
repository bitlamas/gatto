using System.Text;
using Gatto.Repl;
using Gatto.Terminal;

namespace Gatto.Cli.Setup;

//what an answered wizard screen leaves behind: the answer below its question, a rule between segments, one row per answer with its count
internal sealed class WizardTranscript(Theme theme, Gatto.Terminal.GlyphSet? glyphs)
{
    private readonly Gatto.Terminal.GlyphSet _glyphs = glyphs ?? Gatto.Terminal.GlyphSet.Unicode;

    private const string Indent = "  ";

    //the segment being built: one question, each answer given to it, and how many times in a row. null means nothing to continue, the state Interrupt leaves
    private string? _question;
    private readonly List<(string Text, int Count, bool Navigation)> _answers = [];
    private int _rows;
    private int _width;

    //a back-step row is not an answer, so it draws dim like the body's skipped rows. the arrow points forward, the option said "←"

    //a function of the glyph set. this text is only drawn, so its wording has no identity and moving it changes only the drawing
    private static string WentBackOf(Gatto.Terminal.GlyphSet g) => $"{g.Right} went back";

    //something other than a committed pair was written, so the next tail appends and never reclaims. every other writer on SetupFace calls this
    public void Interrupt()
    {
        _question = null;
        _answers.Clear();
        _rows = 0;
    }

    //the empty tail: the widget erases its block and writes nothing. it must reset the transcript, or the next commit eats the rows that replaced them
    public EchoTail Skip()
    {
        Interrupt();
        return new EchoTail([]);
    }

    //compose the tail that replaces the last block, with the row count it reclaims
    public EchoTail Commit(EchoInput input)
    {
        var question = input.Question is null ? null : TermText.Sanitize(input.Question);
        //match on the option's identity, the drawn label changes with the glyph set. free text has no identity, so it falls back to the answer
        var navigation = string.Equals(input.Identity ?? input.Answer, SetupFace.BackOptionKey,
            StringComparison.Ordinal);
        var answer = navigation ? WentBackOf(_glyphs) : TermText.Sanitize(input.Answer);
        var width = input.Width;

        //a body block sits between this tail and the previous one, so the rows above it are not ours
        if (input.HasBody) Interrupt();

        //gated on the width, a resize between two presses changes how many rows the block takes. a count measured at the old width erases the wrong lines
        var continues = question is not null
            && question == _question
            && width == _width
            && _rows > 0;

        if (!continues)
        {
            _question = question;
            _answers.Clear();
            _answers.Add((answer, 1, navigation));
            _width = width;
            var fresh = Compose(width);
            _rows = fresh.Count;
            return new EchoTail(fresh);
        }

        //a question is asked once, so its answers stack beneath it and a new answer never prints it again
        if (_answers[^1].Text == answer) _answers[^1] = (answer, _answers[^1].Count + 1, navigation);
        else _answers.Add((answer, 1, navigation));

        var rows = Compose(width);
        var reclaim = _rows;
        _rows = rows.Count;
        return new EchoTail(rows, reclaim);
    }

    //the rendered segment: the question, every answer beneath it, then the divider. the question is the wizard's own sentence, so it reads white
    private List<string> Compose(int width)
    {
        var rows = new List<string>();
        if (_question is not null) rows.AddRange(Wrap(_question, width, s => s));

        //the (2x) only appears because a second commit came through here. a navigation row is dim and counts the same as an answer
        foreach (var (text, count, navigation) in _answers)
            rows.AddRange(Wrap(count > 1 ? $"{text} ({count}x)" : text, width,
                s => theme.Paint(s, navigation ? Theme.Dim : Theme.Accent)));

        rows.Add(Divider(width));
        return rows;
    }

    //the grey rule between segments, full width and never indented so it doesn't read as content. a width the surface won't report still draws a short rule
    private string Divider(int width) =>
        theme.Paint(new string('-', Math.Max(8, width > 0 ? width : 8)), Theme.Dim);

    //soft-wrap at the live width with the wizard's indent. a row wider than the terminal wraps on its own and throws the reclaim count off
    private static List<string> Wrap(string text, int width, Func<string, string> paint)
    {
        if (width <= 0) return [Indent + paint(text)];
        var budget = Math.Max(1, width - Indent.Length);
        var segs = SoftWrap.Wrap(text, budget, budget);
        if (segs.Count == 0) return [Indent + paint(text)];

        var rows = new List<string>(segs.Count);
        foreach (var seg in segs) rows.Add(Indent + paint(seg.Text));
        return rows;
    }
}
