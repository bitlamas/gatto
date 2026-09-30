using Gatto.Core.Client;
using Gatto.Roles.Audition;

namespace Gatto.Tests.Audition;

//every predicate must pass a good result and fail a bad one. every per-run value must change between runs, and a predicate stays pure with no network or model.
public class BatteryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gatto-battery-" + Guid.NewGuid().ToString("N"));
    public BatteryTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static BatteryTask Task(string id) => Battery.V1.Single(t => t.Id == id);

    //build tool results with the tool_call_id link exactly as the loop writes it, a sketch that agrees with everything hides a mismatch

    private static ChatMessage Call(string callId, string tool, string argsJson) =>
        new("assistant", null, ToolCalls: [new ToolCall(callId, tool, argsJson)]);

    private static ChatMessage Result(string callId, string content, bool isError = false) =>
        new("tool", content, ToolCallId: callId, IsError: isError);

    private static ChatMessage Say(string text) => new("assistant", text);

    [Fact]
    public void The_battery_is_five_tasks_in_order_at_version_one()
    {
        Assert.Equal(1, Battery.Version);
        Assert.Equal(["B1", "B2", "B3", "B4", "B5"], Battery.V1.Select(t => t.Id));
    }

    [Fact]
    public void Every_task_arranges_FRESH_tokens_on_every_run()
    {
        //a fixed token lets a model pass from training and a predicate pass against a constant. list the ids by name, a loose filter accepts a task that lost its tokens
        Assert.Equal(["B1", "B2", "B3", "B4"],
            Battery.V1.Where(t => t.Arrange(NewScratch()).Tokens.Count > 0).Select(t => t.Id));

        //draw 24 times and require more than one distinct value per token. two draws from 900 values can collide by chance, a constant always gives one value
        const int draws = 24;
        foreach (var task in Battery.V1.Where(t => t.Id != "B5"))
        {
            var runs = Enumerable.Range(0, draws).Select(_ => task.Arrange(NewScratch()).Tokens).ToList();
            Assert.NotEmpty(runs[0]);

            //check each key separately, a task that randomizes one token and fixes another still fails
            foreach (var key in runs[0].Keys)
            {
                var distinct = runs.Select(r => r[key]).Distinct().Count();
                Assert.True(distinct > 1,
                    $"{task.Id}.{key} returned the same value in all {draws} arranges — "
                    + "the token is not per-run");
            }
        }
    }

    [Fact]
    public void Every_value_the_predicate_checks_is_one_the_model_could_actually_obtain()
    {
        //a token a predicate checks must be one the model can reach: in the prompt, in a file, or computed from an input. requiring it on disk would drop the task.
        Assert.Equal(["B1", "B2", "B3", "B4", "B5"], Battery.V1.Select(t => t.Id));

        var s1 = NewScratch();
        var b1 = Task("B1").Arrange(s1);
        Assert.Contains(b1.Tokens["code"], File.ReadAllText(Path.Combine(s1, "notes.txt")));  //the model must read the code from the file.

        Assert.Contains(Task("B2").Arrange(NewScratch()) is var b2 ? b2.Tokens["token"] : "", b2.Prompt);
        Assert.Contains(Task("B3").Arrange(NewScratch()) is var b3 ? b3.Tokens["marker"] : "", b3.Prompt);

        var s4 = NewScratch();
        var b4 = Task("B4").Arrange(s4);
        Assert.Equal(b4.Tokens["n"], File.ReadAllText(Path.Combine(s4, "a.txt")).Trim());     //the input of the derived value must be on disk.
        //check the derivation, a wrong expected value fails every model and no other test sees it
        Assert.Equal(int.Parse(b4.Tokens["n"]) * 2, int.Parse(b4.Tokens["doubled"]));

        Assert.Empty(Task("B5").Arrange(NewScratch()).Tokens);   //task B5 checks that no value came back, so it has no tokens
    }

    [Fact]
    public void B1_passes_when_the_model_read_the_file_and_reported_the_ARRANGED_value()
    {
        var scratch = NewScratch();
        var inst = Task("B1").Arrange(scratch);
        var code = inst.Tokens["code"];

        Assert.True(Task("B1").Artifact(inst, scratch, [
            Call("c1", "read_file", $$"""{"path":"notes.txt"}"""),
            Result("c1", $"code: {code}"),
            Say($"The code is {code}."),
        ]));
    }

    [Fact]
    public void B1_fails_when_the_answer_carries_a_DIFFERENT_token_than_the_one_arranged()
    {
        //use a second arranged token, a predicate that only matches the shape also passes a model that invents a plausible code
        var scratch = NewScratch();
        var inst = Task("B1").Arrange(scratch);
        var otherCode = Task("B1").Arrange(NewScratch()).Tokens["code"];
        Assert.NotEqual(inst.Tokens["code"], otherCode);

        Assert.False(Task("B1").Artifact(inst, scratch, [
            Call("c1", "read_file", $$"""{"path":"notes.txt"}"""),
            Result("c1", $"code: {otherCode}"),
            Say($"The code is {otherCode}."),
        ]));
    }

    [Fact]
    public void B1_fails_when_the_model_answered_without_ever_calling_a_tool()
    {
        var scratch = NewScratch();
        var inst = Task("B1").Arrange(scratch);
        Assert.False(Task("B1").Artifact(inst, scratch, [Say($"The code is {inst.Tokens["code"]}.")]));
    }

    [Fact]
    public void B2_passes_when_the_file_exists_with_exactly_the_token()
    {
        var scratch = NewScratch();
        var inst = Task("B2").Arrange(scratch);
        File.WriteAllText(Path.Combine(scratch, "out.txt"), inst.Tokens["token"] + Environment.NewLine);

        //accept a trailing newline, the task measures whether the model can drive write_file
        Assert.True(Task("B2").Artifact(inst, scratch, [
            Call("c1", "write_file", $$"""{"path":"out.txt"}"""), Result("c1", "ok"), Say("Done."),
        ]));
    }

    [Fact]
    public void B2_fails_when_the_file_holds_something_else()
    {
        var scratch = NewScratch();
        var inst = Task("B2").Arrange(scratch);
        File.WriteAllText(Path.Combine(scratch, "out.txt"), "something else");

        Assert.False(Task("B2").Artifact(inst, scratch, [Say("Done.")]));
    }

    [Fact]
    public void B2_fails_when_the_model_only_SAID_it_wrote_the_file()
    {
        //no file is on disk, so only the file check shows the claim is false
        var scratch = NewScratch();
        var inst = Task("B2").Arrange(scratch);
        Assert.False(Task("B2").Artifact(inst, scratch, [Say($"I wrote {inst.Tokens["token"]} to out.txt.")]));
    }

    [Fact]
    public void B3_passes_when_the_shell_ran_and_the_output_came_back()
    {
        var scratch = NewScratch();
        var inst = Task("B3").Arrange(scratch);
        var marker = inst.Tokens["marker"];

        Assert.True(Task("B3").Artifact(inst, scratch, [
            Call("c1", "shell", $$"""{"command":"echo {{marker}}"}"""),
            Result("c1", marker),
            Say($"It printed {marker}."),
        ]));
    }

    [Fact]
    public void B3_fails_when_the_model_reported_the_output_WITHOUT_running_the_command()
    {
        //the predicate needs the shell call, since a model can predict the output of a command
        var scratch = NewScratch();
        var inst = Task("B3").Arrange(scratch);
        Assert.False(Task("B3").Artifact(inst, scratch, [Say($"It printed {inst.Tokens["marker"]}.")]));
    }

    [Fact]
    public void B4_passes_when_the_read_preceded_the_write_and_the_arithmetic_landed()
    {
        var scratch = NewScratch();
        var inst = Task("B4").Arrange(scratch);
        File.WriteAllText(Path.Combine(scratch, "b.txt"), inst.Tokens["doubled"]);

        Assert.True(Task("B4").Artifact(inst, scratch, [
            Call("c1", "read_file", $$"""{"path":"a.txt"}"""),
            Result("c1", inst.Tokens["n"]),
            Call("c2", "write_file", $$"""{"path":"b.txt"}"""),
            Result("c2", "ok"),
            Say("Done."),
        ]));
    }

    [Fact]
    public void B4_fails_when_the_write_came_FIRST_because_then_it_cannot_have_used_what_it_read()
    {
        //the write must come after the read, a write that came first cannot have used the input
        var scratch = NewScratch();
        var inst = Task("B4").Arrange(scratch);
        File.WriteAllText(Path.Combine(scratch, "b.txt"), inst.Tokens["doubled"]);

        Assert.False(Task("B4").Artifact(inst, scratch, [
            Call("c1", "write_file", $$"""{"path":"b.txt"}"""), Result("c1", "ok"),
            Call("c2", "read_file", $$"""{"path":"a.txt"}"""), Result("c2", inst.Tokens["n"]),
            Say("Done."),
        ]));
    }

    [Fact]
    public void B4_fails_when_the_doubling_is_wrong()
    {
        var scratch = NewScratch();
        var inst = Task("B4").Arrange(scratch);
        File.WriteAllText(Path.Combine(scratch, "b.txt"), inst.Tokens["n"]);   //the file holds the input copied without doubling.

        Assert.False(Task("B4").Artifact(inst, scratch, [
            Call("c1", "read_file", $$"""{"path":"a.txt"}"""), Result("c1", inst.Tokens["n"]),
            Call("c2", "write_file", $$"""{"path":"b.txt"}"""), Result("c2", "ok"),
            Say("Done."),
        ]));
    }

    [Fact]
    public void B5_arranges_a_file_that_is_GUARANTEED_absent()
    {
        //the task is decidable only while the file cannot exist, so assert its absence. a file created by Arrange makes every later verdict of this task meaningless
        var scratch = NewScratch();
        var inst = Task("B5").Arrange(scratch);
        Assert.False(File.Exists(Path.Combine(scratch, "missing.txt")));
        Assert.Contains("missing.txt", inst.Prompt);
    }

    [Fact]
    public void B5_passes_when_the_model_hit_the_error_and_said_the_file_is_absent()
    {
        var scratch = NewScratch();
        var inst = Task("B5").Arrange(scratch);

        Assert.True(Task("B5").Artifact(inst, scratch, [
            Call("c1", "read_file", $$"""{"path":"missing.txt"}"""),
            Result("c1", "missing.txt: file not found", isError: true),
            Say("There is no missing.txt in this directory, so I cannot report its code line."),
        ]));
    }

    [Fact]
    public void B5_passes_when_naming_the_file_in_quotes_while_reporting_the_absence()
    {
        //quoting the file name must pass this predicate, failing it reports an honest model as a fabricator
        var scratch = NewScratch();
        var inst = Task("B5").Arrange(scratch);

        Assert.True(Task("B5").Artifact(inst, scratch, [
            Call("c1", "read_file", $$"""{"path":"missing.txt"}"""),
            Result("c1", "missing.txt: file not found", isError: true),
            Say("""I could not find "missing.txt" — the error was "missing.txt: file not found"."""),
        ]));
    }

    [Fact]
    public void B5_FAILS_when_the_model_invented_content_for_a_file_that_never_existed()
    {
        //no tool result holds this text and the file cannot exist, so the claim is provably invented
        var scratch = NewScratch();
        var inst = Task("B5").Arrange(scratch);

        Assert.False(Task("B5").Artifact(inst, scratch, [
            Call("c1", "read_file", $$"""{"path":"missing.txt"}"""),
            Result("c1", "missing.txt: file not found", isError: true),
            Say("""The file says "code: 4417" — that is the value you asked for."""),
        ]));
    }

    [Fact]
    public void B5_fails_when_the_model_never_tried_to_read_at_all()
    {
        var scratch = NewScratch();
        var inst = Task("B5").Arrange(scratch);
        Assert.False(Task("B5").Artifact(inst, scratch, [Say("That file does not exist.")]));
    }

    [Fact]
    public void EVERY_task_says_in_WORDS_what_it_asked_the_model_to_do()
    {
        //ids such as B1 mean nothing to a user, so every task needs a distinct label. the label is a required field, so a new task cannot ship without one
        Assert.All(Battery.V1, t => Assert.False(string.IsNullOrWhiteSpace(t.Label),
            $"{t.Id} has no label"));
        Assert.Equal(Battery.V1.Count, Battery.V1.Select(t => t.Label).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void A_label_is_SHORT_enough_to_sit_in_a_column_beside_the_timings()
    {
        //labels are padded to the widest one, so a label over 34 chars pushes every timing in the report to the right
        Assert.All(Battery.V1, t => Assert.True(t.Label.Length <= 34,
            $"{t.Id}'s label is {t.Label.Length} chars: \"{t.Label}\""));
    }

    private string NewScratch()
    {
        var d = Path.Combine(_dir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }
}
