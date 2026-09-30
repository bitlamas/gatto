using System.Net;
using System.Net.Sockets;

namespace Gatto.Core.Web;

//pure, I/O-free SSRF address guard. rejects a non-http(s) scheme or an address in loopback, private, link-local, ULA, unspecified, broadcast or multicast space
public static class UrlGuard
{
    //parse the URL as absolute, require an http(s) scheme, and reject a literal IP host that IsBlocked. a DNS host passes here and must be checked after resolution
    public static Uri Validate(string url) => Validate(url, null);

    //as Validate, but an origin in the allowlist skips the literal-IP block, and the scheme and absolute-URL rules still apply
    public static Uri Validate(string url, IReadOnlySet<string>? allowedOrigins)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new GattoFetchException($"blocked: not an absolute url '{url}'");

        var scheme = uri.Scheme;
        if (scheme != Uri.UriSchemeHttp && scheme != Uri.UriSchemeHttps)
            throw new GattoFetchException($"blocked: scheme '{scheme}' — only http/https");

        if (allowedOrigins is not null && allowedOrigins.Contains(Origin(uri)))
            return uri;

        if (uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6
            && IPAddress.TryParse(uri.DnsSafeHost, out var ip)
            && IsBlocked(ip))
        {
            throw new GattoFetchException($"blocked: address {ip} is not a permitted target");
        }

        return uri;
    }

    //the allowlist key: lowercase scheme://host:port, and the port is always explicit since Uri fills in the scheme default
    public static string Origin(Uri uri) =>
        $"{uri.Scheme.ToLowerInvariant()}://{uri.Host.ToLowerInvariant()}:{uri.Port}";

    //every resolved address must clear IsBlocked, and one blocked address poisons the whole set. an empty list throws
    public static void ValidateResolved(IReadOnlyList<IPAddress> addresses, string host)
    {
        if (addresses.Count == 0)
            throw new GattoFetchException($"blocked: could not resolve '{host}'");

        foreach (var ip in addresses)
        {
            if (IsBlocked(ip))
                throw new GattoFetchException($"blocked: '{host}' resolves to {ip}, which is not a permitted target");
        }
    }

    //true when the address is loopback, private, link-local, ULA, unspecified, broadcast or multicast. an IPv4-mapped IPv6 address is unwrapped and checked again
    public static bool IsBlocked(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
            return IsBlocked(ip.MapToIPv4());

        if (IPAddress.IsLoopback(ip))
            return true;

        return ip.AddressFamily switch
        {
            AddressFamily.InterNetwork => IsBlockedV4(ip.GetAddressBytes()),
            AddressFamily.InterNetworkV6 => IsBlockedV6(ip),
            _ => true, //an unknown address family is blocked, so this fails closed
        };
    }

    private static bool IsBlockedV4(byte[] b)
    {
        //b[0] through b[3] are the octets in order
        return b[0] switch
        {
            0 => true,                                   //0.0.0.0/8, this network and unspecified
            10 => true,                                  //10.0.0.0/8, private
            100 when b[1] >= 64 && b[1] <= 127 => true,  //100.64.0.0/10, CGNAT shared space
            127 => true,                                 //127.0.0.0/8, loopback
            169 when b[1] == 254 => true,                //169.254.0.0/16, link-local
            172 when b[1] >= 16 && b[1] <= 31 => true,   //172.16.0.0/12, private
            192 when b[1] == 168 => true,                //the Class C private block
            >= 224 and <= 239 => true,                   //224.0.0.0/4, multicast
            >= 240 => true,                              //240.0.0.0/4, reserved Class E and broadcast
            _ => false,
        };
    }

    private static bool IsBlockedV6(IPAddress ip)
    {
        if (ip.IsIPv6Multicast)          //ff00::/8
            return true;
        if (ip.IsIPv6LinkLocal)          //fe80::/10
            return true;
        if (ip.IsIPv6UniqueLocal)        //fc00::/7
            return true;

        var b = ip.GetAddressBytes();

        //the ::/96 block is the deprecated IPv4-compatible form, so unwrap and re-check its embedded IPv4. this also covers :: itself, an unspecified address
        var first12Zero = true;
        for (var i = 0; i < 12; i++)
        {
            if (b[i] != 0) { first12Zero = false; break; }
        }
        return first12Zero && IsBlockedV4(b[12..]);
    }
}
