namespace Gatto.Cli;

//the sentences about a server that is already loaded, every fact read from the probe
internal static class ServeNotice
{
    //the one line a launch says about a server it found up, through the command layer rather than the warning sink, which marks every line a warning
    public static void SayUsing(CliSurface cli, Gatto.Terminal.GlyphSet glyphs, (string Text, CliInk Ink)[] line, bool blank)
    {
        //the header face and no pulse, since a pulse that never moves claims motion on a line that never waits
        if (blank) cli.Blank();
        cli.Row([(glyphs.Header + " ", CliInk.Accent), .. line]);
    }

    //the model the launch armed is the one the server holds
    public static (string, CliInk)[] Using(string armed, int port) =>
        [("using ", CliInk.Plain), (armed, CliInk.Model), ($", served on :{port}", CliInk.Plain)];

    //the same words with no ink, for a session row that paints its own
    public static string Text((string Text, CliInk Ink)[] line) => string.Concat(line.Select(p => p.Text));

    //no shelf model claims the served file, so the default stays armed. the stem is read off the server, which the surface sanitizes
    public static (string, CliInk)[] UsingOffShelf(string armed, int port, string held) =>
        [("using ", CliInk.Plain), (armed, CliInk.Model), ($", the server on :{port} holds ", CliInk.Plain), (held, CliInk.Model), (", not on the shelf", CliInk.Plain)];

    //the shelf model the server holds names another port, so arming it would send every request where no server answered
    public static (string, CliInk)[] UsingOtherPort(string armed, int port, string held) =>
        [("using ", CliInk.Plain), (armed, CliInk.Model), ($", the server on :{port} holds ", CliInk.Plain), (held, CliInk.Model)];

    //a server that names no file cannot be matched to a model, and saying it serves the default would claim what nothing measured
    public static (string, CliInk)[] UsingNoFile(string armed, int port) =>
        [("using ", CliInk.Plain), (armed, CliInk.Model), ($", the server on :{port} does not name its file", CliInk.Plain)];

    //the sentence for a -m that names a model other than the one serving. give both ways out, and say the limit is temporary
    public static string Refusing(string? servedModelPath)
    {
        var served = Name(servedModelPath) ?? "another model";
        return $"{served} is running. Stop it with gatto serve stop, or open gatto without -m to "
            + $"talk to {served}. Running several servers at once comes later.";
    }

    //the hint when stop_server_on_exit is off, the size is what makes it worth reading. answers null when nothing is running
    public static InkedLine? ExitHint(bool serverRunning, long? sizeBytes, Gatto.Terminal.GlyphSet glyphs)
    {
        if (!serverRunning) return null;
        var size = Size(sizeBytes);
        return InkedLine.Of(("model still loaded", CliInk.Plain),
            ((size is null ? "" : $" ({size})") + $" {glyphs.Dot} ", CliInk.Dim),
            CliSurface.Command("gatto serve stop"), (" to eject", CliInk.Dim));
    }

    private static string? Name(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return System.IO.Path.GetFileName(path) is { Length: > 0 } n ? n : null; }
        catch (Exception) { return null; }
    }

    //a size that rounds to zero reads as <0.1 GB, since ~0 GB says nothing is loaded just when a partial file is
    private static string? Size(long? bytes)
    {
        if (bytes is not > 0) return null;

        return SizeWords.Gb(bytes.Value, approx: true);
    }
}
