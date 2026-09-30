using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Gatto.Core.Web;

//the outcome of a guarded fetch: the URL that actually delivered, its content type, the body and whether the cache answered
public sealed record FetchResult(string FinalUrl, string? ContentType, string Text, bool FromCache);

//a non-null PostJson makes the request a POST and keeps it out of the URL-keyed cache, and headers go on every hop. a redirect answering a POST is an error
public sealed record FetchOptions(
    IReadOnlyDictionary<string, string>? Headers = null,
    string? PostJson = null);

//SSRF-guarded fetch, the URL guard re-runs on every redirect hop. the connect is pinned to the addresses checked at connect time, blocking a DNS rebind
public sealed class GuardedFetch
{
    public const int MaxBodyBytes = 2 * 1024 * 1024;
    public const int TimeoutSeconds = 30;
    public const int MaxRedirects = 5;
    public const int CacheEntries = 50;

    private readonly Action<string, string?, bool>? _recorder;
    private readonly Func<string, Task<IPAddress[]>> _resolver;
    private readonly HttpClient _client;
    private readonly HashSet<string> _allowedOrigins;     //scheme://host:port, as UrlGuard.Origin builds it
    private readonly HashSet<string> _allowedHostPorts;   //host:port, since the connect pin sees no scheme

    private readonly Dictionary<string, FetchResult> _cache = new();
    private readonly Queue<string> _cacheOrder = new();

    public GuardedFetch(
        Action<string, string?, bool>? recorder = null,
        HttpMessageHandler? handler = null,
        Func<string, Task<IPAddress[]>>? resolver = null,
        IReadOnlyCollection<string>? allowedOrigins = null)
    {
        _recorder = recorder;
        _resolver = resolver ?? Dns.GetHostAddressesAsync;
        _allowedOrigins = new HashSet<string>(allowedOrigins ?? Array.Empty<string>(), StringComparer.Ordinal);
        _allowedHostPorts = new HashSet<string>(
            _allowedOrigins.Select(o => new Uri(o)).Select(u => $"{u.Host.ToLowerInvariant()}:{u.Port}"),
            StringComparer.Ordinal);
        //the client timeout is infinite so the TimeoutSeconds token is the only deadline. leaving the default in place would cap a raised budget without saying so
        _client = new HttpClient(handler ?? PinnedHandler(_resolver, _allowedHostPorts))
        {
            Timeout = System.Threading.Timeout.InfiniteTimeSpan,
        };
    }

