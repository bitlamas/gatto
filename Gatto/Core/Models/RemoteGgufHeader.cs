namespace Gatto.Core.Models;

//a read shorter than count means end of file, the loop stops there and never asks for the rest
internal delegate Task<byte[]> RangeFetch(long offset, int count, CancellationToken ct);

//the window one read uses (start, growth, cap), sized by what that read looks for, small for the per-row read
internal readonly record struct HeaderWindow(int Initial, int Growth, int Cap)
{
    //the read for a selected candidate, everything FitArithmetic wants
    public static HeaderWindow FitTerms =>
        new(RemoteGgufHeader.InitialWindowBytes, RemoteGgufHeader.GrowthFactor,
            RemoteGgufHeader.HardCapBytes);

    //the structure column read, 16 KB with one retry to 256 KB
    public static HeaderWindow Structure => new(16 << 10, 16, 256 << 10);
}

internal static class RemoteGgufHeader
{
    public const int InitialWindowBytes = 1 << 20;
    public const int GrowthFactor = 4;
    public const int HardCapBytes = 16 << 20;

    //the fit-terms read, stop when satisfied and extend the fetch, with a fresh stream handed to the parser each round
    public static Task<GgufHeader> ReadAsync(RangeFetch fetch, CancellationToken ct) =>
        ReadAsync(fetch, HeaderWindow.FitTerms, static h => h.HasAllFitTerms, ct);

    //satisfied alone decides when there is enough, the other stop clauses below are about giving up
    public static async Task<GgufHeader> ReadAsync(
        RangeFetch fetch, HeaderWindow window, Func<GgufHeader, bool> satisfied, CancellationToken ct)
    {
        var have = Array.Empty<byte>();
        var target = window.Initial;
        while (true)
        {
            ct.ThrowIfCancellationRequested();   //a cancel between fetches stops the loop before the next fetch
                                                 //a cancel between fetches stops the loop before the next fetch
            var delta = await fetch(have.Length, target - have.Length, ct).ConfigureAwait(false);
            var eof = delta.Length < target - have.Length;
            var buf = new byte[have.Length + delta.Length];
            have.CopyTo(buf, 0); delta.CopyTo(buf, have.Length);
            have = buf;

            var h = GgufHeaderParser.Parse(new MemoryStream(have, writable: false));
            //satisfied is an OR with Complete, a truncated header that has the terms is a real shape
            if (satisfied(h) || h.Outcome != GgufOutcome.Truncated
                || eof || target >= window.Cap)
                return h;
            target = Math.Min(target * window.Growth, window.Cap);
        }
    }
}
