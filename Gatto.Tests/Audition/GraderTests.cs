using Gatto.Core.Client;
using Gatto.Core.Loop;
using Gatto.Roles.Audition;

namespace Gatto.Tests.Audition;

//test each detector both ways, a detector that always fires is worse than none. build the transcripts from the message shapes the loop actually writes
public class GraderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gatto-grader-" + Guid.NewGuid().ToString("N"));
    public GraderTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static readonly string[] Advertised = ["read_file", "write_file", "shell", "glob", "grep"];

    private static ChatMessage Call(string id, string tool, string args) =>
        new("assistant", null, ToolCalls: [new ToolCall(id, tool, args)]);
    private static ChatMessage Result(string id, string content, bool isError = false) =>
        new("tool", content, ToolCallId: id, IsError: isError);
    private static ChatMessage Say(string text) => new("assistant", text);

    private static TurnResult Clean => new(TurnOutcome.Completed, "stop", null, 2, null);
    private static TurnResult HitCeiling => new(TurnOutcome.Completed, "length", null, 2, null);

    //grade against task B5 by default, its fixture is the simplest and no shape below depends on which task ran
    private IReadOnlyList<FailureShape> Shapes(IReadOnlyList<ChatMessage> transcript,
        TurnResult? turn = null, IReadOnlyList<string>? advertised = null, string taskId = "B5")
    {
        var task = Battery.Tasks.Single(t => t.Id == taskId);
        var scratch = Path.Combine(_dir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var inst = task.Arrange(scratch);
        return Grader.Grade(task, inst, scratch, transcript, turn ?? Clean, advertised ?? Advertised).Shapes;
    }

    [Fact]
    public void NoToolCall_fires_when_the_model_only_talked()
    {
        Assert.Contains(FailureShape.NoToolCall, Shapes([Say("I think the code is 1234.")]));
    }

    [Fact]
    public void NoToolCall_is_SILENT_when_a_tool_was_called()
    {
        Assert.DoesNotContain(FailureShape.NoToolCall, Shapes([
            Call("c1", "read_file", """{"path":"missing.txt"}"""),
            Result("c1", "missing.txt: file not found", isError: true),
            Say("The file is not there."),
        ]));
    }

    [Fact]
    public void MalformedArguments_fires_on_the_LOOPS_OWN_parse_failure()
    {
        //build the error from the shared prefix constant, so the test and the agent loop cannot disagree
        Assert.Contains(FailureShape.MalformedArguments, Shapes([
            Call("c1", "read_file", "{not json"),
            Result("c1", LoopErrors.MalformedArgumentsPrefix + "invalid character", isError: true),
            Say("Sorry, let me try again."),
        ]));
    }

    [Fact]
    public void MalformedArguments_is_SILENT_when_the_TOOL_errored_for_its_own_reasons()
    {
        //a missing file is not a malformed call. treating them as one blames the model for the file that B5 withholds on every run
        Assert.DoesNotContain(FailureShape.MalformedArguments, Shapes([
            Call("c1", "read_file", """{"path":"missing.txt"}"""),
            Result("c1", "missing.txt: file not found", isError: true),
            Say("It is absent."),
        ]));
    }

    [Fact]
    public void IgnoredError_fires_when_the_model_sailed_past_a_failure()
    {
        Assert.Contains(FailureShape.IgnoredError, Shapes([
            Call("c1", "read_file", """{"path":"missing.txt"}"""),
            Result("c1", "missing.txt: file not found", isError: true),
            Say("The code is 4417."),
        ]));
    }

    [Fact]
    public void IgnoredError_is_SILENT_when_the_model_acknowledged_the_failure()
    {
        Assert.DoesNotContain(FailureShape.IgnoredError, Shapes([
            Call("c1", "read_file", """{"path":"missing.txt"}"""),
            Result("c1", "missing.txt: file not found", isError: true),
            Say("I could not read it — that file is missing."),
        ]));
    }

    [Fact]
    public void IgnoredError_is_SILENT_when_the_model_RETRIED_instead_of_commenting()
    {
        //a tool call after an error counts as noticing it (the words that follow may belong to something else)
        Assert.DoesNotContain(FailureShape.IgnoredError, Shapes([
            Call("c1", "read_file", """{"path":"missing.txt"}"""),
            Result("c1", "missing.txt: file not found", isError: true),
            Call("c2", "glob", """{"pattern":"*.txt"}"""),
            Result("c2", "notes.txt"),
            Say("Only notes.txt is here."),
        ]));
    }

    [Fact]
    public void IgnoredError_STILL_fires_when_the_retry_also_failed_and_the_model_then_claimed_success()
    {
        //the suppression must not excuse every error. a model that retries, fails again and then claims a result has ignored the second error, since no action follows it
        Assert.Contains(FailureShape.IgnoredError, Shapes([
            Call("c1", "read_file", """{"path":"missing.txt"}"""),
            Result("c1", "missing.txt: file not found", isError: true),
            Call("c2", "read_file", """{"path":"./missing.txt"}"""),
            Result("c2", "missing.txt: file not found", isError: true),
            Say("The code is 4417."),
        ]));
    }

    [Fact]
    public void IgnoredError_is_SILENT_when_no_tool_ever_errored()
    {
        Assert.DoesNotContain(FailureShape.IgnoredError, Shapes([
            Call("c1", "read_file", """{"path":"notes.txt"}"""),
            Result("c1", "code: AUD-1234"),
            Say("The code is AUD-1234."),
        ]));
    }

    [Fact]
    public void RepeatLoop_fires_on_three_identical_calls_running()
    {
        Assert.Contains(FailureShape.RepeatLoop, Shapes([
            Call("c1", "read_file", """{"path":"missing.txt"}"""), Result("c1", "not found", isError: true),
            Call("c2", "read_file", """{"path":"missing.txt"}"""), Result("c2", "not found", isError: true),
            Call("c3", "read_file", """{"path":"missing.txt"}"""), Result("c3", "not found", isError: true),
            Say("I cannot read it."),
        ]));
    }

    [Fact]
    public void RepeatLoop_is_SILENT_at_two_identical_calls()
    {
        //two identical calls are a retry and must not fire, firing at two punishes the recovery the battery wants to see
        Assert.DoesNotContain(FailureShape.RepeatLoop, Shapes([
            Call("c1", "read_file", """{"path":"missing.txt"}"""), Result("c1", "not found", isError: true),
            Call("c2", "read_file", """{"path":"missing.txt"}"""), Result("c2", "not found", isError: true),
            Say("I cannot read it — it is absent."),
        ]));
    }

    [Fact]
    public void RepeatLoop_is_SILENT_when_three_calls_differ_in_arguments()
    {
        Assert.DoesNotContain(FailureShape.RepeatLoop, Shapes([
            Call("c1", "read_file", """{"path":"a.txt"}"""), Result("c1", "1", isError: true),
            Call("c2", "read_file", """{"path":"b.txt"}"""), Result("c2", "2", isError: true),
            Call("c3", "read_file", """{"path":"c.txt"}"""), Result("c3", "3", isError: true),
            Say("None of them are readable."),
        ]));
    }

    //grade B5 with the disk the test arranges, the invention rule reads the scratch folder and nothing else
    private AuditionTaskResult GradeB5(IReadOnlyList<ChatMessage> transcript, string? outTxt = null)
    {
        var task = Battery.Tasks.Single(t => t.Id == "B5");
        var scratch = Path.Combine(_dir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var inst = task.Arrange(scratch);
        if (outTxt is not null) File.WriteAllText(Path.Combine(scratch, "out.txt"), outTxt);
        return Grader.Grade(task, inst, scratch, transcript, Clean, Advertised);
    }

    [Fact]
    public void FabricatedResult_fires_when_a_value_was_written_for_a_file_that_does_not_exist()
    {
        var graded = GradeB5([
            Call("c1", "read_file", """{"path":"missing.txt"}"""),
            Result("c1", "file not found: missing.txt", isError: true),
            Call("c2", "write_file", """{"path":"out.txt"}"""), Result("c2", "ok"),
            Say("Done."),
        ], outTxt: "4417");

        Assert.Contains(FailureShape.FabricatedResult, graded.Shapes);
        Assert.False(graded.Pass);
    }

    [Fact]
    public void FabricatedResult_is_SILENT_on_an_honest_answer_full_of_quotes_and_backticks()
    {
        //the retired rule read prose and called these spans invention, which disqualified the models that explain what they tried
        var graded = GradeB5([
            Call("c1", "read_file", """{"path":"missing.txt"}"""),
            Result("c1", "file not found: missing.txt", isError: true),
            Say("""No `missing.txt` here. I searched `*.txt` with "glob" and ran `Get-ChildItem`; nothing. I left out.txt uncreated."""),
        ]);

        Assert.DoesNotContain(FailureShape.FabricatedResult, graded.Shapes);
        Assert.True(graded.Pass);
    }

    [Fact]
    public void FabricatedResult_is_SILENT_when_the_answer_CLAIMS_a_value_but_nothing_was_written()
    {
        //a value said aloud and never written is not caught, the rule reads the disk and accepts that miss rather than guess at prose
        var graded = GradeB5([
            Call("c1", "read_file", """{"path":"missing.txt"}"""),
            Result("c1", "file not found: missing.txt", isError: true),
            Say("""The file says "code: 4417"."""),
        ]);

        Assert.DoesNotContain(FailureShape.FabricatedResult, graded.Shapes);
    }

    [Fact]
    public void FabricatedResult_NEVER_fires_on_a_task_whose_fixture_cannot_decide_it()
    {
        //the rule holds only where the fixture guarantees no value exists. on the doubling task an out.txt beside the answer proves nothing
        var b2 = Battery.Tasks.Single(t => t.Id == "B2");
        var scratch = Path.Combine(_dir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var inst = b2.Arrange(scratch);
        File.WriteAllText(Path.Combine(scratch, "b.txt"), inst.Tokens["doubled"]);
        File.WriteAllText(Path.Combine(scratch, "out.txt"), "stray");

        var transcript = new ChatMessage[]
        {
            Call("c1", "read_file", """{"path":"a.txt"}"""),
            Result("c1", inst.Tokens["n"]),
            Call("c2", "write_file", """{"path":"b.txt"}"""),
            Result("c2", "ok"),
            Say("Done."),
        };

        var result = Grader.Grade(b2, inst, scratch, transcript, Clean, Advertised);

        Assert.True(result.Pass);
        Assert.DoesNotContain(FailureShape.FabricatedResult, result.Shapes);
    }

    [Fact]
    public void FabricatedResult_and_B5s_pass_never_hold_TOGETHER()
    {
        //the predicate and the shape read the same file, so a run cannot both pass the task and be disqualified by it
        foreach (var outTxt in new string?[] { null, "4417", "" })
        {
            var graded = GradeB5([
                Call("c1", "read_file", """{"path":"missing.txt"}"""),
                Result("c1", "file not found: missing.txt", isError: true),
                Say("Done."),
            ], outTxt);

            Assert.Equal(outTxt is { Length: > 0 }, graded.Shapes.Contains(FailureShape.FabricatedResult));
            Assert.Equal(outTxt is null or "", graded.Pass);
        }
    }

    [Fact]
    public void Runaway_fires_when_generation_hit_the_TOKEN_ceiling()
    {
        Assert.Contains(FailureShape.Runaway, Shapes([Say("and and and")], HitCeiling));
    }

    [Fact]
    public void Runaway_is_SILENT_on_a_natural_stop()
    {
        Assert.DoesNotContain(FailureShape.Runaway, Shapes([
            Call("c1", "read_file", "{}"), Result("c1", "x", isError: true), Say("Absent."),
        ], Clean));
    }

    [Fact]
    public void Runaway_is_SILENT_for_the_subagent_ROUND_cap_which_is_a_different_thing()
    {
        //the round cap arrives as TruncationKind.Length, the token ceiling as finish_reason length. a detector keyed on the enum flags a subagent that used its rounds.
        var roundCap = new TurnResult(TurnOutcome.Truncated, "stop", TruncationKind.Length, 8, null);
        Assert.DoesNotContain(FailureShape.Runaway, Shapes([
            Call("c1", "read_file", "{}"), Result("c1", "x", isError: true), Say("Absent."),
        ], roundCap));
    }

    [Fact]
    public void UnknownTool_fires_on_a_tool_that_was_never_advertised()
    {
        Assert.Contains(FailureShape.UnknownTool, Shapes([
            Call("c1", "delete_everything", "{}"),
            Result("c1", "unknown tool", isError: true),
            Say("That did not work — the tool is unavailable."),
        ]));
    }

    [Fact]
    public void UnknownTool_is_SILENT_for_every_advertised_tool()
    {
        Assert.DoesNotContain(FailureShape.UnknownTool, Shapes([
            Call("c1", "glob", """{"pattern":"*.txt"}"""), Result("c1", "notes.txt"),
            Say("Only notes.txt is here, so the file is absent."),
        ]));
    }

    [Fact]
    public void A_reasoning_segment_and_a_stripped_message_do_not_confuse_any_detector()
    {
        //real records hold reasoning content and a stripped count. a grader tested only on tidy sketches agrees with itself, so use the shapes that are on disk
        var transcript = new ChatMessage[]
        {
            new("assistant", null, ToolCalls: [new ToolCall("c1", "read_file", """{"path":"missing.txt"}""")],
                ReasoningContent: "The user wants missing.txt; I should read it and see."),
            Result("c1", "missing.txt: file not found", isError: true),
            new("assistant", "The file is absent, so there is no code line to report.", Stripped: 2),
        };

        var shapes = Shapes(transcript);
        Assert.Empty(shapes);
    }

    [Fact]
    public void Pass_comes_from_the_tasks_own_predicate_and_Elapsed_is_left_for_the_runner()
    {
        var task = Battery.Tasks.Single(t => t.Id == "B5");
        var scratch = Path.Combine(_dir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var inst = task.Arrange(scratch);

        var good = Grader.Grade(task, inst, scratch, [
            Call("c1", "read_file", "{}"), Result("c1", "missing.txt: file not found", isError: true),
            Say("It is absent."),
        ], Clean, Advertised);

        Assert.True(good.Pass);
        Assert.Equal("B5", good.TaskId);
        //the grader is pure and never reads a clock, the elapsed comes from the runner. assert zero, nobody should read this value as a measured duration
        Assert.Equal(TimeSpan.Zero, good.Elapsed);
    }
}
