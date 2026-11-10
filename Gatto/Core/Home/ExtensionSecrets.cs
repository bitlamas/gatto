using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Gatto.Core.Home;

//an extension's secrets in secrets\<name>.bin, one sealed JSON object of strings bound to one script's name. read on every call, so a value another session wrote is the one returned
public sealed class ExtensionSecrets(string home, string name, ISecretSeal seal)
{
    public string Path { get; } = System.IO.Path.Combine(home, "secrets", name + ".bin");

    public string? Get(string key) =>
        Read().TryGetPropertyValue(key, out var node) && node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public void Set(string key, string value) => Set(new Dictionary<string, string> { [key] = value });

    //several keys in one write, so an access token and its refresh token land together
    public void Set(IReadOnlyDictionary<string, string> values)
    {
        var root = Read();
        foreach (var (k, v) in values) root[k] = v;
        Write(root);
    }

    public void Remove(string key)
    {
        var root = Read();
        if (root.Remove(key)) Write(root);
    }

    public void RemoveAll()
    {
        try { if (File.Exists(Path)) File.Delete(Path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new GattoConfigException($"could not delete {Path}: {ex.Message}");
        }
    }

    public bool Any() => File.Exists(Path) && Read().Count > 0;

    //a value is never printed, a log line that formats this object names the file only
    public override string ToString() => Path;

    private JsonObject Read()
    {
        byte[] sealedBytes;
        try
        {
            if (!File.Exists(Path)) return new JsonObject();
            sealedBytes = File.ReadAllBytes(Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new GattoConfigException($"could not read {Path}: {ex.Message}");
        }

        var plain = seal.Unseal(sealedBytes, Path);   //a failure throws the seal's own message and leaves the file
        try
        {
            return JsonNode.Parse(Encoding.UTF8.GetString(plain)) as JsonObject
                   ?? throw new GattoConfigException($"{Path} does not hold a JSON object");
        }
        catch (JsonException) { throw new GattoConfigException($"{Path} does not hold a JSON object"); }
    }

    private void Write(JsonObject root) =>
        SecretFile.WriteAtomic(Path, seal.Seal(Encoding.UTF8.GetBytes(root.ToJsonString())));
}
