using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Unicode;

namespace Gatto.Core.Home;

//the kinds a declared setting takes, which a settings screen draws as its control
public enum SettingKind { Bool, Int, String, Choice }

//one setting an extension declared at load, the data a settings screen renders
public sealed record SettingDeclaration(string Key, SettingKind Kind, string Summary, object? Default);

//an extension's own settings in extensions\<name>.json, bound to one script's name. the file appears on the first write, never on a read
public sealed class ExtensionSettings(string home, string name)
{
    //the key gatto keeps a contributed endpoint's defaults under, so a script can neither list nor clobber it
    internal const string DefaultsKey = "defaults";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),   //non-ASCII stays literal, as gatto.json writes it
    };

    private readonly List<SettingDeclaration> _declared = new();

    public string Path { get; } = System.IO.Path.Combine(home, "extensions", name + ".json");

    public IReadOnlyList<SettingDeclaration> Declared => _declared;

    //null, or the type's own default, when the file or the key is absent
    public T? Get<T>(string key)
    {
        if (!Read().TryGetPropertyValue(key, out var node) || node is null) return default;
        try { return node.Deserialize<T>(); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException)
        {
            throw new GattoConfigException($"{Path} {key} has the wrong type");
        }
    }

    //a null value removes the key
    public void Set(string key, object? value)
    {
        Reserved(key);
        if (value is null) { Mutate(root => root.Remove(key)); return; }
        var node = JsonSerializer.SerializeToNode(value);
        Mutate(root => root[key] = node);
    }

    public void Remove(string key)
    {
        Reserved(key);
        Mutate(root => root.Remove(key));
    }

    public IReadOnlyList<string> Keys() =>
        [.. Read().Select(p => p.Key).Where(k => k != DefaultsKey)];

    //declarations live in memory for this run, so declaring writes nothing
    public void Declare(string key, SettingKind kind, string summary, object? @default) =>
        _declared.Add(new SettingDeclaration(key, kind, summary, @default));

    //read, edit and write back atomically through a temp file of its own name, so two windows writing at once never share one
    internal void Mutate(Action<JsonObject> edit)
    {
        var root = Read();
        edit(root);
        var tmp = Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(tmp, root.ToJsonString(WriteOptions));
            File.Move(tmp, Path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new GattoConfigException($"could not write {Path}: {ex.Message}");
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch (Exception) { }   //best effort, a failed delete must not replace the write error
        }
    }

    //the one read path, an absent file is an empty object and every other failure is a config error naming the file
    internal JsonObject Read()
    {
        string text;
        try
        {
            if (!File.Exists(Path)) return new JsonObject();
            text = File.ReadAllText(Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new GattoConfigException($"could not read {Path}: {ex.Message}");
        }

        JsonNode? node;
        try { node = JsonNode.Parse(text); }
        catch (JsonException ex) { throw new GattoConfigException($"{Path} is not valid JSON: {ex.Message}"); }
        return node as JsonObject ?? throw new GattoConfigException($"{Path} must be a JSON object");
    }

    private void Reserved(string key)
    {
        if (key == DefaultsKey) throw new GattoConfigException($"{Path} defaults is gatto's own key");
    }
}
