using System.Text.RegularExpressions;

namespace Gatto.Core.Acquire;

//the one home for the quant a model file's name declares and for the band, don't write another copy in Core
internal static class QuantToken
{
    //a new family is added as an element, the grammar stays per element rather than a full token table
    private static readonly string[] Qualifiers = ["K", "S", "M", "L", "XL", "XS", "XXS", "NL", "0", "1"];

    //anchored, so a segment matches whole (af16k doesn't read as F16) and one non-member element disqualifies it
    private static readonly Regex Shape = new(
        $@"^(?:I?Q\d+(?:_(?:{string.Join('|', Qualifiers)}))*|BF16|F16|F32)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    //split on - and ., the underscore belongs to the token, and a segment that doesn't match is omitted rather than guessed
    public static string? Of(string modelPath)
    {
        string name;
        try { name = Path.GetFileName(modelPath); }
        catch (ArgumentException) { return null; }   //invalid path chars, just unknown

        //shard-stripped stem for a set, the plain stem otherwise
        var stem = ShardName.Parse(name) is { } shard
            ? shard.Stem
            : Path.GetFileNameWithoutExtension(name);

        foreach (var segment in stem.Split('-', '.'))
            if (Shape.IsMatch(segment)) return segment.ToUpperInvariant();

        return null;
    }

    //the quality/speed band smallest first, Q4 to Q6 with the top spelled Q6_K (Q6 has no Q6_K_M variant)
    private static readonly string[] Band = ["Q4_K_S", "Q4_K_M", "Q5_K_S", "Q5_K_M", "Q6_K"];

    //a preference rather than a filter, -1 still wins its tier when nothing better fits
    public static int BandRank(string fileName) => Array.IndexOf(Band, Of(fileName) ?? "");

    //a screen that explains which rule chose the file states this, read off the same token the pick was
    public static bool InBand(string fileName) => BandRank(fileName) >= 0;
}
