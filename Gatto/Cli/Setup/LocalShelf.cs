using Gatto.Core.Acquire;
using Gatto.Core.Hardware;
using Gatto.Core.Models;

namespace Gatto.Cli.Setup;

//a local file as a shelf row, priced like the Hub shelf and with the Hub-only fields left empty
internal static class LocalShelf
{
    //a model's files priced against this machine, one publisher per folder holding that folder's files. the row's file decides the row's fit, quant and folder, and the badge is passed in by the flow
    public static ModelRow Row(LocalGroup g, FoundModel rowFile, HardwareClass? hw, int ctxForFit = 4096,
        Gatto.Core.Acquire.Badge? badge = null)
    {
        var folders = Folders(g);
        var offers = folders.Select(f =>
        {
            var quants = f.Files.Select(x => new HubQuant(System.IO.Path.GetFileName(x.Path), x.FileBytes, null)).ToList();
            var pick = f.Files.Contains(rowFile) ? rowFile : f.Files.FirstOrDefault(x => x.IsComplete) ?? f.Files[0];
            return new PublisherOffer(f.Folder, [new RepoFiles(f.Folder, quants, [], quants.Count, 0)],
                FileRefOf(pick), quants[f.Files.IndexOf(pick)], FitOf(pick, hw, ctxForFit), 0);
        }).ToList();
        var rowFolder = folders.FindIndex(f => f.Files.Contains(rowFile));
        return new ModelRow(
            Model: g.Name, Family: null, Generation: 0,
            Params: null, Active: null, Arch: rowFile.Header?.Architecture, NativeCtx: rowFile.Header?.ContextLength, Vision: false,
            Publishers: offers,
            RowPublisher: rowFolder, RowFile: FileRefOf(rowFile), Fit: FitOf(rowFile, hw, ctxForFit),
            Structure: rowFile.Header is { } sh ? ModelStructure.Cell(sh) : null,
            Experts: rowFile.Header is { } eh ? ModelStructure.Experts(eh) : null,
            Badge: badge,
            ParamsLabel: ModelStructure.SizeCell(rowFile.Header?.SizeLabel));
    }

    //a group's folders in scan order, each with its own files in scan order
    private static List<(string Folder, List<FoundModel> Files)> Folders(LocalGroup g)
    {
        var folders = new List<(string Folder, List<FoundModel> Files)>();
        foreach (var f in g.Files)
        {
            var dir = System.IO.Path.GetDirectoryName(f.Path) ?? "";
            var at = folders.FindIndex(x => string.Equals(x.Folder, dir, StringComparison.OrdinalIgnoreCase));
            if (at < 0) folders.Add((dir, [f])); else folders[at].Files.Add(f);
        }
        return folders;
    }

    //a local file named the way a pane line names a Hub file: the folder as publisher and repo, the file name as path
    internal static FileRef FileRefOf(FoundModel m)
    {
        var dir = System.IO.Path.GetDirectoryName(m.Path) ?? "";
        return new FileRef(dir, dir, System.IO.Path.GetFileName(m.Path));
    }

    //the full path a pane line's file names, the inverse of FileRefOf
    internal static string PathOf(FileRef f) => System.IO.Path.Combine(f.RepoId, f.Path);

    //the pane's folder lines and the folded frame's file, each file priced by FitOf as its row is. a row whose file no rule chose draws no pick mark
    public static Gatto.Cli.Setup.Tui.ModelFacts FactsFor(LocalGroup g, FoundModel rowFile, bool ruled, HardwareClass? hw,
        int ctxForFit = 4096, Gatto.Terminal.HaveMark have = Gatto.Terminal.HaveMark.None, string? haveId = null)
    {
        var dir = System.IO.Path.GetDirectoryName(rowFile.Path);
        Gatto.Cli.Setup.Tui.PaneFile Line(FoundModel f) => new(QuantToken.Of(f.Path), f.FileBytes, FitOf(f, hw, ctxForFit),
            FileRefOf(f), Tag: f.Set is { IsSet: true } ? FilesWords(f) : null);
        var publishers = Folders(g).Select(f => new Gatto.Cli.Setup.Tui.PanePublisher(f.Folder, [.. f.Files.Select(Line)],
            FileRefOf(f.Files.Contains(rowFile) ? rowFile : f.Files.FirstOrDefault(x => x.IsComplete) ?? f.Files[0]),
            Marked: ruled && f.Files.Contains(rowFile))).ToList();
        return new Gatto.Cli.Setup.Tui.ModelFacts(
            Structure: rowFile.Header is { } h ? ModelStructure.Cell(h) : null,
            Experts: rowFile.Header is { } eh ? ModelStructure.Experts(eh) : null,
            //the folded frame steps through the row's folder, the face starts it on the row's file
            Files: [.. Folders(g).First(f => f.Files.Contains(rowFile)).Files.Select(Line)],
            Publishers: publishers,
            LocalPath: dir is { Length: > 0 } ? dir + System.IO.Path.DirectorySeparatorChar : null,
            FilesHere: FilesWords(rowFile),
            Have: have,
            HaveId: haveId);
    }

    //say how many of the set's files are here, a partial set has to read as partial before the user picks it
    internal static string FilesWords(FoundModel m) =>
        m.Set is { IsSet: true } s ? $"{m.ShardsPresent} of {s.Count} files here" : "1 file";

    //a model's files on this machine, under the name they share
    internal sealed record LocalGroup(string Name, IReadOnlyList<FoundModel> Files);

