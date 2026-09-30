using System.Net;
using Gatto.Core.Acquire;

namespace Gatto.Tests;

//silence on the connect path is a state, so most of these cases assert that nothing throws
public class ServerConnectTests
{
    private sealed class PortHandler(Func<int, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public readonly List<string> Tried = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var uri = r.RequestUri!;
            Tried.Add($"{uri.Port}{uri.AbsolutePath}");
            return Task.FromResult(respond(uri.Port, uri.AbsolutePath));
        }
    }

    private static HttpClient Client(PortHandler h) => new(h) { Timeout = Timeout.InfiniteTimeSpan };
    private static HttpResponseMessage Dead() => new(HttpStatusCode.ServiceUnavailable);
    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private const string TwoModels = """{"data":[{"id":"qwen3.6-35b"},{"id":"gemma-4-26b"}]}""";

    [Fact]
    public async Task Ports_are_tried_in_order_and_probing_stops_at_the_first_answer()
    {
        var handler = new PortHandler((port, path) =>
            port == 8080 && path == "/v1/models" ? Json(TwoModels) : Dead());

        var probe = await ServerConnect.ProbeAsync(Client(handler), CancellationToken.None);

        Assert.NotNull(probe);
        Assert.Equal("http://127.0.0.1:8080", probe!.BaseUrl);
        //1235 is tried first, and 8080 answers, so probing stops there.
        Assert.Equal("1235/v1/models", handler.Tried[0]);
        Assert.Equal("8080/v1/models", handler.Tried[1]);
        //11434 sits below 1234 in the ladder, so stopping at 8080 reaches neither.
        Assert.DoesNotContain(handler.Tried, t => t.StartsWith("1234"));
        Assert.DoesNotContain(handler.Tried, t => t.StartsWith("11434"));
    }

    //the order is the rule, 11434 stays last so gatto's own server wins a machine running both
    [Fact]
    public void The_probe_ladder_is_1235_then_8080_then_1234_then_11434()
    {
        Assert.Equal([1235, 8080, 1234, 11434], ServerConnect.ProbePorts);   //1234 and 11434 are probed but never named in copy, the wizard's render owns that
    }

    //assert the probe, a ladder stopping before the fourth port would satisfy the constant and find nothing
    [Fact]
    public async Task A_server_answering_only_on_11434_is_found()
    {
        var handler = new PortHandler((port, path) =>
            port == 11434 && path == "/v1/models" ? Json(TwoModels) : Dead());

        var probe = await ServerConnect.ProbeAsync(Client(handler), CancellationToken.None);

        Assert.NotNull(probe);
        Assert.Equal("http://127.0.0.1:11434", probe!.BaseUrl);
        //only the /v1/models calls are rungs, the probe asks /props after the last one
        var rungs = handler.Tried.Where(t => t.EndsWith("/v1/models")).ToList();
        Assert.Equal(["1235/v1/models", "8080/v1/models", "1234/v1/models", "11434/v1/models"], rungs);
    }

    [Fact]
    public async Task The_models_list_is_parsed_from_the_answering_port()
    {
        var handler = new PortHandler((port, path) =>
            port == 1235 && path == "/v1/models" ? Json(TwoModels) : Dead());

        var probe = await ServerConnect.ProbeAsync(Client(handler), CancellationToken.None);

        Assert.Equal(["qwen3.6-35b", "gemma-4-26b"], probe!.Models);
    }

    [Fact]
    public async Task N_ctx_comes_from_props_when_the_server_offers_it()
    {
        var handler = new PortHandler((port, path) => path switch
        {
            "/v1/models" when port == 1235 => Json(TwoModels),
            //the real llama-server shape nests n_ctx under default_generation_settings, a root-level n_ctx reads as absent
            "/props" when port == 1235 =>
                Json("""{"model_path": "C:\\m.gguf","default_generation_settings":{"n_ctx":32768}}"""),
            _ => Dead(),
        });

        var probe = await ServerConnect.ProbeAsync(Client(handler), CancellationToken.None);

        Assert.Equal(32768, probe!.NCtx);
    }

    //a server that names no model_path still reports its context, the connect launch would otherwise claim it reported nothing
    [Fact]
    public async Task A_SERVER_THAT_NAMES_NO_FILE_still_reports_its_context()
    {
        var handler = new PortHandler((port, path) => path switch
        {
            "/v1/models" when port == 1235 => Json(TwoModels),
            "/props" when port == 1235 => Json("""{"default_generation_settings":{"n_ctx":55555}}"""),
            _ => Dead(),
        });

        var probe = await ServerConnect.ProbeAsync(Client(handler), CancellationToken.None);

        Assert.NotNull(probe);
        Assert.Equal(55555, probe!.NCtx);
        Assert.Equal(2, probe.Models.Count);
    }

    [Theory]
    [InlineData("")]                                                              //an empty body stands for a /props call that was not served
    [InlineData("""{"model_path": "C:\\m.gguf"}""")]                               //the answer is served but holds no n_ctx
    [InlineData("""{"model_path": "C:\\m.gguf","n_ctx":32768}""")]                 //a root-level n_ctx is not the real shape, so it reads as absent
    [InlineData("not json at all")]                                               //the body answers but is not json.
    public async Task A_missing_or_malformed_props_leaves_n_ctx_null_and_never_throws(string props)
    {
        //a failed context probe is not a connect failure, the caller gets the context ask instead
        var handler = new PortHandler((port, path) => path switch
        {
            "/v1/models" when port == 1235 => Json(TwoModels),
            "/props" when port == 1235 && props.Length > 0 => Json(props),
            _ => Dead(),
        });

        var probe = await ServerConnect.ProbeAsync(Client(handler), CancellationToken.None);

        Assert.NotNull(probe);
        Assert.Null(probe!.NCtx);
        Assert.Equal(2, probe.Models.Count);                //the connect itself still succeeded even with no context reported.
    }

    [Fact]
    public async Task Silence_on_every_port_is_a_null_result_not_a_throw()
    {
        //no answer is a state, the caller starts a server and probes again
        var handler = new PortHandler((_, _) => Dead());

        var probe = await ServerConnect.ProbeAsync(Client(handler), CancellationToken.None);

        Assert.Null(probe);
        //count the ports from ProbePorts, so a rung added and never tried fails this check
        Assert.Equal(ServerConnect.ProbePorts.Count, handler.Tried.Count);
    }

    [Fact]
    public async Task A_transport_failure_on_every_port_is_also_just_silence()
    {
        var handler = new PortHandler((_, _) => throw new HttpRequestException("refused"));
        Assert.Null(await ServerConnect.ProbeAsync(Client(handler), CancellationToken.None));
    }

    [Fact]
    public async Task A_server_that_answers_with_an_empty_model_list_still_counts_as_answering()
    {
        //an empty model list is an answer, so probing stops there and the next step asks the user to name a model
        var handler = new PortHandler((port, path) =>
            port == 1235 && path == "/v1/models" ? Json("""{"data":[]}""") : Dead());

        var probe = await ServerConnect.ProbeAsync(Client(handler), CancellationToken.None);

        Assert.NotNull(probe);
        Assert.Empty(probe!.Models);
        Assert.DoesNotContain(handler.Tried, t => t.StartsWith("8080"));
    }

    [Fact]
    public void The_capability_table_carries_all_four_rows_with_real_degradation_sentences()
    {
        //the wizard and the doctor share this table, so the sentences live here as data
        Assert.Equal(4, ServerConnect.Capabilities.Count);
        Assert.All(ServerConnect.Capabilities, c =>
        {
            Assert.False(string.IsNullOrWhiteSpace(c.Name));
            Assert.False(string.IsNullOrWhiteSpace(c.IfAbsent));
            Assert.True(c.IfAbsent.Length > 30, $"'{c.Name}' has a degradation sentence too short to inform anyone");
        });
        Assert.Contains(ServerConnect.Capabilities, c => c.Name.Contains("/v1/models"));
        Assert.Contains(ServerConnect.Capabilities, c => c.Name.Contains("n_ctx"));
    }

    [Fact]
    public void Probing_cannot_write_anything_because_it_is_given_nowhere_to_write()
    {
        //probing writes nothing by construction, ProbeAsync is given no path and no sink
        var parameters = typeof(ServerConnect).GetMethod(nameof(ServerConnect.ProbeAsync))!.GetParameters();
        var mayWrite = new[]
        {
            typeof(string), typeof(System.IO.TextWriter), typeof(System.IO.Stream),
            typeof(System.IO.FileInfo), typeof(System.IO.DirectoryInfo),
        };

        Assert.NotEmpty(parameters);
        Assert.DoesNotContain(parameters, p => mayWrite.Contains(p.ParameterType));
        //assert the matcher can catch string, otherwise the DoesNotContain above proves nothing
        Assert.Contains(mayWrite, t => t == typeof(string));
    }
}
