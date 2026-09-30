using System.IO.Compression;

namespace Gatto.Cli;

//what a fetch left behind or why it did not, with a null error as the only success
internal sealed record EngineFetch(string? ServerPath, string? Error);

//what one half's download and digest check produced, a zip or the reason there is none. exactly one of the two fields is set
internal readonly record struct Landing(string? Zip, string? Error);

//the download engine is UpdateDownload's and stays shared. an entry that escapes the destination refuses the whole fetch, and the folder is removed
internal static class LlamaFetch
{
    //llama.cpp's release payload, parsed by the same UpdateCheck.ParseRelease the updater uses
    public static string ReleaseUrl(string tag) =>
        $"https://api.github.com/repos/ggml-org/llama.cpp/releases/tags/{tag}";

    //where a fetched engine lives, composed once so the fetch and EngineSearch agree. a folder here whose banner does not match its name was placed by hand
    public static string DirFor(string home, string tag) => Path.Combine(home, "llama", tag);

    //both halves arrive and verify before anything is unpacked. the sequence is a pure helper so it can be driven without the network
    public static EngineFetch Land(
        Gatto.Cli.Setup.EnginePair assets,
        Func<Gatto.Cli.Setup.EngineAsset, Landing> get,
        Func<string, bool, EngineFetch> extract)
    {
        (Gatto.Cli.Setup.EngineAsset Asset, bool IsServer)[] halves = assets.Companion is { } companion
            ? [(assets.Server, true), (companion, false)]
            : [(assets.Server, true)];

        //nothing is written yet, so a refusal here leaves no folder to clean up
        var ready = new List<(string Zip, bool IsServer)>();
        foreach (var (asset, isServer) in halves)
        {
            //one call, asking again for the error would download a second time and could succeed
            var got = get(asset);
            if (got.Zip is not { } zip)
                return new(null, got.Error
                    ?? $"gatto couldn't get {asset.ZipName}, so nothing was extracted.");
            ready.Add((zip, isServer));
        }

        //only now is anything unpacked, server first. a refusal here has already deleted the destination, so both-or-nothing holds for the folder too
        EngineFetch? landed = null;
        foreach (var (zip, isServer) in ready)
        {
            var one = extract(zip, isServer);
            if (one.ServerPath is null) return one;
            landed ??= one;
        }

        //non-null by construction, halves always holds the server
        return landed!;
    }

    //each refusal takes the whole folder, a half-extracted engine is worse than none. the server's zip needs exactly one llama-server.exe and the companion none
    public static EngineFetch Extract(string zipPath, string intoDir, bool expectServer = true)
    {
        var root = Path.GetFullPath(intoDir);

        try
        {
            Directory.CreateDirectory(root);
            using var archive = ZipFile.OpenRead(zipPath);

            //every entry is checked against the destination before a byte is written. the compare is on the full path, so a same-prefix sibling is outside
            var fence = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) continue;   //a trailing slash marks a folder, so there is nothing to extract
                var target = Path.GetFullPath(Path.Combine(root, entry.FullName));
                if (!target.StartsWith(fence, StringComparison.OrdinalIgnoreCase))
                    return Refuse(root,
                        $"the download contains a file that would be written outside gatto's own "
                        + $"folder ({entry.FullName}), so nothing was extracted.");
            }

            //exactly one server in the server's zip and none in the companion's, and the refusal says the count it found
            var servers = archive.Entries
                .Where(e => string.Equals(Path.GetFileName(e.FullName),
                    Gatto.Core.Acquire.LlamaSiblings.ServerName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var wanted = expectServer ? 1 : 0;
            if (servers.Count != wanted)
                return Refuse(root,
                    $"the download contains {servers.Count} copies of "
                    + $"{Gatto.Core.Acquire.LlamaSiblings.ServerName} and gatto expected {wanted}, "
                    + "so nothing was extracted.");

            //the wrapper folder is flattened away, prefix filters and GetFileName flattens. the companion needs no prefix, a runtime archive has no anchor to filter on
            var from = (expectServer ? Path.GetDirectoryName(servers[0].FullName) : null)
                ?.Replace('\\', '/') ?? "";
            var prefix = from.Length == 0 ? "" : from + "/";

            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) continue;
                var rel = entry.FullName.Replace('\\', '/');
                if (prefix.Length > 0 && !rel.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

                var name = Path.GetFileName(rel);
                if (name.Length == 0) continue;
                entry.ExtractToFile(Path.Combine(root, name), overwrite: true);
            }

            var exe = Path.Combine(root, Gatto.Core.Acquire.LlamaSiblings.ServerName);
            //the companion reports the folder it went into, asking for a server would refuse every runtime archive
            if (!expectServer) return new EngineFetch(root, null);
            return File.Exists(exe)
                ? new EngineFetch(exe, null)
                : Refuse(root, "the download extracted without leaving a server behind, so nothing was kept.");
        }
        catch (Exception ex) { return Refuse(root, $"couldn't read the download: {ex.Message}"); }
    }

    //every refusal deletes the destination, and the delete's own failure is swallowed so it cannot bury the sentence
    private static EngineFetch Refuse(string dir, string sentence)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (Exception) { } //the delete's failure is swallowed so the sentence survives
        return new EngineFetch(null, sentence);
    }
}
