using Gatto.Core.Home;
using Gatto.Repl;
using Gatto.Roles;
using Gatto.Terminal;

namespace Gatto.Cli;

//the cat and identity line that open a command, for output a person is looking at (--json, --version and -p stay bare)
internal static class CommandBanner
{
    //the four marks come from the resolved glyph set, one composition point so an engine row and the banner stay in one vocabulary
    public static Gatto.Roles.EngineMarks MarksFor(string home)
    {
        var g = GlyphsFor(home);
        return new(g.Ok, g.Bad, g.Dot, g.Ellipsis);
    }

    //the glyph set is resolved once here, and threaded as a defaulted constructor parameter rather than read from a static
    public static GlyphSet GlyphsFor(string home)
    {
        var configured = "auto";
        try { configured = GattoConfig.Load(home).Glyphs; }
        catch (GattoConfigException) { } //the command's own checks own this failure, as with the theme

        return GlyphSet.Resolve(
            configured switch
            {
                "unicode" => GlyphMode.Unicode,
                "ascii" => GlyphMode.Ascii,
                _ => GlyphMode.Auto,
            },
            //environment names on Windows are case-insensitive, a host that spells one differently would read as unset and drop to ASCII
            GlyphSet.HostEnv());
    }

    //the theme a command paints with, detected from output only (a redirected stdout stays plain), and an unloadable gatto.json falls back to auto
    public static Theme? Chrome(string home)
    {
        var configTheme = "auto";
        try { configTheme = GattoConfig.Load(home).Theme; }
        catch (GattoConfigException) { } //the command's own checks own this failure

        var caps = TermCaps.DetectForOutput();
        return caps.Rich
            ? new Theme(caps, Theme.ResolveMode(configTheme, rich: true, TermCaps.QueryBackgroundColor))
            : null;
    }

    //the theme for a surface inside a live session, built from the mode the session already resolved so it cannot probe the terminal again
    public static Theme? ForSession(TermCaps caps, ThemeMode mode) =>
        caps.Rich ? new Theme(caps, mode) : null;

    //the four-line banner, the same art and the same single blank row between art and text as the REPL's launch
    public static void Write(TextWriter output, Theme? theme, string command,
        Gatto.Terminal.GlyphSet? glyphs = null, VersionStamp? stamp = null)
    {
        //a blank row before the art, or the cat sits right under the command the user typed. the REPL needs none because its banner opens a cleared screen
        output.WriteLine();

        foreach (var line in Cats.For("generalist", glyphs).Split('\n'))   //the GENERALIST cat always: these commands run under no role, and a role tint would invent an affiliation the command does not have
        {
            var art = line.TrimEnd('\r');
            output.WriteLine(theme is null ? art : theme.Paint(art, Theme.Accent));
        }
        output.WriteLine();

        output.WriteLine(Identity(theme, command, glyphs, stamp));
        output.WriteLine();
    }

    //the one-liner header: face, two spaces, the identity line Write draws. the glyph set is required here, forgetting it puts a row of boxes on the command
    public static void WriteHeader(TextWriter output, Theme? theme, string command,
        Gatto.Terminal.GlyphSet glyphs, VersionStamp? stamp = null)
    {
        output.WriteLine();
        output.WriteLine(HeaderLine(theme, command, glyphs, stamp));
        output.WriteLine();
    }

    //the face and the identity line as one row, for every surface that opens with a header, the wizard's printed record included
    public static string HeaderLine(Theme? theme, string command, Gatto.Terminal.GlyphSet glyphs,
        VersionStamp? stamp = null, string? lead = null, string? tail = null)
    {
        var face = theme is null ? glyphs.Header : theme.Paint(glyphs.Header, Theme.Accent);
        return face + "  " + Identity(theme, command, glyphs, stamp, lead, tail);
    }

    //the line both forms print, no subject in the header, the body names what the command acts on
    private static string Identity(Theme? theme, string command,
        Gatto.Terminal.GlyphSet? glyphs, VersionStamp? stamp, string? lead = null, string? tail = null)
    {
        var running = stamp ?? VersionStamp.Running;
        var version = DottedVersion(running.Version, running.Build, running.Dev, glyphs);
        var sep = " " + (glyphs ?? Gatto.Terminal.GlyphSet.Unicode).Dot + " ";
        var dot = theme is null ? sep : theme.Paint(sep, Theme.Dim);

        //gatto and the command are one run, the words the user typed, so the dot separates only asides about the program
        var invocation = (theme is null ? "gatto" : theme.Paint("gatto", Theme.Accent, bold: true))
            + " " + (theme is null ? command : theme.Paint(command, Theme.Bright));

        //lead is an aside about the run before the program's own, and tail one after it. an empty one draws no separator
        string Aside(string? text) => text is { Length: > 0 } a ? dot + (theme is null ? a : theme.Paint(a, Theme.Dim)) : "";

        return invocation + Aside(lead) + dot + (theme is null ? version : theme.Paint(version, Theme.Dim))
            + Aside(tail);
    }

    //the line gatto --version prints: parentheses because the release gate matches that frame, and an empty stamp leaves no empty brackets
    public static string VersionLine(string version, string build, bool dev) =>
        Gatto.Core.GattoVersion.BuildTail(build, dev) is { Length: > 0 } tail
            ? "gatto " + version + " (" + tail + ")"
            : "gatto " + version;

    //the banner and REPL frame: a dot separator and a leading v, with BannerDevMarker because this surface stays BMP-only
    public static string DottedVersion(string version, string build, bool dev,
        Gatto.Terminal.GlyphSet? glyphs) =>
        Gatto.Core.GattoVersion.BuildTail(build, dev, Gatto.Core.GattoVersion.BannerDevMarker)
                is { Length: > 0 } tail
            ? "v" + version + " " + (glyphs ?? Gatto.Terminal.GlyphSet.Unicode).Dot + " " + tail
            : "v" + version;

    //a development build is a launcher with its dll beside it, which dotnet build produces and a single-file publish does not
    public static bool IsDevBuild() => SelfInstall.IsFrameworkDependent(Environment.ProcessPath);
}

//the three version facts, threaded so a golden cannot hold the sha of the commit that exported it, production gets Running
internal readonly record struct VersionStamp(string Version, string Build, bool Dev)
{
    public static VersionStamp Running => new(
        Gatto.Core.GattoVersion.String,
        Gatto.Core.GattoVersion.Build ?? "",
        CommandBanner.IsDevBuild());
}
