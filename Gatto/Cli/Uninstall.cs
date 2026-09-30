namespace Gatto.Cli;

//one row of the uninstall map: something on this machine, and whether uninstalling removes it
internal sealed record OwnedItem(string What, string Where, UninstallMark Mark, long? Bytes = null,   //three marks, since a stopped process is neither removed nor kept
    OwnedKind Kind = OwnedKind.Other)   //a field, so a copy edit to the label cannot change the sentence about what survived
{
    //a question the map can be asked, so RemovesAnything and the delete loop read one fact instead of each testing the enum
    public bool Removed => Mark == UninstallMark.Removed;
}

//the kinds the home row's parenthetical can name. the Other case is the default, since most rows aren't about a thing that survives in the home
internal enum OwnedKind
{
    Other,
    Weights,
    Engine,
}

//semantic colour for this map: removed red, stays green, stopped warn, because a misread here is unrecoverable. it is scoped to this map alone
internal enum UninstallMark
{
    Removed,
    Stays,
    Stopped,
}

//one thing that could not be removed, and where it is. naming the file lets a user close what holds it and run the command again
internal sealed record RemovalFailure(string Path, string Why);

//what the caller read, passed in so the map and the deletion share one look at the disk. null bytes means unmeasurable rather than absent
internal readonly record struct InstallFacts(bool ExePresent, long? ExeBytes, bool OnPath);

//what gatto owns, what it does not, and what it is about to do. gatto never deletes software it did not install, and the deletion reads this same map
internal static class Uninstall
{
    //build the map. keeping the home is the default, and an absent exe or PATH entry gets no row, since stays is a promise about something that exists
    public static IReadOnlyList<OwnedItem> Map(
        string installDir, string home, bool deleteHome, InstallFacts install,
        string? llamaServerPath, IReadOnlyList<(string Id, string ModelPath)> models,
        Gatto.Roles.RunningInfo? stopping = null, bool otherWeightFiles = false)
    {
        var items = new List<OwnedItem>();

        //the install rows are conditional, so a machine with no installed gatto gives an empty map. the server row comes first when there is one
        if (stopping is { } server)
            items.Add(new($"llama-server for {server.Model}", $"port {server.Port}, pid {server.Pid}",
                UninstallMark.Stopped));

        if (install.ExePresent)
            items.Add(new("gatto", installDir, UninstallMark.Removed, install.ExeBytes));
        if (install.OnPath)
            items.Add(new("PATH entry", installDir, UninstallMark.Removed));

        //the home row is always shown. keep the wording "model setups": the word "models" here means the 46 GB of GGUFs
        items.Add(new("your settings, model setups, sessions and memory", home,
            deleteHome ? UninstallMark.Removed : UninstallMark.Stays));

        //the fetched engine is named gatto's when it sits under the home's llama folder, and its mark follows the home's answer
        if (!string.IsNullOrWhiteSpace(llamaServerPath))
        {
            var ours = Gatto.Core.Acquire.ModelLocation.IsInside(
                Path.Combine(home, "llama"), llamaServerPath);
            items.Add(new(
                ours ? "llama.cpp (fetched by gatto)" : "llama.cpp (not fetched by gatto)",
                Path.GetDirectoryName(llamaServerPath) ?? llamaServerPath,
                ours && deleteHome ? UninstallMark.Removed : UninstallMark.Stays,
                Kind: OwnedKind.Engine));
        }

        //a model weight file is never offered for removal, and the deed obeys that: the removal keeps every path the map marks as staying
        foreach (var (id, modelPath) in models)
            items.Add(new($"model weight file for {id} (yours)", modelPath,
                UninstallMark.Stays,
                Gatto.Core.Acquire.ModelDiscovery.SetBytesOrNull(modelPath), OwnedKind.Weights));

        //weight files under the home's weights folder that no model row names. the map names them too, and the caller is what reads the disk
        if (otherWeightFiles)
            items.Add(new("other model weight files (yours)",
                Gatto.Core.Acquire.ModelLocation.SuggestedDir(home, null), UninstallMark.Stays,
                Kind: OwnedKind.Weights));

        //a general statement when no model row names weights. otherwise a user with no model reads a map that says nothing about their downloads
        if (models.Count == 0)
            items.Add(new("your model files, wherever they are (gatto never removes these)", "",
                UninstallMark.Stays));

        //the parenthetical is composed from what is actually kept under the home, one clause per kind, in the map's own order
        if (deleteHome && items.FindIndex(i => i.Where == home) is >= 0 and var homeRow)
        {
            var kept = items
                .Where(i => i.Mark == UninstallMark.Stays && i.Where is { Length: > 0 } w
                            && Gatto.Core.Acquire.ModelLocation.IsInside(home, w))
                .Select(i => i.Kind)
                .Distinct()
                .Select(ClauseFor)
                .Where(c => c is { Length: > 0 })
                .ToList();

            if (kept.Count > 0)
                items[homeRow] = items[homeRow] with
                {
                    What = items[homeRow].What + $" (except {string.Join(" and ", kept)})",
                };
        }

        return items;
    }

