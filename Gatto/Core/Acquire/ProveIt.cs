using System.Diagnostics;
using System.Text.Json;
using Gatto.Core.Client;

namespace Gatto.Core.Acquire;

//the result of running a just-written config, with the first token measured so a slow cold load is reported as a wait
internal sealed record ProveOutcome(bool Ok, string Detail, TimeSpan FirstToken,
    bool StartFailed = false, int? ServerAnswered = null, string? ServedByAnother = null,   //the headline reads the start flag, a null status means nothing replied, and serving another model is a decline and no failure
    double? TokensPerSecond = null,   //the server's own decode rate, and without it the speed row is dropped rather than taken from the wall clock
    bool StreamedEmpty = false);   //the server streamed and closed with no content, which a screen tells apart from a server that never streamed

//setup's last step proves the config with one real minimal completion, the same seam for both setup forks
internal static class ProveIt
{
    //no deadline of its own, the caller owns the CTS (a cold 30 GB load takes minutes)
    public static async Task<ProveOutcome> RunAsync(OpenAiCompatClient client, string model, CancellationToken ct)
    {
        var request = new ChatRequest(
            Model: model,                    //the client writes it as given, so a server that validates the field must be handed the real name
            Messages: [new ChatMessage("user", "Say OK.")],
            BodyOverrides: JsonDocument.Parse("""{"max_tokens":16}""").RootElement);

        var clock = Stopwatch.StartNew();
        var firstToken = TimeSpan.Zero;
        var sawContent = false;
        var finished = false;
        string? timings = null;

        try
        {
            await foreach (var ev in client.StreamAsync(request, ct).ConfigureAwait(false))
            {
                switch (ev)
                {
                    case StreamEvent.TextDelta or StreamEvent.ReasoningDelta or StreamEvent.ToolCallDelta:
                        if (!sawContent) { firstToken = clock.Elapsed; sawContent = true; }
                        break;
                    case StreamEvent.Finished f:
                        finished = true;
                        //the server's own timings, kept for the skip path's speed row (null when it reports none, and the row is dropped)
                        timings = f.Timings;
                        break;
                }
            }
        }
        catch (OperationCanceledException) { throw; }        //the caller's deadline belongs to the caller, pass it on
        catch (Gatto.Core.Client.GattoHttpStatusException ex)
        {
            //a server replied, put the status on the outcome so the screen can discriminate, and prefer the server's own words
            return new ProveOutcome(false, ex.Message, clock.Elapsed, ServerAnswered: ex.StatusCode);
        }
        catch (Exception ex)
        {
            //nothing answered (refused, reset, timed out), ServerAnswered stays null and that is what licenses the capability sentence
            return new ProveOutcome(false, ex.Message, clock.Elapsed);
        }

        if (sawContent)
            return new ProveOutcome(true, "the model answered", firstToken,
                TokensPerSecond: Gatto.Core.Client.ServerTimings.DecodeRate([timings]));

        //the status is 200 by construction here, so a screen can tell this from a server that never answered
        return new ProveOutcome(false,
            finished
                ? "the server closed the response without sending any content"
                : "the server answered but did not STREAM the completion — gatto needs streaming "
                  + "chat completions (SSE), and this server does not appear to send them",
            clock.Elapsed, ServerAnswered: 200, StreamedEmpty: finished);
    }
}
