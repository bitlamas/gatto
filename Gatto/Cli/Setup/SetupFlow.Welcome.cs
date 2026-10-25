using Gatto.Cli.Install;
using Gatto.Cli.Update;

namespace Gatto.Cli.Setup;

//the welcome, machine and install screens, the first segment of the setup
internal sealed partial class SetupFlow
{
    //the welcome screen draws the flow as a map (a step counter can't be right when the flow forks). keep the copy from the generator, a paraphrase reads well but is wrong about the screen
    private SetupScreen Welcome()
    {
        var engineInstalled = probes.LlamaServerPath() is { Length: > 0 };

        return new SetupScreen.Choice(
            WelcomeKey,
            "Ready to set gatto up?",
            //keep these labels lowercase, the footer is their only reader and the face renders them verbatim
            [
                new ChoiceOption(WelcomeGo, "get started"),
                new ChoiceOption(WelcomeNot, "not now"),
            ],
            BodyRows:
            [
                //keep the second body sentence as one clause, examples inside it read as a list that turns out to be an aside
                new SetupRow("gatto is an AI assistant that lives in your terminal. Describe a job "
                    + "in plain words and it does the work itself: reading, writing and running "
                    + "things on your computer, showing you every step."),
                new SetupRow(""),
                new SetupRow("The thinking happens on this machine too: the model is a file on your "
                    + "own disk, so your words and your files never have to leave it."),
                new SetupRow(""),
                //machine line on the welcome, the nothing-fits screen shouldn't be where the user first sees it
                new SetupRow(HardwareLine(probes.Hardware())),
                new SetupRow(""),
                new SetupRow("Setting up takes a few minutes:"),
                new SetupRow(""),
                //one gloss row per word of the strip, the user meets each step before it. the engine row states a fact about this run (gatto reports what it discovered)
                .. StripMap.Rows(engineInstalled, Glyphs),
                new SetupRow(""),
                //optional row, dropped last by height fitting. it names a glyph, so the word and the highlight both read the glyph table and can't disagree
                new SetupRow($"Inside, Tab moves between the parts of a screen, {Glyphs.Prompt} shows "
                    + "where the keys are.", RowTone.Aside, Highlight: [Glyphs.Prompt],
                    Optional: true),
            ],
            KeysOnly: true)
        {
            //draw the cat here and nowhere else, in the wizard or in session (no entry point may draw a second cat)
            Hero = _addRoad ? null : CatOf(Glyphs),
        };
    }

    //the cat rows come from Cats.For, the one home for the drawing. the glyph set is a parameter, or a value computed too early draws the Unicode cat
    private static string[] CatOf(Gatto.Terminal.GlyphSet g) =>
        [.. Gatto.Repl.Render.Chrome.Cats.For(null, g).Split('\n').Select(l => Gutter + l.TrimEnd('\r'))];

    //a two-space gutter for hero rows (the cat is pre-composed, without it it sits two cells left of the rows below)
    private const string Gutter = "  ";

    //the map column is separate from ColumnLabel's, one width over both label sets fits neither
    private static class StripMap
    {
        public static readonly string[] Labels =
            [WalkSection.Machine, WalkSection.Engine, WalkSection.Model, WalkSection.Check, WalkSection.Done];

        private static readonly Dictionary<string, string> Gloss = new(StringComparer.Ordinal)
        {
            [WalkSection.Machine] = "what this computer can run",
            [WalkSection.Engine] = "a copy of llama.cpp, fetched for it",
            [WalkSection.Model] = "pick one from Hugging Face, or bring your own",
            [WalkSection.Check] = "gatto proves it works",
            [WalkSection.Done] = "start talking to gatto",
        };

        public static readonly int Column = Labels.Max(l => l.Length) + 3;

        public static IEnumerable<SetupRow> Rows(bool engineInstalled,
            Gatto.Terminal.GlyphSet? glyphs) =>
            Labels.Select(l => new SetupRow(
                l.PadRight(Column) + (l == WalkSection.Engine && engineInstalled
                    ? $"{(glyphs ?? Gatto.Terminal.GlyphSet.Unicode).Ok} already on this machine"
                    : Gloss[l]),
                RowTone.Aside, Indent: 2, Hang: Column));
    }

    //the one home for the two-column labels the map and the install pair share. the width reads every label here, so a rename can't collapse the alignment
    private static class ColumnLabel
    {
        public const string Install = "Install";
        public const string Model = "Model";
        public const string First = "First question";
        public const string From = "from";
        public const string To = "to";

