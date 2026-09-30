namespace Gatto.Roles;

//an engine found on disk. the GattoFolder field is set when gatto fetched that folder, and null when gatto can't say where the engine came from
internal readonly record struct FoundEngine(string Path, string? Build, string? GattoFolder = null);

//only PATH, gatto's own llama folder and <drive>\llama* one level in are swept, a disk-wide hunt would find engines nobody installed
internal static class EngineSearch
{
    public const string ExeName = "llama-server.exe";

    //every engine found, each once, in a fixed order: gatto's folder, the configured exe, PATH, the drive. the configured path has no default, so a caller can't leave it out by accident
    public static IReadOnlyList<FoundEngine> Find(
        IEnumerable<string>? pathDirs, string? gattoLlamaRoot, string? driveRoot, string? configured)
    {
        var found = new List<FoundEngine>();

        //gatto's own folder is swept first, since the de-duplication keeps the first sighting. an engine that is also on PATH loses its folder name otherwise
        foreach (var build in Children(gattoLlamaRoot))
            Keep(found, build, System.IO.Path.GetFileName(build));

        //the configured path is checked after gatto's own folder, so an engine under that root keeps the folder name of the earlier sighting
        KeepExe(found, configured);

        foreach (var dir in pathDirs ?? [])
            Keep(found, dir);

        //the drive sweep takes <drive>\llama* and its immediate children, two segments below the root at most, like an extracted zip
        foreach (var top in Children(driveRoot, "llama*"))
        {
            Keep(found, top);
            foreach (var inner in Children(top))
                Keep(found, inner);
        }

        return found;
    }

    //adds <dir>\llama-server.exe when it is there and not already known. no try/catch, since File.Exists answers false rather than throwing
    private static void Keep(List<FoundEngine> found, string? dir, string? gattoFolder = null)
    {
        if (string.IsNullOrWhiteSpace(dir)) return;
        KeepExe(found, System.IO.Path.Combine(dir, ExeName), gattoFolder);
    }

    //the same check for the configured path, which holds an exe rather than a folder. one file found twice stays one engine
    private static void KeepExe(List<FoundEngine> found, string? exe, string? gattoFolder = null)
    {
        if (string.IsNullOrWhiteSpace(exe)) return;

        if (System.IO.File.Exists(exe)
            && !found.Any(f => string.Equals(f.Path, exe, StringComparison.OrdinalIgnoreCase)))
            found.Add(new FoundEngine(exe, null, gattoFolder));
    }

    //enumerating a directory can throw where testing a file cannot, so the catch stays even though no test can make it fire
    private static IReadOnlyList<string> Children(string? root, string pattern = "*")
    {
        if (string.IsNullOrWhiteSpace(root)) return [];

        try
        {
            return System.IO.Directory.Exists(root)
                ? [.. System.IO.Directory.EnumerateDirectories(root, pattern).OrderBy(d => d, StringComparer.OrdinalIgnoreCase)]
                : [];
        }
        catch (Exception) { return []; }
    }
}
