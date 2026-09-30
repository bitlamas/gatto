using Gatto.Cli;
using Gatto.Roles;
using Gatto.Terminal;

namespace Gatto.Tests.Cli;

//the corpus renders every serve line a command can reach. this file holds the ones no fixture can drive, so they are not left with the words and no oracle.
public class ServeLinesTests
{
    private static (ServeLines Lines, StringWriter Sink) Plain()
    {
        var sink = new StringWriter();
        var cli = new CliSurface(sink, null, GlyphSet.Unicode);
        return (new ServeLines(cli, GlyphSet.Unicode), sink);
    }

    //the committed row must come after a take-down, the live line ends with no newline so the two would read as one row
    [Fact]
    public void A_COMMITTED_ROW_TAKES_THE_LIVE_LINE_DOWN_FIRST()
    {
        var sink = new StringWriter();
        var cli = new CliSurface(sink, null, GlyphSet.Unicode);
        using var tick = new TickLine(sink, rich: true, windowWidth: () => 100);
        var row = LoadingLine.Over(tick, GlyphSet.Unicode, theme: null);
        var lines = new ServeLines(cli, GlyphSet.Unicode, new StartWatch(
            elapsed => row.Draw(elapsed, "qwen3.6-35b-a3b", "~23.8 GB"), tick.Finish, "~23.8 GB"));

        lines.Loading(TimeSpan.FromSeconds(1));
        lines.StillLoading("qwen3.6-35b-a3b", 7777);

        //the committed row follows the take-down, or it is written onto the live row's own text
        Assert.Matches("\r +\rleft it loading qwen3.6-35b-a3b, ~23.8 GB \\(pid 7777\\)", sink.ToString());
    }

    //the last rung the poll reached is erased, and what replaces it names the same facts the ready line would have
    [Fact]
    public void THE_LEFT_IT_LOADING_ROW_NAMES_THE_MODEL_THE_SIZE_AND_THE_PID()
    {
        var sink = new StringWriter();
        var lines = new ServeLines(new CliSurface(sink, null, GlyphSet.Unicode), GlyphSet.Unicode,
            new StartWatch(_ => { }, () => { }, "~95.4 GB"));

        lines.StillLoading("big-model", 4242);

        Assert.Equal(
            ["left it loading big-model, ~95.4 GB (pid 4242)", "check with gatto serve status."],
            sink.ToString().Replace("\r\n", "\n").TrimEnd('\n').Split('\n'));
    }

    //a size that could not be measured is dropped rather than guessed, the same way the last rung drops it
    [Fact]
    public void THE_LEFT_IT_LOADING_ROW_DROPS_A_SIZE_IT_DOES_NOT_HAVE()
    {
        var sink = new StringWriter();
        var lines = new ServeLines(new CliSurface(sink, null, GlyphSet.Unicode), GlyphSet.Unicode);

        lines.StillLoading("big-model", 4242);

        Assert.StartsWith("left it loading big-model (pid 4242)", sink.ToString(), StringComparison.Ordinal);
    }

    //the two log faults must read differently, only a write that failed is a symptom of a stopped drain
    [Fact]
    public void THE_TWO_LOG_FAULTS_DO_NOT_READ_THE_SAME()
    {
        var (opened, openedSink) = Plain();
        var (written, writtenSink) = Plain();
        opened.PumpFault(ServeLogFault.NotOpened);
        written.PumpFault(ServeLogFault.NotWritten);

        Assert.NotEqual(openedSink.ToString(), writtenSink.ToString());
        Assert.Contains("could not be opened", openedSink.ToString(), StringComparison.Ordinal);
        Assert.Contains("could not be written", writtenSink.ToString(), StringComparison.Ordinal);
    }

    //say the server is unaffected, a row that only named the fault would send a reader after a server that is fine
    [Fact]
    public void A_LOG_FAULT_SAYS_THE_SERVER_IS_UNAFFECTED()
    {
        foreach (var fault in new[] { ServeLogFault.NotOpened, ServeLogFault.NotWritten })
        {
            var (lines, sink) = Plain();
            lines.PumpFault(fault);
            Assert.Contains("the server is fine", sink.ToString(), StringComparison.Ordinal);
        }
    }
}
