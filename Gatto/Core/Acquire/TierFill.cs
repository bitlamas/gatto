namespace Gatto.Core.Acquire;

//what the fill selected and left off, the rows keep the arrival order and the two hidden counts stay apart for the caller
internal readonly record struct TierFillResult<T>(
    IReadOnlyList<T> Rows, int HiddenOlder, int HiddenNewer);

//the default shelf fills from the current generation downwards, one guaranteed row from each of the first three versioned tiers
internal static class TierFill
{
    //three, the current tier and the two below it, raising this reaches unversioned
    internal const int GuaranteedTiers = 3;

    //the rows a tiered shelf shows, the axis order comes in and the tier of a row is supplied rather than looked up
    public static TierFillResult<T> Select<T>(
        IReadOnlyList<T> rows, Func<T, ModelTier> tierOf, ModelTier current, int budget)
    {
        if (rows.Count == 0 || budget <= 0) return new([], 0, 0);

        //tiers newest-first and only from the pin down, rows above the pin stay out of the chain
        var tiers = rows.Select(tierOf).Distinct()
            .Where(t => t.IsVersioned && ModelTier.Compare(t, current) >= 0)
            .Order(Comparer<ModelTier>.Create(ModelTier.Compare))
            .ToList();

        //unversioned goes last with no guarantee, it is reached only when the versioned tiers run out
        var chain = rows.Any(r => !tierOf(r).IsVersioned)
            ? tiers.Append(ModelTier.Unversioned).ToList()
            : tiers;

        var taken = new HashSet<int>();

        //the guarantee comes first, so a thin current generation can't crowd out the tiers below it
        foreach (var tier in chain.Where(t => t.IsVersioned).Take(GuaranteedTiers))
        {
            if (taken.Count >= budget) break;
            var top = First(rows, tierOf, tier, taken);
            if (top >= 0) taken.Add(top);
        }

        //then depth-first, every row of one tier before the next
        foreach (var tier in chain)
        {
            if (taken.Count >= budget) break;
            for (var i = 0; i < rows.Count && taken.Count < budget; i++)
                if (!taken.Contains(i) && Same(tierOf(rows[i]), tier)) taken.Add(i);
        }

        //back into the arrival order, the reading order is the axis's and re-sorting by generation would lie about it
        var selected = new List<T>(taken.Count);
        var newer = 0;
        var older = 0;
        for (var i = 0; i < rows.Count; i++)
        {
            if (taken.Contains(i)) { selected.Add(rows[i]); continue; }
            var t = tierOf(rows[i]);
            if (t.IsVersioned && ModelTier.Compare(t, current) < 0) newer++;
            else older++;
        }

        return new TierFillResult<T>(selected, older, newer);
    }

    //the first row of a tier in the arrival order, the axis already chose the best row of the generation
    private static int First<T>(
        IReadOnlyList<T> rows, Func<T, ModelTier> tierOf, ModelTier tier, HashSet<int> taken)
    {
        for (var i = 0; i < rows.Count; i++)
            if (!taken.Contains(i) && Same(tierOf(rows[i]), tier)) return i;
        return -1;
    }

    //compare the parsed pair, 3.6 and 3.60 are different tiers and the same decimal
    private static bool Same(ModelTier a, ModelTier b) => a.Major == b.Major && a.Minor == b.Minor;
}
