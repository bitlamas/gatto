using System.Text.RegularExpressions;

namespace Gatto.Core.Acquire;

//the one home for the quant a model file's name declares and for the band, don't write another copy in Core
internal static class QuantToken
{
    //a new family is added as an element, the grammar stays per element rather than a full token table
    private static readonly string[] Qualifiers = ["K", "S", "M", "L", "XL", "XS", "XXS", "NL", "0", "1"];

    //anchored, so a segment matches whole (af16k doesn't read as F16) and one non-member element disqualifies it
    private static readonly Regex Shape = new(
        $@"^(?:I?Q\d+(?:_(?:{string.Join('|', Qualifiers)}))*|BF16|F16|F32|MXFP4(?:_MOE)?)$",   //MXFP4 is the only quant some mixture-of-experts repositories ship
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
        {
            if (Shape.IsMatch(segment)) return segment.ToUpperInvariant();
            for (var i = segment.IndexOf('_'); i >= 0; i = segment.IndexOf('_', i + 1))   //from the first underscore, so 26B_q4_0 reads q4_0 before 0
                if (Shape.IsMatch(segment[(i + 1)..])) return segment[(i + 1)..].ToUpperInvariant();
        }

        return null;
    }

    //the stem before its quant token, found as Of finds it, with unsloth's UD segment cut with the token. null when the name declares none
    public static string? HeadBefore(string fileName)
    {
        string name;
        try { name = Path.GetFileName(fileName); }
        catch (ArgumentException) { return null; }
        var stem = ShardName.Parse(name) is { } shard ? shard.Stem : Path.GetFileNameWithoutExtension(name);

        var start = 0;
        var prevStart = -1;
        var prev = "";
        for (var i = 0; i <= stem.Length; i++)
        {
            if (i < stem.Length && stem[i] is not ('-' or '.')) continue;
            var segment = stem[start..i];
            if (Shape.IsMatch(segment))
                return stem[..(string.Equals(prev, "UD", StringComparison.OrdinalIgnoreCase) && prevStart >= 0 ? prevStart : start)];
            for (var u = segment.IndexOf('_'); u >= 0; u = segment.IndexOf('_', u + 1))
                if (Shape.IsMatch(segment[(u + 1)..])) return stem[..(start + u)];
            prev = segment;
            prevStart = start;
            start = i + 1;
        }
        return null;
    }

    //the floor's number: the bits after Q or IQ, 4 for MXFP4, the float width for BF16, F16 and F32
    public static int? ClassOf(string? token)
    {
        if (token is not { Length: > 0 }) return null;
        var t = token.ToUpperInvariant();
        if (t.StartsWith("MXFP4", StringComparison.Ordinal)) return 4;
        if (t is "BF16" or "F16") return 16;
        if (t == "F32") return 32;
        var at = t.StartsWith('I') ? 2 : 1;
        var end = at;
        while (end < t.Length && char.IsAsciiDigit(t[end])) end++;
        return end > at && int.TryParse(t.AsSpan(at, end - at), out var bits) ? bits : null;
    }

    //class 4 up, 3 up at 20B or more, lifted admits every file with a token, and a file with no token is never admitted
    public static bool AtFloor(string fileName, long? totalParams, bool lifted)
    {
        if (ClassOf(Of(fileName)) is not { } cls) return false;
        return lifted || cls >= 4 || (cls >= 3 && totalParams >= 20_000_000_000);
    }

    //the quality/speed band smallest first, Q4 to Q6 with the top spelled Q6_K (Q6 has no Q6_K_M variant)
    private static readonly string[] Band = ["Q4_K_S", "Q4_K_M", "Q5_K_S", "Q5_K_M", "Q6_K"];

    //a preference rather than a filter, -1 still wins its tier when nothing better fits
    public static int BandRank(string fileName) => Array.IndexOf(Band, Of(fileName) ?? "");

    //a screen that explains which rule chose the file states this, read off the same token the pick was
    public static bool InBand(string fileName) => BandRank(fileName) >= 0;
}
