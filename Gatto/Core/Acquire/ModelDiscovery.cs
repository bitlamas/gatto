using System.Text.RegularExpressions;
using Gatto.Core.Models;

namespace Gatto.Core.Acquire;

//one home for what a shard file name asserts, used by the disk scan and the Hub listing. whether the files exist is for whoever can see them
internal sealed record ShardName(string Stem, int Index, int Count)
{
    private static readonly Regex Pattern =
        new(@"^(?<stem>.*)-(?<n>\d{5})-of-(?<total>\d{5})\.gguf$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    //the shard name of this file, or null when it has none. takes a path or a bare name and reads only the file name
    public static ShardName? Parse(string pathOrName)
    {
        var m = Pattern.Match(System.IO.Path.GetFileName(pathOrName));
        return m.Success
            ? new ShardName(
                m.Groups["stem"].Value,
                int.Parse(m.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture),
                int.Parse(m.Groups["total"].Value, System.Globalization.CultureInfo.InvariantCulture))
            : null;
    }

    //false for -of-00000, which is a file with a shard-shaped name, so callers ask whether it exists instead of looping
    public bool IsSet => Count > 0;

    //the name shard n would have, always with a lowercase .gguf, since the real path is what keeps the case on disk
    public string FileNameFor(int index) => $"{Stem}-{index:D5}-of-{Count:D5}.gguf";
}

//the Path and Header.Name fields are attacker text, so sanitize at render. the size stays unclamped here, since Estimate is the only clamp point
internal sealed record FoundModel(string Path, long FileBytes, GgufHeader? Header, int ShardsPresent = 1,
    long? StreamedBytes = null)   //bytes of the set the engine never puts on the GPU, read only for a per-layer input header
{
    //the shard set this file belongs to, or null for a plain file or a name that asserts no set
    public ShardName? Set =>
        ShardName.Parse(System.IO.Path.GetFileName(Path)) is { IsSet: true } s ? s : null;

    //a folder holding more files than the name claims is odd naming, so the count is compared with >=
    public bool IsComplete => Set is not { } s || ShardsPresent >= s.Count;
}

//what a model needs and does not have, split into weights and mmproj so a caller's sentence cannot mis-name it
internal sealed record MissingModelFiles(
    IReadOnlyList<string> Model, IReadOnlyList<string> Projector)
{
    public int Count => Model.Count + Projector.Count;
}

//discovery comes first, since re-downloading 30 GB someone already has is user-hostile
internal static class ModelDiscovery
{
    //every file in the same shard set, in shard order, or just the file when it is not a shard. never empty, so element zero is always a usable shard one
    public static IReadOnlyList<string> ShardSiblings(string path)
    {
        if (ShardName.Parse(path) is not { } shard) return [path];

        var dir = System.IO.Path.GetDirectoryName(path) ?? "";
        var found = new List<string>();
        for (var i = 1; i <= 99999; i++)
        {
            var candidate = System.IO.Path.Combine(dir, shard.FileNameFor(i));
            if (!File.Exists(candidate)) break;      //stop at the first gap, shard sets are contiguous from 00001
            found.Add(candidate);
        }
        return found.Count > 0 ? found : [path];
    }

    //the bytes of the model a path belongs to, summed over its shard set. null rather than zero when nothing is readable, so a missing file gets no size clause
    public static long? SetBytesOrNull(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        long total = 0;
        var any = false;
        try
        {
            foreach (var shard in ShardSiblings(path))
                try
                {
                    if (new FileInfo(shard) is not { Exists: true } f) continue;
                    total += f.Length;
                    any = true;
                }
                catch (Exception) { } //keep the total from the shards that did read, one bad file is not a reason to say nothing
        }
        catch (Exception) { return null; }   //an unusable path, such as no directory or bad characters, gives null

        return any ? total : null;
    }

    //an mmproj name or a clip architecture, so a renamed projector is still caught. picking one would scaffold a model llama-server cannot run
    public static bool IsProjector(string path, GgufHeader? header) =>
        System.IO.Path.GetFileName(path).Contains("mmproj", StringComparison.OrdinalIgnoreCase)
        || string.Equals(header?.Architecture, "clip", StringComparison.OrdinalIgnoreCase);