    //what one kind is called inside the home row's parenthetical. an Other row contributes nothing, since a clause the sentence can't name is better absent
    private static string? ClauseFor(OwnedKind kind) => kind switch
    {
        OwnedKind.Weights => "your model weight files",
        OwnedKind.Engine => "the llama.cpp you put there",
        _ => null,
    };

    //the label column's width: the longest mark plus a space, computed from the words so a fourth label can't leave the indent behind
    internal static int MarkColumn => Enum.GetValues<UninstallMark>().Max(m => Word(m).Length) + 1;

    internal static string Word(UninstallMark mark) => mark switch
    {
        UninstallMark.Removed => "removed",
        UninstallMark.Stopped => "stopped",
        _ => "stays",
    };

    //the two rows a map line renders as: what, then where, aligned under the label, which a prefix on the first line can't do
    public static IReadOnlyList<string> Rows(OwnedItem item)
    {
        var size = item.Bytes is > 0 ? " (" + Size(item.Bytes.Value) + ")" : "";
        var head = Word(item.Mark).PadRight(MarkColumn) + item.What + size;

        //one row when there is no location: the model-files disclaimer has no path, and a disclaimer in the location column is a different thing
        return item.Where.Length == 0 ? [head] : [head, new string(' ', MarkColumn) + item.Where];
    }

    //whether the map removes anything at all, so a user is not asked to confirm a yes that removes nothing
    public static bool RemovesAnything(IReadOnlyList<OwnedItem> map) => map.Any(i => i.Removed);

    //remove a tree and name per entry what would not go. reparse points are never followed, or deleting a link would take its target
    public static IReadOnlyList<RemovalFailure> RemoveTree(string root) => RemoveTree(root, []);

    //root and the paths to keep: directories go bottom-up and only when nothing inside them survived, so a kept weight leaves its ancestors standing
    public static IReadOnlyList<RemovalFailure> RemoveTree(string root, IReadOnlyList<string> keep)
    {
        var failures = new List<RemovalFailure>();
        if (!Directory.Exists(root)) return failures;

        var files = new List<string>();
        Collect(root, files, failures);

        foreach (var file in files)
        {
            if (IsKept(file, keep)) continue;
            try { File.Delete(file); }
            catch (Exception ex) { failures.Add(new RemovalFailure(file, ex.Message)); }
        }

        Prune(root, keep, failures);

        //the safety net: if the tree is still there and nothing above explained why, say so once rather than staying silent
        if (Directory.Exists(root) && failures.Count == 0 && !IsKept(root, keep)
            && !KeepsAnythingUnder(root, keep))
            failures.Add(new RemovalFailure(root, "the folder is still there"));

        return failures;
    }

    //what the map promised survives, as paths the removal must not cross. a model row's own path is the truth, since weights_root can point anywhere
    public static IReadOnlyList<string> KeptUnder(IReadOnlyList<OwnedItem> map, string home)
    {
        var kept = new List<string>();
        foreach (var item in map)
            if (item.Mark == UninstallMark.Stays && item.Where is { Length: > 0 } where
                && Gatto.Core.Acquire.ModelLocation.IsInside(home, where))
                kept.Add(where);

        //the kept paths come from the map rows and nothing else. a hard-coded weights folder kept a path no row mentioned, and made the safety net below unreachable
        return kept;
    }

