using System.Text.RegularExpressions;
using Gatto.Core.Models;

namespace Gatto.Core.Acquire;

//only an anchored A-number reaches the cell, since it is the only route a size label has. the HasAllFitTerms flag is evidence, since nothing fixes the key order
internal static partial class ModelStructure
{
    //anchored, and the only route a size label has to a table cell. the digits are bounded, since an unbounded pattern admits a ten-thousand-character cell
    [GeneratedRegex(@"^A\d{1,4}(?:\.\d{1,2})?[BM]$", RegexOptions.CultureInvariant)]
    private static partial Regex ActiveSizeShape();

    //the kind cell or null, and an expert count of 0 or 1 means dense, since converters do write the key on dense models
    public static string? Cell(GgufHeader h)
    {
        if (h.ExpertCount is { } experts && experts > 1)
            return Tail(h.SizeLabel) is { } a ? "MoE " + a : "MoE";

        //told there are no experts, or told everything else and nothing about experts
        return h.ExpertCount is not null || h.HasAllFitTerms ? "dense" : null;
    }

    //both halves or neither, since a total with no active count cannot be a ratio. bounded, since these numbers are printed and nothing downstream bounds them
    public static (long Total, long Active)? Experts(GgufHeader h) =>
        h.ExpertCount is { } total && total is > 1 and <= MaxExperts
        && h.ExpertUsedCount is { } active && active > 0 && active <= total
            ? (total, active)
            : null;

    //the ceiling on a printed expert count, rather than a claim about what a model can be
    private const long MaxExperts = 10_000;

    //positive evidence only, so a dense-by-absence reading does not stop the retry ladder. a dense model with no stated count pays two requests
    public static bool Answered(GgufHeader h) =>
        h.ExpertCount is not null || h.Outcome == GgufOutcome.Complete;

    //the params cell's anchored shape: an optional multiplier, a bounded number and unit, an optional active tail. the label is uploader text and reaches the cell only through this
    [GeneratedRegex(@"^(\d{1,3}x)?(\d{1,5}(?:\.\d{1,3})?)([KMBT])(-A\d{1,5}(?:\.\d{1,3})?[KMBT])?\z", RegexOptions.CultureInvariant)]   //the end anchor is the end of the text, since a dollar sign also matches before a final newline
    private static partial Regex SizeShape();

    //32 characters bound the match, so a label of any length costs one short check
    private const int MaxSizeLabel = 32;

    //the label when it has the anchored shape, else null, so an escape or a newline never reaches the table
    public static string? SizeCell(string? label) =>
        label is { Length: > 0 and <= MaxSizeLabel } && SizeShape().IsMatch(label) ? label : null;

    //the label's parameters in one unit, an N x M label as the product, null when the shape does not match
    public static double? SizeMagnitude(string? label)
    {
        if (SizeCell(label) is null) return null;
        var m = SizeShape().Match(label!);
        var times = m.Groups[1].Success ? double.Parse(m.Groups[1].Value[..^1], System.Globalization.CultureInfo.InvariantCulture) : 1;
        var n = double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
        var unit = m.Groups[3].Value switch { "K" => 1e3, "M" => 1e6, "B" => 1e9, _ => 1e12 };
        return times * n * unit;
    }

    //the label's last segment, admitted only if it is an A-number, so 512x2.5B leaves the cell as bare MoE
    private static string? Tail(string? sizeLabel)
    {
        if (sizeLabel is null) return null;
        var tail = sizeLabel[(sizeLabel.LastIndexOf('-') + 1)..];
        return ActiveSizeShape().IsMatch(tail) ? tail : null;
    }
}