    //each connect validates the host again and dials those addresses, so a second DNS answer cannot race the check. TLS still runs against the original host name
    private static SocketsHttpHandler PinnedHandler(
        Func<string, Task<IPAddress[]>> resolver, IReadOnlySet<string> allowedHostPorts) => new()
    {
        AllowAutoRedirect = false,
        ConnectCallback = async (ctx, ct) =>
        {
            //an allowed host:port still resolves through the pin but skips the blocked-range check
            var addresses = allowedHostPorts.Contains($"{ctx.DnsEndPoint.Host.ToLowerInvariant()}:{ctx.DnsEndPoint.Port}")
                ? await ResolveUnvalidatedAsync(resolver, ctx.DnsEndPoint.Host)
                : await ResolveValidatedAsync(resolver, ctx.DnsEndPoint.Host);
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(addresses, ctx.DnsEndPoint.Port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };

    //resolve the host and require every address to clear UrlGuard, with a literal IP skipping DNS. internal so a test can drive this path without a socket

    //resolve for an allowed host:port without the blocked-range check, a literal IP still skips DNS
    private static async Task<IPAddress[]> ResolveUnvalidatedAsync(
        Func<string, Task<IPAddress[]>> resolver, string host) =>
        IPAddress.TryParse(host, out var literal) ? new[] { literal } : await resolver(host);

    internal static async Task<IPAddress[]> ResolveValidatedAsync(
        Func<string, Task<IPAddress[]>> resolver, string host)
    {
        if (IPAddress.TryParse(host, out var literal))
        {
            var one = new[] { literal };
            UrlGuard.ValidateResolved(one, host);
            return one;
        }
        var addresses = await resolver(host);
        UrlGuard.ValidateResolved(addresses, host);
        return addresses;
    }

    public Task<FetchResult> FetchAsync(string url, CancellationToken ct) => FetchAsync(url, null, ct);

    public async Task<FetchResult> FetchAsync(string url, FetchOptions? options, CancellationToken ct)
    {
        var cacheable = options?.PostJson is null;
        if (cacheable && _cache.TryGetValue(url, out var cached))
        {
            Record(url, cached);
            return cached with { FromCache = true };
        }

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(TimeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            var result = await FetchLiveAsync(url, options, linked.Token);
            if (cacheable) Cache(url, result);
            Record(url, result);
            return result;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new GattoFetchException($"timed out after {TimeoutSeconds}s");
        }
        catch (HttpRequestException ex) when (ex.InnerException is GattoFetchException blocked)
        {
            throw blocked;   //the connect-time pin refused the address, so pass the guard's own message through
        }
        catch (HttpRequestException ex)
        {
            throw new GattoFetchException($"fetch failed: {ex.Message}");
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            throw new GattoFetchException($"blocked: could not resolve host ({ex.Message})");
        }
    }

    private async Task<FetchResult> FetchLiveAsync(string url, FetchOptions? options, CancellationToken ct)
    {
        var current = url;
        int hops = 0;

        while (true)
        {
            var uri = UrlGuard.Validate(current, _allowedOrigins);
            if (!_allowedOrigins.Contains(UrlGuard.Origin(uri)))
            {
                var addresses = await _resolver(uri.Host);
                UrlGuard.ValidateResolved(addresses, uri.Host);
            }

            using var request = new HttpRequestMessage(
                options?.PostJson is null ? HttpMethod.Get : HttpMethod.Post, uri);
            if (options?.PostJson is { } body)
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            if (options?.Headers is { } headers)
                foreach (var (k, v) in headers)
                    request.Headers.TryAddWithoutValidation(k, v);
            var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

            if (IsRedirect(response.StatusCode) && response.Headers.Location is { } location)
            {
                response.Dispose();
                if (options?.PostJson is not null)
                    throw new GattoFetchException($"unexpected redirect answering POST {uri.AbsoluteUri}");
                if (++hops > MaxRedirects)
                    throw new GattoFetchException($"too many redirects (max {MaxRedirects})");
                current = new Uri(uri, location).AbsoluteUri;
                continue;
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                    throw new GattoFetchException($"HTTP {(int)response.StatusCode} from {uri.AbsoluteUri}");

                var text = await ReadCappedAsync(response, ct);
                var contentType = response.Content.Headers.ContentType?.ToString();
                return new FetchResult(uri.AbsoluteUri, contentType, text, false);
            }
        }
    }

    private static async Task<string> ReadCappedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[8192];
        using var body = new MemoryStream();
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            if (total > MaxBodyBytes)
                throw new GattoFetchException("response exceeds 2 MB cap");
            body.Write(buffer, 0, read);
        }
        return Encoding.UTF8.GetString(body.GetBuffer(), 0, (int)body.Length);
    }

    //tell the citation recorder about the final URL and, after a redirect, the requested one too. the alias has no content, the model cites the URL it asked for
    private void Record(string requestedUrl, FetchResult result)
    {
        _recorder?.Invoke(result.FinalUrl, result.Text, false);
        if (!string.Equals(requestedUrl, result.FinalUrl, StringComparison.Ordinal))
            _recorder?.Invoke(requestedUrl, null, false);
    }

    private void Cache(string key, FetchResult result)
    {
        if (_cache.ContainsKey(key)) return;
        if (_cache.Count >= CacheEntries)
        {
            var oldest = _cacheOrder.Dequeue();
            _cache.Remove(oldest);
        }
        _cache[key] = result;
        _cacheOrder.Enqueue(key);
    }

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently   //301
            or HttpStatusCode.Found                  //302
            or HttpStatusCode.SeeOther               //303
            or HttpStatusCode.TemporaryRedirect      //307
            or HttpStatusCode.PermanentRedirect;     //308
}
