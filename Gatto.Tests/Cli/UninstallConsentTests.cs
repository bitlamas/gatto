using Gatto.Cli;
using Gatto.Terminal;

namespace Gatto.Tests.Cli;

//the consent path runs through its ask seam, so the most destructive action is exercised without a human at a keyboard.
public class UninstallConsentTests
{
    private static Gatto.Roles.RunningInfo Running(int pid = 31416) =>
        new("lfm2.5-1.2b-thinking", 1235, "2026-08-17T00:00:00Z", pid);

    //the scripted ask records the questions and options it was given. a screen nobody sees is a consent nobody gave, so the questions are half the subject.
    private sealed class Script(params int?[] answers)
    {
        private int _i;
        public List<string> Questions { get; } = [];
        public List<IReadOnlyList<string>> Options { get; } = [];

        public UninstallConsent.Asker Ask => (q, o) =>
        {
            Questions.Add(q);
            Options.Add(o);
            return _i < answers.Length ? answers[_i++] : null;
        };
    }

    private static IReadOnlyList<OwnedItem> Map(bool deleteHome = false, int models = 1,
        Gatto.Roles.RunningInfo? stopping = null) =>
        Uninstall.Map(
            @"C:\install", @"C:\home", deleteHome,
            new InstallFacts(ExePresent: true, ExeBytes: 1_000_000, OnPath: true),
            @"C:\llama\llama-server.exe",
            [.. Enumerable.Range(0, models).Select(i => ($"m{i}", $@"C:\weights\m{i}.gguf"))],
            stopping);

    [Fact]
    public void NO_SCREEN_ON_THE_CONSENT_PATH_ASKS_WITH_A_BRACKETED_LETTER()
    {
        //the path routes through SelectPrompt, so no screen parses a typed character. questions and options are both checked (a bracketed letter is wrong either way)
        var script = new Script(0, 0);
        UninstallConsent.Ask(script.Ask, @"C:\home", Running());
        UninstallConsent.Confirm(script.Ask);

        var said = string.Join(" | ", script.Questions.Concat(script.Options.SelectMany(o => o)));
        foreach (var banned in new[] { "[y/N]", "[y/n]", "[Y/n]", "(y/n)" })
            Assert.DoesNotContain(banned, said, StringComparison.OrdinalIgnoreCase);
    }

