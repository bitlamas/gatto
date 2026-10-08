using Gatto.Core.Hardware;
using Gatto.Core.Models;
using Gatto.Terminal;

namespace Gatto.Cli.Setup.Tui;

//a fit verdict as the shelf says it: a glyph and one short word. a fixed nine-cell width beside a model name, so the longer FitWords wording cannot go here
internal readonly record struct FitMark(string Glyph, string Word, RunInk Ink)
{
    //the cell the table and the pane both draw: glyph, space, word
    public string Text => Glyph + " " + Word;
}

//one home for the marks, read by the table, the pane and the legend. the word follows the machine shape, so a unified machine says fits rather than GPU
internal static class FitMarks
{
    //the glyph set, passed in from the face. defaulted, so every existing caller and golden stays unchanged
    public static FitMark Of(FitRegime fit, MachineShape shape, GlyphSet? glyphs)
    {
        //the marks come from the glyph table, so an ASCII console draws + ! x and a Unicode one the ticks. the words beside them are the same copy in both sets
        var g = glyphs ?? GlyphSet.Unicode;
        return fit switch
        {
        FitRegime.FitsGpu when shape == MachineShape.Discrete => new(g.Ok, "GPU", RunInk.Ok),
        FitRegime.FitsGpu => new(g.Ok, "fits", RunInk.Ok),
        FitRegime.FitsRamOnly when shape == MachineShape.Discrete => new(g.Warn, "RAM", RunInk.Warn),
        FitRegime.FitsRamOnly => new(g.Ok, "fits", RunInk.Ok),
        FitRegime.DoesNotFit => new(g.Bad, "too big", RunInk.Warn),
        //unknown draws nothing, since the arithmetic never ran for this file
        _ => new("", "", RunInk.Plain),
        };
    }

    //whether the shelf has a runs column at all. only a discrete card splits on the card from system memory, so elsewhere the column is decoration
    public static bool HasRunsColumn(MachineShape shape) => shape == MachineShape.Discrete;

    //what sits right of the footer's keys. a marks legend may ellipse, a sentence one is whole or nothing, and fewer params = faster speaks for the column
    public static Legend LegendFor(MachineShape shape, GlyphSet? glyphs) =>
        HasRunsColumn(shape)
            ? new Legend(LegendKind.Marks, string.Join($" {(glyphs ?? GlyphSet.Unicode).Dot} ",
                new[] { FitRegime.FitsGpu, FitRegime.FitsRamOnly }
                    .Select(f => Of(f, shape, glyphs).Text)))
            : new Legend(LegendKind.Sentence, "fewer params = faster");
}
