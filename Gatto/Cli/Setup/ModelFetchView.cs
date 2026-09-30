using Gatto.Core.Acquire;
using Gatto.Terminal;
namespace Gatto.Cli.Setup;

//all pulled files in pull order, the same record backs the consent block and the fetch screen
internal readonly record struct ModelView(
    string RepoLabel, string Into, IReadOnlyList<ModelFetchFile> Files, string? Free = null)   //each model gets its own Into folder, and a null Free draws nothing on the into row
{
    //the weights are always first, so this is where a fetch starts
    public ModelFetchFile First => Files[0];

    //the next file by position in the list rather than by set or vision pair, null when the tick names nothing here
    public ModelFetchFile? Next(FetchTick t)
    {
        for (var i = 0; i < Files.Count - 1; i++)
            if (string.Equals(Files[i].Name, t.FileName, StringComparison.OrdinalIgnoreCase))
                return Files[i + 1];
        return null;
    }
}

//the size arrives already worded so two screens can't disagree about one file, and the encoder flag keeps the wording on the screen
internal readonly record struct ModelFetchFile(string Name, string Size, bool Encoder = false);

//the offer exists only when gatto can prove what arrives, and the projector is a second quant and a second fetch
internal sealed record ModelFetchOffer(
    string RepoId, string ModelId, string IntoDir,
    Gatto.Core.Acquire.HubQuant Weights, Gatto.Core.Acquire.HubQuant? Projector);

//shorten a hash to six and four characters, one spelling so the two screens that show a fingerprint agree
internal static class Fingerprint
{
    private const int Head = 6, Tail = 4;

    public static string Short(GlyphSet g, string? sha) =>
        sha is not { Length: > Head + Tail } ? sha ?? ""
            : sha[..Head] + $"{g.Ellipsis}" + sha[^Tail..];
}

//the fetch's fact rows, laid out at the width the face has
internal static class ModelFetchView
{
    //below 90 columns the from row's clause shortens, the fact it qualifies stays put
    private const int CompactBelow = 90;

    //the short form keeps the claim and drops the sentence around it, and the clause says every file rather than the set
    private static string From(ModelView v, int width, GlyphSet g) =>
        v.RepoLabel + (width < CompactBelow
            ? $" {g.Dot} fingerprints verified"
            : $" {g.Dot} every file's fingerprint verified");

    //the glyph set comes in from the face, defaulted so every existing caller and golden stays as it was
    public static IReadOnlyList<WizardRow> Rows(ModelView v, int width, GlyphSet? glyphs)
    {
        var g = glyphs ?? GlyphSet.Unicode;
        var rows = new List<WizardRow>
        {
            SetupFlow.ModelFacts.Row("fetch", v.First.Name + $" {g.Dot} " + v.First.Size,
                lift: v.First.Name),
        };

        //further shards read as and, and the encoder as with plus what it is for, since a second file needs a reason
        foreach (var file in v.Files.Skip(1))
            rows.Add(file.Encoder
                ? SetupFlow.ModelFacts.Row("with",
                    file.Name + $" {g.Dot} " + file.Size + $" {g.Dot} the vision encoder", lift: file.Name)
                : SetupFlow.ModelFacts.Row("and", file.Name + $" {g.Dot} " + file.Size, lift: file.Name));

        rows.Add(SetupFlow.ModelFacts.Row("from", From(v, width, g)));
        rows.Add(SetupFlow.ModelFacts.Row("into",
            v.Free is { Length: > 0 } free ? v.Into + $" {g.Dot} " + free : v.Into));
        return rows;
    }
}
