using Gatto.Core.Acquire;

namespace Gatto.Cli.Setup;

//what the wizard says when the pinned build cannot load a model's architecture. the marker names the build, and it shows before the download
internal static class ArchNote
{
    //the embedded set read once, a cache of a constant. every member also takes the set, which is the overload the tests drive
    private static readonly Lazy<SupportedArchitectures> Shared = new(SupportedArchitectures.Load);

    //the marker, a statement about the build rather than about the model
    internal const string MarkerText = "needs a different llama.cpp build";

    //the dim factual row marker, or null when there is nothing to say
    public static string? Marker(ModelRow r) => Marker(r, Shared.Value);

    public static string? Marker(ModelRow r, SupportedArchitectures set) =>
        set.WillNotLoad(r.Arch) ? MarkerText : null;

    //whether the pinned build can load this architecture, for a caller with a header rather than a row
    public static bool NeedsAnotherBuild(string? arch) => NeedsAnotherBuild(arch, Shared.Value);

    public static bool NeedsAnotherBuild(string? arch, SupportedArchitectures set) => set.WillNotLoad(arch);

    //what the scaffold path says when the file on disk needs another build, about what comes next. the rows name this model, so the blast radius is on screen
    public static IReadOnlyList<string> AskRows(string? arch) => AskRows(arch, Shared.Value);

    public static IReadOnlyList<string> AskRows(string? arch, SupportedArchitectures set) =>
        !set.WillNotLoad(arch)
            ? []
            :
            [
                $"This model's architecture is \"{arch}\", which gatto's llama.cpp build "
                + $"({set.Release}) can't load. A build that can will run it fine.",
                "gatto would use it for this model only, everything else keeps the build gatto ships.",
            ];
}
