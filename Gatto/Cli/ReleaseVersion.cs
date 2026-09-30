using System.Text.RegularExpressions;

namespace Gatto.Cli;

//an ordered release version, so update tells a newer candidate from an older republished tag. v?N.N.N only, except this process's own version

//vN.N.N, the leading v optional. a prerelease tail, a four-part version or build metadata is not an update
internal readonly record struct ReleaseVersion(int Major, int Minor, int Patch)
{
    //keep [0-9] rather than \d, which in .NET matches every Unicode decimal digit. it is not the gate that rejects non-ASCII digits, so nothing pins it
    private static readonly Regex Shape =
        new(@"^[vV]?([0-9]+)\.([0-9]+)\.([0-9]+)$", RegexOptions.CultureInvariant);

    //never throws, a number that does not fit an int is unparseable rather than an exception
    public static bool TryParse(string? text, out ReleaseVersion v)
    {
        v = default;
        if (text is null) return false;

        var m = Shape.Match(text);
        if (!m.Success) return false;

        if (!int.TryParse(m.Groups[1].Value, out var major)
            || !int.TryParse(m.Groups[2].Value, out var minor)
            || !int.TryParse(m.Groups[3].Value, out var patch)) return false;

        v = new ReleaseVersion(major, minor, patch);
        return true;
    }

    //this process's own version only, so drop the dash and everything after it before the strict parse
    public static bool TryParseRunning(string? text, out ReleaseVersion v)
    {
        var dash = text?.IndexOf('-') ?? -1;
        return TryParse(dash >= 0 ? text![..dash] : text, out v);
    }

    //a tag with its leading v removed, since GattoVersion never has one. text only, and a string that is not a tag is returned as it came
    public static string Bare(string? tag) =>
        tag is { Length: > 1 } t && (t[0] == 'v' || t[0] == 'V') && char.IsDigit(t[1]) ? t[1..] : (tag ?? "");

    //component by component, and equal is not newer, the caller reads that as being on the latest
    public bool IsNewerThan(ReleaseVersion o) =>
        Major != o.Major ? Major > o.Major
        : Minor != o.Minor ? Minor > o.Minor
        : Patch > o.Patch;
}
