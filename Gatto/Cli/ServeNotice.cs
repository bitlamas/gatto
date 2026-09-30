namespace Gatto.Cli;

//the two sentences about a server that is already loaded, every fact read from the probe. a clause with no input is dropped rather than guessed
internal static class ServeNotice
{
    //the reuse line goes through the command layer rather than the warning sink, which marks and writes to stderr
    public static void SayReusing(CliSurface cli, Gatto.Terminal.GlyphSet glyphs, string? servedModelPath,
        string? startedIso, DateTimeOffset now, long? sizeBytes)
    {
        var model = Name(servedModelPath);
        var size = Size(sizeBytes);
        var age = Age(startedIso, now);

        //the header face and no pulse, since a pulse that never moves claims motion on a line that never waits
        cli.Blank();
        cli.Row((glyphs.Header, CliInk.Accent),
            (" " + (age is null ? "reusing the llama-server already running" : $"reusing llama-server started {age}"),
                CliInk.Plain));

        //the file and its size get a row of their own, and the model name here is read off the server, which the surface sanitizes
        var detail = new List<(string Text, CliInk Ink)>();
        if (model is not null) detail.Add((model, CliInk.Model));
        if (model is not null && size is not null) detail.Add(($" {glyphs.Dot} ", CliInk.Dim));
        if (size is not null) detail.Add((size, CliInk.Dim));
        if (detail.Count == 0) return;
        cli.Blank();
        cli.Row([.. detail]);
    }

    //the sentence for a -m that names a model other than the one serving. give both ways out, and say the limit is temporary
    public static string Refusing(string? servedModelPath)
    {
        //use the same reader as the reuse line, so both sentences name one server the same way
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

    //coarse on purpose, hours or minutes are enough. a timestamp that will not parse, or a future one, gives null
    private static string? Age(string? startedIso, DateTimeOffset now)
    {
        if (!DateTimeOffset.TryParse(startedIso, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var started)) return null;

        var elapsed = now - started;
        if (elapsed < TimeSpan.Zero) return null;
        if (elapsed < TimeSpan.FromMinutes(1)) return "moments ago";
        if (elapsed < TimeSpan.FromHours(1)) return $"{(int)elapsed.TotalMinutes}m ago";
        if (elapsed < TimeSpan.FromDays(1)) return $"{(int)elapsed.TotalHours}h ago";
        return $"{(int)elapsed.TotalDays}d ago";
    }
}