    //without this, a small draft head wins the GPU tier and is picked. match whole tokens of the file name, since a Contains would delete a real model
    public static bool IsCompanionArtifact(string fileName)
    {
        var name = System.IO.Path.GetFileName(fileName);
        var stem = name.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) ? name[..^5] : name;
        var tokens = stem.Split('-', '_', '.');
        if (tokens.Length == 0) return false;

        //leading tokens for the head kinds, anywhere for imatrix. never a bare Contains, since a token in the middle is part of a model's name
        var kinds = Kinds.Value;
        return kinds.SidecarLeading.Contains(tokens[0]) || tokens.Any(kinds.SidecarAnywhere.Contains);
    }

    //half a bit per weight, since no real quantization goes below 1.5 bits. null params pass silently, which is why the names stay the first signal
    public static bool IsTooSmallToBeQuantization(long bytes, long? paramCount) =>
        paramCount is { } p && p > 0 && bytes < p / 16;

    //the kind tokens, read once from model-kinds.json, a dated list someone reviewed
    private static readonly Lazy<ModelKinds> Kinds = new(ModelKinds.Load);

    //zero tensors, since a file with no weights cannot be loaded whatever it says it is. no filename rule, and a null or truncated header answers false
    public static bool IsWeightless(GgufHeader? header) => header?.TensorCount == 0;

    //the same fact as zero tensors, and the count arrives on the listing. null means the expand did not answer, which says nothing about the repo's weights
    public static bool IsWeightless(long? paramCount) => paramCount == 0;

    //true only when every shard parses and reports zero tensors. an unreadable shard answers false for the whole set
    public static bool IsWeightlessSet(string path, GgufHeader? header)
    {
        if (!IsWeightless(header)) return false;

        foreach (var shard in ShardSiblings(path))
        {
            if (string.Equals(shard, path, StringComparison.OrdinalIgnoreCase)) continue;
            if (!IsWeightless(ReadHeader(shard))) return false;
        }
        return true;
    }

    //name first, headers only when no name matches. null means we did not find one, so the caller must not claim the repo has none
    public static string? ProjectorFor(string modelPath)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(modelPath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;

            var siblings = Directory.EnumerateFiles(dir, "*.gguf")
                .Where(p => !string.Equals(p, modelPath, StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var path in siblings)
                if (IsProjector(path, header: null)) return path;

            //no name matched, so the header reads are worth it now
            foreach (var path in siblings)
                if (IsProjector(path, ReadHeader(path))) return path;

            return null;
        }
        catch (Exception) { return null; }
    }

    //checks every file the name claims, since a set with a missing shard cannot load. the two lists stay separate so a sentence cannot call a projector a model
    public static MissingModelFiles MissingFiles(string modelPath, string? projectorPath) =>
        new(MissingFilesFor(modelPath),
            string.IsNullOrWhiteSpace(projectorPath) ? [] : MissingFilesFor(projectorPath));

    private static IReadOnlyList<string> MissingFilesFor(string path)
    {
        //a shard name asserts how many there are, and a nonsense count falls back to the plain does-it-exist question
        if (ShardName.Parse(path) is not { IsSet: true } shard) return File.Exists(path) ? [] : [path];

        var dir = System.IO.Path.GetDirectoryName(path) ?? "";
        var missing = new List<string>();
        for (var i = 1; i <= shard.Count; i++)
        {
            var expected = System.IO.Path.Combine(dir, shard.FileNameFor(i));
            if (!File.Exists(expected)) missing.Add(expected);
        }
        return missing;
    }

    //two levels below each root, which reaches models/<org>/<repo>/x.gguf. roots are taken verbatim and only *.gguf files are read
    public const int AmbientDepth = 2;

    //four levels, since typing a path is consent to look inside, but four is a ceiling rather than an invitation
    public const int TypedDepth = 4;

    public static IReadOnlyList<FoundModel> Scan(IEnumerable<string> roots, int depth = AmbientDepth)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   //keyed on the full path, case-insensitively, so two spellings of one file count once
        foreach (var root in roots)
        {
            foreach (var f in Enumerate(root, depth))
            {
                var full = System.IO.Path.GetFullPath(f);
                files.TryAdd(full, full);
            }
        }

