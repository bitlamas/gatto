using Gatto.Cli;
using Gatto.Repl.Term;

namespace Gatto.Tests.Copy;

//every line gatto prints outside the REPL transcript, rendered rather than read
[Collection("e2e")]
public class CommandCorpusTests : IDisposable
{
    private readonly CorpusDriver _d = new();

    public void Dispose() => _d.Dispose();

    public static TheoryData<CorpusMode> PaintedAndPlain =>
        new() { CorpusMode.Dark, CorpusMode.Light, CorpusMode.Plain };

    //the funnel, driven through GattoApp.RunAsync

    [Theory]
    [InlineData(CorpusMode.Dark)]
    [InlineData(CorpusMode.Plain)]
    public void HELP(CorpusMode mode) =>
        CopyGolden.Check($"help-{mode}", CorpusDriver.Frame(_d.Run(mode, "--help")));

    [Theory]
    [InlineData(CorpusMode.Dark)]
    [InlineData(CorpusMode.Plain)]
    public void VERSION(CorpusMode mode) =>
        CopyGolden.Check($"version-{mode}", CorpusDriver.Frame(_d.Run(mode, "--version")));

    [Theory]
    [InlineData(CorpusMode.Dark)]
    [InlineData(CorpusMode.Light)]
    [InlineData(CorpusMode.Plain)]
    public void SERVE_STATUS_NOT_SERVING(CorpusMode mode) =>
        CopyGolden.Check($"serve-status-{mode}", CorpusDriver.Frame(_d.Run(mode, "serve", "status")));

    [Theory]
    [InlineData(CorpusMode.Dark)]
    [InlineData(CorpusMode.Plain)]
    public void SERVE_STOP_NOTHING_TO_STOP(CorpusMode mode) =>
        CopyGolden.Check($"serve-stop-{mode}", CorpusDriver.Frame(_d.Run(mode, "serve", "stop")));

    //the json arm has no banner and no ink by contract, so it is here to pin that it stays bare
    [Fact]
    public void SERVE_STATUS_JSON_STAYS_BARE() =>
        CopyGolden.Check("serve-status-json",
            CorpusDriver.Frame(_d.Run(CorpusMode.Dark, "serve", "status", "--json")));

    //the refusal column, which exists because the driver says Interactive is false

    [Fact]
    public void MODEL_REFUSES_AN_ARGUMENT() =>
        CopyGolden.Check("refusal-model-argument",
            CorpusDriver.Frame(_d.Run(CorpusMode.Dark, "model", "list")));

    [Fact]
    public void MODEL_REFUSES_WITHOUT_A_TERMINAL() =>
        CopyGolden.Check("refusal-model-no-terminal",
            CorpusDriver.Frame(_d.Run(CorpusMode.Dark, "model")));

    [Fact]
    public void SETUP_REFUSES_WITHOUT_A_TERMINAL() =>
        CopyGolden.Check("refusal-setup", CorpusDriver.Frame(_d.Run(CorpusMode.Dark, "setup")));

    [Fact]
    public void UNINSTALL_REFUSES_WITHOUT_A_TERMINAL() =>
        CopyGolden.Check("refusal-uninstall",
            CorpusDriver.Frame(_d.Run(CorpusMode.Dark, "uninstall")));

    [Fact]
    public void AN_UNKNOWN_SERVE_SUBCOMMAND_IS_A_USAGE_ERROR() =>
        CopyGolden.Check("usage-serve-subcommand",
            CorpusDriver.Frame(_d.Run(CorpusMode.Dark, "serve", "wobble")));

    //the header, at the composition point both forms share

    [Theory]
    [MemberData(nameof(PaintedAndPlain))]
    public void THE_ONE_LINER_HEADER(CorpusMode mode)
    {
        var w = new StringWriter();
        CommandBanner.WriteHeader(w, CorpusDriver.ThemeFor(mode), "serve stop",
            CorpusDriver.Glyphs, stamp: CorpusFacts.Stamp);
        CopyGolden.Check($"header-oneliner-{mode}", CorpusDriver.Visible(w.ToString()));
    }

    [Theory]
    [MemberData(nameof(PaintedAndPlain))]
    public void THE_FOUR_LINE_BANNER(CorpusMode mode)
    {
        var w = new StringWriter();
        CommandBanner.Write(w, CorpusDriver.ThemeFor(mode), "uninstall",
            glyphs: CorpusDriver.Glyphs, stamp: CorpusFacts.Stamp);
        CopyGolden.Check($"header-banner-{mode}", CorpusDriver.Visible(w.ToString()));
    }

