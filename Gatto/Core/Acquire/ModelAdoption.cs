using System.Runtime.Versioning;

namespace Gatto.Core.Acquire;

//one home for where a model file is and where it should go, so the doctor line and the move offer cannot disagree
internal static class ModelLocation
{
    //the Downloads folder as Windows reports it, with the concatenation kept only as the fallback when the shell call fails
    public static string DownloadsDir =>
        Resolve(KnownDownloadsPath(), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    //pure so a test can drive the fallback: the known folder when the shell gave one, the concatenation otherwise
    internal static string Resolve(string? knownFolder, string userProfile) =>
        knownFolder is { Length: > 0 } known ? known : Path.Combine(userProfile, "Downloads");

    //the shell call needs this GUID because .NET's SpecialFolder has no Downloads member
    private static readonly Guid FolderIdDownloads = new("374DE290-123F-4565-9164-39C4925E467B");

    //check Windows at runtime, since a [SupportedOSPlatform] attribute cascades the platform warning into every caller
    private static string? KnownDownloadsPath()
    {
        if (!OperatingSystem.IsWindows()) return null;

        var ptr = IntPtr.Zero;
        try
        {
            return SHGetKnownFolderPath(FolderIdDownloads, 0, IntPtr.Zero, out ptr) == 0
                ? System.Runtime.InteropServices.Marshal.PtrToStringUni(ptr)
                : null;
        }
        catch (Exception) { return null; }   //a shell that will not answer falls back to the guessed path
        finally
        {
            if (ptr != IntPtr.Zero) System.Runtime.InteropServices.Marshal.FreeCoTaskMem(ptr);
        }
    }

    //the declaration stays DllImport because LibraryImport needs AllowUnsafeBlocks turned on for the whole project. the other P/Invoke in the tree does the same
    [SupportedOSPlatform("windows")]
    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPStruct)] Guid rfid,
        uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

    //a prefix test says C:\Downloads-old is inside C:\Downloads, so this uses Path.GetRelativePath. a malformed path answers false
    public static bool IsInside(string dir, string path)
    {
        try
        {
            var rel = Path.GetRelativePath(dir, Path.GetFullPath(path));
            return rel != "." && !rel.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(rel);
        }
        catch (Exception) { return false; }
    }

    //the configured weights_root wins, so gatto does not second-guess where the user's weights go. otherwise home\weights, a suggestion and never a mandate
    public static string SuggestedDir(string homePath, string? configuredWeightsDir) =>
        configuredWeightsDir is { Length: > 0 } d ? d : Path.Combine(homePath, "weights");

    //one folder per model under the weights root, and the only place that joins the two. the id becomes a path segment, so validate it through ModelId first
    public static string ForModel(string homePath, string? configuredWeightsDir, string modelId) =>
        ForModelIn(SuggestedDir(homePath, configuredWeightsDir), modelId);

    //for the write path, which already holds the weights root, so the join and the id validation stay in one place
    public static string ForModelIn(string weightsRoot, string modelId)
    {
        Gatto.Core.Models.ModelId.Validate(modelId);
        return Path.Combine(weightsRoot, modelId);
    }

    //true when the move crosses volumes, which the screen must say before starting, since a copy of 30 GB is not free
    public static bool IsCrossVolume(string path, string destDir)
    {
        try
        {
            return !string.Equals(
                Path.GetPathRoot(Path.GetFullPath(path)),
                Path.GetPathRoot(Path.GetFullPath(destDir)),
                StringComparison.OrdinalIgnoreCase);
        }
        //an unreadable path answers true, since warning about a copy that turns out to be free costs a sentence
        catch (Exception) { return true; }
    }
}

//what to adopt and how to record it. the config write is injected so a test can drive the failing leg
internal sealed record AdoptionRequest(string SourcePath, string DestinationDir, Action<string> WriteConfigPath);

//where the model ended up: the destination on success, the untouched original on failure
internal sealed record AdoptionResult(bool Ok, string FinalPath, bool WasRename, string? Detail);