    //the model's name a file declares: its stem less the quant and what follows it, and less the -GGUF a conversion adds
    internal static string ModelNameOf(string fileName)
    {
        var stem = StemOf(fileName);
        var head = (QuantToken.HeadBefore(fileName) ?? stem).TrimEnd('-', '_', '.');
        return HubSearch.ModelNameOf(head.Length > 0 ? head : stem);
    }

    //one group per model name, without case or folder. architecture and size label split a name, the architecture alone where no label says more, so a generic name such as model never merges two models
    internal static IReadOnlyList<LocalGroup> Group(IReadOnlyList<FoundModel> found)
    {
        var groups = new List<LocalGroup>();
        foreach (var byName in found.GroupBy(f => ModelNameOf(Path.GetFileName(f.Path)), StringComparer.OrdinalIgnoreCase))
        {
            var files = byName.ToList();
            var keyed = new List<(string Arch, string Label, List<FoundModel> Files)>();
            foreach (var f in files)
                if (f.Header is { Architecture: { Length: > 0 } a, SizeLabel: { Length: > 0 } l })
                {
                    var at = keyed.FindIndex(k => k.Arch == a && k.Label == l);
                    if (at < 0) keyed.Add((a, l, [f])); else keyed[at].Files.Add(f);
                }
            //a file with an architecture and no label joins a group of its architecture, or starts one, since no other field can tell two models apart
            foreach (var f in files.Where(f => f.Header is { Architecture: { Length: > 0 }, SizeLabel: not { Length: > 0 } }))
            {
                var at = keyed.FindIndex(k => k.Arch == f.Header!.Architecture);
                if (at < 0) keyed.Add((f.Header!.Architecture!, "", [f])); else keyed[at].Files.Add(f);
            }
            //a file with no architecture joins the group its label matches, else the first one, so it is never a row of its own
            foreach (var f in files.Where(f => f.Header is not { Architecture: { Length: > 0 } }))
            {
                if (keyed.Count == 0) { keyed.Add(("", "", [f])); continue; }
                var match = keyed.FindIndex(k => f.Header is { SizeLabel: { Length: > 0 } hl } && hl == k.Label);
                keyed[match < 0 ? 0 : match].Files.Add(f);
            }
            groups.AddRange(keyed.Select(k => new LocalGroup(byName.Key, [.. files.Where(k.Files.Contains)])));
        }
        return groups;
    }

    //the row's file by the first rule that applies: the served file, the active file by path, a listed file, then the pick rule over the complete files. null leaves the first complete file and no rule mark
    internal static FoundModel? RowFileOf(LocalGroup g, HardwareClass? hw, Func<FoundModel, bool> served,
        string? activePath, Func<FoundModel, bool> listed, int ctxForFit = 4096, long? totalParams = null)
    {
        if (g.Files.FirstOrDefault(served) is { } s) return s;
        if (activePath is not null && g.Files.FirstOrDefault(f => string.Equals(f.Path, activePath, StringComparison.OrdinalIgnoreCase)) is { } active)
            return active;
        //a partial set is never chosen by the later rules while a complete file of the row exists
        var complete = g.Files.Where(f => f.IsComplete).ToList();
        var pool = complete.Count > 0 ? complete : [.. g.Files];
        if (pool.FirstOrDefault(listed) is { } l) return l;
        return Picked(complete, hw, ctxForFit, totalParams);
    }

    //the Hub's pick over local files with the local pricer: the floor, then the card's best, memory's best, and the smallest of the rest. no hardware means one tier, so the quant decides alone
    private static FoundModel? Picked(IReadOnlyList<FoundModel> files, HardwareClass? hw, int ctxForFit, long? totalParams)
    {
        FoundModel? gpu = null, ram = null, over = null, any = null;
        foreach (var f in files)
        {
            var q = QuantOf(f);
            if (ModelDiscovery.IsTooSmallToBeQuantization(q.Bytes, totalParams)) continue;
            if (!QuantToken.AtFloor(q.FileName, totalParams, lifted: false)) continue;
            bool Better(FoundModel? incumbent) => incumbent is null || HubSearch.Beats(q, QuantOf(incumbent));
            switch (hw is null ? FitRegime.Unknown : FitOf(f, hw, ctxForFit))
            {
                case FitRegime.FitsGpu when Better(gpu): gpu = f; break;
                case FitRegime.FitsRamOnly when Better(ram): ram = f; break;
                case FitRegime.DoesNotFit when over is null || HubSearch.Smaller(q, QuantOf(over)): over = f; break;
                case FitRegime.Unknown when Better(any): any = f; break;
            }
        }
        return gpu ?? ram ?? over ?? any;
    }

    private static HubQuant QuantOf(FoundModel f) => new(Path.GetFileName(f.Path), f.FileBytes, null, StreamedBytes: f.StreamedBytes);

    //the one pricer of a local file, for its row and its pane line alike: its own header and streamed bytes
    internal static FitRegime FitOf(FoundModel m, HardwareClass? hw, int ctxForFit) =>
        m.Header is { } h && hw is { } machine
            ? FitArithmetic.Judge(FitArithmetic.Estimate(h, m.FileBytes, ctxForFit, KvCacheKind.F16, m.StreamedBytes), machine)
            : FitRegime.Unknown;

    //strip the shard suffix and the extension, so every file of a set ends up with one name
    internal static string StemOf(string fileName)
    {
        var bare = System.IO.Path.GetFileNameWithoutExtension(fileName);
        return ShardName.Parse(fileName) is { IsSet: true } s && s.Stem.Length > 0 ? s.Stem : bare;
    }
}
