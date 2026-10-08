using Gatto.Core.Acquire;
using Gatto.Core.Models;

namespace Gatto.Tests;

//a one-publisher, one-repo model row from the fields a test names, so a fixture states the file the row shows and nothing else
internal static class ShelfRows
{
    public static ModelRow Of(
        string RepoId, string Publisher, HubQuant PickedQuant, FitRegime Fit,
        long? NativeCtx = null, bool Vision = false, Badge? Badge = null, long Downloads = 0, bool Gated = false,
        DateTimeOffset? LastModified = null, long? Params = null,
        IReadOnlyList<HubQuant>? AllQuants = null, IReadOnlyList<HubQuant>? Projectors = null,
        string? Arch = null, string? Structure = null, (long Total, long Active)? Experts = null, int FileCount = 0,
        string? Model = null)
    {
        //the row's own file is always among the repo's files, since the row names it by reference
        var quants = AllQuants is { Count: > 0 } all
            ? all.Any(q => q.RepoPath == PickedQuant.RepoPath) ? all : [.. all, PickedQuant]
            : (IReadOnlyList<HubQuant>)[PickedQuant];
        var file = new FileRef(Publisher, RepoId, PickedQuant.RepoPath);
        var repo = new RepoFiles(RepoId, quants, Projectors ?? [], FileCount, Downloads);
        return new ModelRow(
            Model ?? RepoId[(RepoId.IndexOf('/') + 1)..], null, 0, Params, null, Arch, NativeCtx, Vision,
            [new PublisherOffer(Publisher, [repo], file, PickedQuant, Fit, Downloads)], 0,
            file, Fit, Structure, Experts, Badge);
    }

    //the same one-publisher row whose repo holds these files, its own file kept as the row's reference even when the list leaves it out
    public static ModelRow WithFiles(this ModelRow r, IReadOnlyList<HubQuant> quants, int? fileCount = null)
    {
        var offer = r.Publishers[0];
        var repo = offer.Repos[0] with { Quants = quants, FileCount = fileCount ?? offer.Repos[0].FileCount };
        return r with { Publishers = [offer with { Repos = [repo] }] };
    }

    //the same one-publisher row showing another file, the way the old fixtures swapped the picked quant
    public static ModelRow WithQuant(this ModelRow r, HubQuant quant)
    {
        var offer = r.Publishers[0];
        var repo = offer.Repos[0] with { Quants = [quant] };
        var file = new FileRef(offer.Org, repo.RepoId, quant.RepoPath);
        return r with { Publishers = [offer with { Repos = [repo], Pick = file, PickQuant = quant }], RowFile = file };
    }
}
