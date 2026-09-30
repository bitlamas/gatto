using Gatto.Core.Acquire;
using Gatto.Core.Hardware;
using Gatto.Core.Models;

namespace Gatto.Cli.Setup;

//a local file as a shelf row, priced like the Hub shelf and with the Hub-only fields left empty
internal static class LocalShelf
{
    //a found model priced against this machine, and the badge is passed in by the flow rather than looked up here
    public static ShelfRow Row(FoundModel m, HardwareClass? hw, int ctxForFit = 4096,
        Gatto.Core.Acquire.Badge? badge = null)
    {
        var name = System.IO.Path.GetFileName(m.Path);
        var stem = StemOf(name);
        var fit = m.Header is { } h && hw is { } machine
            ? FitArithmetic.Judge(FitArithmetic.Estimate(h, m.FileBytes, ctxForFit, KvCacheKind.F16, m.StreamedBytes), machine)
            : FitRegime.Unknown;

        return new ShelfRow(
            RepoId: stem,
            Publisher: "",
            PickedQuant: new HubQuant(name, m.FileBytes, null),
            Fit: fit,
            NativeCtx: m.Header?.ContextLength,
            Vision: false,
            Badge: badge,
            Downloads: 0,
            Gated: false,
            LastModified: null,
            Params: null,
            AllQuants: null,
            Projectors: null,
            Arch: m.Header?.Architecture,
            Structure: m.Header is { } sh ? ModelStructure.Cell(sh) : null,
            Experts: m.Header is { } eh ? ModelStructure.Experts(eh) : null);
    }

    //no hardware means no fit at all, an invented machine would put marks on rows nobody measured
    public static ShelfRow Unpriced(FoundModel m, Gatto.Core.Acquire.Badge? badge = null) =>
        Row(m, null, badge: badge);

    //the shard words are composed here from the set, and the have marks come in from the flow rather than from a probe
    public static Gatto.Cli.Setup.Tui.ModelFacts FactsFor(
        FoundModel m, Gatto.Terminal.HaveMark have = Gatto.Terminal.HaveMark.None,
        string? haveId = null)
    {
        var dir = System.IO.Path.GetDirectoryName(m.Path);
        return new Gatto.Cli.Setup.Tui.ModelFacts(
            Structure: m.Header is { } h ? ModelStructure.Cell(h) : null,
            Experts: m.Header is { } eh ? ModelStructure.Experts(eh) : null,
            LocalPath: dir is { Length: > 0 } ? dir + System.IO.Path.DirectorySeparatorChar : null,
            FilesHere: FilesWords(m),
            Have: have,
            HaveId: haveId);
    }

    //say how many of the set's files are here, a partial set has to read as partial before the user picks it
    internal static string FilesWords(FoundModel m) =>
        m.Set is { IsSet: true } s ? $"{m.ShardsPresent} of {s.Count} files here" : "1 file";

    //strip the shard suffix and the extension, so every file of a set ends up with one name
    internal static string StemOf(string fileName)
    {
        var bare = System.IO.Path.GetFileNameWithoutExtension(fileName);
        return ShardName.Parse(fileName) is { IsSet: true } s && s.Stem.Length > 0 ? s.Stem : bare;
    }
}
