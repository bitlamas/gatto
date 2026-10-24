using System.Text.Json;
using Gatto.Core.Acquire;

namespace Gatto.Tests.Fakes;

//a row of the register that ships, read off the resource, so a test marks a file by bytes gatto really tested and survives a reseed
internal static class ShippedRows
{
    public static ShippedMark Any(bool fellShort)
    {
        using var s = typeof(ShippedMarks).Assembly.GetManifestResourceStream("Gatto.Core.Acquire.tool-use-marks.json")!;
        using var doc = JsonDocument.Parse(s);
        var sha = doc.RootElement.GetProperty("rows").EnumerateArray()
            .First(r => r.GetProperty("fell_short").GetArrayLength() > 0 == fellShort)
            .GetProperty("sha256").GetString();
        return ShippedMarks.Lookup(sha)!;
    }

    //the words a fell-short clause names, in the battery's own labels
    public static string Labels(IReadOnlyList<string> ids, Gatto.Terminal.GlyphSet g) =>
        string.Join($" {g.Dot} ", ids.Select(id => Gatto.Roles.Audition.Battery.Tasks.First(t => t.Id == id).Label));
}
