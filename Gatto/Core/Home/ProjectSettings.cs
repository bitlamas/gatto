using System.Text.Json;
using System.Text.Json.Serialization;

namespace Gatto.Core.Home;

//only the answers the user gave to gatto's own questions live here. anything that shapes a request or a prompt belongs in gatto.json
public sealed class ProjectSettings
{
    private readonly string _path;

    private ProjectSettings(string path, bool attachManyGranted)
    {
        _path = path;
        AttachManyGranted = attachManyGranted;
    }

    //suppresses the image-count question only, the byte backstop is checked before it and no grant can reach it
    public bool AttachManyGranted { get; private set; }

    public static string PathFor(string projectRoot) =>
        Path.Combine(projectRoot, ".gatto", "settings.json");

    //never throws, a missing or malformed file yields the defaults so the user is asked again rather than locked out
    public static ProjectSettings Load(string projectRoot)
    {
        var path = PathFor(projectRoot);
        try
        {
            if (File.Exists(path))
            {
                var dto = JsonSerializer.Deserialize<Dto>(File.ReadAllText(path));
                return new ProjectSettings(path, dto?.AttachManyGranted ?? false);
            }
        }
        catch (Exception) { } //a read failure falls back to the defaults
        return new ProjectSettings(path, attachManyGranted: false);
    }

    //records the grant and writes it, a write failure leaves it in memory for this session and the user is asked again next time
    public void GrantAttachMany()
    {
        AttachManyGranted = true;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path,
                JsonSerializer.Serialize(new Dto { AttachManyGranted = true },
                    new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception) { } //the write failed, the grant still applies for this session
    }

    private sealed class Dto
    {
        [JsonPropertyName("attach_many_granted")]
        public bool AttachManyGranted { get; set; }
    }
}
