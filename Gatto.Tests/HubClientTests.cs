using System.Net;
using Gatto.Core.Acquire;

namespace Gatto.Tests;

//check the measured request shapes and the typed failure contract from fixture data through a stub handler, so no test touches the network.
public class HubClientTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public readonly List<string> Urls = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        { Urls.Add(r.RequestUri!.ToString()); return Task.FromResult(respond(r)); }
    }

    private static HttpClient Client(StubHandler h) =>
        new(h) { Timeout = Timeout.InfiniteTimeSpan };      //use the same client setup with an infinite timeout that every test in this file uses.

    //read fixtures from AppContext.BaseDirectory, since parallel tests change the process directory and a relative path fails at random in full runs
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Listing_parses_arch_context_gated_and_downloads()
    {
        var handler = new StubHandler(_ => Json(Fixture("hub-listing.json")));
        var rows = await new HubClient(Client(handler)).ListAsync("lmstudio-community", 20_000_000_000, 40_000_000_000, CancellationToken.None);

        Assert.Equal(3, rows.Count);
        var first = rows[0];
        Assert.Equal("lmstudio-community/Qwen3.6-35B-A3B-GGUF", first.RepoId);
        Assert.Equal("qwen3moe", first.Arch);
        Assert.Equal(262144L, first.NativeCtx);
        Assert.False(first.Gated);
        Assert.Equal(184213L, first.Downloads);
    }

    [Fact]
    public async Task A_gated_repo_is_flagged_even_though_the_field_is_a_STRING()
    {
        //the hub reports gated as false, auto or manual. a bool-only parse reads every gated repo as open and wastes a tree call
        var handler = new StubHandler(_ => Json(Fixture("hub-listing.json")));
        var rows = await new HubClient(Client(handler)).ListAsync("google", 0, long.MaxValue, CancellationToken.None);
        Assert.True(rows.Single(r => r.RepoId.StartsWith("google/")).Gated);
    }

    [Fact]
    public async Task A_listing_without_the_gguf_expand_degrades_to_nulls_not_a_throw()
    {
        var handler = new StubHandler(_ => Json(Fixture("hub-listing.json")));
        var rows = await new HubClient(Client(handler)).ListAsync("bartowski", 0, long.MaxValue, CancellationToken.None);
        var bare = rows.Single(r => r.RepoId.Contains("Without-Expand"));
        Assert.Null(bare.Arch);
        Assert.Null(bare.NativeCtx);
    }

    [Fact]
    public async Task The_request_carries_the_measured_shape_with_exactly_one_author()
    {
        //the hub accepts only one author= value. two author entries return zero results, so each org needs its own request or the search comes back empty
        var handler = new StubHandler(_ => Json("[]"));
        await new HubClient(Client(handler)).ListAsync("unsloth", 20_000_000_000, 40_000_000_000, CancellationToken.None);

        var url = Assert.Single(handler.Urls);
        Assert.Equal(1, url.Split("author=").Length - 1);
        Assert.Contains("author=unsloth", url);
        Assert.Contains("filter=gguf", url);
        //keep pipeline_tag out of the query, server-side filtering hid half the repos. the tag is still requested by name, so a typo can't pass both halves
        Assert.DoesNotContain("&pipeline_tag=", url);
        //the expand[] mode lists fields explicitly, so the tag must be requested by name. an untagged row passes through, silently switching the kind filter off
        Assert.Contains("expand%5B%5D=pipeline_tag", url);
        Assert.Contains("num_parameters=min%3A20000000000%2Cmax%3A40000000000", url);
        Assert.Contains("expand%5B%5D=gguf", url);
        Assert.Contains("expand%5B%5D=gated", url);
        //explicit-field mode hides downloads unless it is asked for, so rows read zero. the count orders the results, so losing it makes the sort arbitrary
        Assert.Contains("expand%5B%5D=downloads", url);
    }

    [Fact]
    public async Task Tree_parses_quant_bytes_and_sha_and_ignores_non_gguf_entries()
    {
        var handler = new StubHandler(_ => Json(Fixture("hub-tree.json")));
        var quants = (await new HubClient(Client(handler)).TreeAsync("lmstudio-community/Qwen3.6-35B-A3B-GGUF", CancellationToken.None)).Quants;

        Assert.Equal(2, quants.Count);                      //the fixture holds a README and an mmproj beside the weights, and only the gguf weights count.
        var q4 = quants.Single(q => q.FileName.Contains("Q4_K_M"));
        Assert.Equal(21474836480L, q4.Bytes);
        Assert.Equal("9f2c1b7a5e4d3c2b1a0f9e8d7c6b5a49382716059f2c1b7a5e4d3c2b1a0f9e8d", q4.Sha256);
        Assert.Contains("recursive=true", Assert.Single(handler.Urls));
    }

    //count every file entry seen, so the heading files (N) describes the repo. the fixture mixes four entries with two quants
    [Fact]
    public async Task Tree_counts_every_file_entry_not_just_the_quants_it_keeps()
    {
        var handler = new StubHandler(_ => Json(Fixture("hub-tree.json")));

        var tree = await new HubClient(Client(handler)).TreeAsync("x/y", CancellationToken.None);

        Assert.Equal(4, tree.FileCount);      //the four are the README, two quants and the mmproj. the directory entry does not count.
        Assert.Equal(2, tree.Quants.Count);
        Assert.True(tree.FileCount > tree.Quants.Count);
        Assert.Single(handler.Urls);
    }

    [Fact]
    public async Task An_mmproj_in_the_tree_marks_every_quant_as_vision_capable()
    {
        //vision comes from a file that exists in the tree call already made. a file fact is reliable enough to show, a tag is not
        var handler = new StubHandler(_ => Json(Fixture("hub-tree.json")));
        var tree = await new HubClient(Client(handler)).TreeAsync("x/y", CancellationToken.None);
        //the marker is derived from the projector list rather than stamped on the quants. the list names, sizes and checks each encoder
        Assert.True(tree.HasProjector);
        Assert.NotEmpty(tree.Projectors);
    }

    //one contains rule must own the test, mmproj sits at the start, end or middle. a prefix-only match let an encoder into the quant list
    [Theory]
    [InlineData("gemma-4-31B-it-mmproj.gguf", "google/gemma-4-31B-it-qat-q4_0-gguf")]
    [InlineData("Ministral-3-8B-Instruct-2512-BF16-mmproj.gguf", "mistralai/Ministral-3-8B-Instruct-2512-GGUF")]
    [InlineData("Qwen3-VL-8B-Instruct-abliterated.mmproj-Q8_0.gguf", "mradermacher/Qwen3-VL-8B-Instruct-abliterated-GGUF")]
    public async Task An_mmproj_is_an_encoder_whatever_position_the_name_puts_it_in(string encoder, string provenance)
    {
        var handler = new StubHandler(_ => Json(
            "[{\"type\":\"file\",\"path\":\"" + encoder + "\",\"size\":1190000000,\"lfs\":{\"oid\":\"aa\"}},"
            + "{\"type\":\"file\",\"path\":\"model-UD-Q4_K_M.gguf\",\"size\":16000000000,"
            + "\"lfs\":{\"oid\":\"bb\"}}]"));
        var tree = await new HubClient(Client(handler)).TreeAsync("x/y", CancellationToken.None);

        Assert.True(tree.HasProjector, provenance);
        Assert.Equal(encoder, Assert.Single(tree.Projectors).FileName);
        //the real oracle is that the encoder is not a quant. the picker reads the candidate list, so a guard on HasProjector alone passes a build with both
        Assert.Equal("model-UD-Q4_K_M.gguf", Assert.Single(tree.Quants).FileName);
    }

    [Fact]
    public async Task A_tree_without_an_mmproj_marks_no_vision()
    {
        var handler = new StubHandler(_ => Json("""
            [{"type":"file","path":"only-Q4.gguf","size":10,"lfs":{"oid":"aa"}}]
            """));
        var tree = await new HubClient(Client(handler)).TreeAsync("x/y", CancellationToken.None);
        Assert.Single(tree.Quants);
        Assert.False(tree.HasProjector);
        Assert.Empty(tree.Projectors);
    }

    [Theory]
    [InlineData("https://huggingface.co/org/repo")]     //a full url is the likeliest paste a user sends.
    [InlineData("org/../../datasets/secret")]           //the row attempts path traversal out of the api/models prefix
    [InlineData("org/repo?full=true")]                  //the row tries to smuggle a query string into the path.
    [InlineData("org/repo#fragment")]
    [InlineData("org/repo/extra")]                      //a repo id is exactly two segments, so the third one fails it.
    [InlineData("org/")]                                //one half of the id is empty.
    [InlineData("/repo")]
    [InlineData("")]
    [InlineData("orgrepo")]                             //the id has no separator.
    [InlineData("org/re po")]                           //a careless copy keeps a space inside a segment.
    [InlineData(".hidden/repo")]                        //a segment may not start with a dot.
    public async Task A_repo_id_that_is_not_org_slash_name_never_reaches_the_wire(string hostile)
    {
        //a user-typed id feeds straight into the request path here. the oracle is that the handler saw nothing, a throw could come after the request
        var handler = new StubHandler(_ => Json("[]"));
        var ex = await Assert.ThrowsAsync<HubUnavailableException>(
            () => new HubClient(Client(handler)).TreeAsync(hostile, CancellationToken.None));

        Assert.Empty(handler.Urls);
        Assert.False(ex.Gated);                          //a malformed id is not a permission problem.
        Assert.Null(ex.Status);                          //no request happened, so the error has no status code.
        Assert.Contains("org/name", ex.Message);         //the friendly message must show the expected id shape, org/name
    }

    [Theory]
    [InlineData("lmstudio-community/Qwen3.6-35B-A3B-GGUF")]
    [InlineData("unsloth/gpt-oss-120b-GGUF")]
    [InlineData("a/b")]                                  //this row is the shortest legal id.
    [InlineData("Org_1/repo.name-v2_final")]             //dots, dashes and underscores are legal in an id.
    public async Task A_real_repo_id_still_reaches_the_wire_unchanged(string legal)
    {
        //a validator that rejects everything would pass the hostile-id test and break every search. these ids are shapes measured on the live hub
        var handler = new StubHandler(_ => Json("[]"));
        await new HubClient(Client(handler)).TreeAsync(legal, CancellationToken.None);
        Assert.Contains($"/api/models/{legal}/tree/main", Assert.Single(handler.Urls));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, true)]
    [InlineData(HttpStatusCode.Forbidden, true)]
    [InlineData(HttpStatusCode.InternalServerError, false)]
    [InlineData(HttpStatusCode.NotFound, false)]
    public async Task Non_success_throws_a_TYPED_failure_carrying_the_gated_distinction(HttpStatusCode code, bool gated)
    {
        //the caller isolates the failing org on this type and maps the gated branch. a gated 401 occurs on the live hub, so this distinction is real
        var handler = new StubHandler(_ => new HttpResponseMessage(code));
        var ex = await Assert.ThrowsAsync<HubUnavailableException>(
            () => new HubClient(Client(handler)).ListAsync("org", 0, 1, CancellationToken.None));
        Assert.Equal(gated, ex.Gated);
        Assert.Equal((int)code, ex.Status);
        Assert.Contains("org", ex.Message);
    }

    [Fact]
    public async Task Malformed_json_is_a_typed_throw_never_a_raw_JsonException()
    {
        var handler = new StubHandler(_ => Json("{ this is not json"));
        var ex = await Assert.ThrowsAsync<HubUnavailableException>(
            () => new HubClient(Client(handler)).ListAsync("org", 0, 1, CancellationToken.None));
        Assert.False(ex.Gated);
    }

    [Fact]
    public async Task A_transport_failure_is_a_typed_throw_too()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("connection refused"));
        await Assert.ThrowsAsync<HubUnavailableException>(
            () => new HubClient(Client(handler)).TreeAsync("org/repo", CancellationToken.None));
    }

    private static string Row(string id, params string[] tags) =>
        "{\"id\":\"" + id + "\",\"tags\":[" + string.Join(",", tags.Select(t => "\"" + t + "\"")) + "]}";

    private static HttpResponseMessage WithNext(HttpResponseMessage res, string next)
    {
        res.Headers.TryAddWithoutValidation("Link", $"<{next}>; rel=\"next\"");
        return res;
    }

    //the source query pages past 1000 rows, so the caller sees every conversion and never the first page alone
    [Fact]
    public async Task CONVERSIONS_FOLLOW_EVERY_PAGE()
    {
        var handler = new StubHandler(r => r.RequestUri!.Query.Contains("cursor=x")
            ? Json($"[{Row("c/three")}]")
            : WithNext(Json($"[{Row("a/one")},{Row("b/two")}]"), "https://huggingface.co/api/models?cursor=x"));
        var rows = await new HubClient(Client(handler)).ConversionsAsync("Qwen/Qwen3.8-27B", CancellationToken.None);
        Assert.Equal(["a/one", "b/two", "c/three"], rows.Select(r => r.RepoId));
    }

    //the quantized tag names the source a conversion was made from, and an adapter tag names none
    [Fact]
    public async Task THE_QUANTIZED_TAG_IS_READ()
    {
        var handler = new StubHandler(_ => Json(
            $"[{Row("u/m-GGUF", "gguf", "base_model:Qwen/Qwen3.8-27B", "base_model:quantized:Qwen/Qwen3.8-27B")}," +
            $"{Row("u/adapter", "base_model:adapter:x/y")}]"));
        var rows = await new HubClient(Client(handler)).ConversionsAsync("Qwen/Qwen3.8-27B", CancellationToken.None);
        Assert.Equal("Qwen/Qwen3.8-27B", rows[0].QuantizedFrom);
        Assert.Null(rows[1].QuantizedFrom);
        Assert.Equal("u", rows[0].Owner);
    }

    [Fact]
    public async Task THE_SOURCE_QUERY_ASKS_THE_TAG_FILTER_AND_THE_EXPANDS()
    {
        var handler = new StubHandler(_ => Json("[]"));
        await new HubClient(Client(handler)).ConversionsAsync("Qwen/Qwen3.8-27B", CancellationToken.None);
        var url = Uri.UnescapeDataString(handler.Urls.Single());
        Assert.Contains("filter=gguf", url);
        Assert.Contains("filter=base_model:quantized:Qwen/Qwen3.8-27B", url);
        foreach (var e in new[] { "gguf", "gated", "downloads", "lastModified", "pipeline_tag", "tags" })
            Assert.Contains($"expand[]={e}", url);
    }

    [Fact]
    public async Task THE_RELEASER_LISTING_ASKS_ITS_TERM()
    {
        var handler = new StubHandler(_ => Json("[]"));
        var client = new HubClient(Client(handler));
        await client.ReleaserAsync("google", "gemma-4", gguf: false, CancellationToken.None);
        await client.ReleaserAsync("google", "gemma-4", gguf: true, CancellationToken.None);
        var sources = Uri.UnescapeDataString(handler.Urls[0]);
        var ggufs = Uri.UnescapeDataString(handler.Urls[1]);
        Assert.Contains("author=google", sources);
        Assert.Contains("search=gemma-4", sources);
        Assert.DoesNotContain("filter=gguf", sources);
        Assert.Contains("expand[]=pipeline_tag", sources);
        Assert.Contains("filter=gguf", ggufs);
        Assert.Contains("expand[]=tags", ggufs);
    }

    //a server that always answers with a next link is a bound, not a loop
    [Fact]
    public async Task A_RUNAWAY_LINK_STOPS()
    {
        var n = 0;
        var handler = new StubHandler(_ => WithNext(Json($"[{Row("a/r" + n++)}]"), $"https://huggingface.co/api/models?cursor={n}"));
        var rows = await new HubClient(Client(handler)).ConversionsAsync("Qwen/Qwen3.8-27B", CancellationToken.None);
        Assert.Equal(20, handler.Urls.Count);
        Assert.Equal(20, rows.Count);
    }

    //the api window as the server last reported it, and a response without the header keeps the last reading
    [Fact]
    public async Task THE_WINDOW_IS_READ()
    {
        var calls = 0;
        var handler = new StubHandler(_ =>
        {
            var res = Json("[]");
            if (calls++ == 0) res.Headers.TryAddWithoutValidation("RateLimit", "\"api\";r=37;t=120");
            return res;
        });
        var client = new HubClient(Client(handler));
        Assert.Null(client.Window);
        await client.ReleaserAsync("google", "gemma-4", false, CancellationToken.None);
        Assert.Equal((37, 120), client.Window);
        await client.ReleaserAsync("google", "gemma-4", false, CancellationToken.None);
        Assert.Equal((37, 120), client.Window);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 10, 7, 9, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    //a reading says nothing once the server's reset has passed, so a low count read long ago never blocks a later search
    [Fact]
    public async Task THE_WINDOW_EXPIRES_AT_ITS_RESET()
    {
        var handler = new StubHandler(_ =>
        {
            var res = Json("[]");
            res.Headers.TryAddWithoutValidation("RateLimit", "\"api\";r=3;t=120");
            return res;
        });
        var clock = new Clock();
        var client = new HubClient(Client(handler), clock);
        await client.ReleaserAsync("google", "gemma-4", false, CancellationToken.None);
        clock.Now = clock.Now.AddSeconds(119);
        Assert.Equal((3, 1), client.Window);
        clock.Now = clock.Now.AddSeconds(2);
        Assert.Null(client.Window);
    }

    //the resolve window is a different budget, so it never moves the api reading
    [Fact]
    public async Task ANOTHER_WINDOWS_HEADER_IS_NOT_THE_API_WINDOW()
    {
        var handler = new StubHandler(_ =>
        {
            var res = Json("[]");
            res.Headers.TryAddWithoutValidation("RateLimit", "\"resolvers\";r=2999;t=10");
            return res;
        });
        var client = new HubClient(Client(handler));
        await client.ReleaserAsync("google", "gemma-4", false, CancellationToken.None);
        Assert.Null(client.Window);
    }

    [Fact]
    public async Task A_429_IS_ITS_OWN_FAILURE()
    {
        var handler = new StubHandler(_ =>
        {
            var res = new HttpResponseMessage((HttpStatusCode)429);
            res.Headers.TryAddWithoutValidation("RateLimit", "\"api\";r=0;t=230");
            return res;
        });
        var ex = await Assert.ThrowsAsync<HubRateLimitedException>(
            () => new HubClient(Client(handler)).TreeAsync("org/repo", CancellationToken.None));
        Assert.Equal(230, ex.ResetSeconds);
    }

    //a 429 that names no reset carries none, so nothing counts down from a number the server never sent
    [Fact]
    public async Task A_429_WITHOUT_A_WINDOW_CARRIES_NO_RESET()
    {
        var calls = 0;
        var handler = new StubHandler(_ =>
        {
            if (calls++ == 0)
            {
                var ok = Json("[]");
                ok.Headers.TryAddWithoutValidation("RateLimit", "\"api\";r=40;t=200");
                return ok;
            }
            return new HttpResponseMessage((HttpStatusCode)429);
        });
        var client = new HubClient(Client(handler));
        await client.ReleaserAsync("google", "gemma-4", false, CancellationToken.None);
        var ex = await Assert.ThrowsAsync<HubRateLimitedException>(
            () => client.TreeAsync("org/repo", CancellationToken.None));
        Assert.Null(ex.ResetSeconds);
    }
}