//write the config before deleting the original, so a failure leaves the config pointing at a file that exists. a shard set moves as one unit
internal static class ModelAdoption
{
    //forceCopy drives the copy branch on a one-volume machine, and afterArrival corrupts a file as it arrives to reach the size check
    public static async Task<AdoptionResult> MoveAsync(
        AdoptionRequest request, CancellationToken ct,
        bool forceCopy = false, Action<string>? afterArrival = null)
    {
        var sources = ModelDiscovery.ShardSiblings(Path.GetFullPath(request.SourcePath));
        var primary = sources[0];

        if (!File.Exists(primary))
            return new AdoptionResult(false, primary, false, $"no such model file: {primary}");

        //roll back the folder too, since an empty weights folder reads later as a model with no files
        var weCreatedDestination = !Directory.Exists(request.DestinationDir);
        Directory.CreateDirectory(request.DestinationDir);
        var rename = !forceCopy && SameVolume(primary, request.DestinationDir);

        var planned = sources
            .Select(s => (Source: s, Dest: Path.Combine(request.DestinationDir, Path.GetFileName(s))))
            .ToList();

        //never overwrite: a file already there is someone's data, so stop and name it
        foreach (var (_, dest) in planned)
            if (File.Exists(dest))
                return new AdoptionResult(false, primary, false, $"a file already exists at {dest}");

        var landed = new List<(string Source, string Dest, long Size)>();
        try
        {
            foreach (var (source, dest) in planned)
            {
                ct.ThrowIfCancellationRequested();
                var size = new FileInfo(source).Length;

                if (rename) File.Move(source, dest);
                else await CopyAsync(source, dest, ct).ConfigureAwait(false);

                afterArrival?.Invoke(dest);

                //verify by size only, since the checksum already ran upstream when the user chose one
                var arrived = new FileInfo(dest);
                if (!arrived.Exists || arrived.Length != size)
                {
                    landed.Add((source, dest, size));
                    throw new IOException(
                        $"{Path.GetFileName(dest)} arrived as {(arrived.Exists ? arrived.Length : 0)} bytes, expected {size}");
                }
                landed.Add((source, dest, size));
            }

            //the config write comes last and is the commit point of the whole move
            request.WriteConfigPath(Path.Combine(request.DestinationDir, Path.GetFileName(primary)));
        }
        catch (Exception ex)
        {
            Rollback(landed, rename);
            //remove the folder only if this call created it and only while empty, since a destination the user already had is theirs
            if (weCreatedDestination) TryRemoveEmptyDirectory(request.DestinationDir);
            return new AdoptionResult(false, primary, rename, ex.Message);
        }

        //the original is deleted only after the config is committed, so a copy leaves it in place until then
        if (!rename)
            foreach (var (source, _, _) in landed)
                try { File.Delete(source); } catch (Exception) { } //the copy is committed, so a stale original is untidy and never unsafe

        return new AdoptionResult(true, Path.Combine(request.DestinationDir, Path.GetFileName(primary)), rename, null);
    }

    //undo whatever landed so the world looks as it did: a rename goes back, a copy is deleted

    //best effort: Directory.Delete without recursion throws when the folder is not empty, so nothing here checks
    private static void TryRemoveEmptyDirectory(string dir)
    {
        try { Directory.Delete(dir); }
        catch (Exception) { } //the folder may be non-empty, gone or locked, and a rollback never throws
    }

    private static void Rollback(List<(string Source, string Dest, long Size)> landed, bool rename)
    {
        foreach (var (source, dest, _) in landed)
        {
            try
            {
                if (!File.Exists(dest)) continue;
                if (rename) File.Move(dest, source, overwrite: false);
                else File.Delete(dest);
            }
            catch (Exception) { } //best effort: a rollback must never throw
        }
    }

    private static async Task CopyAsync(string source, string dest, CancellationToken ct)
    {
        await using var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        await using var dst = new FileStream(dest, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);
        await src.CopyToAsync(dst, 1 << 20, ct).ConfigureAwait(false);
    }

    private static bool SameVolume(string a, string b) =>
        string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)),
            StringComparison.OrdinalIgnoreCase);
}

//a Downloads file can be deleted after N days with Storage Sense on, so a model left there is at risk. this read informs a sentence and decides nothing
internal static class StorageSense
{
    //three answers: "armed" when Storage Sense is on, "off" when disabled, null when unreadable or not Windows
    public static string? DescribeState()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try { return ReadPolicy(); }
        catch (Exception) { return null; }
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadPolicy()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\StorageSense\Parameters\StoragePolicy");
        if (key?.GetValue("01") is not int enabled) return null;
        return enabled != 0 ? "armed" : "off";
    }
}