    //the writer's own rows, which every command body goes through

    [Theory]
    [MemberData(nameof(PaintedAndPlain))]
    public void THE_WRITER_ROWS(CorpusMode mode)
    {
        var w = new StringWriter();
        var cli = new CliSurface(w, CorpusDriver.ThemeFor(mode), CorpusDriver.Glyphs);
        cli.Say("a plain sentence in the body");
        cli.Ok(CorpusFacts.ModelId, $"pid {CorpusFacts.Pid}");
        cli.Row(("run ", CliInk.Plain), CliSurface.Command("gatto serve stop"), (" to eject", CliInk.Dim));
        cli.Row(("serving ", CliInk.Plain), (CorpusFacts.ModelId, CliInk.Model), (" on port 1235", CliInk.Dim));
        cli.Under("a row subordinate to the one above it");
        cli.Blank();
        CopyGolden.Check($"writer-rows-{mode}", CorpusDriver.Visible(w.ToString()));
    }

    //the serve notices, composed in Cli today

    //each notice renders through its own call site, since a convenient writer pins an indent no user sees
    [Fact]
    public void THE_REUSE_NOTICE_GOES_THROUGH_THE_COMMAND_SURFACE()
    {
        var w = new StringWriter();
        var cli = new CliSurface(w, CorpusDriver.ThemeFor(CorpusMode.Dark), CorpusDriver.Glyphs);
        ServeNotice.SayReusing(cli, CorpusDriver.Glyphs, @"C:\weights\" + CorpusFacts.LongModelId + ".gguf",
            CorpusFacts.Now.AddMinutes(-7).ToString("O"), CorpusFacts.Now, 102_400_000_000);
        CopyGolden.Check("notice-reusing", CorpusDriver.Visible(w.ToString()));
    }

    //the refusal is written bare to stderr, with no surface and no ink
    [Fact]
    public void THE_REFUSAL_IS_BARE_ON_STDERR()
    {
        var text = "--- stderr ---\n" + ServeNotice.Refusing(CorpusFacts.ModelId + ".gguf") + "\n";
        CopyGolden.Check("notice-bare-stderr", CorpusDriver.Visible(text));
    }

    //the exit block, the one place the end of a session is printed

    //every state the block can show, rendered, since a phrase test cannot see the order or the blank rows
    public static TheoryData<string, bool, bool, string> ExitStates => new()
    {
        { "quit-stopped", true, true, "stopped" },
        { "quit-still-loaded", true, true, "hint" },
        { "quit-nothing-closed", true, true, "nothing" },
        { "quit-no-session", true, false, "stopped" },
        { "input-died", false, true, "hint" },
        { "no-server", true, true, "none" },
        { "empty", false, false, "none" },
    };

    [Theory]
    [MemberData(nameof(ExitStates))]
    public void THE_EXIT_BLOCK(string state, bool farewell, bool resume, string server)
    {
        foreach (var mode in new[] { CorpusMode.Dark, CorpusMode.Plain })
        {
            var w = new StringWriter();
            var cli = new CliSurface(w, CorpusDriver.ThemeFor(mode), CorpusDriver.Glyphs);
            var line = server switch
            {
                "stopped" => Gatto.Cli.ServeLines.AtExit.AfterStop(
                    new(Gatto.Roles.ServeManager.StopResult.Stopped, CorpusFacts.ModelId, CorpusFacts.Pid, null),
                    sessionHadServer: true, CorpusDriver.Glyphs)!.Words,
                "nothing" => Gatto.Cli.ServeLines.AtExit.AfterStop(
                    new(Gatto.Roles.ServeManager.StopResult.NotServing, null, null, null),
                    sessionHadServer: true, CorpusDriver.Glyphs)!.Words,
                "hint" => ServeNotice.ExitHint(true, 102_400_000_000, CorpusDriver.Glyphs),
                _ => null,
            };
            ExitBlock.Write(cli, CorpusDriver.Glyphs, farewell, resume, line);
            CopyGolden.Check($"exit-block-{state}-{mode}", CorpusDriver.Visible(w.ToString()));
        }
    }

    //what the wizard leaves in scrollback, across the width sweep

