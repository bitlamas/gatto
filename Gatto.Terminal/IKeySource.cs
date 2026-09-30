namespace Gatto.Terminal;

//source of key presses for the line editor. tests script it, the paste-burst heuristic reads KeyAvailable
public interface IKeySource
{
    ConsoleKeyInfo ReadKey();
    bool KeyAvailable { get; }

    //this default checks the token around the read and cannot interrupt a blocked read, so only scripted test sources may rely on it
    ConsoleKeyInfo ReadKey(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var key = ReadKey();
        ct.ThrowIfCancellationRequested();
        return key;
    }
}

//live console key source, raw ReadKey with intercept: true
public sealed class ConsoleKeySource : IKeySource
{
    //how often a cancellable console read looks for a key. it never reaches a screen
    internal const int CancelPollMs = 15;

    private readonly Func<bool> _available;
    private readonly Func<ConsoleKeyInfo> _read;

    public ConsoleKeySource() : this(() => Console.KeyAvailable, () => Console.ReadKey(intercept: true)) { }

    internal ConsoleKeySource(Func<bool> keyAvailable, Func<ConsoleKeyInfo> readKey)
    {
        _available = keyAvailable;
        _read = readKey;
    }

    public ConsoleKeyInfo ReadKey() => _read();

    //the read's only bound is the turn token. that token cancels only on the user's ctrl+break, the one bound a tool-path wait may take
    public ConsoleKeyInfo ReadKey(CancellationToken ct)
    {
        while (!_available())
            if (ct.WaitHandle.WaitOne(CancelPollMs)) ct.ThrowIfCancellationRequested();
        return _read();
    }

    public bool KeyAvailable => _available();
}
