using Gatto.Core.Hardware;
using Gatto.Core.Acquire;
using Gatto.Core.Models;

namespace Gatto.Cli.Setup.Shelves;

//the row's words (name, size, fit, marks, context) and nothing more, and a mark says what was measured rather than recommending. keep Gb, FitWords, the mark words and Ctx here, the table, the pane and the tier line all call them
internal static class SearchRow
{
    public static string Label(ModelRow r) => r.Model;

    //the bytes and the file name of the row's file, zero and empty when the row has none, so a cell renders blank rather than throwing
    internal static long RowBytes(ModelRow r) => r.RowQuant?.Bytes ?? 0;

    internal static string RowFileName(ModelRow r) => r.RowQuant?.FileName ?? "";

    //the words of the three marks, each naming who measured and where, with no date, count or score
    internal const string TestedWords = "tested by gatto";
    internal const string PassedHereWords = "passed on this machine";
    internal const string FailedHereWords = "failed on this machine";

    //the tasks a marked file fell short on in the battery's own labels, two joined by the dot, empty when it fell short on none
    internal static string FellShort(IReadOnlyList<string> ids, Gatto.Terminal.GlyphSet g) =>
        ids.Count == 0 ? ""
        : ", fell short: " + string.Join($" {g.Dot} ", ids.Select(id =>
            Gatto.Roles.Audition.Battery.Tasks.FirstOrDefault(t => t.Id == id)?.Label ?? id));

    //the marks of the row's own file
    internal static MarkSet MarksOf(ModelRow r) =>
        new(r.Tested is not null, r.Badge is { Passed: true }, r.Badge is { Passed: false });

    //the marks of every file a row lists: its own, each hub file the pane lists by the oid its listing carries, and each local file the pane lists
    internal static MarkSet ListedMarks(ModelRow r, ModelFacts? facts)
    {
        var marks = MarksOf(r);
        foreach (var file in facts?.Publishers?.SelectMany(p => p.Files) ?? []) marks = marks.Or(file.Marks);
        var hubTested = r.Publishers.Any(p => p.Repos.Any(repo =>
            HubSearch.Candidates(repo.Quants, r.Params).Any(q => ShippedMarks.Lookup(q.Members[0].Sha256) is not null)));
        return marks with { Tested = marks.Tested || hubTested };
    }

    //fit said as a computation, the estimate errs conservative and the words must not promise more than the arithmetic knows. they name where the weights sit, since fit can't speak for speed, and "slower" needs a faster tier
    internal static string FitWords(FitRegime fit, MachineShape shape, Gatto.Terminal.GlyphSet g) => fit switch
    {
        FitRegime.FitsGpu => shape switch
        {
            MachineShape.Discrete => "fits on the graphics card",
            MachineShape.UnifiedWithShare => "fits in the graphics share",
            _ => "fits in memory",
        },
        FitRegime.FitsSplit => "on the card and in memory",
        FitRegime.FitsRamOnly => shape switch
        {
            MachineShape.Discrete => $"fits in memory {g.Dot} slower",
            MachineShape.UnifiedWithShare => $"fits in system memory {g.Dot} slower",
            _ => "fits in memory",
        },
        FitRegime.DoesNotFit => "too big for this machine",
        _ => "size not known",
    };

    //the shelf's size column and the pane show one figure, so both go through SizeWords.Gb
    internal static string Gb(long bytes) =>
        Gatto.Cli.SizeWords.Gb(bytes);

    //the context window as digits with comma groups from the invariant culture ("32 768" reads as a typo). the table's column and the pane both read it here
    internal static string Ctx(long tokens) =>
        tokens.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
}
