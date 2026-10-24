using Gatto.Core.Acquire;

namespace Gatto.Cli.Setup;

//a file is read before its model is written, on a watched screen under the purr's timer, never behind a still screen
internal sealed partial class SetupFlow
{
    public const string ReadingKey = "model.reading";
    public const string ReadingStop = "reading.stop";

    //the read the screen waits on, what comes after it and where a stop goes, all cleared in AfterReading
    private Task<string?>? _reading;
    private CancellationTokenSource? _readingCancel;
    private Func<string?, SetupScreen>? _afterReading;
    private Func<SetupScreen>? _readingStopped;

    //long-running as the check is, since a pool worker blocked on a 20 GB read gets replaced on a timer
    private SetupScreen Read(FoundModel model, Func<string?, SetupScreen> then, Func<SetupScreen> stopped)
    {
        _readingCancel = new CancellationTokenSource();
        var ct = _readingCancel.Token;
        var path = model.Path;
        _afterReading = then;
        _readingStopped = stopped;
        _reading = Task.Factory.StartNew(() => probes.Sha256Of(path, ct),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        return Emit(new SetupScreen.Choice(
            ReadingKey,
            ModelTitle,
            [new ChoiceOption(ReadingStop, "stop reading", Advances: false)],
            BodyRows: [new SetupRow(Gatto.Roles.Audition.AuditionProgress.ReadingWords(System.IO.Path.GetFileName(path)), RowTone.Aside)],
            Watching: true,
            KeysOnly: true,
            FullPurr: true), ReadingKey);
    }

    //a stop cancels the read itself and waits for it to let go of the file, so nothing keeps reading behind the next screen
    private SetupScreen AfterReading(string answer)
    {
        var reading = _reading!;
        var then = _afterReading!;
        var stopped = _readingStopped!;
        _reading = null;
        _afterReading = null;
        _readingStopped = null;

        if (answer == ReadingStop) _readingCancel!.Cancel();
        string? hash;
        try { hash = reading.GetAwaiter().GetResult(); }
        catch (Exception) { hash = null; }   //a read that threw is a file gatto could not read, which the next screen has words for
        _readingCancel!.Dispose();
        _readingCancel = null;

        return answer == ReadingStop ? stopped() : then(hash);
    }
}
