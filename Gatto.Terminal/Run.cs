namespace Gatto.Terminal;

//what a run of text means in the frame's vocabulary (the painter knows no theme, the face maps an ink to the palette)
public enum RunInk
{
    //ordinary body text, and it is white, so a row must ask to be greyed out
    Plain,
    //a key the user can press, a question, a number that holds the answer
    Bright,
    //the why-clause behind a fact, a verb behind its key, chrome
    Dim,
    //what the user is about to act on, and the cat
    Accent,
    //a status glyph for something well, reachable only through RowGlyph so no whole sentence goes green
    Ok,
    //a status glyph for something that failed, amber rather than red, since the page already says what to do next
    Warn,
}

//one run of text and the ink it uses, since the keys row needs bright and dim on one line. the tag is what a press on this text does, carried by every step that copies the run
public readonly record struct Run(string Text, RunInk Ink = RunInk.Plain, object? Tag = null,
    bool Joined = false,   //a joined run was split from the one before it only to carry a tag, so a face paints the two as one
    bool Band = false);   //the run sits on the selection band, the ground under the row the keys are on

//what the fit may drop, in order: a structural blank, then a paragraph blank, then an optional row
public enum RowFit
{
    //never dropped, for a row that says something
    Fixed,
    //a blank the painter puts around structure
    Structural,
    //a blank between two prose rows, composed by the flow
    Paragraph,
    //a row the screen says it can live without, dropped last and only if still over
    Optional,
}

//a composed row: its runs and the plain text they spell, derived rather than stored so the two cannot disagree
public readonly record struct PaintedRow(IReadOnlyList<Run> Runs, RowFit Fit = RowFit.Fixed)
{
    public static PaintedRow Of(string text, RunInk ink = RunInk.Plain,
        RowFit fit = RowFit.Fixed) => new([new Run(text, ink)], fit);

    //a bare string is a plain row, like the conversion WizardRow already has, so a composer can say nothing about ink
    public static implicit operator PaintedRow(string text) => Of(text);

    public string Text => string.Concat(Runs.Select(r => r.Text));

    //the same row with every run on the selection band
    public PaintedRow Banded() => this with { Runs = [.. Runs.Select(r => r with { Band = true })] };

    //cut to a visible-cell budget, run by run and rune by rune, so a surrogate pair is never halved
    public PaintedRow Clamp(int width)
    {
        if (UnicodeWidth.Of(Text) <= width) return this;

        var kept = new List<Run>();
        var n = 0;
        foreach (var run in Runs)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var rune in run.Text.EnumerateRunes())
            {
                var w = UnicodeWidth.Of(rune.ToString());
                if (n + w > width) break;
                sb.Append(rune.ToString());
                n += w;
            }
            if (sb.Length > 0) kept.Add(run with { Text = sb.ToString() });
            if (n >= width) break;
        }
        return new PaintedRow(kept);
    }
}
