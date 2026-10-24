namespace Gatto.Cli.Setup;

//one moment of the running check, every event so far added up. the total is never zero, and finished tasks are numbered rather than marked
internal readonly record struct CheckTick(
    int Index, int Total, string Task, IReadOnlyList<string> Done, bool ServerUp = false,
    bool StillLoading = false,
    string? Reading = null);   //the file the run is hashing before the battery, null once the battery starts or when it read nothing

//the live rows of the running check, pure composition with no clock and no width
internal static class CheckRows
{
    //each row drops when its fact is absent, so a null tick still gets the starting row. the server row goes once the battery counts
    public static IReadOnlyList<SetupRow> Of(CheckTick? tick, Gatto.Terminal.GlyphSet glyphs)
    {
        //spell the empty tick out, since a defaulted struct hands back null for Done and the count below dereferences it
        var t = tick ?? new CheckTick(0, 0, "", []);
        return
        [
        //the read takes the place of the server rows, it runs on a server already up or before one starts
        .. t.Reading is { } file && t.Total == 0
            ? (IReadOnlyList<SetupRow>)[SetupFlow.CheckFacts.Row("running",
                Gatto.Roles.Audition.AuditionProgress.ReadingWords(file))]
            : [],
        //the first thing the check does is start a server, so the starting row shares the running label on purpose
        .. t.Reading is null && !t.ServerUp && t.Total == 0
            ? (IReadOnlyList<SetupRow>)[SetupFlow.CheckFacts.Row("running",
                "starting the server" + glyphs.Ellipsis)]
            : [],
        //the words are the flow's and the fact is the probe's, since copy shipped by a probe reaches no screen test
        .. t.Reading is null && t.ServerUp && t.Total == 0
            ? (IReadOnlyList<SetupRow>)[SetupFlow.CheckFacts.Row("server", "started, loading the model")]
            : [],
        //the count is lifted out of the aside, since the number is what the eye looks for and the task's words explain it
        .. t.Total > 0
            ? (IReadOnlyList<SetupRow>)[SetupFlow.CheckFacts.Row("running",
                $"task {t.Index} of {t.Total} {glyphs.Dot} {t.Task}",
                lift: $"task {t.Index} of {t.Total}")]
            : [],
        .. t.Done.Count > 0
            ? (IReadOnlyList<SetupRow>)[SetupFlow.CheckFacts.Row("done",
                string.Join($" {glyphs.Dot} ", t.Done))]
            : [],
        ];
    }

    //the block's prose, wrapped inside the frame rather than folding like a fact row. it drops once a task starts, since the model has come up by then
    public static IReadOnlyList<SetupRow> Aside(CheckTick? t) =>
        t is { StillLoading: true, Total: 0 }
            ? [new SetupRow("Still loading, a model that barely fits can take a while. "
                + "Nothing is wrong.", RowTone.Aside)]
            : [];
}