    private static bool IsKept(string path, IReadOnlyList<string> keep)
    {
        foreach (var k in keep)
        {
            try
            {
                if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(k),
                        StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch (Exception) { } //a malformed keep path throws out of GetFullPath, so catch it and treat it as not kept
            if (Gatto.Core.Acquire.ModelLocation.IsInside(k, path)) return true;
        }
        return false;
    }

    private static bool KeepsAnythingUnder(string dir, IReadOnlyList<string> keep) =>
        keep.Any(k => Gatto.Core.Acquire.ModelLocation.IsInside(dir, k) || IsKept(dir, keep));

    //removes directories bottom-up and says whether this one went. a reparse point is deleted as the link it is and never descended into
    private static bool Prune(string dir, IReadOnlyList<string> keep, List<RemovalFailure> failures)
    {
        if (IsKept(dir, keep)) return false;

        var emptied = true;
        try
        {
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                if (!Descends(new DirectoryInfo(sub).Attributes))
                {
                    if (IsKept(sub, keep)) { emptied = false; continue; }
                    try { Directory.Delete(sub); }
                    catch (Exception ex)
                    {
                        failures.Add(new RemovalFailure(sub, ex.Message));
                        emptied = false;
                    }
                    continue;
                }
                if (!Prune(sub, keep, failures)) emptied = false;
            }
            if (Directory.EnumerateFiles(dir).Any()) emptied = false;
        }
        catch (Exception ex) { failures.Add(new RemovalFailure(dir, ex.Message)); return false; }

        if (!emptied) return false;
        try { Directory.Delete(dir); return true; }
        catch (Exception ex) { failures.Add(new RemovalFailure(dir, ex.Message)); return false; }
    }

    //whether the removal may descend into a directory with these attributes. a reparse point is skipped, since descending a link deletes its target's files
    internal static bool Descends(FileAttributes attributes) =>
        (attributes & FileAttributes.ReparsePoint) == 0;

    //every file under dir, gathered before anything is deleted, so an unreadable directory is reported before half the work is done
    private static void Collect(string dir, List<string> files, List<RemovalFailure> failures)
    {
        try
        {
            files.AddRange(Directory.EnumerateFiles(dir));
            foreach (var sub in Directory.EnumerateDirectories(dir))
                if (Descends(new DirectoryInfo(sub).Attributes)) Collect(sub, files, failures);
        }
        catch (Exception ex) { failures.Add(new RemovalFailure(dir, ex.Message)); }
    }

    //another gatto is running and may hold files under the home. reported, since it may be someone's live session, and it is not a map row
    public static string? SiblingNotice(IReadOnlyList<int> siblings) =>
        siblings.Count == 0 ? null
            : siblings.Count == 1
                ? $"another gatto is running (pid {siblings[0]}). It may be holding files under "
                  + "this folder, and those won't be removed. Close it first."
                : $"{siblings.Count} other gatto processes are running "
                  + $"(pids {string.Join(", ", siblings)}). They may be holding files under this "
                  + "folder, and those won't be removed. Close them first.";

    //what stayed after the deed, since the map's removed row only plans. the advice is to delete by hand, because this run removed the command itself
    public static string? PartialRemovalNotice(
        string home, IReadOnlyList<RemovalFailure> failures, IReadOnlyList<int> siblings)
    {
        if (failures.Count == 0) return null;

        var what = Gatto.Core.Plural.Of(failures.Count, "file");
        var line = $"{home} was NOT fully removed. {what} stayed.";
        return siblings.Count == 0
            ? line + " Delete the folder by hand once whatever is holding them has closed."
            //the wording is "is probably holding", a correlation, since a sibling exists and files stayed, and an ACL denial would otherwise take the blame
            : line + $" Another gatto (pid {string.Join(", ", siblings)}) is probably holding them; "
                   + "close it, then delete the folder by hand.";
    }

    //an unreadable folder answers false, keep the disk read here so the map stays a pure function of the facts handed in
    public static bool HasUnnamedWeights(string home, IReadOnlyList<(string Id, string ModelPath)> models)
    {
        try
        {
            var dir = Gatto.Core.Acquire.ModelLocation.SuggestedDir(home, null);
            if (!Directory.Exists(dir)) return false;

            var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (_, path) in models)
                try { named.Add(Path.GetFullPath(path)); } catch (Exception) { } //a path GetFullPath cannot resolve names nothing

            return Directory.EnumerateFiles(dir, "*.gguf", SearchOption.AllDirectories)
                .Any(f => !named.Contains(Path.GetFullPath(f)));
        }
        catch (Exception) { return false; }
    }

    private static string Size(long bytes)
    {
        return SizeWords.Gb(bytes, approx: true);
    }

    //null means the size could not be measured, and it is public so the caller reuses this rule instead of its own copy
    public static long? FileSizeOrNull(string path)
    {
        try { return new FileInfo(path) is { Exists: true } f ? f.Length : null; }
        catch (Exception) { return null; }
    }
}