    //the sweep drives Lines, since Head takes no width and slicing its string exercises no renderer
    [Theory]
    [InlineData(40)]
    [InlineData(60)]
    [InlineData(80)]
    [InlineData(100)]
    [InlineData(120)]
    public void THE_WIZARD_RECORD_AT_EVERY_WIDTH(int width)
    {
        //the drawn record, built by the row builder the flow uses, so the label column and the hang are the product's
        var dot = CorpusDriver.Glyphs.Dot;
        var facts = new List<Gatto.Cli.Setup.WizardRow>
        {
            Gatto.Cli.Setup.SetupFlow.SummaryFacts.Row("fetched", $"llama-b10076-bin-win-vulkan-x64.zip {dot} digest matched"),
            Gatto.Cli.Setup.SetupFlow.SummaryFacts.Row("into", @"C:\Users\you\.gatto\llama\b10076\"),
            Gatto.Cli.Setup.SetupFlow.SummaryFacts.Row("model", $"{CorpusFacts.ModelId} {dot} ~23.8 GB"),
            Gatto.Cli.Setup.SetupFlow.SummaryFacts.Row("checked", "llama-server.exe ran, and it is the path gatto wrote to its config"),
        };
        var rows = Gatto.Cli.Setup.Tui.Epilogue.Lines(facts, CorpusFacts.Version, CorpusFacts.Build,
            CorpusFacts.Today, closing: new Gatto.Cli.Setup.WizardRow("run gatto to start a session"),
            width, CorpusDriver.Glyphs);
        CopyGolden.Check($"wizard-record-{width}",
            CorpusDriver.Visible(string.Join("\n", rows) + "\n"));
    }

    //a wrapped fact breaks where a packed list does, so the separator never stays on the edge of a row it left
    [Fact]
    public void THE_WIZARD_RECORD_AT_60_ENDS_NO_ROW_WITH_THE_DOT()
    {
        var dot = CorpusDriver.Glyphs.Dot;
        var facts = new List<Gatto.Cli.Setup.WizardRow>
        {
            Gatto.Cli.Setup.SetupFlow.SummaryFacts.Row("fetched", $"llama-b10076-bin-win-vulkan-x64.zip {dot} digest matched"),
            Gatto.Cli.Setup.SetupFlow.SummaryFacts.Row("into", @"C:\Users\you\.gatto\llama\b10076\"),
            Gatto.Cli.Setup.SetupFlow.SummaryFacts.Row("model", $"{CorpusFacts.ModelId} {dot} ~23.8 GB"),
            Gatto.Cli.Setup.SetupFlow.SummaryFacts.Row("checked", "llama-server.exe ran, and it is the path gatto wrote to its config"),
        };
        var rows = Gatto.Cli.Setup.Tui.Epilogue.Lines(facts, CorpusFacts.Version, CorpusFacts.Build,
            CorpusFacts.Today, closing: new Gatto.Cli.Setup.WizardRow("run gatto to start a session"),
            60, CorpusDriver.Glyphs);

        Assert.DoesNotContain(rows, r => r.TrimEnd().EndsWith(dot, StringComparison.Ordinal));
    }

    //the ascii dot is a full stop too, so only a dot with its separator space beside it is dropped and the text itself keeps every character
    [Fact]
    public void AN_ASCII_FULL_STOP_AT_A_ROW_EDGE_STAYS()
    {
        const string text = "write the folder's own .gatto.json and keep the old file where it was.";
        var row = new Gatto.Cli.Setup.WizardRow(text);
        for (var width = 20; width <= 80; width++)   //from 20 no word is hard-broken, so the rows rejoin with single spaces
        {
            var lines = Gatto.Cli.Setup.WizardRows.Wrap(row, width, glyphs: Gatto.Terminal.GlyphSet.Ascii);
            var joined = string.Join(" ", lines.Select(l => l.Text.Trim()));
            Assert.Equal(text, joined);
        }
    }

    //the same record from the gatto model entry, with the command passed in as a parameter
    [Fact]
    public void THE_WIZARD_RECORD_FROM_GATTO_MODEL()
    {
        var facts = new List<Gatto.Cli.Setup.WizardRow>
        {
            Gatto.Cli.Setup.SetupFlow.SummaryFacts.Row("model", $"{CorpusFacts.ModelId} {CorpusDriver.Glyphs.Dot} ~23.8 GB"),
        };
        var rows = Gatto.Cli.Setup.Tui.Epilogue.Lines(facts, CorpusFacts.Version, CorpusFacts.Build,
            CorpusFacts.Today, closing: null, 100, CorpusDriver.Glyphs, stoppedAt: null,
            command: "gatto model");
        CopyGolden.Check("wizard-record-model",
            CorpusDriver.Visible(string.Join("\n", rows) + "\n"));
    }

    //a leave at the first screen from each entry, composed by the session member the launcher calls. the painted frame is the real one, so a fallback shows here
    [Theory]
    [InlineData("setup", false)]
    [InlineData("model", true)]
    public void THE_WIZARD_LEAVE_FRAME(string road, bool modelRoad)
    {
        var home = Directory.CreateTempSubdirectory("gatto-corpus-leave-").FullName;
        try
        {
            var flow = new Gatto.Cli.Setup.SetupFlow(new Gatto.Tests.Fakes.WizardProbes());
            var face = new Gatto.Tests.Fakes.WizardRig(100).TuiFace(Gatto.Tests.Fakes.WizardRig.Esc, Gatto.Tests.Fakes.WizardRig.Esc);
            Gatto.Cli.Setup.SetupRunner.Run(flow, face, home, modelSegmentOnly: modelRoad);
            foreach (var mode in new[] { CorpusMode.Dark, CorpusMode.Plain })
            {
                var rows = Gatto.Cli.Setup.WizardSession.Scrollback(flow, () => face.LastPainted, 100,
                    CorpusDriver.Glyphs, CorpusFacts.Stamp, CorpusFacts.Today, "gatto " + road, CorpusDriver.ThemeFor(mode));
                CopyGolden.Check($"wizard-leave-frame-{road}-{mode}",
                    CorpusDriver.Visible(string.Join("\n", rows) + "\n"));
            }
        }
        finally { try { Directory.Delete(home, true); } catch (IOException) { } }
    }

    //the line a person reads when gatto is the console's only client and the exit code is not zero
    [Fact]
    public void THE_FATAL_PAUSE()
    {
        var said = new List<string>();
        var exit = FatalPause.Hold(1, clients: () => 1, say: said.Add, wait: () => { });
        CopyGolden.Check("fatal-pause",
            CorpusDriver.Visible(string.Join("\n", said) + "\n--- exit ---\n" + exit + "\n"));
    }

    //the matcher's own oracle

    //the matcher is the corpus's only judge, so it is tested here against a known difference rather than by hand
    [Fact]
    public void THE_MATCHER_REPORTS_A_PLANTED_DIFFERENCE()
    {
        const string name = "planted-positive";
        CopyGolden.Check(name, "the byte this corpus plants so its comparer is tested on every run\n");

        Assert.True(CopyGolden.Mismatches(name, "a different byte\n"),
            "the corpus matcher accepted text that differs from its golden, so every other case in "
            + "this file is passing for a reason nobody has tested.");
        Assert.False(CopyGolden.Mismatches(name,
            "the byte this corpus plants so its comparer is tested on every run\n"),
            "the corpus matcher rejected text identical to its golden.");
    }

    //the manifest

    //the manifest is asserted both ways: no golden on disk that nothing names, and no name without a golden
    [Fact]
    public void THE_MANIFEST_MATCHES_THE_GOLDENS_ON_DISK()
    {
        //skip during an export, when the directory is still being written and xunit fixes no order between cases
        if (CopyGolden.Exporting) return;

        Assert.True(Directory.Exists(CopyGolden.Dir),
            "no corpus directory at " + CopyGolden.Dir + ": export with GATTO_CORPUS_EXPORT=1 first.");

        var onDisk = Directory.GetFiles(CopyGolden.Dir, "*.txt")
            .Select(f => Path.GetFileNameWithoutExtension(f) ?? "")
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(Manifest.OrderBy(n => n, StringComparer.Ordinal).ToList(), onDisk);

        //a surface listed as owed must not already have a golden
        foreach (var (golden, who) in Owed)
            Assert.False(onDisk.Any(n => n == golden || n.StartsWith(golden + "-", StringComparison.Ordinal)),
                $"'{golden}' has a golden but is still listed as owed ({who}). Delete its Owed row in "
                + "the commit that covers it, or the list stops meaning anything.");
    }

    //each row names the golden it owes, and the manifest asserts that golden is absent, so a covered surface with a stale row fails
    private static readonly (string Golden, string Who)[] Owed =
    [
        ("launch-preamble", "no single emitter exists, and the reuse line alone is pinned as notice-reusing"),
        //the checks read the real cwd and home, so a golden needs those reads behind a seam first
        ("doctor-funnel", "the checks read the real cwd and home; a golden needs those reads behind a seam"),
        ("status-funnel", "same"),
        ("select-prompt", "UninstallConsent.cs:155 and UpdateSeam.cs:65"),
        ("oneshot-notices", "the stream rule is pinned by the -p continue test, and the stderr copy has no golden"),
    ];

    //every golden the corpus writes, which the manifest asserts the directory matches both ways
    private static readonly string[] Manifest =
    [
        "help-Dark", "help-Plain",
        "version-Dark", "version-Plain",
        "serve-status-Dark", "serve-status-Light", "serve-status-Plain",
        "serve-stop-Dark", "serve-stop-Plain",
        "serve-status-json",
        "refusal-model-argument", "refusal-model-no-terminal",
        "refusal-setup", "refusal-uninstall",
        "usage-serve-subcommand",
        "header-oneliner-Dark", "header-oneliner-Light", "header-oneliner-Plain",
        "header-banner-Dark", "header-banner-Light", "header-banner-Plain",
        "writer-rows-Dark", "writer-rows-Light", "writer-rows-Plain",
        "notice-reusing", "notice-bare-stderr",
        "exit-block-quit-stopped-Dark", "exit-block-quit-stopped-Plain",
        "exit-block-quit-still-loaded-Dark", "exit-block-quit-still-loaded-Plain",
        "exit-block-quit-nothing-closed-Dark", "exit-block-quit-nothing-closed-Plain",
        "exit-block-quit-no-session-Dark", "exit-block-quit-no-session-Plain",
        "exit-block-input-died-Dark", "exit-block-input-died-Plain",
        "exit-block-no-server-Dark", "exit-block-no-server-Plain",
        "exit-block-empty-Dark", "exit-block-empty-Plain",
        "wizard-record-40", "wizard-record-60", "wizard-record-80",
        "wizard-record-100", "wizard-record-120", "wizard-record-model",
        "wizard-leave-frame-setup-Dark", "wizard-leave-frame-setup-Plain",
        "wizard-leave-frame-model-Dark", "wizard-leave-frame-model-Plain",
        "fatal-pause",
        "planted-positive",
        //these come from AuditionCorpusTests, the three verdict openings and the runner's kept rows
        "audition-report-pass-Dark", "audition-report-pass-Plain",
        "audition-report-fabrication-Dark", "audition-report-fabrication-Plain",
        "audition-report-stopped-Dark", "audition-report-stopped-Plain",
        "audition-progress-Dark", "audition-progress-Plain",
        //these come from ServeCorpusTests, and they are named here because the manifest asserts the whole directory
        "serve-stop-live-Dark", "serve-stop-live-Plain",
        "serve-stop-kill-failed-Dark", "serve-stop-kill-failed-Plain",
        "serve-stop-not-ours-Dark", "serve-stop-not-ours-Plain",
        "serve-stop-stale-Dark", "serve-stop-stale-Plain",
        "serve-status-stale-Dark", "serve-status-stale-Plain",
        "serve-status-died-foreground-Dark", "serve-status-died-foreground-Plain",
        "serve-status-died-detached-Dark", "serve-status-died-detached-Plain",
        "serve-status-unhealthy-Dark", "serve-status-unhealthy-Plain",
        "serve-status-healthy-Dark", "serve-status-healthy-Plain",
        "serve-start-cancelled-Dark", "serve-start-cancelled-Plain",
        "serve-start-foreground-exited-Dark", "serve-start-foreground-exited-Plain",
        "serve-start-detached-watched-Plain",
        "serve-start-already-Dark", "serve-start-already-Plain",
        "serve-start-still-loading-Dark", "serve-start-still-loading-Plain",
        "serve-start-ready-Dark", "serve-start-ready-Plain",
        "serve-start-died-Dark", "serve-start-died-Plain",
        "serve-start-died-tail-Dark", "serve-start-died-tail-Plain",
        "serve-start-died-empty-log-Dark", "serve-start-died-empty-log-Plain",
    ];
}
