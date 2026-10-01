using System.Net;
using Gatto.Core.Web;

namespace Gatto.Core.Client;

//an endpoint is cloud when its host is neither localhost nor a loopback or private address, so a server on the user's own machine or LAN is not
public static class CloudEndpoint
{
    //what the user is told once per home, with the model the session runs
    public static string Notice(string mark, string model) =>
        $"{mark} {model} is a cloud model: what you type and the files gatto reads are sent to its provider.";

    public static bool Is(EndpointConfig endpoint)
    {
        if (endpoint.BaseUrl is null) return false;
        if (!Uri.TryCreate(endpoint.BaseUrl, UriKind.Absolute, out var uri)) return false;
        if (uri.IsLoopback) return false;
        //a name that is not an address counts as cloud, gatto resolves nothing to decide it
        return !IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) || !UrlGuard.IsBlocked(ip);
    }
}
