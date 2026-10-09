using Gatto.Terminal;
namespace Gatto.Cli.Setup.Tui;

//the live countdown before the REPL takes the screen, so the record is read rather than glimpsed. the figure comes from the loop rather than the Seconds constant
internal static class Countdown
{
    //3, and exactly one test hardcodes the literal, so changing this needs that test changed too
    internal const int Seconds = 3;

    //the callback gets the words only and the surface decides where the line goes and erases it, so wait runs after the last render
    public static void Run(Action<string> render, Action<TimeSpan> wait, GlyphSet? glyphs)
    {
        var g = glyphs ?? GlyphSet.Unicode;
        for (var left = Seconds; left > 0; left--)
        {
            render($"starting gatto in {left}s{g.Ellipsis}");
            wait(TimeSpan.FromSeconds(1));
        }
    }
}

//one composer for the done step's summary and the record printed to scrollback, so the two cannot drift apart. every difference between the two lives in Compose
internal static class Epilogue
{
    //which render is being composed. it is read once at the top, so every difference between the two renders is visible in one expression
    internal enum Face
    {
        //the closing screen, model first and its folder under it
        Summary,

        //the record printed to scrollback, with the machine first and no folder line
        Record,
    }

    //the facts a finished run has, each already worded. a fact nothing measured is null, so neither render claims it, and no row prints an unknown value
    internal readonly record struct Facts(
        WizardRow? Machine,
        WizardRow? Model,
        WizardRow? ModelFolder,
        WizardRow? Engine,
        WizardRow Check,
        WizardRow Gatto,
        WizardRow Config,
        ServerFact? Server = null);

    //the connect server as data rather than as rows, since the composer owns the fold. the note holds the long form the record cuts short
    internal readonly record struct ServerFact(
        string Url, string? Model, string Context, string Note, string? Beside = null);

