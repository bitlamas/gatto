using Gatto.Terminal;
namespace Gatto.Cli.Setup;

//the fetch as facts for the face to lay out. both arms name the same Into, so the instruction and the sweep agree
internal readonly record struct EngineView(
    bool GattoFetches, string ZipName, string? Size, string Release, string Into,
    string? WithName = null, string? WithSize = null);

//what gatto would fetch, as GitHub reports it. a null sha256 stops the fetch rather than skipping the check
internal sealed record EngineAsset(string ZipName, string Url, string? Sha256, long Size);

//the server and its companion runtime as one unit, both downloaded and verified before either is extracted
internal sealed record EnginePair(EngineAsset Server, EngineAsset? Companion);

//the fetch on offer, a null Assets means GitHub did not answer. the folder is set even with nothing to fetch, since the offline arm names the same one
internal sealed record EngineFetchOffer(string IntoDir, EnginePair? Assets);

//the fact rows for the fetch, at a live width
internal static class EngineFetchView
{
    //below 90 columns the from row's why-clause shortens, so the label column still reads as a table
    private const int CompactBelow = 90;

    //what the fetch is checked against, in a short and a long form. keep the claim in both, since this row is why an in-app fetch beats a hand download
    private static string From(int width, GlyphSet? glyphs) =>
        Gatto.Roles.LlamaAssetSteering.RepoLabel
        + (width < CompactBelow ? $" {G(glyphs)} digest-checked"
            : $" {G(glyphs)} checked against GitHub's own digest");

    //the table's dot, and the default keeps every existing caller and golden as it was
    private static string G(GlyphSet? g) => (g ?? GlyphSet.Unicode).Dot;

    //the glyph vocabulary threaded from the face, defaulted so existing callers and goldens are unchanged
    public static IReadOnlyList<WizardRow> Rows(EngineView v, int width, GlyphSet? glyphs) =>
        v.GattoFetches
            ?
            [
                //the release clause gets its own continuation row, since the label column is taken and one row has no room at 100 columns
                Row("fetch", v.ZipName + $" {G(glyphs)} " + v.Size, lift: v.ZipName),
                Row("", $"{v.Release}, the release gatto is tested with"),
                //the companion gets a row of the same block, and it borrows the model step's own with-vocabulary
                .. v.WithName is { Length: > 0 } companion
                    ? (IReadOnlyList<WizardRow>)[Row("with",
                        companion + $" {G(glyphs)} " + v.WithSize + $" {G(glyphs)} the CUDA runtime it needs",
                        lift: companion)]
                    : [],
                Row("from", From(width, glyphs)),
                Row("into", v.Into),
            ]
            :
            [
                Row("download", v.ZipName, lift: v.ZipName),
                //the companion is the half a manual download forgets, so it gets named here and the row says what it is for
                .. v.WithName is { Length: > 0 } byHand
                    ? (IReadOnlyList<WizardRow>)[Row("with",
                        byHand + $" {G(glyphs)} the CUDA runtime it needs", lift: byHand)]
                    : [],
                //the release page rather than the asset's link, since a stale link leaves the user stuck
                Row("from", Gatto.Roles.LlamaAssetSteering.ReleasePageLabel),
                Row("extract to", v.Into),
            ];

    //the Hang property keeps a wrapped value under its own column, so a long path folds under itself
    private static WizardRow Row(string label, string value, string? lift = null) =>
        new(label.PadRight(SetupFlow.EngineFacts.Column) + value, RowTone.Aside,
            Hang: SetupFlow.EngineFacts.Column, Lift: lift is null ? null : [lift]);
}
