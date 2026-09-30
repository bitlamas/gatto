using System.Runtime.CompilerServices;
using System.Text;

namespace Gatto.Core.Client;

public static class SseReader
{
    public static async IAsyncEnumerable<string> ReadDataPayloads(
        Stream stream, [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var data = new List<string>();
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            if (line.Length == 0)
            {
                if (data.Count > 0) { yield return string.Join("\n", data); data.Clear(); }
                continue;
            }
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                var payload = line[5..];
                if (payload.StartsWith(' ')) payload = payload[1..];
                data.Add(payload);
            }
            //a comment line and the event:, id: and retry: fields are ignored
        }
        if (data.Count > 0) yield return string.Join("\n", data);
    }
}
