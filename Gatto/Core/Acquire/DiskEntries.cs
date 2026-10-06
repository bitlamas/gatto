using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Gatto.Core.Acquire;

//one JSON file per entry under the home's cache folder, named by a hash of its key, with the key kept inside so a collision reads as a miss
internal sealed class DiskEntries(string homePath, string folder)
{
    private readonly string _dir = Path.Combine(homePath, "cache", folder);

    //where the entries live, for a test that inspects them
    internal string Dir => _dir;

    //a number or null, since TryGetInt64 throws on a value of another kind and a bad entry must read as a miss
    internal static long? Long(JsonElement e) =>
        e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var n) ? n : null;

    //the file name is a hash of the key, since a key holds characters a file name cannot
    private string PathOf(string key) =>
        Path.Combine(_dir, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".json");

    //an entry that cannot be read is a miss, since the source still answers
    public JsonDocument? Read(string? key)
    {
        if (key is null) return null;
        try
        {
            var path = PathOf(key);
            if (!File.Exists(path)) return null;
            var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            //the key is stored in the entry, so a hash collision reads as a miss rather than another file's facts
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.String
                && k.GetString() == key)
                return doc;
            doc.Dispose();
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    //nothing is written into a home that does not exist yet, since the wizard creates the home at its first real write
    public void Write(string? key, Action<Utf8JsonWriter> fields)
    {
        if (key is null || !Directory.Exists(homePath)) return;
        var path = PathOf(key);
        //written beside the entry and moved over it, so a reader in another gatto never sees half a file
        var temp = path + "." + Environment.ProcessId + "." + Environment.CurrentManagedThreadId + ".tmp";
        try
        {
            Directory.CreateDirectory(_dir);
            using (var stream = File.Create(temp))
            using (var w = new Utf8JsonWriter(stream))
            {
                w.WriteStartObject();
                w.WriteString("key", key);
                fields(w);
                w.WriteEndObject();
            }
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            //a move refused while another gatto holds the entry leaves the old entry, and the temp file goes
            try { File.Delete(temp); }
            catch (Exception gone) when (gone is IOException or UnauthorizedAccessException) { }
        }
    }
}
