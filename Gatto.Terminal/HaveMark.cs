namespace Gatto.Terminal;

//what the user already has, one glyph per cell. the loaded mark wins over Added when both are true, it's the more specific fact
public enum HaveMark
{
    //not on the list and not loaded, draws a blank cell, no glyph that reads as a no
    None,

    //on gatto's list, the user already added this model
    Added,

    //the weights the session is holding right now
    Loaded,

    //a sibling of the loaded file, the model is on the list but these weights aren't live. so the added glyph fits
    OtherFile,
}

//one home for the mark-to-glyph mapping, every display site and the legend read it. that's how the legend can't drift from the screen
public static class HaveMarks
{
    //the glyph, empty for None
    public static string Glyph(HaveMark have, GlyphSet g) => have switch
    {
        HaveMark.Loaded => g.Loaded,
        //a sibling of the loaded file shares the added glyph, only the live claim was wrong
        HaveMark.Added or HaveMark.OtherFile => g.Ok,
        _ => "",
    };

    //the ink needs its own check, a golden diffs characters and can't see color. both marks mean well, so both get Ok ink
    public static RunInk Ink(HaveMark have) => have switch
    {
        HaveMark.Loaded or HaveMark.Added or HaveMark.OtherFile => RunInk.Ok,
        _ => RunInk.Plain,
    };

    //the dim word beside the glyph, empty for None
    public static string Word(HaveMark have) => have switch
    {
        HaveMark.Loaded => "loaded",
        HaveMark.Added or HaveMark.OtherFile => "added",
        _ => "",
    };

    //glyph, space, word. the hub tail's cell and the pane's opening
    public static string Text(HaveMark have, GlyphSet g) =>
        have == HaveMark.None ? "" : Glyph(have, g) + " " + Word(have);

    //the legend holds only marks the rows actually show, in the order loaded then added. the eye meets the live one first
    public static string? Legend(IEnumerable<HaveMark> marks, GlyphSet g)
    {
        var seen = marks.ToList();
        var parts = new List<string>();
        if (seen.Contains(HaveMark.Loaded)) parts.Add(Text(HaveMark.Loaded, g));
        //a sibling draws the added mark, so the legend explains added. a third entry would describe a glyph no row shows
        if (seen.Contains(HaveMark.Added) || seen.Contains(HaveMark.OtherFile))
            parts.Add(Text(HaveMark.Added, g));
        return parts.Count == 0 ? null : string.Join($" {g.Dot} ", parts);
    }
}
