using Gatto.Core.Home;

namespace Gatto.Core.Models;

//a null context means the file declares none (the scaffolder leaves the key out and says so), and pooling_type is the embedding-model tell
public sealed record GgufMetadata(string Architecture, string? Name, int? ContextLength, int? PoolingType = null);

//a thin wrapper over GgufHeaderParser that turns a malformed header into a path-prefixed GattoConfigException, reading only the first few kilobytes
public static class GgufReader
{
    public static GgufMetadata Read(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var h = GgufHeaderParser.Parse(fs);
            if (h.Outcome == GgufOutcome.Malformed)
                throw new GattoConfigException($"{path} {h.Malformation}");
            if (h.Outcome == GgufOutcome.Truncated)
                throw new GattoConfigException($"{path} is truncated or not a valid GGUF");
            if (h.Architecture is null)
                throw new GattoConfigException($"{path} declares no general.architecture");
            int? ctx = h.ContextLength is > 0 and <= int.MaxValue ? (int)h.ContextLength : null;
            int? pooling = h.PoolingType is >= 0 and <= int.MaxValue ? (int)h.PoolingType : null;
            return new GgufMetadata(h.Architecture, h.Name, ctx, pooling);
        }
        catch (FileNotFoundException) { throw new GattoConfigException($"no such model file: {path}"); }
        catch (DirectoryNotFoundException) { throw new GattoConfigException($"no such model file: {path}"); }
        catch (IOException ex) { throw new GattoConfigException($"could not read {path}: {ex.Message}"); }
    }
}
