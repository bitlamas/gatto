using System.Net.Http.Headers;

namespace Gatto.Core.Client;

//the one authorization rule: the endpoint's header callback when it has one, else ApiKey, else KeyEnv, and no header at all when none is configured
public static class EndpointAuth
{
    //how long a header callback may take before the request fails, so a script that never returns cannot hold a turn
    public static TimeSpan HeaderBound = TimeSpan.FromSeconds(10);

    public static void Apply(HttpRequestMessage msg, EndpointConfig endpoint)
    {
        if (endpoint.ApiKey is { Length: > 0 } apiKey)
            msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        else if (endpoint.KeyEnv is { Length: > 0 } keyEnv &&
                 Environment.GetEnvironmentVariable(keyEnv) is { Length: > 0 } key)
            msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    //a callback that throws, returns nothing or outlasts the bound fails the request with its own message, the request is never sent without its headers
    public static async Task ApplyAsync(HttpRequestMessage msg, EndpointConfig endpoint, string endpointName, CancellationToken ct)
    {
        if (endpoint.Headers is not { } callback)
        {
            Apply(msg, endpoint);
            return;
        }

        IReadOnlyDictionary<string, string>? headers;
        try
        {
            //run on the pool and awaited with a bound of its own, since a script can block before it returns its task or ignore the token
            headers = await Task.Run(() => callback(ct), ct).WaitAsync(HeaderBound, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (TimeoutException)
        {
            throw new GattoConnectionException($"endpoint {endpointName}: its extension did not return the request headers in time");
        }
        catch (Exception ex)
        {
            throw new GattoConnectionException(ex.Message);
        }

        if (headers is null)
            throw new GattoConnectionException($"endpoint {endpointName}: its extension returned no request headers");
        foreach (var (name, value) in headers)
        {
            msg.Headers.Remove(name);
            if (!msg.Headers.TryAddWithoutValidation(name, value))
                throw new GattoConnectionException($"endpoint {endpointName}: its extension returned a header gatto cannot send: {name}");
        }
    }
}
