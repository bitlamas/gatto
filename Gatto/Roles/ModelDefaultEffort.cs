using System.Text.Json;
using System.Text.Json.Nodes;
using Gatto.Core.Home;

namespace Gatto.Roles;

//edit profile.json as a document (re-serialising would reshape keys this class doesn't own). the caller passes the level that actually resolved on the map
internal static class ModelDefaultEffort
{
    //load the model first, a profile the loader refuses must be refused before an edit can make it differently broken
    public static void Set(string modelsDir, string id, ThinkingLevel level)
    {
        Model.Load(modelsDir, id);   //the loader's refusals run here, reused rather than copied

        var path = Path.Combine(modelsDir, id, "profile.json");
        var root = JsonNode.Parse(File.ReadAllText(path))
            ?? throw new GattoConfigException($"model '{id}' profile at {path} is empty");
        root["default_effort"] = EffortSwitch.Name(level);
        Save(path, root);
    }

    //write a temp file and move it, a half-written profile is a model that no longer loads
    private static void Save(string path, JsonNode root)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, path, overwrite: true);
    }
}
