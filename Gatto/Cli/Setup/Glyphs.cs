namespace Gatto.Cli.Setup;

//the wizard's glyph vocabulary, chosen once so one tick keeps one meaning. no emoji, every glyph comes from the terminal vocabulary
internal static class Glyphs
{


    //which mark a row glyph spells, one home read by both faces. the RowGlyphTests suite checks every member against both maps, so a new one with no mark fails
    public static string Of(RowGlyph glyph, Gatto.Terminal.GlyphSet? glyphs)
    {
        //the semantic map stays here, since the dependency runs one way and the mark table lives in the Repl layer
        var g = glyphs ?? Gatto.Terminal.GlyphSet.Unicode;
        return glyph switch
        {
        RowGlyph.Good => g.Ok,
        RowGlyph.Bad => g.Bad,
        RowGlyph.Warn => g.Warn,
        //the NotRun mark is an en dash, since no screen may show an em dash
        RowGlyph.NotRun => g.NotRun,
        _ => throw new ArgumentOutOfRangeException(nameof(glyph), glyph, "no mark is defined for this row glyph"),
        };
    }
}
