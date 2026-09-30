using System.Security.Cryptography;
using System.Text;

namespace Gatto.Extensions;

//one discovered .csx extension, a single top-level file or a folder with main.csx. the file list holds every .csx under it, in the order the host compiles them
public sealed record ExtensionSource(string Name, string EntryPath, IReadOnlyList<string> Files);

//find the .csx extensions under ~\.gatto\extensions\ and compute both hashes. the content hash re-prompts approval on a byte change, the cache hash names the DLL
public static class ExtensionDiscovery
{
    private const string EntryFileName = "main.csx";

    //find every top-level .csx file and every folder that has a main.csx. a folder with no main.csx is skipped and reported, a dot-prefixed entry is ignored
    public static IReadOnlyList<ExtensionSource> Discover(string extensionsDir, Action<string>? diagnostic = null)
    {
        if (!Directory.Exists(extensionsDir)) return Array.Empty<ExtensionSource>();

        var sources = new List<ExtensionSource>();

        foreach (var file in Directory.EnumerateFiles(extensionsDir, "*.csx", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (name.StartsWith('.')) continue;

            var fullPath = Path.GetFullPath(file);
            sources.Add(new ExtensionSource(name, fullPath, new[] { fullPath }));
        }

        foreach (var dir in Directory.EnumerateDirectories(extensionsDir, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(name) || name.StartsWith('.')) continue;   //the dot check also skips .cache

            var entryPath = Path.Combine(dir, EntryFileName);
            if (!File.Exists(entryPath))
            {
                diagnostic?.Invoke($"extension folder '{name}' has no main.csx — skipped");
                continue;
            }

            var files = Directory.EnumerateFiles(dir, "*.csx", SearchOption.AllDirectories)
                .Select(Path.GetFullPath)
                .OrderBy(f => Path.GetRelativePath(dir, f), StringComparer.Ordinal)
                .ToList();

            sources.Add(new ExtensionSource(name, Path.GetFullPath(entryPath), files));
        }

        return sources.OrderBy(s => s.Name, StringComparer.Ordinal).ToList();
    }

    //the cache-naming hash over the three version salts and each file's path plus bytes, so any version bump changes every hash
    public static string ComputeHash(ExtensionSource src, string hostApiVersion, string roslynVersion, string hostBinaryVersion) =>
        HashCore(src, hostApiVersion, roslynVersion + "\0" + hostBinaryVersion);

    //same scheme without the version salts, over the extension's own .csx bytes. the vetting hash, stable across a host upgrade and changed by any byte edit
    public static string ContentHash(ExtensionSource src) =>
        HashCore(src, hostApiVersion: null, roslynVersion: null);

    private static string HashCore(ExtensionSource src, string? hostApiVersion, string? roslynVersion)
    {
        var root = Path.GetDirectoryName(src.EntryPath) ?? string.Empty;

        using var incremental = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        void FeedString(string s) => incremental.AppendData(Encoding.UTF8.GetBytes(s));
        void FeedNul() => incremental.AppendData(new byte[] { 0 });

        if (hostApiVersion is not null && roslynVersion is not null)
        {
            FeedString(hostApiVersion);
            FeedNul();
            FeedString(roslynVersion);
            FeedNul();
        }

        foreach (var file in src.Files)
        {
            //use / so the hash does not depend on how the path was joined
            var relativePath = Path.GetRelativePath(root, file).Replace('\\', '/');
            FeedString(relativePath);
            FeedNul();
            incremental.AppendData(NormalizeNewlines(File.ReadAllBytes(file)));
        }

        return Convert.ToHexString(incremental.GetHashAndReset()).ToLowerInvariant();
    }

    //fold CRLF and lone CR to LF over the raw bytes, so a CRLF checkout hashes the same as the LF canonical. byte-level, so a BOM and every other byte survive
    internal static byte[] NormalizeNewlines(byte[] raw)
    {
        if (Array.IndexOf(raw, (byte)0x0D) < 0) return raw;   //no CR in the file, so nothing to fold

        var outBytes = new List<byte>(raw.Length);
        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] == 0x0D)
            {
                outBytes.Add(0x0A);
                if (i + 1 < raw.Length && raw[i + 1] == 0x0A) i++;   //skip the LF of a CRLF pair
            }
            else outBytes.Add(raw[i]);
        }
        return outBytes.ToArray();
    }
}
