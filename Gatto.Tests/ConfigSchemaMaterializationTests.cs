using Gatto.Core.Home;

namespace Gatto.Tests;

//the schema file sits beside gatto.json and is overwritten whenever it differs. a hand-edited copy is replaced and a deleted one comes back
public class ConfigSchemaMaterializationTests
{
    private static string NewHome()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gatto-mat-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string SchemaPath(string home) => Path.Combine(home, "gatto.schema.json");

    [Fact]
    public void A_FRESH_HOME_GETS_THE_SCHEMA_BESIDE_THE_CONFIG()
    {
        var home = NewHome();
        GattoHome.EnsureInitialized(home);

        Assert.True(File.Exists(SchemaPath(home)), "EnsureInitialized wrote no gatto.schema.json");
        Assert.Equal(ConfigSchema.Text, File.ReadAllText(SchemaPath(home)));
        Assert.True(File.Exists(Path.Combine(home, "gatto.json")), "the config is its neighbour");
    }

    //the schema is gatto's own documentation, an edited copy is overwritten rather than preserved
    [Fact]
    public void A_MODIFIED_COPY_IS_OVERWRITTEN()
    {
        var home = NewHome();
        GattoHome.EnsureInitialized(home);
        File.WriteAllText(SchemaPath(home), "{\"i\":\"edited this by hand\"}");

        GattoHome.EnsureInitialized(home);

        Assert.Equal(ConfigSchema.Text, File.ReadAllText(SchemaPath(home)));
    }

    //a deleted schema copy comes back, otherwise a user who deleted it once never gets the documentation again
    [Fact]
    public void A_DELETED_COPY_COMES_BACK()
    {
        var home = NewHome();
        GattoHome.EnsureInitialized(home);
        File.Delete(SchemaPath(home));
        Assert.False(File.Exists(SchemaPath(home)), "the fixture did not actually delete it");

        GattoHome.EnsureInitialized(home);

        Assert.True(File.Exists(SchemaPath(home)), "a deletion stuck — that is the manifest's rule, not R3's");
        Assert.Equal(ConfigSchema.Text, File.ReadAllText(SchemaPath(home)));
    }

    //an identical copy is left alone, so a launch does not churn the file's timestamp. the fixed old mtime proves a write whatever the clock resolution
    [Fact]
    public void AN_IDENTICAL_COPY_IS_NOT_REWRITTEN()
    {
        var home = NewHome();
        GattoHome.EnsureInitialized(home);

        var stamp = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(SchemaPath(home), stamp);

        GattoHome.EnsureInitialized(home);

        Assert.Equal(stamp, File.GetLastWriteTimeUtc(SchemaPath(home)));
    }

    //doctor reports and never scaffolds, a check that repairs the home it inspects cannot report on it
    [Fact]
    public void DOCTOR_DOES_NOT_CREATE_THE_SCHEMA()
    {
        var home = NewHome();
        File.WriteAllText(Path.Combine(home, "gatto.json"),
            """{"endpoints":{"local":{"base_url":"http://x"}},"default_endpoint":"local"}""");

        //the real claim is that EnsureInitialized is absent from the doctor path, which the census below pins. this test shows only the behavior
        Assert.False(File.Exists(SchemaPath(home)));
        GattoConfig.Load(home);                       //this load is the doctor's own read, and a read must not scaffold.
        Assert.False(File.Exists(SchemaPath(home)), "reading the config created the schema");
    }

    //one writer covers every EnsureInitialized caller, so no list needs maintaining (a second WriteAllText of the schema text must fail this census)
    [Fact]
    public void THE_SCHEMA_HAS_EXACTLY_ONE_WRITER()
    {
        var writers = new List<string>();
        foreach (var file in Census.SourceTree.ProductionFiles())
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
                if (lines[i].Contains("WriteAllText", StringComparison.Ordinal)
                    && lines[i].Contains("ConfigSchema.Text", StringComparison.Ordinal))
                    writers.Add($"{Path.GetFileName(file)}:{i + 1}");
        }

        Assert.True(writers.Count == 1,
            "the schema must be written from exactly one place so every EnsureInitialized caller is "
            + "covered by construction; found: " + string.Join(", ", writers));
        Assert.StartsWith("GattoHome.cs:", writers[0], StringComparison.Ordinal);
    }
}
