using Gatto.Core.Hardware;

namespace Gatto.Roles;

//the hardware fact is a noun phrase and the build name a bare adjective, so both drop into a sentence. a non-null CudartZipName brings the CUDA runtime too
internal sealed record LlamaAsset(
    string ZipName, string? CudartZipName, string HardwareFact, string BuildName);

//pinned data rather than a question: choosing needs the GPU vendor, the CUDA generation, the CPU architecture and the AVX level
internal static class LlamaAssetSteering
{
    //the release gatto is tested against, rather than latest, and it moves together with the architecture data
    public const string PinnedRelease = "b11071";

    //where a found engine sits against the pin, and only an older one is a reason to fetch the pinned release
    public enum FoundState { Exact, Newer, Older, Fork }

    //place a build against the pin. anything that isn't b plus digits is a fork, even when it has a number in it
    public static FoundState Classify(string? build, string? gattoFolder = null)
    {
        //check the folder first, gatto's own folders are named b plus the banner number, so a folder that doesn't match is a fork
        if (gattoFolder is not null && !IsGattosOwnName(gattoFolder, build)) return FoundState.Fork;

        if (build is null) return FoundState.Fork;
        if (string.Equals(build, PinnedRelease, StringComparison.OrdinalIgnoreCase)) return FoundState.Exact;

        return Number(build) is { } found && Number(PinnedRelease) is { } pinned
            ? found > pinned ? FoundState.Newer : found < pinned ? FoundState.Older : FoundState.Exact
            : FoundState.Fork;
    }

    //true when the folder is b plus the number the exe inside it reports. a fork reports a small number like 1, which a real first release could report too
    private static bool IsGattosOwnName(string folder, string? build) =>
        Number(build) is { } n
        && string.Equals(folder, "b" + n.ToString(System.Globalization.CultureInfo.InvariantCulture),
            StringComparison.OrdinalIgnoreCase);

    //the bare number gets the b prefix every folder, link and pin writes. a name that isn't a number comes back as it is, so a fork never gets a b
    public static string ReleaseName(string build) =>
        Number(build) is { } n && !(build.StartsWith('b') || build.StartsWith('B'))
            ? "b" + n.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : build;

    //the build number, or null when the string isn't an optional b plus digits. don't scan for digits, a fork like b10061-laguna would read as an older release
    private static long? Number(string? build)
    {
        if (build is null) return null;

        var digits = build.StartsWith('b') || build.StartsWith('B') ? build[1..] : build;
        return digits.Length > 0 && long.TryParse(digits, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    //where the pinned assets live, so naming a zip also says where to get it
    private const string ReleaseBase = "https://github.com/ggml-org/llama.cpp/releases";

    //the release page, for when a direct link is wrong, since a page still works after an asset is renamed upstream
    public static string ReleasePage => $"{ReleaseBase}/tag/{PinnedRelease}";

    //the two locations as a person reads them, without the scheme, derived from the base so a pin move only happens in one place
    public static string RepoLabel => Bare(ReleaseBase)[..^"/releases".Length];

    public static string ReleasePageLabel => Bare(ReleasePage);

    private static string Bare(string url) => url["https://".Length..];

    //pick from the pinned table, by the graphics device's PCI vendor id
    public static LlamaAsset Choose(HardwareClass hw)
    {
        var vendor = hw.Snapshot.GraphicsVendorId?.ToUpperInvariant();

        //a device with no graphics budget left is not a GPU here, the classifier already subtracted the reserve
        var hasGpuBudget = hw.GpuBudgetBytes > 0;

        //every HardwareFact is a noun phrase and every BuildName a bare adjective, and neither mentions the CUDA companion that CudartZipName already names
        if (hasGpuBudget && vendor == "10DE")
            return new LlamaAsset(
                $"llama-{PinnedRelease}-bin-win-cuda-12.4-x64.zip",
                $"cudart-llama-bin-win-cuda-12.4-x64.zip",
                "an NVIDIA adapter", "CUDA");

        if (hasGpuBudget && vendor == "1002")
            return new LlamaAsset(
                $"llama-{PinnedRelease}-bin-win-vulkan-x64.zip", null,
                hw.Topology == MemoryTopology.Unified
                    ? "an AMD integrated adapter sharing one memory pool"
                    : "an AMD discrete card",
                "Vulkan");

        //the Intel arm needs its own words, the generic one would say a 8086 adapter
        if (hasGpuBudget && vendor == "8086")
            return new LlamaAsset(
                $"llama-{PinnedRelease}-bin-win-vulkan-x64.zip", null,
                hw.Topology == MemoryTopology.Unified
                    ? "an Intel integrated adapter sharing one memory pool"
                    : "an Intel discrete card",
                "Vulkan");

        if (hasGpuBudget)
            return new LlamaAsset(
                $"llama-{PinnedRelease}-bin-win-vulkan-x64.zip", null,
                vendor is null
                    ? "a usable GPU whose vendor could not be read"
                    : $"a {vendor} adapter",
                "Vulkan");

        //one cpu zip serves every processor, and the fact says no usable GPU since the frame already says your hardware
        return new LlamaAsset($"llama-{PinnedRelease}-bin-win-cpu-x64.zip", null,
            "no usable GPU", "CPU");

    }

    //the one home for path cleanup: paired quotes stripped, either slash accepted, padding trimmed. a lone quote stays, it can be part of a legal filename
    public static string NormalizePath(string input)
    {
        var t = input.Trim();
        if (t.Length >= 2 && ((t[0] == '"' && t[^1] == '"') || (t[0] == '\'' && t[^1] == '\'')))
            t = t[1..^1].Trim();
        return t.Replace('/', Path.DirectorySeparatorChar);
    }

    //the name every llama.cpp Windows build ships its server as
    private const string ServerExe = "llama-server.exe";

    //a folder answers the exe question too, one level down since the zip extracts into its own folder. a path with no exe under it comes back unchanged
    public static string ResolveServerExe(string path)
    {
        if (path.Length == 0 || File.Exists(path)) return path;

        try
        {
            if (!Directory.Exists(path)) return path;

            var direct = Path.Combine(path, ServerExe);
            if (File.Exists(direct)) return direct;

            foreach (var sub in Directory.GetDirectories(path).OrderBy(d => d, StringComparer.Ordinal))
            {
                var nested = Path.Combine(sub, ServerExe);
                if (File.Exists(nested)) return nested;
            }
        }
        catch (Exception)
        {
            //an unreadable folder is not an answer to improve on. hand back what was typed and let verification say what happened
        }

        return path;
    }
}
