using System.Security.Cryptography;
using System.Text;

namespace Gatto.Extensions;

//the extensions, agent files and role files gatto ships, written once each so a user edit survives and a deleted file stays deleted
public static class ShippedExtensions
{
    //the files under Gatto\Shipped, and the rule each one keeps

    //the shipped ask_user.csx rejects a missing or non-string field, an empty one reaches the length check, every message matches the built-in it replaced

    //the shipped web_search.csx registers its two tools with no readClass, so they still prompt, a DDG bot-check page throws rather than reading as no results

    //each agents/*.md body is a whole reviewer prompt with nothing factored out, so editing one never changes another

    //the roles/*.json append values hold escaped newlines and backslashes that load back as newlines and paths, so leave the escapes alone

    //the manifest prefix every shipped resource has, from the csproj glob, defined once so the loader and its guards cannot drift
    internal const string ResourcePrefix = "shipped/";

    //a path relative to the gatto folder with forward slashes, to the file's bytes decoded as UTF-8
    private static readonly Dictionary<string, string> _files = LoadShipped();

    //read every embedded Shipped\ file, the bytes are taken verbatim and the manifest name is normalised once to a forward-slash key
    private static Dictionary<string, string> LoadShipped()
    {
        var asm = typeof(ShippedExtensions).Assembly;
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var name in asm.GetManifestResourceNames())
        {
            if (!name.StartsWith(ResourcePrefix, StringComparison.Ordinal)) continue;
            var key = name[ResourcePrefix.Length..].Replace('\\', '/');

            using var stream = asm.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException(
                    $"shipped resource '{name}' is named in the manifest but could not be opened");
            using var mem = new MemoryStream();
            stream.CopyTo(mem);
            var bytes = mem.ToArray();

            map[key] = DecodeShipped(key, bytes);
        }

