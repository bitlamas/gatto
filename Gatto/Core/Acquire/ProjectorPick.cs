namespace Gatto.Core.Acquire;

//prefer the f16 encoder, since the quant saves little and costs precision. a preference rather than a filter, so a quant-only repo still gets an offer
internal static class ProjectorPick
{
    //the encoder to offer, or null when the repo ships none
    public static HubQuant? Best(IReadOnlyList<HubQuant>? projectors)
    {
        if (projectors is not { Count: > 0 }) return null;

        //reference precision first, then the largest, since among equals size tracks precision. the file name breaks the final tie, so two runs agree
        return projectors
            .OrderByDescending(IsReferencePrecision)
            .ThenByDescending(q => q.Bytes)
            .ThenBy(q => q.FileName, StringComparer.OrdinalIgnoreCase)
            .First();
    }

    //the pick still runs, and nothing narrates which encoder was chosen for a repo that ships several

    //match the name's own tokens, since a bare Contains("f16") would also fire on a name like -af16k-
    private static bool IsReferencePrecision(HubQuant q) =>
        System.IO.Path.GetFileNameWithoutExtension(q.FileName)
            .Split('-', '.', '_')
            .Any(seg => seg.Equals("f16", StringComparison.OrdinalIgnoreCase)
                     || seg.Equals("bf16", StringComparison.OrdinalIgnoreCase)
                     || seg.Equals("f32", StringComparison.OrdinalIgnoreCase));
}
