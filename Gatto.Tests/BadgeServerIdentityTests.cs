using System.Text.Json;
using Gatto.Core.Acquire;
using Gatto.Core.Client;
using Gatto.Roles;
using Gatto.Roles.Audition;

namespace Gatto.Tests;

//the badge must name the server binary, since one gguf can fail on mainline and pass on a fork. the build comes from /props once, for the stamp and the JSON.
public class BadgeServerIdentityTests : IDisposable
{
    private readonly string _home = Path.Combine(
        Path.GetTempPath(), "gatto-t16p4-" + Guid.NewGuid().ToString("N"));

    public BadgeServerIdentityTests() => Directory.CreateDirectory(Path.Combine(_home, "audition"));

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch (Exception) { } //a failed cleanup must not fail the test.
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task THE_PROBE_CAPTURES_THE_SERVERS_OWN_BUILD_INFO()
    {
        var loaded = await Probe("""
            {"model_path":"C:\\m\\x.gguf","build_info":"b10076-305ba519a"}
            """);

        Assert.Equal("b10076-305ba519a", loaded!.BuildInfo);
    }

    [Fact]
    public async Task A_SERVER_THAT_DOES_NOT_REPORT_ITS_BUILD_leaves_it_null_and_probes_fine()
    {
        //the build field is optional, like the other probe fields. a probe that fails over a missing build also loses the mismatch chip.
        var loaded = await Probe("""{"model_path":"C:\\m\\x.gguf","default_generation_settings":{"n_ctx":4096}}""");

        Assert.NotNull(loaded);
        Assert.Null(loaded!.BuildInfo);
        Assert.Equal(4096, loaded.NCtx);
    }

    [Fact]
    public async Task A_BLANK_BUILD_INFO_IS_NULL_rather_than_an_empty_claim()
    {
        var loaded = await Probe("""{"model_path":"C:\\m\\x.gguf","build_info":"   "}""");
        Assert.Null(loaded!.BuildInfo);
    }

    [Fact]
    public void THE_BADGE_RECORDS_THE_SERVER_and_the_reader_reads_it_back()
    {
        BadgeWriter.Write(_home, "org/forked", Verdict(server: "b10999-deadbee"));

        using var doc = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(_home, "audition", BadgeRegister.FileNameFor("org/forked"))));
        Assert.Equal("b10999-deadbee", doc.RootElement.GetProperty("server").GetString());

        //change the writer and the reader of a field together. a field nothing reads is a wasted schema change, and a reader with no writer waits forever.
        Assert.Equal("b10999-deadbee", BadgeRegister.Lookup(_home, "org/forked")!.Server);
    }

    [Fact]
    public void A_SERVER_THAT_DID_NOT_SAY_omits_the_key_entirely()
    {
        //omit the key when the server did not report its build. a later reader could take an empty string for a known build with no name.
        BadgeWriter.Write(_home, "org/quiet", Verdict(server: null));

        using var doc = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(_home, "audition", BadgeRegister.FileNameFor("org/quiet"))));
        Assert.False(doc.RootElement.TryGetProperty("server", out _));
        Assert.Null(BadgeRegister.Lookup(_home, "org/quiet")!.Server);
    }

    //a schema 1 badge needs no migration and no new audition. each one ran on the pinned build of gatto, which the report hides, so a null server is correct.
    [Fact]
    public void A_SCHEMA_1_BADGE_STILL_READS_and_simply_names_no_server()
    {
        File.WriteAllText(
            Path.Combine(_home, "audition", BadgeRegister.FileNameFor("org/old")),
            """
            {"schema":1,"model_key":"org/old","verdict":"pass","battery_version":4,"measured":"2026-08-09",
             "gatto_build":"v0.3.4","sampling_note":"defaults"}
            """);

        var badge = BadgeRegister.Lookup(_home, "org/old");

        Assert.NotNull(badge);
        Assert.Null(badge!.Server);
        Assert.Equal("v0.3.4", badge.GattoBuild);
    }

    [Fact]
    public void THE_SCHEMA_NUMBER_IS_THREE_and_moving_it_is_a_deliberate_act()
    {
        //keep the literal 3 here, since every other site reads BadgeRegister.Schema and would not notice a change.
        Assert.Equal(3, BadgeRegister.Schema);
    }

    [Fact]
    public void THE_PINNED_BUILD_SAYS_NOTHING()
    {
        //the report hides the pinned build on purpose, since it is the common case. a row that repeats the shipped version every time would teach a reader to skip it.
        Assert.Null(AuditionReport.ServerNote(LlamaAssetSteering.PinnedRelease));
        Assert.Null(AuditionReport.ServerNote(LlamaAssetSteering.PinnedRelease + "-305ba519a"));
    }

    [Fact]
    public void A_FORK_IS_NAMED()
    {
        Assert.Equal("b10999-deadbee", AuditionReport.ServerNote("b10999-deadbee"));
    }

    [Fact]
    public void A_LONGER_TAG_THAT_MERELY_STARTS_WITH_THE_PIN_IS_NOT_THE_PIN()
    {
        //the match needs the hyphen after the pinned tag. without it, a later release such as b100761 would match b10076 and hide a server that is not the pinned build.
        Assert.NotNull(AuditionReport.ServerNote(LlamaAssetSteering.PinnedRelease + "1"));
        Assert.NotNull(AuditionReport.ServerNote(LlamaAssetSteering.PinnedRelease + "1-abc"));
    }

    [Fact]
    public void AN_UNRECORDED_SERVER_SAYS_NOTHING_because_we_were_not_told()
    {
        Assert.Null(AuditionReport.ServerNote(null));
        Assert.Null(AuditionReport.ServerNote(""));
    }

    [Fact]
    public void THE_STAMP_BLOCK_SHOWS_A_FORK_AND_HIDES_OUR_OWN()
    {
        //assert on the rendered block rather than the predicate, a unit test of the fragment cannot see a block that never asks for it
        Assert.Contains("served by", Render(server: "b10999-deadbee"), StringComparison.Ordinal);
        Assert.Contains("b10999-deadbee", Render(server: "b10999-deadbee"), StringComparison.Ordinal);

        Assert.DoesNotContain("served by", Render(server: LlamaAssetSteering.PinnedRelease),
            StringComparison.Ordinal);
        Assert.DoesNotContain("served by", Render(server: null), StringComparison.Ordinal);
    }

    private static Task<LoadedModel?> Probe(string body)
    {
        var http = new HttpClient(new CannedHandler(body)) { Timeout = Timeout.InfiniteTimeSpan };
        return ServeProbe.ProbeAsync(http, "http://127.0.0.1:1235", CancellationToken.None);
    }

    private sealed class CannedHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
    }

    private static AuditionVerdict Verdict(string? server) => new(
        Pass: true, Disqualified: false,
        Tasks: [new AuditionTaskResult("t1", true, [], TimeSpan.FromSeconds(1))],
        Stamp: new AuditionStamp("v0.4.0", "m-Q4_K_M.gguf", "Q4_K_M", 32768, "defaults", "", server),
        WallClock: TimeSpan.FromSeconds(25), DecodeTokS: 28.4);

    private static string Render(string? server) =>
        AuditionReport.Render(Verdict(server), s => s, Gatto.Roles.EngineMarks.Unicode, (t, _) => t);
}
