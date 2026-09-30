using System.IO.Compression;
using Gatto.Cli;
using Gatto.Repl;
using Gatto.Terminal;

namespace Gatto.Tests;

//the oracle for every refusal row is that Download was never called, an exit code can come from a path that fetched first
public class UpdateCommandTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-upd-").FullName;
    public void Dispose() { try { Directory.Delete(_home, true); } catch { } }

    private readonly StringWriter _out = new();
    private string Output => _out.ToString();

    private CommandContext Ctx(bool interactive = true) =>
        new(_out, _home, interactive, Theme: null);

    private static UpdateSite Site(
        bool exePresent = true, string? installed = "0.4.1",
        int[]? siblings = null, bool oldHeld = false) =>
        new(@"C:\Programs\gatto", exePresent, installed, siblings ?? [], oldHeld);

    private static Release Rel(string tag = "v0.4.2", string? sha = "ab12", string asset = "gatto-0.4.2-win-x64.zip") =>
        new(new UpdateState(DateTimeOffset.UnixEpoch, tag, false),
            [new ReleaseAsset(asset, "https://example.invalid/z", sha, 38_000_000)],
            "https://github.com/bitlamas/gatto/releases/tag/" + tag);

    //records what the command did, each deed defaults to the success path and Downloads is what the refusal rows assert on
    private sealed class Deeds
    {
        public int Downloads, Applies;
        public HttpClient? DownloadClient;
        public string? AppliedFrom, AppliedTo;
        public Release? Fetched = Rel();
        public string? VerifySays, ExtractSays, ApplySays;
        public Exception? DownloadThrows;
        public int ProgressSteps;

        public UpdateActs Acts() => new(
            (_, _, _) => Task.FromResult(Fetched),
            (http, _, _, progress, _) =>
            {
                Downloads++;
                DownloadClient = http;
                for (var i = 1; i <= ProgressSteps; i++) progress?.Report((i, ProgressSteps));
                if (DownloadThrows is { } ex) throw ex;
                return Task.FromResult(Path.Combine("dl", "gatto-0.4.2-win-x64.zip"));
            },
            (_, _, _) => Task.FromResult(VerifySays),
            (_, _) => ExtractSays,
            (from, to) => { Applies++; AppliedFrom = from; AppliedTo = to; return ApplySays; });
    }

    //counts the asks, so a refusal row can fail on a flow that asks before comparing the versions
    private int _asks;
    private SelectSpec? _askedWith;

    private Task<int> Run(UpdateSite site, Deeds deeds, int? answer = 0, bool interactive = true) =>
        UpdateCommand.RunAsync(Ctx(interactive), CancellationToken.None,
            site, deeds.Acts(), spec => { _asks++; _askedWith = spec; return answer; });

    //the download gets the untimed client, the check's 10 second one is a whole-exchange budget on 38 MB
    [Fact]
    public async Task THE_DOWNLOAD_RUNS_ON_THE_UNTIMED_CLIENT_not_the_checks()
    {
        var deeds = new Deeds();

        Assert.Equal(0, await Run(Site(), deeds));

        Assert.NotNull(deeds.DownloadClient);
        Assert.Equal(Timeout.InfiniteTimeSpan, deeds.DownloadClient!.Timeout);
    }

    //rows 1-2: usage refusals, exit 2

    [Fact]
    public async Task A_PIPED_RUN_REFUSES_because_consent_needs_a_terminal()
    {
        var deeds = new Deeds();
        Assert.Equal(2, await Run(Site(), deeds, interactive: false));
        Assert.Equal(0, deeds.Downloads);
        Assert.Equal(0, _asks);
    }

    //with nothing in the install directory this refuses, a first install is the wizard's job
    [Fact]
    public async Task NOTHING_INSTALLED_REFUSES_and_names_the_directory()
    {
        var deeds = new Deeds();
        Assert.Equal(2, await Run(Site(exePresent: false), deeds));
        Assert.Equal(0, deeds.Downloads);
        Assert.Equal(0, _asks);
        Assert.Contains(@"C:\Programs\gatto", Output, StringComparison.Ordinal);
    }

    //row 3: the fetch

    [Fact]
    public async Task AN_UNREACHABLE_GITHUB_IS_A_REFUSAL_not_a_crash()
    {
        var deeds = new Deeds { Fetched = null };
        Assert.Equal(1, await Run(Site(), deeds));
        Assert.Equal(0, deeds.Downloads);
        Assert.Equal(0, _asks);
    }

    [Fact]
    public async Task A_RELEASE_WITH_NO_WINDOWS_ZIP_IS_NOT_AN_UPDATE()
    {
        var deeds = new Deeds { Fetched = Rel(asset: "gatto-0.4.2-linux.tar.gz") };
        Assert.Equal(1, await Run(Site(), deeds));
        Assert.Equal(0, deeds.Downloads);
        Assert.Equal(0, _asks);
    }

    //row 4: forward-only

    [Fact]
    public async Task ALREADY_ON_THE_LATEST_IS_SUCCESS_and_downloads_nothing()
    {
        var deeds = new Deeds { Fetched = Rel("v0.4.1") };
        Assert.Equal(0, await Run(Site(installed: "0.4.1"), deeds));
        Assert.Equal(0, deeds.Downloads);
        Assert.Equal(0, _asks);
    }

    //a republished or yanked release can present an older tag, so the compare has to be ordered
    [Fact]
    public async Task AN_OLDER_RELEASE_IS_REFUSED_never_installed_backwards()
    {
        var deeds = new Deeds { Fetched = Rel("v0.4.0") };
        Assert.Equal(0, await Run(Site(installed: "0.4.1"), deeds));
        Assert.Equal(0, deeds.Downloads);
        Assert.Equal(0, _asks);
    }

    [Theory]
    [InlineData("0.4.1-beta")]
    [InlineData(null)]
    public async Task A_VERSION_GATTO_CANNOT_COMPARE_IS_A_REFUSAL(string? installed)
    {
        var deeds = new Deeds();
        Assert.Equal(0, await Run(Site(installed: installed), deeds));
        Assert.Equal(0, deeds.Downloads);
        Assert.Equal(0, _asks);
    }

    //row 5: probed before the download

    //the held binary is read at probe time, so a doomed update refuses without spending the download
    [Fact]
    public async Task A_HELD_PREVIOUS_BINARY_REFUSES_BEFORE_SPENDING_THE_DOWNLOAD()
    {
        var deeds = new Deeds();
        Assert.Equal(1, await Run(Site(oldHeld: true), deeds));
        Assert.Equal(0, deeds.Downloads);
        Assert.Equal(0, _asks);
        Assert.DoesNotContain("cannot access the file", Output, StringComparison.OrdinalIgnoreCase);
    }

    //row 6: the ask

    [Theory]
    [InlineData(1)]      //the answer for Not now
    [InlineData(null)]   //null is the Esc key
    public async Task DECLINING_CHANGES_NOTHING_and_is_not_an_error(int? answer)
    {
        var deeds = new Deeds();
        Assert.Equal(0, await Run(Site(), deeds, answer));
        Assert.Equal(0, deeds.Downloads);
        //the ask happened exactly once here, which is why no blanket rule can assert zero asks
        Assert.Equal(1, _asks);
    }

    //update_check gates the ambient weekly check only, so a typed update runs with it false
    [Fact]
    public async Task A_TYPED_UPDATE_RUNS_EVEN_WITH_THE_WEEKLY_CHECK_REFUSED()
    {
        await File.WriteAllTextAsync(Path.Combine(_home, "gatto.json"), """{"update_check": false}""");
        var deeds = new Deeds();

        Assert.Equal(0, await Run(Site(), deeds));

        Assert.Equal(1, deeds.Downloads);
        Assert.Equal(1, deeds.Applies);
    }

    //this is the half that checks the update ask actually sets the flag the widget honours
    [Fact]
    public async Task THE_ASK_IS_RENDERED_WITH_NO_CURSOR_ON_ITS_FIRST_FRAME()
    {
        //assert on the spec the command hands the asker, building one here would test the wrong object
        var deeds = new Deeds();
        await Run(Site(), deeds, answer: 1);
        var spec = Assert.IsType<SelectSpec>(_askedWith);

        Assert.True(spec.NoInitialCursor);
        Assert.All(spec.Options, o => Assert.False(o.Recommended));

        //render it through the real widget, the flag is a claim about a frame and the frame is the oracle
        var surface = new Gatto.Tests.Fakes.RecordingSurface();
        new SelectPrompt(surface, new Theme(TermCaps.Plain),
            new ScriptedKeys([new('\0', ConsoleKey.Escape, false, false, false)])).Show(spec);

        Assert.DoesNotContain('❯', surface.Text);
    }

    private sealed class ScriptedKeys(IEnumerable<ConsoleKeyInfo> keys) : IKeySource
    {
        private readonly IEnumerator<ConsoleKeyInfo> _e = keys.GetEnumerator();
        public bool KeyAvailable => true;
        public ConsoleKeyInfo ReadKey() =>
            _e.MoveNext() ? _e.Current : throw new InvalidOperationException("the widget asked for a key the test did not script");
    }

    //one line rewritten in place, the oracle is the newline count in the progress region. a flaky count means someone made the reporting asynchronous again
    [Fact]
    public async Task THE_DOWNLOAD_REPORTS_ON_ONE_LINE_REWRITTEN_IN_PLACE()
    {
        //the command resolves its glyph set from this home, so the config pins unicode (a harness without WT_SESSION would render ascii)
        await File.WriteAllTextAsync(Path.Combine(_home, "gatto.json"),
            """{"endpoints":{"local":{"base_url":"http://127.0.0.1:1235"}},"default_endpoint":"local","glyphs":"unicode"}""");
        var deeds = new Deeds { ProgressSteps = 8 };

        Assert.Equal(0, await Run(Site(), deeds));

        var live = Output[..Output.IndexOf("updated gatto", StringComparison.Ordinal)];

        Assert.Equal(8, live.Split("downloading").Length - 1);

        //pin the frame itself, scoped to the progress region so the assert can't depend on what the scheduler wrote first
        var firstReport = live.IndexOf("\rdownloading", StringComparison.Ordinal);
        Assert.True(firstReport >= 0, "the progress region must exist before its shape can be pinned");
        var progress = live[firstReport..];
        Assert.StartsWith("\rdownloading… 0.0 MB of 0.0 MB", progress, StringComparison.Ordinal);
        //how each report is written is the oracle, Live opens with a carriage return so no report can begin a fresh line
        Assert.DoesNotContain("\ndownloading", live, StringComparison.Ordinal);
        //don't count carriage returns, Blank writes one of its own after the progress region
    }

    //rows 7-9: the deeds

    //the progress row must be closed on the failure path too, Say writes onto the open row otherwise
    [Fact]
    public async Task A_FAILED_DOWNLOAD_IS_A_REFUSAL_and_nothing_is_applied()
    {
        var deeds = new Deeds { DownloadThrows = new HttpRequestException("connection reset") };
        Assert.Equal(1, await Run(Site(), deeds));
        Assert.Equal(0, deeds.Applies);

        var failed = Output.IndexOf("the download failed", StringComparison.Ordinal);
        Assert.True(failed > 0, "the refusal must be said");
        //the refusal needs a line break in front of it, otherwise it continues the progress row
        Assert.EndsWith(Environment.NewLine, Output[..failed].TrimEnd(' '), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_FAILED_VERIFY_IS_A_REFUSAL_and_nothing_is_applied()
    {
        var deeds = new Deeds { VerifySays = "the download didn't match the release's digest" };
        Assert.Equal(1, await Run(Site(), deeds));
        Assert.Equal(0, deeds.Applies);
        Assert.Contains("digest", Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_FAILED_EXTRACT_IS_A_REFUSAL_and_nothing_is_applied()
    {
        var deeds = new Deeds { ExtractSays = "the download doesn't contain gatto.exe" };
        Assert.Equal(1, await Run(Site(), deeds));
        Assert.Equal(0, deeds.Applies);
    }

    [Fact]
    public async Task A_FAILED_APPLY_IS_A_REFUSAL()
    {
        var deeds = new Deeds { ApplySays = "couldn't install gatto to C:\\Programs\\gatto" };
        Assert.Equal(1, await Run(Site(), deeds));
        Assert.Equal(1, deeds.Applies);
    }

    //rows 9-10: the deed and the report

    //the apply call gets the install directory, whatever image is running
    [Fact]
    public async Task A_SUCCESSFUL_UPDATE_APPLIES_TO_THE_INSTALL_DIRECTORY()
    {
        var deeds = new Deeds();
        Assert.Equal(0, await Run(Site(), deeds));

        Assert.Equal(1, deeds.Applies);
        Assert.Equal(@"C:\Programs\gatto", deeds.AppliedTo);
        Assert.Contains("0.4.2", Output, StringComparison.Ordinal);
    }

    //the update folder must be empty afterwards, which the stubbed extractor tests could never show
    [Fact]
    public async Task A_SUCCESSFUL_UPDATE_LEAVES_THE_UPDATE_FOLDER_COMPLETELY_EMPTY()
    {
        var dir = Directory.CreateDirectory(UpdateDownload.Dir(_home)).FullName;
        string? appliedFrom = null;
        //capture the exe at Apply time, the sweep has deleted it by the end of the run
        var existedWhenApplied = false;

        var acts = new UpdateActs(
            (_, _, _) => Task.FromResult<Release?>(Rel()),
            (_, asset, home, _, _) =>
            {
                //a real zip with three entries, so the extractor has something to open
                var zip = Path.Combine(UpdateDownload.Dir(home), asset.Name);
                using (var fs = File.Create(zip))
                using (var ar = new ZipArchive(fs, ZipArchiveMode.Create))
                    foreach (var n in new[] { "gatto.exe", "LICENSE", "README.md" })
                    {
                        using var w = new StreamWriter(ar.CreateEntry(n).Open());
                        w.Write("content of " + n);
                    }
                return Task.FromResult(zip);
            },
            (_, _, _) => Task.FromResult<string?>(null),
            UpdateDownload.ExtractExe,                       //the production extractor, the stub would never touch the zip
            (from, _) => { appliedFrom = from; existedWhenApplied = File.Exists(from); return null; });

        Assert.Equal(0, await UpdateCommand.RunAsync(
            Ctx(), CancellationToken.None, Site(), acts, _ => 0));

        Assert.NotNull(appliedFrom);
        Assert.True(existedWhenApplied, "the real extractor produced the exe Apply was handed");
        Assert.Empty(Directory.GetFileSystemEntries(dir));
    }

    //the siblings line shows only when there are siblings, and its number is the count the probe read
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    public async Task THE_REPORT_NAMES_LIVE_SESSIONS_ONLY_WHEN_THERE_ARE_ANY(int count)
    {
        var pids = Enumerable.Range(1000, count).ToArray();
        var deeds = new Deeds();

        Assert.Equal(0, await Run(Site(siblings: pids), deeds));

        if (count == 0)
            Assert.DoesNotContain("still on", Output, StringComparison.OrdinalIgnoreCase);
        else
        {
            Assert.Contains("still on", Output, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(count.ToString(), Output, StringComparison.Ordinal);
        }
    }
}
