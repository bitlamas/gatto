using System.Net;
using Gatto.Cli;

namespace Gatto.Tests;

//the guard is a negative: no consent, no request, pinned at the handler where a silent request can't hide
public class UpdateCheckTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-uc-").FullName;
    public void Dispose() { try { Directory.Delete(_home, true); } catch { } }

    private static readonly DateTimeOffset Now = new(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);

    //fails the test on any request, the oracle is that it is never called (an assertion about output would miss a silent request)
    private sealed class ForbiddenHandler : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            throw new InvalidOperationException(
                "a request was made with no consent, and the update check never asks the network without one");
        }
    }

    [Fact]
    public void NO_CONSENT_MEANS_NO_REQUEST_and_absent_is_a_no()
    {
        //null is nobody has agreed and false is a standing no, so only true lets a request out
        Assert.False(UpdateCheck.DueForRepl(consent: null, cached: null, Now));
        Assert.False(UpdateCheck.DueForRepl(consent: false, cached: null, Now));
        Assert.True(UpdateCheck.DueForRepl(consent: true, cached: null, Now));
    }

    [Fact]
    public async Task THE_HANDLER_IS_NEVER_TOUCHED_WITHOUT_CONSENT()
    {
        //the same rule at the HTTP seam, with the gate closed the handler is never touched
        var handler = new ForbiddenHandler();
        using var http = new HttpClient(handler);

        if (UpdateCheck.DueForRepl(consent: null, cached: null, Now))
            await UpdateCheck.FetchAsync(http, Now, CancellationToken.None);

        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public void A_FRESH_ANSWER_IS_NOT_ASKED_AGAIN_until_a_week_has_passed()
    {
        var yesterday = new UpdateState(Now.AddDays(-1), "v0.4.1", false);
        var lastMonth = new UpdateState(Now.AddDays(-30), "v0.4.1", false);

        Assert.False(UpdateCheck.DueForRepl(true, yesterday, Now));
        Assert.True(UpdateCheck.DueForRepl(true, lastMonth, Now));
        //exactly a week is due, the boundary is inclusive
        Assert.True(UpdateCheck.DueForRepl(true, new UpdateState(Now - UpdateCheck.Weekly, "v0.4.1", false), Now));
    }

    [Fact]
    public void A_SECURITY_RELEASE_SHOUTS_and_says_why_it_is_shouting()
    {
        //the security marker comes from the release name, so recognising it costs no extra request
        var state = UpdateCheck.Parse(
            """{"tag_name":"v0.4.2","name":"v0.4.2 [security] — prompt-injection fix"}""", Now)!;

        Assert.True(state.Security);
        var line = UpdateCheck.Line(state, "0.4.0", installed: false)!;
        Assert.Contains("SECURITY", line, StringComparison.Ordinal);
    }

    [Fact]
    public void AN_ORDINARY_RELEASE_DOES_NOT_SHOUT()
    {
        var state = UpdateCheck.Parse("""{"tag_name":"v0.4.1","name":"v0.4.1 — the shelf"}""", Now)!;

        Assert.False(state.Security);
        Assert.DoesNotContain("SECURITY", UpdateCheck.Line(state, "0.4.0", installed: false)!, StringComparison.Ordinal);
    }

    [Fact]
    public void THE_LINE_IS_SILENT_WHEN_YOU_ARE_ALREADY_ON_IT_however_the_tag_is_spelled()
    {
        //v0.4.0 and 0.4.0 are one version, so the line stays silent for a build already on it
        var state = new UpdateState(Now, "v0.4.0", false);

        Assert.Null(UpdateCheck.Line(state, "0.4.0", installed: false));
        Assert.Null(UpdateCheck.Line(new UpdateState(Now, "0.4.0", false), "0.4.0", installed: false));
        Assert.NotNull(UpdateCheck.Line(new UpdateState(Now, "v0.5.0", false), "0.4.0", installed: false));
    }

    //never advertise a release older than this build, gatto update refuses that release by name
    [Fact]
    public void THE_LINE_NEVER_ADVERTISES_A_RELEASE_OLDER_THAN_THIS_BUILD()
    {
        //a tagged but unreleased build ahead of the live release, the case a dev build hits daily
        Assert.Null(UpdateCheck.Line(new UpdateState(Now, "v0.4.0", false), "0.4.1", installed: true));
        Assert.Null(UpdateCheck.Line(new UpdateState(Now, "0.3.4", false), "0.4.1", installed: false));
        //a security marker cannot make an older release speak, it only changes how a real update is said
        Assert.Null(UpdateCheck.Line(new UpdateState(Now, "v0.4.0", true), "0.4.1", installed: true));

        //a real update must still speak, so the fix cannot be a mute button
        Assert.NotNull(UpdateCheck.Line(new UpdateState(Now, "v0.4.2", false), "0.4.1", installed: true));
    }

    //an unreadable tag is silence, gatto update refuses to parse it too
    [Fact]
    public void AN_UNREADABLE_TAG_IS_SILENCE_BECAUSE_THE_COMMAND_CANNOT_ACT_ON_IT()
    {
        Assert.Null(UpdateCheck.Line(new UpdateState(Now, "v0.4.2-rc1", false), "0.4.1", installed: true));
        Assert.Null(UpdateCheck.Line(new UpdateState(Now, "nightly", false), "0.4.1", installed: true));
    }

    //silence has three meanings and each gets its own sentence, from the same comparison Line uses
    [Fact]
    public void SILENCE_HAS_THREE_MEANINGS_AND_SAYS_WHICH()
    {
        //the tag stays raw, v and all, SilenceNote quotes it as Line does
        Assert.Equal("this build (0.4.1) is ahead of the latest release (v0.4.0)",
            UpdateCheck.SilenceNote("v0.4.0", "0.4.1"));

        //the build level with the release
        Assert.Equal("you're on the latest release (0.4.1)",
            UpdateCheck.SilenceNote("v0.4.1", "0.4.1"));

        //an unreadable tag must not report latest, the note can only say the two couldn't be compared
        foreach (var latest in new[] { null, "", "nightly", "v0.4.2-rc1" })
        {
            var note = UpdateCheck.SilenceNote(latest, "0.4.1");
            Assert.Contains("couldn't compare", note, StringComparison.Ordinal);
            Assert.DoesNotContain("latest release", note, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_SHAPE_WE_DO_NOT_RECOGNISE_IS_NOT_AN_UPDATE()
    {
        //a partial parse must give null, inventing a version from it is worse than silence
        Assert.Null(UpdateCheck.Parse("not json", Now));
        Assert.Null(UpdateCheck.Parse("[]", Now));
        Assert.Null(UpdateCheck.Parse("""{"name":"no tag here"}""", Now));
        Assert.Null(UpdateCheck.Parse("""{"tag_name":""}""", Now));
        Assert.Null(UpdateCheck.Line(null, "0.4.0", installed: false));
    }

    [Fact]
    public void THE_CACHE_ROUND_TRIPS_and_an_unreadable_one_is_simply_no_answer_yet()
    {
        UpdateCheck.WriteCache(_home, new UpdateState(Now, "v0.4.1", Security: true));

        var read = UpdateCheck.ReadCache(_home)!;
        Assert.Equal("v0.4.1", read.Latest);
        Assert.True(read.Security);
        Assert.Equal(Now, read.CheckedAt);

        File.WriteAllText(Path.Combine(_home, UpdateCheck.CacheFile), "{corrupt");
        Assert.Null(UpdateCheck.ReadCache(_home));
        Assert.Null(UpdateCheck.ReadCache(Path.Combine(_home, "nope")));
    }

    [Fact]
    public void THE_CLIENT_SETS_BOTH_BUDGETS_never_just_the_token()
    {
        //build the client for each consumer, a shared one can cap the deadline it was given
        using var doctor = UpdateCheck.Client(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(500));
        using var repl = UpdateCheck.Client(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(1));

        Assert.Equal(TimeSpan.FromSeconds(1), doctor.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(3), repl.Timeout);
    }

    [Fact]
    public async Task A_FAILING_REQUEST_IS_SILENT_because_a_courtesy_that_interrupts_is_not_one()
    {
        //the 404 is the live path today, the repo is private until release day
        using var http = new HttpClient(new StubHandler(HttpStatusCode.NotFound, ""));

        Assert.Null(await UpdateCheck.FetchAsync(http, Now, CancellationToken.None));
    }

    [Fact]
    public async Task A_SUCCESSFUL_REQUEST_IS_PARSED_and_carries_the_time_it_was_asked()
    {
        using var http = new HttpClient(new StubHandler(HttpStatusCode.OK,
            """{"tag_name":"v0.9.0","name":"v0.9.0 — audition"}"""));

        var state = (await UpdateCheck.FetchAsync(http, Now, CancellationToken.None))!.State;

        Assert.Equal("v0.9.0", state.Latest);
        Assert.Equal(Now, state.CheckedAt);   //the answer carries the time it was asked
    }

    //the fetch returns the release, Parse is a projection of it

    private const string AssetDigest = "d44f38419fe3468ce2e1d2a5a1e555fe921f9beb4bd19991b6c4bff4b9bbf4db";

    //deliberately different from AssetDigest so a body-reading implementation can't pass by accident
    private const string BodySha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    //mirror the measured v0.4.0 payload, and build each shape rather than cutting the digest out of one string
    private static string ReleaseJson(string? digest = AssetDigest) => $$"""
        {
          "tag_name": "v0.4.0",
          "name": "gatto 0.4.0",
          "html_url": "https://github.com/bitlamas/gatto/releases/tag/v0.4.0",
          "body": "the first public release.\n\n**verify what you downloaded**\n\n    sha256  {{BodySha}}\n",
          "assets": [
            { "name": "gatto-0.4.0-win-x64.zip",
              "size": 38125967,
              "browser_download_url": "https://github.com/bitlamas/gatto/releases/download/v0.4.0/gatto-0.4.0-win-x64.zip"{{(digest is null ? "" : $", \"digest\": \"sha256:{digest}\"")}} }
          ]
        }
        """;

    [Fact]
    public void THE_RELEASE_CARRIES_ITS_ASSET_AND_DIGEST_from_the_same_payload_the_line_reads()
    {
        var r = UpdateCheck.ParseRelease(ReleaseJson(), Now)!;

        Assert.Equal("v0.4.0", r.State.Latest);
        var zip = UpdateCheck.WindowsZip(r.Assets)!;
        Assert.Equal(AssetDigest, zip.Sha256);           //the digest comes from the asset's own field
        Assert.NotEqual(BodySha, zip.Sha256);
        Assert.Equal(38125967, zip.Size);
        Assert.EndsWith("/tag/v0.4.0", r.NotesUrl);
    }

    //the fixture keeps a body with its sha256 line, so a body-reading implementation would show up here
    [Fact]
    public void AN_ABSENT_DIGEST_IS_NULL_never_read_from_anywhere_else()
    {
        var json = ReleaseJson(digest: null);
        Assert.DoesNotContain("digest", json);           //a check on the fixture json itself
        Assert.Contains(BodySha, json);                  //the body's sha stays in the fixture as the tempting other source

        Assert.Null(UpdateCheck.WindowsZip(UpdateCheck.ParseRelease(json, Now)!.Assets)!.Sha256);
    }

    //exactly one Windows zip, the same posture Parse takes toward a shape it doesn't recognise
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void ZERO_OR_TWO_WINDOWS_ZIPS_IS_NOT_AN_UPDATE(int count)
    {
        var assets = Enumerable.Range(0, count)
            .Select(i => new ReleaseAsset($"gatto-0.4.{i}-win-x64.zip", "u", null, 1))
            .ToList();

        Assert.Null(UpdateCheck.WindowsZip(assets));
    }

    //the weekly line reads the same payload through Parse, a projection of ParseRelease that can't disagree with it
    [Fact]
    public void THE_OLD_PARSE_IS_THE_NEW_ONE_PROJECTED()
        => Assert.Equal(UpdateCheck.ParseRelease(ReleaseJson(), Now)!.State, UpdateCheck.Parse(ReleaseJson(), Now));

    //the line knows whether gatto is installed

    //only an installed gatto is told to run gatto update, a folder run gets the releases page
    [Fact]
    public void THE_LINE_NAMES_THE_COMMAND_ONLY_FOR_AN_INSTALLED_GATTO()
    {
        var s = new UpdateState(Now, "v0.4.2", false);

        Assert.Equal("gatto v0.4.2 is out (you're on 0.4.1): run gatto update",
            UpdateCheck.Line(s, "0.4.1", installed: true));

        var notInstalled = UpdateCheck.Line(s, "0.4.1", installed: false)!;
        Assert.DoesNotContain("gatto update", notInstalled);
        Assert.Contains(UpdateCheck.ReleasesPage, notInstalled);
    }

    //the fetch keeps the api URL, no line a person reads may show it (all four combinations)
    [Fact]
    public void NO_USER_FACING_LINE_EVER_CARRIES_AN_API_URL()
    {
        foreach (var security in new[] { false, true })
        foreach (var installed in new[] { false, true })
        {
            var line = UpdateCheck.Line(new UpdateState(Now, "v0.4.2", security), "0.4.1", installed)!;
            Assert.DoesNotContain("api.github.com", line, StringComparison.Ordinal);
        }
    }

    private sealed class StubHandler(HttpStatusCode code, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body) });
    }
}
