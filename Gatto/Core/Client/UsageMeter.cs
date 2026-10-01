using System.Text.Json;

namespace Gatto.Core.Client;

//what the provider reported over the session, the summed cost and the latest quota, fed once per request by any loop on the client
public sealed class UsageMeter(Func<JsonElement, QuotaReading?>? quota = null)
{
    //the label sits in the footer row, so a script cannot spend more cells on it than this
    public const int MaxLabel = 12;

    private readonly object _gate = new();
    private decimal? _cost;
    private QuotaReading? _quota;
    private long _seq;
    private long _quotaSeq;

    //the cost is summed here, the reader runs on the pool so a blocking script never holds the stream that called
    public Task Record(JsonElement usage)
    {
        long seq;
        lock (_gate)
        {
            seq = ++_seq;
            if (usage.ValueKind == JsonValueKind.Object
                && usage.TryGetProperty("cost", out var c) && c.ValueKind == JsonValueKind.Number
                && c.TryGetDecimal(out var spent))
                _cost = (_cost ?? 0m) + spent;
        }
        var raw = usage.Clone();
        return Task.Run(() =>
        {
            StoreQuota(raw, seq);
            try { Changed?.Invoke(); }
            catch (Exception) { }   //the footer's repaint must never fault the meter
        });
    }

    //raised on the pool once a request's cost and quota are both stored, so the footer repaints without waiting for the next key
    public Action? Changed { get; set; }

    private void StoreQuota(JsonElement raw, long seq)
    {
        if (quota is null) return;
        QuotaReading? reading;
        try { reading = Bounded(quota(raw)); }
        catch (Exception) { return; }   //extension code, a failure costs the quota part only
        if (reading is null) return;
        lock (_gate)
        {
            //an older request finishing late must not overwrite a newer reading
            if (seq < _quotaSeq) return;
            _quotaSeq = seq;
            _quota = reading;
        }
    }

    public (decimal? Cost, QuotaReading? Quota) Read()
    {
        lock (_gate) return (_cost, _quota);
    }

    //a label with a control character is refused whole, a long one is cut, and a negative count is not a reading
    private static QuotaReading? Bounded(QuotaReading? r)
    {
        if (r is null || r.Remaining < 0 || string.IsNullOrWhiteSpace(r.Label)) return null;
        var label = r.Label.Trim();
        if (label.Any(char.IsControl)) return null;
        if (label.Length > MaxLabel) label = label[..MaxLabel];
        return r with { Label = label, Total = r.Total is > 0 ? r.Total : null };
    }
}
