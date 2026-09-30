using Gatto.Terminal;

namespace Gatto.Cli;

//the one row every wait on a loading model draws, from the one table of seconds behind it
internal static class LoadingRow
{
    //the pulse is a fixed three-cell field, so nothing after it moves as the frame changes
    internal static string Pulse(int frame) => new string('~', frame % 3 + 1).PadRight(3);

    //read the rungs from the longest wait down, a chain that asked about thirty seconds first would swallow every later rung
    internal static string Words(TimeSpan elapsed, string modelId, string? size, GlyphSet glyphs)
    {
        var seconds = elapsed.TotalSeconds;
        var end = glyphs.Ellipsis;
        if (seconds >= 120)
            return size is null ? $"still pumping {modelId}{end}" : $"still pumping {modelId}, {size}{end}";
        if (seconds >= 60) return $"pumping {modelId}{end}";
        if (seconds >= 30) return $"still loading {modelId}{end}";
        return $"loading {modelId}{end}";
    }

    //returns a plain string alongside the painted one, the face and pulse share one accent run so the gap between them costs no reset
    internal static (string Plain, string Painted) Render(
        TimeSpan elapsed, string modelId, string? size, GlyphSet glyphs, Theme? theme, int frame)
    {
        var words = Words(elapsed, modelId, size, glyphs);
        var clock = Gatto.Core.ElapsedText.Of(elapsed);
        var plain = $"{glyphs.Face}  {Pulse(frame)}  {words}  {clock}";
        if (theme is null) return (plain, plain);

        //the model id sits inside the words, so it's painted in place rather than composed beside them
        var painted = theme.Paint(glyphs.Face + "  " + Pulse(frame), Theme.Accent) + "  "
            + theme.PaintSpans(words, [modelId], Theme.Bright, Theme.CodeInlineFg) + "  "
            + theme.Paint(clock, Theme.Dim);
        return (plain, painted);
    }
}
