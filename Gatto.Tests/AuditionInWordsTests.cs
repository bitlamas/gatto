using Gatto.Core.Client;
using Gatto.Core.Loop;
using Gatto.Roles.Audition;

namespace Gatto.Tests;

//assert the ruled failure sentences through the rendered report, the user reads the words on the screen. the words have one home in TaskWords
public class AuditionInWordsTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "gatto-inwords-" + Guid.NewGuid().ToString("N"));

    public AuditionInWordsTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    //a public test method cannot take the internal FailureShape, so the theories pass its name. the nameof form makes a rename break the build.
    private static FailureShape Shape(string name) => Enum.Parse<FailureShape>(name);

    private static AuditionStamp Stamp() =>
        new("v0.5.0-1-gdeadbee", "m.gguf", "Q4_K_M", 8192, "temp 0.7");

    //render one failing task with only the shape under test, other tasks let the substring match find the sentence on the wrong row
    private static string Render(FailureShape shape, string id = "B3",
        string label = "run a command", TimeSpan? silence = null, string act = "ran it")
    {
        var task = new AuditionTaskResult(id, Pass: false, [shape],
            TimeSpan.FromSeconds(3), label, Skipped: false, silence, act);
        var verdict = new AuditionVerdict(Pass: false,
            Disqualified: shape == FailureShape.FabricatedResult, [task], Stamp(),
            TimeSpan.FromSeconds(9), 28.4);
        return AuditionReport.Render(verdict, s => s, Gatto.Roles.EngineMarks.Unicode);
    }

    //these six shapes have fixed sentences. the NoToolCall sentence ends in the verb of the task, so it has its own test
    [Theory]
    [InlineData(nameof(FailureShape.IgnoredError), "a step failed and it carried on as if it had worked")]
    [InlineData(nameof(FailureShape.MalformedArguments), "asked for it in a way gatto couldn't read")]
    [InlineData(nameof(FailureShape.RepeatLoop), "got stuck repeating the same step")]
    [InlineData(nameof(FailureShape.FabricatedResult), "made up the contents of a file that doesn't exist")]
    [InlineData(nameof(FailureShape.Runaway), "kept talking until gatto cut it off")]
    [InlineData(nameof(FailureShape.UnknownTool), "asked for a tool gatto doesn't have")]
    public void EACH_RULED_SHAPE_RENDERS_ITS_RULED_SENTENCE(string shape, string ruled)
    {
        var report = Render(Shape(shape));

        Assert.Contains(ruled, report, StringComparison.Ordinal);
    }

    //the NoToolCall sentence must end in the act of its own task. the test runs the real task, grader and render, so a change to any of the three fails it.
    [Theory]
    [InlineData("B1", "answered in words, never ran it")]
    [InlineData("B2", "answered in words, never used either")]
    [InlineData("B3", "answered in words, never edited it")]
    [InlineData("B4", "answered in words, never read it")]
    [InlineData("B5", "answered in words, never looked")]
    public void NO_TOOL_CALL_NAMES_THE_TASKS_OWN_ACT(string id, string ruled)
    {
        var task = Battery.Tasks.Single(t => t.Id == id);
        var scratch = Path.Combine(_dir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var instance = task.Arrange(scratch);

        //a prose-only transcript fires NoToolCall and satisfies no predicate. the predicate of B5 needs a tool call, and this transcript holds none
        var graded = Grader.Grade(task, instance, scratch,
            [new ChatMessage("assistant", "I would read that file for you.")],
            new TurnResult(TurnOutcome.Completed, "stop", null, 2, null),
            ["read_file", "write_file", "shell", "glob", "grep"]);

        Assert.False(graded.Pass);
        Assert.Contains(FailureShape.NoToolCall, graded.Shapes);

        var verdict = new AuditionVerdict(Pass: false, Disqualified: false, [graded], Stamp(),
            TimeSpan.FromSeconds(9), 28.4);

        Assert.Contains(ruled, AuditionReport.Render(verdict, s => s, Gatto.Roles.EngineMarks.Unicode), StringComparison.Ordinal);
    }

    //a result with no act is not a battery task, so the sentence gives the general fact rather than inventing a verb
    [Fact]
    public void WITH_NO_ACT_THE_SENTENCE_FALLS_BACK_TO_THE_GENERAL_FACT()
    {
        var report = Render(FailureShape.NoToolCall, act: "");

        Assert.Contains("never called a tool", report, StringComparison.Ordinal);
        Assert.DoesNotContain("answered in words", report, StringComparison.Ordinal);
    }

    //the token cap and stall rows keep their blunt words, the reader must learn whether to blame the model or the machine
    [Fact]
    public void TWO_KILL_ROWS_ARE_UNTOUCHED_BY_THE_REWORDING()
    {
        var cap = Render(FailureShape.StoppedAtCap);
        var stalled = Render(FailureShape.Stalled, silence: TimeSpan.FromSeconds(45));

        Assert.Contains("stopped at the token cap, still generating, no answer produced", cap,
            StringComparison.Ordinal);
        Assert.Contains("stopped, nothing came back for 45 seconds", stalled,
            StringComparison.Ordinal);
    }

    //assert the old words absent on the same render as the new words. a Contains check stays green when a partial rewording shows both sentences
    [Theory]
    [InlineData(nameof(FailureShape.NoToolCall), "never called a tool")]
    [InlineData(nameof(FailureShape.IgnoredError), "carried on after a tool reported an error")]
    [InlineData(nameof(FailureShape.MalformedArguments), "sent arguments the tool could not read")]
    [InlineData(nameof(FailureShape.RepeatLoop), "repeated the same call without changing anything")]
    [InlineData(nameof(FailureShape.FabricatedResult), "asserted the content of a file that does not exist")]
    [InlineData(nameof(FailureShape.Runaway), "kept generating until it hit the token ceiling")]
    [InlineData(nameof(FailureShape.UnknownTool), "called a tool that does not exist")]
    public void THE_SHIPPED_WORDS_ARE_GONE_NOT_JOINED(string shape, string shipped)
    {
        var report = Render(Shape(shape));

        Assert.DoesNotContain(shipped, report, StringComparison.Ordinal);
    }

    //derive the rows from the shared composer, and cover a pass, a failure and a skipped task. the skipped sentence uses a colon, since no em dash is allowed.
    [Fact]
    public void THE_REPORTS_ROWS_ARE_THE_SHARED_COMPOSERS_OWN_WORDS()
    {
        var battery = Battery.Tasks;
        var ran = new AuditionTaskResult(battery[0].Id, Pass: true, [], TimeSpan.FromSeconds(2),
            battery[0].Label, Act: battery[0].Act);
        var failed = new AuditionTaskResult(battery[1].Id, Pass: false, [FailureShape.NoToolCall],
            TimeSpan.FromSeconds(3), battery[1].Label, Act: battery[1].Act);
        var never = new AuditionTaskResult(battery[2].Id, Pass: false, [], TimeSpan.Zero,
            battery[2].Label, Skipped: true, Act: battery[2].Act);
        var verdict = new AuditionVerdict(Pass: false, Disqualified: false,
            [ran, failed, never], Stamp(), TimeSpan.FromSeconds(9), 28.4);

        var report = AuditionReport.Render(verdict, s => s, Gatto.Roles.EngineMarks.Unicode);

        //a passing row with no shape has nothing to explain, so it gives no words.
        Assert.Null(TaskWords.Why(ran));
        foreach (var task in new[] { failed, never })
        {
            var words = TaskWords.Why(task);
            Assert.False(string.IsNullOrEmpty(words), $"{task.TaskId} produced no words to check");
            Assert.Contains(words!, report, StringComparison.Ordinal);
        }

        //the skipped sentence must use a colon and no em dash.
        Assert.Contains("not run: the result was already clear", report, StringComparison.Ordinal);
        Assert.DoesNotContain("not run —", report, StringComparison.Ordinal);
    }
}
