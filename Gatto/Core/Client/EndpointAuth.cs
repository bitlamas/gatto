using System.Net.Http.Headers;

namespace Gatto.Core.Client;

//the one authorization rule, ApiKey first then KeyEnv, and no header at all when neither is configured
public static class EndpointAuth
{
    public static void Apply(HttpRequestMessage msg, EndpointConfig endpoint)
    {
        if (endpoint.ApiKey is { Length: > 0 } apiKey)
            msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        else if (endpoint.KeyEnv is { Length: > 0 } keyEnv &&
                 Environment.GetEnvironmentVariable(keyEnv) is { Length: > 0 } key)
            msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }
}
