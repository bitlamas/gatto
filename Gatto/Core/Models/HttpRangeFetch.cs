using System.Net;
using System.Net.Http.Headers;

namespace Gatto.Core.Models;

//the HTTP RangeFetch, a ranged GET against a GGUF on the Hub answers 206
internal static class HttpRangeFetch
{
    //the client must set an explicit Timeout, a timeout constant is only a budget if every client beneath it is untimed
    public static RangeFetch Create(HttpClient http, Uri url) => async (offset, count, ct) =>
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Range = new RangeHeaderValue(offset, offset + count - 1);

        using var res = await http
            .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        if (res.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            return [];                                     //past the end of the file, so the read comes back empty
        if (res.StatusCode is not (HttpStatusCode.PartialContent or HttpStatusCode.OK))
            throw new HttpRequestException(
                $"range request for {url} answered {(int)res.StatusCode} {res.StatusCode}");

        //a 200 at a nonzero offset would label the file's opening bytes as the bytes at offset, so it throws
        if (res.StatusCode == HttpStatusCode.OK && offset > 0)
            throw new HttpRequestException(
                $"range request for {url} at offset {offset} was ignored by the server " +
                $"(answered 200, not 206) — it cannot serve partial reads");

        //a 200 means the server is sending the whole file, so the read is bounded by count and never buffered whole
        var body = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var _ = body.ConfigureAwait(false);

        var buf = new byte[count];
        var total = 0;
        while (total < count)
        {
            //a socket can return a partial read, so loop until the buffer is full or the stream ends
            var n = await body.ReadAsync(buf.AsMemory(total, count - total), ct).ConfigureAwait(false);
            if (n == 0) break;                             //genuine EOF
            total += n;
        }
        return total == count ? buf : buf[..total];
    };
}
