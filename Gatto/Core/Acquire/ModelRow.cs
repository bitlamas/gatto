using Gatto.Core.Models;

namespace Gatto.Core.Acquire;

//one file by publisher, repo and path, never by quant token, since one publisher's two repos can both hold a Q4_K_M
internal sealed record FileRef(string Publisher, string RepoId, string Path);

//one repo of a publisher, with every weight file and every projector its tree holds
internal sealed record RepoFiles(string RepoId, IReadOnlyList<HubQuant> Quants,
    IReadOnlyList<HubQuant> Projectors, int FileCount, long Downloads);

//one approved publisher of a model: its repos, the file it would take and where that runs, or no pick
internal sealed record PublisherOffer(string Org, IReadOnlyList<RepoFiles> Repos,
    FileRef? Pick, HubQuant? PickQuant, FitRegime? Fit, long Downloads);

//one original model on the shelf, every approved publisher priced, and the one file the row shows
internal sealed record ModelRow(
    string Model, string? Family, int Generation,
    long? Params, long? Active, string? Arch, long? NativeCtx, bool Vision,
    IReadOnlyList<PublisherOffer> Publishers, int RowPublisher,
    FileRef? RowFile, FitRegime Fit,   //the fit is DoesNotFit when RowFile is null, and RowPublisher is -1
    string? Structure = null, (long Total, long Active)? Experts = null,
    Badge? Badge = null,   //set only by the local shelf, a Hub row carries none
    string? ParamsLabel = null)   //the size label in its anchored shape, drawn and sorted by where Params is unknown, set only by the local shelf
{
    //the quant a reference names, so the choice and the fetch read Members, Bytes and Sha256 from one place
    public HubQuant? QuantOf(FileRef file) =>
        Publishers.SelectMany(p => p.Repos)
            .Where(r => string.Equals(r.RepoId, file.RepoId, StringComparison.OrdinalIgnoreCase))
            .SelectMany(r => r.Quants)
            .FirstOrDefault(q => string.Equals(q.RepoPath, file.Path, StringComparison.Ordinal));

    //the quant the row shows, null when no publisher had a file to take
    public HubQuant? RowQuant => RowFile is { } f ? QuantOf(f) : null;

    //the vision encoders of the row file's own repo, since another publisher's encoder belongs to another download
    public IReadOnlyList<HubQuant> RowProjectors => RowFile is { } f && RepoOf(f) is { } repo ? repo.Projectors : [];

    //the publisher the row's file comes from, or null when the row has none
    public PublisherOffer? RowOffer => RowPublisher >= 0 && RowPublisher < Publishers.Count ? Publishers[RowPublisher] : null;

    //the repo a reference names, for its projectors and its file count
    public RepoFiles? RepoOf(FileRef file) =>
        Publishers.SelectMany(p => p.Repos)
            .FirstOrDefault(r => string.Equals(r.RepoId, file.RepoId, StringComparison.OrdinalIgnoreCase));
}