        public static readonly string[] All = [Install, Model, First, From, To];

        //the gutter between the two columns: widest label plus two spaces
        public static readonly int Column = All.Max(s => s.Length) + 2;
    }

    //the from and to rows sit on SummaryFacts rows, so they take the done step's column rather than ColumnLabel's
    private static SetupRow MapRow(string step, string what) => SummaryFacts.Row(step, what);

    private SetupScreen AnswerWelcome(string key)
    {
        if (key != WelcomeGo) return Leave();
        return Machine();
    }

    //the machine screen asks nothing (gatto reports what it found), its summary is the welcome's HardwareLine
    private SetupScreen Machine() => Emit(new SetupScreen.Choice(
        MachineKey,
        "What can this machine run?",
        [
            new ChoiceOption(MachineNext, "next"),
            new ChoiceOption(WelcomeNot, "leave"),
        ],
        BodyRows: [new SetupRow(MachineSummary(probes.Hardware()))],
        Machine: new MachineView(probes.Hardware(), probes.HardwareNames()),
        KeysOnly: true), MachineKey);

    //the readable shapes reuse the welcome's HardwareLine. the unreadable shape gets its own sentence, its system-memory pricing promise fits only the rows below
    private string MachineSummary(Gatto.Core.Hardware.HardwareSnapshot? hw) =>
        //null means the probe told us nothing, CpuOnly means it found no graphics driver. different facts get different sentences
        hw is null
            ? "Couldn't read this machine's memory, so nothing here is priced against it."
            : HardwareLine(hw);

    private SetupScreen AnswerMachine(string key) =>
        key == MachineNext ? EngineStep() : Leave();

    //true only when the running image is strictly newer than the installed copy, so both sites share one comparison and an unreadable version gives false
    private bool RunningIsNewerThanInstalled() =>
        ReleaseVersion.TryParseRunning(probes.RunningVersion(), out var mine)
        && ReleaseVersion.TryParse(probes.InstalledVersion(), out var theirs)
        && mine.IsNewerThan(theirs);

    //the one home for which install states ask. any state not named here falls into the offer, so a new state is asked rather than silently skipped
    private bool InstallAsks(InstallState state) => state switch
    {
        InstallState.Installed => false,
        InstallState.DevBuild => false,
        //an installed copy elsewhere asks only while this image is newer than it. an older or equal copy passes in silence, with the fact on the summary's row
        InstallState.AlreadyInstalledElsewhere => RunningIsNewerThanInstalled(),
        _ => true,
    };

