namespace Gatto.Terminal;

//the strip of sections, where exactly one takes the cursor: the focused one with the keys, otherwise the current one
public static class Strip
{
    //functions rather than consts, since a const cannot read the glyph set
    private static string CursorOf(GlyphSet g) => g.Prompt + " ";
    private static string TickOf(GlyphSet g) => " " + g.Ok;
    private static string JoinOf(GlyphSet g) => " " + g.Dot + " ";
    private const string Indent = "  ";

    //the strip row in plain cells, where focused holds the section with the keys or -1, and out of range means no focus

    //the strip row as plain text, the concatenation of Runs, so the goldens and the terminal share one composition
    public static string Compose(IReadOnlyList<StripSection> sections, int focused = -1) =>
        new PaintedRow(Runs(sections, focused)).Text;

    //the strip inked: a done section and its tick green, the current one accent, the sections ahead dim, and the cursor accent
    public static IReadOnlyList<Run> Runs(IReadOnlyList<StripSection> sections, int focused = -1,
        GlyphSet? glyphs = null)
    {
        var hasFocus = focused >= 0 && focused < sections.Count;
        var g = glyphs ?? GlyphSet.Unicode;
        var runs = new List<Run> { new(Indent) };

        for (var i = 0; i < sections.Count; i++)
        {
            var s = sections[i];
            var tick = s.State == StripState.Done ? TickOf(g) : "";
            //the cursor marks the focused section, or the current one when nothing has focus
            var cursor = hasFocus
                ? (i == focused ? CursorOf(g) : "")
                : (s.State == StripState.Current ? CursorOf(g) : "");
            if (i > 0) runs.Add(new Run(JoinOf(g), RunInk.Dim));
            if (cursor.Length > 0) runs.Add(new Run(cursor, RunInk.Accent));
            runs.Add(new Run(s.Name, s.State switch
            {
                StripState.Done => RunInk.Ok,
                StripState.Current => RunInk.Accent,
                _ => RunInk.Dim,
            }));
            if (tick.Length > 0) runs.Add(new Run(tick, RunInk.Ok));
        }

        return runs;
    }
}
