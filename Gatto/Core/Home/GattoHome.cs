namespace Gatto.Core.Home;

public static class GattoHome
{
    public static string Resolve() =>
        Environment.GetEnvironmentVariable("GATTO_HOME") is { Length: > 0 } h
            ? h
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gatto");

    //no default_model, so a fresh home is unconfigured rather than wrongly configured, and $schema is first so a reader finds it on line one
    private const string StarterConfig = """
        {
          "$schema": "./gatto.schema.json",
          "endpoints": {
            "local": { "base_url": "http://127.0.0.1:1235" }
          },
          "default_endpoint": "local"
        }
        """;

    //scaffolds the directories, the starter config and the schema, and never writes a home GATTO.md
    public static void EnsureInitialized(string homePath)
    {
        foreach (var sub in new[] { "sessions", "extensions", "models", "roles" })
            Directory.CreateDirectory(Path.Combine(homePath, sub));
        var cfgPath = Path.Combine(homePath, "gatto.json");
        if (!File.Exists(cfgPath))
            File.WriteAllText(cfgPath, StarterConfig);

        //the schema is overwritten whenever it differs from the embedded copy, unlike gatto.json, since gatto's own documentation has nothing worth preserving
        var schemaPath = Path.Combine(homePath, ConfigSchema.FileName);
        if (!File.Exists(schemaPath) || File.ReadAllText(schemaPath) != ConfigSchema.Text)
            File.WriteAllText(schemaPath, ConfigSchema.Text);
    }
}
