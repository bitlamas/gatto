using Gatto.Core.Models;

namespace Gatto.Core.Acquire;

//the string arrays walked this launch, keyed by key and element count, so the next file with the same tokenizer jumps it. the first file to reach one walks it and the rest wait
internal sealed class TokenizerMemory
{
    //how long a file waits on another's walk before it walks for itself
    internal static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    //one array's walk: who walks it, and what it learned once done
    private sealed class Walk(Reader owner)
    {
        public readonly Reader Owner = owner;
        public readonly TaskCompletionSource<(long Bytes, byte[] Tail)?> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly Dictionary<(string Key, ulong Count), Walk> _arrays = [];
    private readonly object _gate = new();

    //the memory as one header read sees it, disposed when that read ends, so a wait ends with its token and no one waits on a walk it abandoned
    public Reader For(CancellationToken ct) => new(this, ct);

    internal sealed class Reader(TokenizerMemory memory, CancellationToken ct) : IStringArrayJumps, IDisposable
    {
        //the reader whose walk this one last jumped. while it runs it walks ahead in the same layout, so this one waits on it rather than walking alongside
        private Reader? _leader;
        private bool _active = true;

        public (long Bytes, byte[] Tail)? Remembered(string key, ulong count)
        {
            Walk walk;
            lock (memory._gate)
            {
                if (!memory._arrays.TryGetValue((key, count), out walk!))
                {
                    var owner = _leader is { _active: true } leader ? leader : this;
                    memory._arrays[(key, count)] = walk = new Walk(owner);
                    if (owner == this) return null;
                }
                else if (walk.Owner == this) return null;
            }
            try
            {
                if (!walk.Done.Task.Wait(Wait, ct) || walk.Done.Task.Result is not { } known) return null;
                _leader = walk.Owner;
                return known;
            }
            catch (OperationCanceledException) { return null; }
        }

        public void Walked(string key, ulong count, long bytes, byte[] tail)
        {
            Walk walk;
            lock (memory._gate)
                if (!memory._arrays.TryGetValue((key, count), out walk!))
                    memory._arrays[(key, count)] = walk = new Walk(this);
            walk.Done.TrySetResult((bytes, tail));
        }

        public void Failed(string key, ulong count)
        {
            lock (memory._gate)
                if (memory._arrays.TryGetValue((key, count), out var walk) && walk.Owner == this && !walk.Done.Task.IsCompleted)
                {
                    memory._arrays.Remove((key, count));
                    walk.Done.TrySetResult(null);
                }
        }

        //the read ended, so every array it was to walk and did not is released to whoever waits on it
        public void Dispose()
        {
            lock (memory._gate)
            {
                _active = false;
                foreach (var (at, walk) in memory._arrays.Where(a => a.Value.Owner == this && !a.Value.Done.Task.IsCompleted).ToList())
                {
                    memory._arrays.Remove(at);
                    walk.Done.TrySetResult(null);
                }
            }
        }
    }
}