        //group by the canonical shard-one path, so a partial set becomes one candidate. the key is never the path reported, so the extension case on disk survives
        var groups = new Dictionary<string, (string Path, long Bytes, int Files, int Index)>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var full in files.Values)
        {
            long size;
            try { size = new FileInfo(full).Length; }
            catch (Exception) { continue; }   //vanished between enumeration and stat, so skip it

            var shard = ShardName.Parse(full) is { IsSet: true } s ? s : null;
            var key = shard is null
                ? full
                : System.IO.Path.Combine(System.IO.Path.GetDirectoryName(full) ?? "", shard.FileNameFor(1));
            var index = shard?.Index ?? 1;

            if (!groups.TryGetValue(key, out var g)) { groups[key] = (full, size, 1, index); continue; }

            groups[key] = index < g.Index
                ? (full, g.Bytes + size, g.Files + 1, index)
                : (g.Path, g.Bytes + size, g.Files + 1, g.Index);
        }

        var result = new List<FoundModel>(groups.Count);
        foreach (var (path, bytes, count, _) in groups.Values.OrderBy(g => g.Path, StringComparer.OrdinalIgnoreCase))
        {
            var header = ReadHeader(path);
            //a projector is a companion file, so filter it here: what counts as a model on disk is discovery's question
            if (IsProjector(path, header)) continue;
            //keep sidecars out here too, since the predicate was only ever called from the Hub tree. no count line for what discovery drops, like the three filters around it
            if (IsCompanionArtifact(path)) continue;
            //ask it of the set, since shard one of a set can be a metadata-only file with no tensors
            if (IsWeightlessSet(path, header)) continue;
            //skip a file a browser is still writing, or the wizard can adopt it and fail later with the wrong sentence
            if (DownloadInProgress(path)) continue;
            result.Add(new FoundModel(path, bytes, header, count, StreamedBytesOrNull(path, header)));
        }
        return result;
    }

    //the set's streamed tensor bytes, summed over every shard's table. null when there is no per-layer input or a table cannot be read
    public static long? StreamedBytesOrNull(string? path, GgufHeader? header)
    {
        if (string.IsNullOrWhiteSpace(path) || header is not { DeclaresPerLayerInput: true }) return null;
        try { return StreamedTensors.Load().BytesIn(ShardSiblings(path).Select(s => TableOf(ReadHeader(s)))); }
        catch (Exception) { return null; }
    }

    //the same bytes for a caller holding only the path, read through the one header parse so the per-layer gate stays in one place
    public static long? StreamedBytesOrNull(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : StreamedBytesOrNull(path, ReadHeader(path));

    //a complete header with zero tensors gives an empty table, so only an unread table is missing
    private static GgufTensors? TableOf(GgufHeader? h) =>
        h is { Outcome: GgufOutcome.Complete, TensorCount: 0 } ? new GgufTensors(0, []) : h?.Tensors;

    //a .part or .crdownload sibling means a browser is still writing. this never reads the header, so a locked file stays discoverable
    public static bool DownloadInProgress(string ggufPath)
    {
        try
        {
            return File.Exists(ggufPath + ".part") || File.Exists(ggufPath + ".crdownload");
        }
        catch (Exception) { return false; }
    }

    private static GgufHeader? ReadHeader(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return GgufHeaderParser.Parse(fs);   //the parse never throws on bytes, and Malformed is a real answer
        }
        catch (Exception) { return null; }       //vanished or unreadable since enumeration, so there is no header to read
    }

    //the root plus depth levels of subdirectory, and the budget counts directories, so depth 0 still returns the root's files
    private static IEnumerable<string> Enumerate(string root, int depth)
    {
        foreach (var f in SafeFiles(root)) yield return f;
        if (depth <= 0) yield break;
        foreach (var sub in SafeDirs(root))
            foreach (var f in Enumerate(sub, depth - 1))
                yield return f;
    }

    private static string[] SafeFiles(string dir)
    {
        try { return Directory.GetFiles(dir, "*.gguf"); }
        catch (Exception) { return []; }         //missing, unreadable, or deleted during the scan, so nothing is listed
    }

    private static string[] SafeDirs(string dir)
    {
        try { return Directory.GetDirectories(dir); }
        catch (Exception) { return []; }
    }
}