    //the no option comes first and names the uninstall itself. this check pins the position and the naming, and leaves the exact wording free.
    [Fact]
    public void THE_CONFIRMS_NO_CANCELS_THE_UNINSTALL_AND_COMES_FIRST()
    {
        var script = new Script(0);

        Assert.False(UninstallConsent.Confirm(script.Ask));

        var options = Assert.Single(script.Options);
        Assert.Equal(2, options.Count);
        Assert.StartsWith("No", options[0], StringComparison.Ordinal);
        Assert.StartsWith("Yes", options[1], StringComparison.Ordinal);
        //the no answer is about the command. the map already states what stays, so this answer must not claim it again.
        Assert.DoesNotContain("everything", options[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("uninstall", options[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void THE_SERVER_SCREEN_IS_CONDITIONAL_on_a_server_actually_running()
    {
        //asking to stop a server that isn't running teaches the user their answer doesn't matter. the scope question always appears, so check its wording too
        var withServer = new Script(0, 0);
        UninstallConsent.Ask(withServer.Ask, @"C:\home", Running());
        Assert.Equal(2, withServer.Questions.Count);
        Assert.Contains("Stop the llama-server", withServer.Questions[0], StringComparison.Ordinal);

        var without = new Script(0);
        UninstallConsent.Ask(without.Ask, @"C:\home", null);
        Assert.Single(without.Questions);
        Assert.DoesNotContain("Stop", without.Questions[0], StringComparison.Ordinal);
    }

    [Fact]
    public void ESC_AT_ANY_SCREEN_MEANS_NOTHING_IS_REMOVED()
    {
        //a null answer from either screen must leave the whole path as null. it must never become a default that quietly proceeds.
        Assert.Null(UninstallConsent.Ask(new Script((int?)null).Ask, @"C:\home", Running()));
        Assert.Null(UninstallConsent.Ask(new Script(0, null).Ask, @"C:\home", Running()));
        Assert.False(UninstallConsent.Confirm(new Script((int?)null).Ask));
    }

    [Fact]
    public void KEEPING_YOUR_RECORDS_IS_THE_FIRST_ROW_and_the_confirm_defaults_to_NO()
    {
        //the cursor opens on row zero, so row zero must be the safe answer. the check asserts the order, since the order is what makes the default safe.
        var script = new Script(0, 0);
        UninstallConsent.Ask(script.Ask, @"C:\home", Running());

        Assert.Equal("Leave it running", script.Options[0][1]);
        Assert.Equal("Keep them", script.Options[1][0]);

        var confirm = new Script(0);
        Assert.False(UninstallConsent.Confirm(confirm.Ask));
        Assert.StartsWith("No,", confirm.Options[0][0], StringComparison.Ordinal);
    }

    [Fact]
    public void A_DECLINED_STOP_NAMES_THE_PID_and_never_the_command_that_is_about_to_be_deleted()
    {
        //the line names the pid, since the pid and Task Manager survive the uninstall. it must not point at a command the same screen is deleting.
        var said = UninstallConsent.LeftRunning(Running(4242));

        Assert.Contains("4242", said, StringComparison.Ordinal);
        Assert.DoesNotContain("gatto serve stop", said, StringComparison.Ordinal);
        //the ban targets pointing at the command, so the test also bans the bare words serve stop
        Assert.DoesNotContain("serve stop", said, StringComparison.Ordinal);
    }

    [Fact]
    public void THE_PATH_LINES_UP_UNDER_THE_LABEL_it_belongs_to()
    {
        //a caller's prefix reaches only the first line, so joining into one string shifts continuation rows left. returning rows makes this shift unexpressible.
        foreach (var item in Map())
        {
            var rows = Uninstall.Rows(item);
            //the check slices at the mark column instead of searching, since a mark's own word contains the letters being measured.
            Assert.Equal(2, rows.Count);
            Assert.StartsWith(item.What, rows[0][Uninstall.MarkColumn..], StringComparison.Ordinal);
            Assert.Equal(Uninstall.MarkColumn, rows[1].TakeWhile(char.IsWhiteSpace).Count());
            Assert.StartsWith(item.Where, rows[1][Uninstall.MarkColumn..], StringComparison.Ordinal);
        }
    }

    [Fact]
    public void REGISTER_A_GIVES_THREE_MARKS_THREE_DISTINCT_COLOURS()
    {
        //the marks paint by meaning, removed red and stays green. the three marks need three distinct colours, since collapsing them would undo the label distinction.
        var w = new StringWriter();
        var cli = new CliSurface(w, new Gatto.Terminal.Theme(new Gatto.Terminal.TermCaps(true, true)), glyphs: GlyphSet.Unicode);
        UninstallConsent.WriteMap(cli, Map(deleteHome: true, models: 1, stopping: Running()));

        var marks = Enum.GetValues<UninstallMark>().Select(Uninstall.Word).ToList();
        var painted = w.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(r => marks.Any(m => r.Contains(m, StringComparison.Ordinal)))
            //the slice up to the first m is the mark's own paint.
            .Select(r => r[..r.IndexOf('m')])
            .Distinct()
            .ToList();

        Assert.Equal(3, painted.Count);
    }

    [Fact]
    public void A_ROW_WITH_NOWHERE_TO_POINT_RENDERS_ONE_LINE()
    {
        //a row's second line is a location a person can verify. the disclaimer has no location, so a row with nothing to point at renders as one line.
        var general = Assert.Single(Map(models: 0), i => i.Where.Length == 0);

        Assert.Single(Uninstall.Rows(general));
        Assert.Contains("never removes", general.What, StringComparison.Ordinal);
    }

    [Fact]
    public void THE_MARK_COLUMN_IS_COMPUTED_FROM_THE_WORDS_not_typed_as_a_number()
    {
        //a hard-coded indent goes stale when another label arrives. the mark column must come from the longest word so the alignment cannot expire.
        Assert.True(Uninstall.MarkColumn > "removed".Length);
        foreach (var mark in Enum.GetValues<UninstallMark>())
            Assert.True(Uninstall.Word(mark).Length < Uninstall.MarkColumn);
    }

    [Fact]
    public void A_RUNNING_SERVER_BEING_STOPPED_IS_ITS_OWN_MARK()
    {
        //a running process is neither a deletion nor a survival, so it gets its own mark. the map states exactly what the confirm covers
        var row = Assert.Single(Map(stopping: Running()), i => i.Mark == UninstallMark.Stopped);

        Assert.Contains("llama-server", row.What, StringComparison.Ordinal);
        Assert.Contains("31416", row.Where, StringComparison.Ordinal);
        //the row vanishes when the stop was declined, since the map states only what the confirm covers.
        Assert.DoesNotContain(Map(), i => i.Mark == UninstallMark.Stopped);
    }

    [Fact]
    public void THE_WEIGHTS_ARE_NEVER_REMOVED_and_the_map_says_so_even_with_none_configured()
    {
        //model weight files are never deleted, and the map says so even when no model is configured.
        foreach (var item in Map(deleteHome: true, models: 2).Where(i => i.What.Contains("model file")))
            Assert.Equal(UninstallMark.Stays, item.Mark);

        //the map must keep the weights row even with no models configured. without it the map is silent on the screen where the user wonders whether downloads go.
        var none = Map(models: 0);
        var general = Assert.Single(none, i => i.What.Contains("model files", StringComparison.Ordinal));
        Assert.Equal(UninstallMark.Stays, general.Mark);
    }

    [Fact]
    public void THE_HOME_ROW_SAYS_MODEL_SETUPS_and_never_bare_models()
    {
        //the words your models on an uninstall screen read as the weight files, so the home row says model setups.
        var home = Assert.Single(Map(), i => i.Where == @"C:\home");

        Assert.Contains("model setups", home.What, StringComparison.Ordinal);
    }
}