    //first question of the done step, asked after the proof (a gatto that can't be typed does not work). null means nothing to ask
    private SetupScreen? InstallSegment()
    {
        var (state, dir) = probes.InstallStatus();
        //record the state, the closing sentence claims whether a new terminal can find gatto. null in Writes.InstallTo means declined, installed and dev build at once
        _installState = state;
        //record the directory beside the state, the summary fact names it. the Writes.InstallTo readers stay safe, they run only after an install screen is answered
        _installDir = dir;

        //offer replacement only when the running image is strictly newer, an older image would be a downgrade. nothing is downloaded, the bytes are already local
        if (!InstallAsks(state))
        {
            //one silent state still speaks, a dev build changes what happens. the other states leave a fact on the summary's gatto row
            if (state is InstallState.DevBuild)
                _narrate.Add(new SetupScreen.Info("segment.install",
                    [new SetupRow("running from a development build, which can't be copied as one "
                        + "file, skipping the install step")]));
            return null;
        }

        if (state is InstallState.AlreadyInstalledElsewhere)
        {
            //no heading here, the install question sits inside the done step and a heading would announce a section the strip doesn't have
            _installDir = dir;
            return Emit(new SetupScreen.Choice(
                UpdateInstalledKey,
                "A newer gatto is running than the one installed",
                [
                    //a replacement offer must not arrive pre-answered.
                    new ChoiceOption(InstallYes, "Update the installed copy"),
                    new ChoiceOption(InstallNo, "Leave it"),
                ],
                BodyRows:
                [
                    new SetupRow($"Installed: {probes.InstalledVersion()} at {dir}"),
                    new SetupRow($"This one:  {probes.RunningVersion()}"),
                    new SetupRow(""),
                    new SetupRow("Updating copies this gatto over the installed one. Nothing is "
                        + "downloaded, the files are already here."),
                ],
                NoDefault: true),
                UpdateInstalledKey);
        }

        _installDir = dir;
        var half = state == InstallState.InstalledButNotOnPath;

        //this repair offer needs the same version comparison as the update offer, or an older image silently downgrades a newer install
        var installedHere = probes.InstalledVersion();
        _repairOnly = half && !RunningIsNewerThanInstalled();
        return Emit(new SetupScreen.Choice(
            InstallKey,
            //the title says what's wrong in the user's terms (PATH is jargon). the option says what gatto will do rather than naming the mechanism
            half ? "gatto is installed, but only this window can find it"
                 : "Should gatto install itself?",
            [
                new ChoiceOption(InstallYes, half ? "Fix that for me" : "Yes, install it", Recommended: true),
                new ChoiceOption(InstallNo, "Not now"),
            ],
            //the first branch names its type, two collection expressions in a ternary leave the spread nothing to convert between
            BodyRows: [.. half
                ?
                (IReadOnlyList<SetupRow>)[
                    //keep these as two rows, the copy's location then why the window can't find it, one paragraph wraps them together
                    new SetupRow($"A copy is already at {dir}."),
                    new SetupRow("Windows doesn't know to look there yet, so typing gatto in a "
                        + "new window won't work until it does."),
                    //show the version rows only when both versions can be read, a row that says unknown teaches nothing
                    .. installedHere is { Length: > 0 } iv && _repairOnly
                        ? new[]
                        {
                            new SetupRow(""),
                            //an equal version also sets the repair flag. an equal pair gets its own sentence, two equal numbers read as a fault
                            new SetupRow(string.Equals(iv, probes.RunningVersion(), StringComparison.Ordinal)
                                ? $"The installed copy is the same version ({iv}), so gatto will "
                                  + "leave it in place and just make it findable."
                                : $"The installed copy is {iv} and this one is "
                                  + $"{probes.RunningVersion()}, so gatto will leave it in "
                                  + "place and just make it findable."),
                        }
                        : [],
                ]
                :
                [
                    //the from and to rows say which gatto this is when the exe sits beside other copies. keep them as two rows, where gatto is now and what installing does
                    new SetupRow("gatto is running from the folder you unzipped it into."),
                    new SetupRow("Installing puts a copy in your account's programs folder, so "
                        + "you can type gatto in any terminal instead of finding this file again."),
                    new SetupRow(""),
                    MapRow(ColumnLabel.From, probes.RunningFrom()),
                    MapRow(ColumnLabel.To, dir),
                    new SetupRow(""),
                    //the new-terminal line belongs on the completion screen (before a decision it reads as a caveat)
                    new SetupRow("Your account only, Windows won't ask for administrator "
                        + "permission."),
                ],
                //one aside from one place, appended to both branches (two copies would drift). it ends by saying this console works, or it would read as a requirement
                .. TerminalNudgeRows()]),
            InstallKey);
    }

    //the Windows Terminal aside, legacy console only, after a blank row. the probe also decides the glyph set, so the aside and the vocabulary can't disagree
    private IReadOnlyList<SetupRow> TerminalNudgeRows() =>
        probes.IsLegacyConsole()
            //mark the nudge optional, a short terminal drops it before it drops a fact (this is not a thing the install does)
            ? [new SetupRow(""),
               new SetupRow("gatto looks better in Windows Terminal (free, in the Microsoft "
                   + "Store); this console works too.", RowTone.Aside, Optional: true)]
            : [];

    private SetupScreen AnswerInstall(string optionKey)
    {
        _awaiting = null;
        //this records intent, leaving after this screen installs nothing. the answer sets exactly one of the two (a copy beside a repair would install the older image)
        if (optionKey == InstallYes)
            Writes = _repairOnly
                ? Writes with { PathOnly = true }
                : Writes with { InstallTo = _installDir };
        //the answer continues the done step. returning EngineStep would send a user who just proved a model back to pick an engine
        return AfterTheInstallAsk();
    }

    //the update answer must set InstallTo with no PathOnly (AnswerInstall would route it through _repairOnly, which could drift when either branch changes)
    private SetupScreen AnswerUpdateInstalled(string optionKey)
    {
        _awaiting = null;
        if (optionKey == InstallYes)
            Writes = Writes with { InstallTo = _installDir };
        return AfterTheInstallAsk();
    }
}
