namespace Gatto.Repl;

//the prompters' way to the current turn's CancellationTokenSource, read live so a swap can't cancel the wrong turn
public sealed class TurnAbortHandle
{
    //reads the live turn's source, null when there is none and then RequestAbort does nothing
    public Func<CancellationTokenSource?>? Current;

    //the live turn's token for a prompt's key read, read at prompt time. no turn, or a source already disposed, gives a token that never cancels
    public CancellationToken Token
    {
        get
        {
            try { return Current?.Invoke()?.Token ?? default; }
            catch (ObjectDisposedException) { return default; }
        }
    }

    //cancels the current turn, a disposed source between the read and the cancel is swallowed like the Ctrl+C sink does
    public void RequestAbort()
    {
        try { Current?.Invoke()?.Cancel(); }
        catch (ObjectDisposedException) { }
    }
}