    //the record's head comes from the command header, so gatto is spelled one way. the date is passed in, because the drawn frame is the only oracle for this row
    public static string Head(GlyphSet g, string version, string build, DateOnly? on,
        string? stoppedAt = null, string command = ScreenPainter.DefaultCommand, bool dev = false,
        Theme? theme = null) =>
        Gatto.Cli.CommandBanner.HeaderLine(theme, Invocation(command), g,
            new Gatto.Cli.VersionStamp(version, build, dev), lead: stoppedAt,
            tail: on is { } day ? day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) : null);

    //the header composer adds gatto itself, so the command reaches it with that word stripped
    private static string Invocation(string command) =>
        command.StartsWith("gatto ", StringComparison.Ordinal) ? command["gatto ".Length..] : command;

    //what a wizard run that wrote nothing leaves in scrollback: the header and the leave sentence at column zero, no title row, no rules
    public static IReadOnlyList<string> LeaveBlock(GlyphSet g, string version, string build, string command,
        WizardRow? said, int width, bool dev = false, Theme? theme = null) =>
        LeaveBlock(g, version, build, command, said is null ? [] : [said], width, dev, theme);

    //one row per model a walk added, each under the header as the single line is
    public static IReadOnlyList<string> LeaveBlock(GlyphSet g, string version, string build, string command,
        IReadOnlyList<WizardRow> said, int width, bool dev = false, Theme? theme = null)
    {
        List<string> lines = [Head(g, version, build, on: null, command: command, dev: dev, theme: theme)];
        if (said.Count > 0)
        {
            lines.Add("");
            lines.AddRange(Body(said, width, g, theme));
        }
        return lines;
    }

    //the record's rows in order: head, blank, facts, blank, closing sentence. an empty list means the run wrote nothing, and that run prints the leave block
    public static IReadOnlyList<string> Lines(
        IReadOnlyList<WizardRow> facts, string version, string build, DateOnly on,
        WizardRow? closing, int width, GlyphSet glyphs, string? stoppedAt = null,
        string command = ScreenPainter.DefaultCommand, bool dev = false, Theme? theme = null)
    {
        var g = glyphs;
        if (facts.Count == 0) return [];

        List<string> lines = [Head(g, version, build, on, stoppedAt, command, dev, theme), ""];
        lines.AddRange(Body(facts, width, g, theme));
        lines.Add("");
        if (closing is not null) lines.AddRange(Body([closing], width, g, theme));
        return lines;
    }

    //trailing blanks are trimmed here, since a label wrapped onto its own row keeps them. the wrap never splits a highlight, so painting after it cannot tear one
    private static IEnumerable<string> Body(IReadOnlyList<WizardRow> rows, int width, GlyphSet g, Theme? theme) =>
        rows.SelectMany(row => WizardRows.Plain([row], width, margin: 2, glyphs: g, frameGutter: "")
            .Select(line => line.TrimEnd(' '))
            .Select(line => theme is null || line.Length == 0
                ? line
                : theme.PaintSpans(line, row.Highlight, baseFg: null, spanFg: CliSurface.Style(CliInk.Command))));

    //a server that never named its model adds nothing to the fold. the summary drops that row, and the record's separators cannot announce a fact nobody has
    private static string Named(GlyphSet g, string? model) =>
        model is { Length: > 0 } named ? $" {g.Dot} " + named : "";

    //the note's first clause, which is what the record has room for. the short form is cut from the long one, so the two cannot disagree
    private static string FirstClause(string note) =>
        note.IndexOf(',', StringComparison.Ordinal) is var at && at > 0 ? note[..at] : note;

    //a question the done step asks, declared in the order it asks them so the leave sentence can name them in that order
    internal enum LeaveQuestion
    {
        //the install question
        Install,

        //the weekly update check, asked last
        Updates,
    }

    //how a leave opens, named here rather than derived from a nullable model. three states share no model to name, and a null would give one the other's wording
    internal enum LeaveLead
    {
        //before the write pause: the record says nothing was written
        NothingWritten,

        //the llama setup, finished as far as it went: gatto opens with this model
        PointedAt,

        //the connect setup: the server row above says what it points at
        SetUp,

        //the model is written, but no default points at it
        SavedNotPointed,
    }

    //one sentence computed from what the run did, with the closing clause only for an install question the run never reached
    public static WizardRow LeaveSentence(
        LeaveLead lead, string? model, IReadOnlyList<LeaveQuestion> unreached)
    {
        //no model and no file yet, so one row says that and points at gatto setup again. no questions clause, since a run that wrote nothing reached none
        if (lead is LeaveLead.NothingWritten)
            return new WizardRow("Nothing has been written, run gatto setup again whenever "
                + "you're ready.", Highlight: ["gatto setup"]);

        var text = lead switch
        {
            LeaveLead.PointedAt => $"gatto is set up and pointed at {model}.",
            LeaveLead.SetUp => "gatto is set up.",
            _ => "The model and the llama.cpp path are saved, gatto is not pointed at a model yet.",
        };

        if (unreached.Count > 0)
        {
            var many = unreached.Count > 1;
            text += $" {Counted(unreached.Count)} reached, "
                + $"{Listed(unreached)}, so gatto setup asks {(many ? "them" : "it")} next time.";
            if (unreached.Contains(LeaveQuestion.Install))
                text += " Until then, run it from the file above.";
        }

        return new WizardRow(text, Highlight: ["gatto setup"]);
    }

    //the count clause, built from the count itself. the arm for three or more is unreachable today and stays, so a third question cannot ship a wrong count
    private static string Counted(int n) => n switch
    {
        1 => "One question wasn't",
        2 => "Two questions weren't",
        _ => $"{n} questions weren't",
    };

    //the questions as the sentence names them, comma-listed with the Oxford comma. the names live only here, so renaming a question means one edit
    private static string Listed(IReadOnlyList<LeaveQuestion> unreached) =>
        string.Join(", and ", unreached.Select(q => q switch
        {
            LeaveQuestion.Install => "installing gatto",
            _ => "the weekly update check",
        }));

    //the rows of one render, in order
    public static IReadOnlyList<WizardRow> Compose(in Facts f, Face face, GlyphSet? glyphs)
    {
        var g = glyphs ?? GlyphSet.Unicode;
        //both differences between the two renders are here: what leads, and what the lead contains. everything below the lead is the same on both
        WizardRow?[] lead = (face, f.Server) switch
        {
            //the record's server row holds the model and the context too, since a record spends one line per fact
            (Face.Record, { } s) => [SetupFlow.SummaryFacts.Row("server",
                $"{s.Url}{Named(g, s.Model)} {g.Dot} context {s.Context} ({FirstClause(s.Note)})")],

            (Face.Summary, { } s) =>
            [
                //the beside-note belongs to the summary alone, since the record's server row already holds the model and the context
                SetupFlow.SummaryFacts.Row("server",
                    s.Beside is { } beside ? $"{s.Url} {g.Dot} {beside}" : s.Url),
                s.Model is { Length: > 0 } named ? SetupFlow.SummaryFacts.Row("model", named) : null,
                SetupFlow.SummaryFacts.Row("context", $"{s.Context} {g.Dot} {s.Note}"),
            ],

            (Face.Summary, null) => [f.Model, f.ModelFolder, f.Engine],
            _ => [f.Machine, f.Engine, f.Model],
        };

        var rows = new List<WizardRow>(lead.Length + 3);
        foreach (var row in lead)
            if (row is not null) rows.Add(row);

        rows.Add(f.Check);
        rows.Add(f.Gatto);
        //the config row is last on every done screen and both epilogues, added in this one place so both renders keep the rule
        rows.Add(f.Config);
        return rows;
    }
}
