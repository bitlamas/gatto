using System.Collections.Concurrent;
using Gatto.Terminal;
using Gatto.Tests.Census;
using Gatto.Tests.Fakes;

namespace Gatto.Tests;

public class InputDeafnessWatchdogTests
{
    //a console queue that the reader drains and the writer fills. a deaf console never returns from a read, which is the wedge the watchdog exists to see.
    private sealed class FakeConsole(bool deaf = false) : IConsoleInputReader, IConsoleInputWriter, IDisposable
    {
        private readonly BlockingCollection<INPUT_RECORD> _queue = new();
        private readonly ManualResetEventSlim _release = new(false);
        private int _writes;

        public int Writes => Volatile.Read(ref _writes);

        public int Read(INPUT_RECORD[] buf)
        {
            if (deaf) { _release.Wait(); throw new InvalidOperationException("released"); }
            buf[0] = _queue.Take();
            return 1;
        }

        public int Peek(INPUT_RECORD[] buf) => 0;

        public bool Write(INPUT_RECORD record)
        {
            Interlocked.Increment(ref _writes);
            _queue.Add(record);
            return true;
        }

        public void Dispose()
        {
            _release.Set();
            _queue.CompleteAdding();
        }
    }

    private sealed class CountingWriter : IConsoleInputWriter
    {
        public int Writes;
        public bool Write(INPUT_RECORD record) { Interlocked.Increment(ref Writes); return true; }
    }

    private static void WaitFor(Func<bool> until)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!until())
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("the condition never held");
            Thread.Sleep(5);
        }
    }

    //the tracer must stay invisible downstream. a later change to key-up handling that let it through would type a key into the composer on every check.
    [Fact]
    public void THE_TRACER_IS_A_KEY_UP_THE_TRANSLATOR_DROPS()
    {
        Assert.Null(NativeInput.TranslateKey(InputDeafnessWatchdog.Tracer().KeyEvent));

        using var console = new FakeConsole();
        var source = new ConsoleInputSource(console);
        console.Write(InputDeafnessWatchdog.Tracer());
        console.Write(new INPUT_RECORD
        {
            EventType = NativeInput.EventKey,
            KeyEvent = new KEY_EVENT_RECORD { bKeyDown = 1, wRepeatCount = 1, wVirtualKeyCode = 0x41, UnicodeChar = 'a' },
        });

        Assert.Equal('a', Assert.IsType<KeyEvent>(source.Read()).Key.KeyChar);
        Assert.Equal(2, source.RecordsRead);
    }

    [Fact]
    public void A_LIVE_READER_TAKES_EVERY_TRACER_AND_NOTHING_PRINTS()
    {
        using var console = new FakeConsole();
        var source = new ConsoleInputSource(console);
        var pump = new InputPump(source);
        pump.Start();
        var rows = new ConcurrentQueue<string>();
        var watch = new InputDeafnessWatchdog(() => source.RecordsRead, console,
            TimeSpan.FromMilliseconds(20), TimeSpan.FromSeconds(5));
        watch.SetDeafSink(rows.Enqueue);

        watch.Arm();
        WaitFor(() => console.Writes >= 3 && source.RecordsRead >= 3);
        watch.Disarm();

        Assert.Empty(rows);
    }

    [Fact]
    public void A_DEAF_READER_PRINTS_THE_ROW_ONCE()
    {
        using var console = new FakeConsole(deaf: true);
        var source = new ConsoleInputSource(console);
        var pump = new InputPump(source);
        pump.Start();
        var rows = new ConcurrentQueue<string>();
        var watch = new InputDeafnessWatchdog(() => source.RecordsRead, console,
            TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(50));
        watch.SetDeafSink(rows.Enqueue);

        watch.Arm();
        WaitFor(() => !rows.IsEmpty);
        var writes = console.Writes;
        watch.Tick();
        watch.Tick();

        Assert.Equal(new[] { InputDeafnessWatchdog.Row }, rows.ToArray());
        Assert.Equal(writes, console.Writes);
    }

    //a modal wait opens inside a turn, so the checks stop only when both have closed.
    [Fact]
    public void ARMING_NESTS_AND_A_DISARMED_WATCHDOG_WRITES_NOTHING()
    {
        var writer = new CountingWriter();
        var rows = new ConcurrentQueue<string>();
        var watch = new InputDeafnessWatchdog(() => Volatile.Read(ref writer.Writes), writer, TimeSpan.FromHours(1), TimeSpan.FromSeconds(5));
        watch.SetDeafSink(rows.Enqueue);

        watch.Tick();
        Assert.Equal(0, writer.Writes);

        watch.Arm();
        watch.Arm();
        watch.Disarm();
        watch.Tick();
        Assert.Equal(1, writer.Writes);

        watch.Disarm();
        watch.Tick();
        Assert.Equal(1, writer.Writes);
        Assert.Empty(rows);
    }

    //a tracer written into a redirected handle, or into a console a key reader drains, is a write with no counting reader.
    [Fact]
    public void ONLY_A_RECORD_READER_ON_A_REAL_CONSOLE_GETS_A_WATCHDOG()
    {
        var built = 0;
        IConsoleInputWriter Writer() { built++; return new CountingWriter(); }
        using var console = new FakeConsole();

        Assert.Null(InputDeafnessWatchdog.For(new KeyInputSource(new ScriptedKeySource()), inputRedirected: false, Writer));
        Assert.Null(InputDeafnessWatchdog.For(new ConsoleInputSource(console), inputRedirected: true, Writer));
        Assert.Equal(0, built);

        Assert.NotNull(InputDeafnessWatchdog.For(new ConsoleInputSource(console), inputRedirected: false, Writer));
        Assert.Equal(1, built);
    }

    [Fact]
    public void THE_ROW_QUOTES_NO_NUMBER()
    {
        Assert.DoesNotMatch(@"\d", InputDeafnessWatchdog.Row);
        Assert.True(InputDeafnessWatchdog.CheckEvery > TimeSpan.Zero && InputDeafnessWatchdog.Bound > TimeSpan.Zero);
    }

    //the rich interactive path is the one place that reads a real console by record. a second construction site could arm a tracer under a pipe.
    [Fact]
    public void THE_WATCHDOG_AND_ITS_WRITER_ARE_BUILT_IN_ONE_PLACE()
    {
        foreach (var token in new[] { "InputDeafnessWatchdog.For(", "new Win32ConsoleInputWriter(" })
        {
            var sites = SourceTree.ProductionFiles()
                .Where(f => SourceTree.CodeOnly(SourceTree.Read(f)).Contains(token, StringComparison.Ordinal))
                .Select(f => Path.GetFileName(f))
                .ToList();
            Assert.Equal(["GattoApp.cs"], sites);
        }
    }
}
