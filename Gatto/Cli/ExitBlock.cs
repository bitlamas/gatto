using Gatto.Terminal;

namespace Gatto.Cli;

//a line as inked runs, and the same line as plain text for a sink that paints nothing, such as the warning sink
internal sealed record InkedLine(IReadOnlyList<(string Text, CliInk Ink)> Runs)
{
    public string Text => string.Concat(Runs.Select(r => r.Text));

    public static InkedLine Of(params (string Text, CliInk Ink)[] runs) => new(runs);
}

//the one place the end of an interactive session is printed. the farewell, the resume line and the server line reach one writer in one order
internal static class ExitBlock
{
    //one space after the face (the cat is speaking, and the command header is a label)
    internal static InkedLine Farewell(GlyphSet glyphs) =>
        InkedLine.Of((glyphs.Header, CliInk.Accent), (" bye ~ !", CliInk.Plain));

    internal static InkedLine Resume(GlyphSet glyphs) =>
        InkedLine.Of(($"session saved {glyphs.Dot} ", CliInk.Dim), CliSurface.Command("gatto --continue"),
            (" to resume", CliInk.Dim));

    public static void Write(CliSurface cli, GlyphSet glyphs, bool farewell, bool resume, InkedLine? server)
    {
        var lines = new List<InkedLine>();
        if (farewell) lines.Add(Farewell(glyphs));
        if (resume) lines.Add(Resume(glyphs));
        if (server is not null) lines.Add(server);
        if (lines.Count == 0) return;

        //a blank row before each line and one after the last, so the shell prompt never touches the block
        foreach (var line in lines)
        {
            cli.Blank();
            cli.Row([.. line.Runs]);
        }
        cli.Close();
    }
}
