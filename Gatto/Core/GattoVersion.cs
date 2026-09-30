using System.Reflection;

namespace Gatto.Core;

//the product version, from the assembly's InformationalVersion with its +<metadata> suffix stripped (Gatto.csproj's Version element sets it)
public static class GattoVersion
{
    //the clean product version with the +<metadata> build suffix cut off
    public static string String { get; } = Compute();

    //the short commit sha, 7 chars plus -dirty, empty when the stamp is not a sha so git's error text never reaches the banner
    public static string Build { get; } = ComputeBuild();

    private static string Info() => typeof(GattoVersion).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";

    private static string Compute()
    {
        var info = Info();
        if (string.IsNullOrEmpty(info)) return "0.0.0";
        var plus = info.IndexOf('+');
        return plus >= 0 ? info[..plus] : info;
    }

    private static string ComputeBuild() => ParseStamp(Info());

    //the stamp rule on its own, so a test needs no git tree: anything that is not a 7-char hex sha stays empty
    internal static string ParseStamp(string informationalVersion)
    {
        var plus = informationalVersion.IndexOf('+');
        if (plus < 0 || plus + 1 >= informationalVersion.Length) return "";

        var raw = informationalVersion[(plus + 1)..];
        //describe answers tag-N-gSHA if the tag exclusion is ever dropped, so cut to the commit part and validate both shapes below
        var gIdx = raw.LastIndexOf("-g", StringComparison.Ordinal);
        var candidate = gIdx >= 0 ? raw[(gIdx + 2)..] : raw;

        var dirty = candidate.EndsWith("-dirty", StringComparison.Ordinal) ? "-dirty" : "";
        var core = dirty.Length > 0 ? candidate[..^dirty.Length] : candidate;

        return core.Length == 7 && core.All(Uri.IsHexDigit) ? core + dirty : "";
    }

    //the marker a development build appends to its sha, so two open REPLs can be told apart
    public const string DevMarker = " 👾";

    //the banner's dev marker, a different glyph on purpose (the banner stays BMP-only, --version keeps DevMarker)
    public const string BannerDevMarker = " ✦";

    //the build stamp on its own (--version asserts the parenthesised form), so each call site keeps its own frame and passes its own marker
    public static string BuildTail(string build, bool dev, string marker = DevMarker) =>
        build.Length > 0 ? "build " + build + (dev ? marker : "") : "";
}