        return map;
    }

    //bytes to canonical text, and its own method so a test can drive the BOM refusal
    internal static string DecodeShipped(string key, byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            throw new InvalidOperationException(
                $"shipped file '{key}' starts with a UTF-8 BOM — save it without one "
                + "(.editorconfig sets charset for Gatto/Shipped/**). A BOM changes the bytes gatto "
                + "writes and the extension's vetting hash.");

        return Encoding.UTF8.GetString(bytes);
    }

    //the shipped files keyed by their forward-slash path relative to the gatto folder
    public static IReadOnlyDictionary<string, string> Files => _files;

    //hashes of earlier canonical revisions, a copy whose hash is in here is stale but unedited and safe to upgrade in place
    private static readonly string[] _historicalHashes =
    {
        //web_search.csx rev 1
        "bc6ae90a0c690bfe6c6a23a53cebbacf5d302254ca44bda726155e5e957d6925",
        //web_search.csx rev 2
        "506f3a8bb5945da7ee89db491eaa996a3910c5cb670f3ec5a6438287799f8396",
        //web_search.csx rev 3
        "c97e1490780479382036df4f0ecce197eb3dcc6cfdcea3ff6fbd2e9406ed0fe7",
        //web_search.csx rev 4
        "f359ec4db6f3d6b6046b233cd4b851651df2c0cf80658ddc3acd1fb6e55af813",
        //ask_user.csx rev 1
        "2475a86d18eae528628b0ee0c40af1f95e668bf4207dcaf76695e28a4895b8e0",
        //ask_user.csx rev 2
        "a85b293c6761f25e6bcb86afdf4ef7d1aa8f4968a911f9ab868c330e6ef4a6b8",
        //ask_user.csx rev 3
        "6220ab72ba0355c42e56da400eb6c16d9c6187c292a6f06c7fa6cec95f6fa4ed",
        //ask_user.csx rev 4
        "a9878d47e69c7162244874806f2d79353529218e9a986ee24107c95a20bf272c",
        //ask_user.csx rev 5
        "3d7e1c6bf925346044ab37c8f091a342a5554c368e88080153f303306287bf90",
        //ask_user.csx rev 6
        "3a7090326db8e0c08bf4be4063afa2b2a0be32810dc4495e61c569361b6bde62",
        //shipped role file hashes start here, coder.json rev A
        "4ec9937c9729b15fddea77b390baa9b40c49a6a1294823356ef42bd54c9561fd",
        //coder.json rev B
        "f17c804ca2366a477389c8492655354f350f105d1c0065a6e066fc88ecfff709",
        //coder.json rev C, the last one that set a temperature
        "bf8418827f2d76f7a536db2dc4f5203d2ea321c02d77751c6c2ea975845a46a4",
        //oracle.json rev O1
        "41f74b3baf05beb65aa1bc2638668eb1410b27e22f5584785c525372fdb6384a",
        //oracle.json rev O2
        "ccc0c9353b6e9e5dfa4552126e3d6bdb0f96c13cf3a75a0ef90a1ad0c8d5f20a",
        //shipped .csx revisions from before the public-comment pass start here, ask_user.csx first
        "d9674d68295a1b9296b196e5d169267064318f0e10615b76de41342df2d8e39b",
        //web_search.csx, the revision before the public-comment pass
        "c765f3c0b3d26be8ac58b1760cc3bd0cf68cb86d4cdd0bb923a18fea3baf4683",
        //ask_user.csx, the revision before it declared a policy line, its description unchanged
        "71f381baac1c5a2ee2a377eb308aa431efecd92393af0123a20e5a48da3fb5bf",
        //ask_user.csx before its width alias named the terminal library, which an installed copy can no longer compile against
        "b07b989999757448219958fa71d5d913abb97340daef614812aa6fd7e7e8203a",
        //ask_user.csx while it refused a header over 32 chars
        "f8a8110df3bbc9dd4535cee47d75c70a1899815e64ee1b87334a84505efdeb6b",
    };

    //hashes of superseded shipped revisions, an unmodified copy that matches one is safely overwritten by the current text
    public static IReadOnlyList<string> HistoricalHashes => _historicalHashes;

    private static readonly HashSet<string> _historicalSet = new(_historicalHashes, StringComparer.Ordinal);

    //the home-relative paths of shipped files still at an unmodified older revision, by EnsureWritten's test, writing nothing
    internal static IReadOnlyList<string> StaleShippedPaths(string homePath) =>
        StaleShippedPaths(homePath, Files, _historicalSet);

    //the overload where the historical set is injectable, mirroring the EnsureWritten seam
    internal static IReadOnlyList<string> StaleShippedPaths(
        string homePath, IReadOnlyDictionary<string, string> files, IReadOnlySet<string> historical)
    {
        var stale = new List<string>();
        foreach (var (relPath, text) in files)
        {
            var full = Path.Combine(homePath, relPath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full)) continue;

            var onDiskHash = SingleFileContentHash(relPath, File.ReadAllBytes(full));
            var currentHash = SingleFileContentHash(relPath, Encoding.UTF8.GetBytes(text));
            if (onDiskHash != currentHash && historical.Contains(onDiskHash)) stale.Add(relPath);
        }
        return stale;
    }

    //the allow-list IsVetted tests against, the current canonical hashes plus every historical one
    public static IReadOnlySet<string> VettedContentHashes { get; } = ComputeVettedHashes(_files, _historicalHashes);

    private const string ManifestFileName = ".shipped";

    //true when the source's content hash is on the allow-list, so only a byte-for-byte shipped copy is vetted
    public static bool IsVetted(ExtensionSource src) => IsVettedAgainst(src, VettedContentHashes);

    //a seam so a test can vet against its own set, production always passes VettedContentHashes
    internal static bool IsVettedAgainst(ExtensionSource src, IReadOnlySet<string> vetted) =>
        vetted.Contains(ExtensionDiscovery.ContentHash(src));

    //write each shipped file once, recorded in the .shipped manifest so a deletion sticks. only an unmodified shipped copy is overwritten, everything else stays
    public static void EnsureWritten(string homePath, IReadOnlyDictionary<string, string>? files = null) =>
        EnsureWritten(homePath, files ?? Files, _historicalSet);

    //the overload where the historical set is injectable, so a test can drive the in-place upgrade against a set it picks
    internal static void EnsureWritten(
        string homePath, IReadOnlyDictionary<string, string> files, IReadOnlySet<string> historical)
    {
        var manifestPath = Path.Combine(homePath, ManifestFileName);

        var manifestLines = new List<string>();
        var manifest = new HashSet<string>(StringComparer.Ordinal);
        if (File.Exists(manifestPath))
            foreach (var raw in File.ReadAllLines(manifestPath))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                if (manifest.Add(line)) manifestLines.Add(line);
            }

        var manifestChanged = false;

        foreach (var (relPath, text) in files)
        {
            var full = Path.Combine(homePath, relPath.Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(full))
            {
                if (manifest.Contains(relPath))
                    continue;                                   //shipped once and recorded in the manifest, so the user's deletion stands

                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, text);                  //first run for this file, write it and record it in the manifest
                manifest.Add(relPath);
                manifestLines.Add(relPath);
                manifestChanged = true;
                continue;
            }

            //record a file that is already on disk so a later deletion sticks on the first try
            if (manifest.Add(relPath))
            {
                manifestLines.Add(relPath);
                manifestChanged = true;
            }

            //only an unmodified old shipped copy is upgraded, a user edit stays
            var onDiskHash = SingleFileContentHash(relPath, File.ReadAllBytes(full));
            var currentHash = SingleFileContentHash(relPath, Encoding.UTF8.GetBytes(text));
            if (onDiskHash != currentHash && historical.Contains(onDiskHash))
                File.WriteAllText(full, text);
        }

        if (manifestChanged)
        {
            Directory.CreateDirectory(homePath);
            File.WriteAllText(manifestPath, string.Join('\n', manifestLines) + '\n');
        }
    }

    //build the allow-list from a file map plus the historical hashes, a seam so a test can vet without the empty production Files
    internal static IReadOnlySet<string> ComputeVettedHashes(
        IReadOnlyDictionary<string, string> files, IEnumerable<string> historical)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (relPath, text) in files)
            set.Add(VettedHashFor(relPath, text));
        foreach (var h in historical)
            set.Add(h);
        return set;
    }

    //the vetting hash of one shipped file, the single-file shape of ExtensionDiscovery.ContentHash
    internal static string VettedHashFor(string homeRelPath, string text) =>
        SingleFileContentHash(homeRelPath, Encoding.UTF8.GetBytes(text));

    //SHA-256 over the UTF-8 file name, a NUL and the bytes with line endings normalised like ContentHash does
    private static string SingleFileContentHash(string homeRelPath, byte[] fileBytes)
    {
        var fileName = Path.GetFileName(homeRelPath.Replace('\\', '/'));
        using var incremental = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        incremental.AppendData(Encoding.UTF8.GetBytes(fileName));
        incremental.AppendData(new byte[] { 0 });
        incremental.AppendData(ExtensionDiscovery.NormalizeNewlines(fileBytes));
        return Convert.ToHexString(incremental.GetHashAndReset()).ToLowerInvariant();
    }
}
