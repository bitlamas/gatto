using System.Text;

namespace Gatto.Core.Home;

//the gatto.json schema, embedded and written beside the file it describes. the home copy is documentation, so it is overwritten whenever it differs
public static class ConfigSchema
{
    //the file's name in the home and in the scaffold's $schema value, one home so the writer and the breadcrumb can't drift
    internal const string FileName = "gatto.schema.json";

    private const string ResourceName = "Gatto.Core.Home.gatto.schema.json";   //kept out of the shipped folder, its files keep a user's edit. git stores the json with -text so bytes match on every machine

    private static readonly string _text = Load();

    //the embedded schema, verbatim, so a byte comparison against the home copy is meaningful
    public static string Text => _text;

    private static string Load()
    {
        using var s = typeof(ConfigSchema).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"the embedded config schema '{ResourceName}' is missing — check the EmbeddedResource "
                + "entry in Gatto.csproj");
        using var mem = new MemoryStream();
        s.CopyTo(mem);
        var bytes = mem.ToArray();

        //a BOM here would end up in every user's home and break the byte comparison, so the schema would be rewritten on every launch
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            throw new InvalidOperationException(
                $"{FileName} starts with a UTF-8 BOM — save it without one.");

        return Encoding.UTF8.GetString(bytes);
    }
}
