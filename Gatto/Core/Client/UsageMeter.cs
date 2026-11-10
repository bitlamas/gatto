using System.Text.Json;

namespace Gatto.Core.Client;

//what the meter holds at one moment, Footer is the usage reader's first row when it has read any and else the latest per-response reading
public sealed record AllowanceReport(
    decimal? Cost,
    Allowance? Footer,
    IReadOnlyList<Allowance> Rows,
    DateTimeOffset? ReadAt,
    string? LastFailure,
    bool HasReader,
    bool Refreshes = false);   //the endpoint has a usage reader, so a fresh read asks the provider something

//what the provider reported over the session: the summed cost, the latest per-response allowance and the usage reader's last rows
public sealed class UsageMeter(EndpointConfig? endpoint = null)
{
    //the label sits in the footer row, so the footer cuts it to this many cells and /usage draws it whole
    public const int MaxLabel = 12;

    private readonly object _gate = new();
    private decimal? _cost;
    private Allowance? _quota;
    private IReadOnlyList<Allowance> _rows = [];
    private DateTimeOffset? _readAt;
    private string? _lastFailure;
    private long _seq;
    private long _quotaSeq;
    private long _refreshSeq;
    private long _storedRefreshSeq;

    //raised on the pool once a reading is stored, so the footer repaints without waiting for the next key
    public Action? Changed { get; set; }

    //once per request, both readers are extension code and run on the pool so a blocking script never holds the stream that called
    public Task Record(JsonElement usage)
    {
        long seq;
        lock (_gate)
        {
            seq = ++_seq;
            if (endpoint?.Cost is null
                && usage.ValueKind == JsonValueKind.Object
                && usage.TryGetProperty("cost", out var c) && c.ValueKind == JsonValueKind.Number
                && c.TryGetDecimal(out var spent))
                _cost = (_cost ?? 0m) + spent;
        }
        var raw = usage.Clone();
        return Task.Run(() =>
        {
            StoreCost(raw);
            StoreQuota(raw, seq);
            Raise();
        });
    }

    //reads the endpoint's usage reader under the header bound, a failure keeps the last rows and names itself
    public async Task RefreshAsync(CancellationToken ct)
    {
        if (endpoint?.Usage is not { } read) return;
        long seq;
        lock (_gate) seq = ++_refreshSeq;
        IReadOnlyList<Allowance>? rows = null;
        string? failure = null;
        try
        {
            var got = await Task.Run(() => read(ct), ct).WaitAsync(EndpointAuth.HeaderBound, ct);
            rows = got?.Select(Bounded).OfType<Allowance>().ToList();
            if (rows is not { Count: > 0 }) failure = "its extension returned no allowance";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch (TimeoutException) { failure = "its extension did not answer in time"; }
        catch (Exception ex) { failure = ex.Message; }
        lock (_gate)
        {
            //an older read finishing late must not overwrite a newer one
            if (seq < _storedRefreshSeq) return;
            _storedRefreshSeq = seq;
            if (failure is null)
            {
                _rows = rows!;
                _readAt = DateTimeOffset.Now;
                _lastFailure = null;
            }
            else _lastFailure = failure;
        }
        Raise();
    }

    public AllowanceReport Read()
    {
        lock (_gate)
        {
            var rows = _quota is null ? _rows : [.. _rows, _quota];
            return new AllowanceReport(_cost, _rows.Count > 0 ? _rows[0] : _quota, rows, _readAt, _lastFailure,
                endpoint?.Usage is not null || endpoint?.Quota is not null, endpoint?.Usage is not null);
        }
    }

    private void Raise()
    {
        try { Changed?.Invoke(); }
        catch (Exception) { }   //the footer's repaint must never fault the meter
    }

    private void StoreCost(JsonElement raw)
    {
        if (endpoint?.Cost is not { } cost) return;
        decimal? spent;
        try { spent = cost(raw); }
        catch (Exception) { return; }   //extension code, a failure costs the cost part only
        if (spent is not { } s) return;
        lock (_gate) _cost = (_cost ?? 0m) + s;
    }

    private void StoreQuota(JsonElement raw, long seq)
    {
        if (endpoint?.Quota is not { } quota) return;
        Allowance? reading;
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

    //a blank label or one with a control character is refused whole, a negative number or a total of zero is one the provider did not send
    private static Allowance? Bounded(Allowance? r)
    {
        if (r is null || string.IsNullOrWhiteSpace(r.Label)) return null;
        var label = r.Label.Trim();
        if (label.Any(char.IsControl)) return null;
        return r with
        {
            Label = label,
            Used = r.Used is >= 0 ? r.Used : null,
            Remaining = r.Remaining is >= 0 ? r.Remaining : null,
            Total = r.Total is > 0 ? r.Total : null,
            UsedPercent = r.UsedPercent is >= 0 ? r.UsedPercent : null,
        };
    }
}
