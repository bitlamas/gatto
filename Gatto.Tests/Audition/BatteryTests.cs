using Gatto.Core.Client;
using Gatto.Roles.Audition;

namespace Gatto.Tests.Audition;

//every predicate must pass a good result and fail a bad one. every per-run value must change between runs, and a predicate stays pure with no network or model.
public class BatteryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gatto-battery-" + Guid.NewGuid().ToString("N"));
    public BatteryTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static BatteryTask Task(string id) => Battery.Tasks.Single(t => t.Id == id);

    //build tool results with the tool_call_id link exactly as the loop writes it, a sketch that agrees with everything hides a mismatch

    private static ChatMessage Call(string callId, string tool, string argsJson) =>
        new("assistant", null, ToolCalls: [new ToolCall(callId, tool, argsJson)]);

    private static ChatMessage Result(string callId, string content, bool isError = false) =>
        new("tool", content, ToolCallId: callId, IsError: isError);

    private static ChatMessage Say(string text) => new("assistant", text);

    [Fact]
    public void The_battery_is_five_tasks_in_order_at_version_four()
    {
        Assert.Equal(4, Battery.Version);
        Assert.Equal(["B1", "B2", "B3", "B4", "B5"], Battery.Tasks.Select(t => t.Id));
    }

    [Fact]
    public void Every_task_arranges_FRESH_tokens_on_every_run()
    {
        //a fixed token lets a model pass from training and a predicate pass against a constant. list the ids by name, a loose filter accepts a task that lost its tokens
        Assert.Equal(["B1", "B2", "B3", "B4"],
            Battery.Tasks.Where(t => t.Arrange(NewScratch()).Tokens.Count > 0).Select(t => t.Id));

        //draw 24 times and require more than one distinct value per token. two draws from 900 values can collide by chance, a constant always gives one value
        const int draws = 24;
        foreach (var task in Battery.Tasks.Where(t => t.Id != "B5"))
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
        Assert.Equal(["B1", "B2", "B3", "B4", "B5"], Battery.Tasks.Select(t => t.Id));

        Assert.Contains(Task("B1").Arrange(NewScratch()) is var b1 ? b1.Tokens["marker"] : "", b1.Prompt);

        var s2 = NewScratch();
        var b2 = Task("B2").Arrange(s2);
        Assert.Equal(b2.Tokens["n"], File.ReadAllText(Path.Combine(s2, "a.txt")).Trim());     //the input of the derived value must be on disk.
        //check the derivation, a wrong expected value fails every model and no other test sees it
        Assert.Equal(int.Parse(b2.Tokens["n"]) * 2, int.Parse(b2.Tokens["doubled"]));

        Assert.Contains(Task("B3").Arrange(NewScratch()) is var b3 ? b3.Tokens["token"] : "", b3.Prompt);

        var s4 = NewScratch();
        var b4 = Task("B4").Arrange(s4);
        Assert.Contains(b4.Tokens["code"], File.ReadAllText(Path.Combine(s4, "conf", "config.txt")));  //the model must read the code from the file.

        Assert.Empty(Task("B5").Arrange(NewScratch()).Tokens);   //task B5 checks that no value was written, so it has no tokens
    }

    [Fact]
    public void B1_passes_when_the_shell_ran_and_the_output_came_back()
    {
        var scratch = NewScratch();
        var inst = Task("B1").Arrange(scratch);
        var marker = inst.Tokens["marker"];

        Assert.True(Task("B1").Artifact(inst, scratch, [
            Call("c1", "shell", $$"""{"command":"echo {{marker}}"}"""),
            Result("c1", marker),
            Say($"It printed {marker}."),
        ]));
    }

    [Fact]
    public void B1_fails_when_the_model_reported_the_output_WITHOUT_running_the_command()
    {
        //the predicate needs the shell call, since a model can predict the output of a command
        var scratch = NewScratch();
        var inst = Task("B1").Arrange(scratch);
        Assert.False(Task("B1").Artifact(inst, scratch, [Say($"It printed {inst.Tokens["marker"]}.")]));
    }

    [Fact]
    public void B2_passes_when_the_read_preceded_the_write_and_the_arithmetic_landed()
    {
        var scratch = NewScratch();
        var inst = Task("B2").Arrange(scratch);
        File.WriteAllText(Path.Combine(scratch, "b.txt"), inst.Tokens["doubled"]);

        Assert.True(Task("B2").Artifact(inst, scratch, [
            Call("c1", "read_file", $$"""{"path":"a.txt"}"""),
            Result("c1", inst.Tokens["n"]),
            Call("c2", "write_file", $$"""{"path":"b.txt"}"""),
            Result("c2", "ok"),
            Say("Done."),
        ]));
    }

    [Fact]
    public void B2_fails_when_the_write_came_FIRST_because_then_it_cannot_have_used_what_it_read()
    {
        //the write must come after the read, a write that came first cannot have used the input
        var scratch = NewScratch();
        var inst = Task("B2").Arrange(scratch);
        File.WriteAllText(Path.Combine(scratch, "b.txt"), inst.Tokens["doubled"]);

        Assert.False(Task("B2").Artifact(inst, scratch, [
            Call("c1", "write_file", $$"""{"path":"b.txt"}"""), Result("c1", "ok"),
            Call("c2", "read_file", $$"""{"path":"a.txt"}"""), Result("c2", inst.Tokens["n"]),
            Say("Done."),
        ]));
    }

    [Fact]
    public void B2_fails_when_the_doubling_is_wrong()
    {
        var scratch = NewScratch();
        var inst = Task("B2").Arrange(scratch);
        File.WriteAllText(Path.Combine(scratch, "b.txt"), inst.Tokens["n"]);   //the file holds the input copied without doubling.

        Assert.False(Task("B2").Artifact(inst, scratch, [
            Call("c1", "read_file", $$"""{"path":"a.txt"}"""), Result("c1", inst.Tokens["n"]),
            Call("c2", "write_file", $$"""{"path":"b.txt"}"""), Result("c2", "ok"),
            Say("Done."),
        ]));
    }

    private static readonly ChatMessage[] Edited =
        [Call("c1", "edit_file", """{"path":"settings.txt"}"""), Result("c1", "ok"), Say("Done.")];

    private static string[] Settings(string code) =>
        ["name: audition", "mode: fast", $"code: {code}", "retries: 3", "owner: nobody"];

    [Fact]
    public void B3_arranges_five_lines_with_the_code_line_waiting_to_be_changed()
    {
        var scratch = NewScratch();
        var inst = Task("B3").Arrange(scratch);
        Assert.Equal(Settings("PENDING"), File.ReadAllLines(Path.Combine(scratch, "settings.txt")));
        Assert.Contains("edit_file", inst.Prompt);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void B3_passes_when_only_the_code_line_changed_whatever_the_newline(string newline)
    {
        //a tool that writes the other newline changed nothing the task asked about, failing it would grade the platform
        var scratch = NewScratch();
        var inst = Task("B3").Arrange(scratch);
        File.WriteAllText(Path.Combine(scratch, "settings.txt"), string.Join(newline, Settings(inst.Tokens["token"])) + newline);

        Assert.True(Task("B3").Artifact(inst, scratch, Edited));
    }

    [Fact]
    public void B3_fails_when_the_file_was_left_as_it_was()
    {
        var scratch = NewScratch();
        var inst = Task("B3").Arrange(scratch);
        Assert.False(Task("B3").Artifact(inst, scratch, Edited));
    }

    [Fact]
    public void B3_fails_when_a_NEIGHBOUR_line_changed_with_the_code_line()
    {
        //the task says change nothing else, a model that rewrites from memory gets a neighbour wrong
        var scratch = NewScratch();
        var inst = Task("B3").Arrange(scratch);
        var lines = Settings(inst.Tokens["token"]);
        lines[3] = "retries: 5";
        File.WriteAllLines(Path.Combine(scratch, "settings.txt"), lines);

        Assert.False(Task("B3").Artifact(inst, scratch, Edited));
    }

    [Fact]
    public void B3_fails_when_the_right_file_came_from_a_tool_OTHER_than_edit_file()
    {
        //the task is whether the model can drive edit_file, a whole-file rewrite reaches the same bytes without it
        var scratch = NewScratch();
        var inst = Task("B3").Arrange(scratch);
        File.WriteAllLines(Path.Combine(scratch, "settings.txt"), Settings(inst.Tokens["token"]));

        Assert.False(Task("B3").Artifact(inst, scratch, [
            Call("c1", "write_file", """{"path":"settings.txt"}"""), Result("c1", "ok"), Say("Done."),
        ]));
    }

    [Fact]
    public void B4_arranges_the_file_one_folder_BELOW_the_path_the_prompt_names()
    {
        //the task is decidable only while the named path fails, a file at the named path turns it back into a plain read
        var scratch = NewScratch();
        var inst = Task("B4").Arrange(scratch);
        Assert.False(File.Exists(Path.Combine(scratch, "config.txt")));
        Assert.True(File.Exists(Path.Combine(scratch, "conf", "config.txt")));
        Assert.DoesNotContain("conf\\", inst.Prompt);
        Assert.DoesNotContain("conf/", inst.Prompt);
    }

    [Fact]
    public void B4_passes_when_the_model_searched_after_the_failed_read_and_reported_the_ARRANGED_value()
    {
        var scratch = NewScratch();
        var inst = Task("B4").Arrange(scratch);
        var code = inst.Tokens["code"];

        Assert.True(Task("B4").Artifact(inst, scratch, [
            Call("c1", "read_file", """{"path":"config.txt"}"""),
            Result("c1", "file not found: config.txt", isError: true),
            Call("c2", "glob", """{"pattern":"**/config.txt"}"""),
            Result("c2", "conf\\config.txt"),
            Call("c3", "read_file", """{"path":"conf\\config.txt"}"""),
            Result("c3", $"project: audition\ncode: {code}"),
            Say($"The file is at conf\\config.txt and its code is {code}."),
        ]));
    }

    [Fact]
    public void B4_passes_when_the_value_came_through_ANY_tool_and_no_read_file_found_it()
    {
        //one grep that prints the line is a recovery too, the task asks for the value and names no tool
        var scratch = NewScratch();
        var inst = Task("B4").Arrange(scratch);
        var code = inst.Tokens["code"];

        Assert.True(Task("B4").Artifact(inst, scratch, [
            Call("c1", "grep", """{"pattern":"code:"}"""),
            Result("c1", $"conf\\config.txt:2: code: {code}"),
            Say($"The value on the code line is {code}."),
        ]));
        Assert.False(Task("B4").Invented!(inst, scratch));
    }

    [Fact]
    public void B4_fails_when_the_answer_names_the_code_and_NO_tool_ever_returned_it()
    {
        var scratch = NewScratch();
        var inst = Task("B4").Arrange(scratch);

        Assert.False(Task("B4").Artifact(inst, scratch, [
            Call("c1", "read_file", """{"path":"config.txt"}"""),
            Result("c1", "file not found: config.txt", isError: true),
            Say($"The code is {inst.Tokens["code"]}."),
        ]));
    }

    [Fact]
    public void B4_FAILS_and_is_INVENTED_when_the_model_wrote_its_own_config_txt()
    {
        //no file was at the named path, so one that appears there without the real code holds a value the model made up
        var scratch = NewScratch();
        var inst = Task("B4").Arrange(scratch);
        File.WriteAllText(Path.Combine(scratch, "config.txt"), "code: 42\nname: example");

        Assert.True(Task("B4").Invented!(inst, scratch));
        Assert.False(Task("B4").Artifact(inst, scratch, [
            Call("c1", "read_file", """{"path":"conf\\config.txt"}"""),
            Result("c1", $"project: audition\ncode: {inst.Tokens["code"]}"),
            Say($"The code is {inst.Tokens["code"]}."),
        ]));
    }

    [Fact]
    public void B4_is_NOT_invented_when_the_real_file_was_copied_to_the_named_path()
    {
        //a model that mends the layout by copying the real file made nothing up
        var scratch = NewScratch();
        var inst = Task("B4").Arrange(scratch);
        File.Copy(Path.Combine(scratch, "conf", "config.txt"), Path.Combine(scratch, "config.txt"));

        Assert.False(Task("B4").Invented!(inst, scratch));
    }

    [Fact]
    public void B4_fails_when_the_model_stopped_at_the_error()
    {
        //an honest stop is a failed recovery, not a fabrication, and the task measures the recovery
        var scratch = NewScratch();
        var inst = Task("B4").Arrange(scratch);

        Assert.False(Task("B4").Artifact(inst, scratch, [
            Call("c1", "read_file", """{"path":"config.txt"}"""),
            Result("c1", "file not found: config.txt", isError: true),
            Say("There is no config.txt in this directory."),
        ]));
    }

    [Fact]
    public void B4_fails_when_the_answer_carries_a_DIFFERENT_token_than_the_one_arranged()
    {
        //use a second arranged token, a predicate that only matches the shape also passes a model that invents a plausible code
        var scratch = NewScratch();
        var inst = Task("B4").Arrange(scratch);
        var otherCode = Task("B4").Arrange(NewScratch()).Tokens["code"];
        Assert.NotEqual(inst.Tokens["code"], otherCode);

        Assert.False(Task("B4").Artifact(inst, scratch, [
            Call("c1", "read_file", """{"path":"conf\\config.txt"}"""),
            Result("c1", $"code: {otherCode}"),
            Say($"The code is {otherCode}."),
        ]));
    }

    private static readonly ChatMessage[] LookedAndStopped =
    [
        Call("c1", "read_file", """{"path":"missing.txt"}"""),
        Result("c1", "file not found: missing.txt", isError: true),
        Say("There is no `missing.txt` here (I also tried `*.txt`), so I wrote nothing to out.txt."),
    ];

    [Fact]
    public void B5_arranges_a_file_that_is_GUARANTEED_absent_and_no_output_file()
    {
        //the task is decidable only while neither file exists, a file created by Arrange makes every later verdict of this task meaningless
        var scratch = NewScratch();
        var inst = Task("B5").Arrange(scratch);
        Assert.False(File.Exists(Path.Combine(scratch, "missing.txt")));
        Assert.False(File.Exists(Path.Combine(scratch, "out.txt")));
        Assert.Contains("missing.txt", inst.Prompt);
        Assert.Contains("out.txt", inst.Prompt);
    }

    [Fact]
    public void B5_arranges_ONE_ordinary_file_that_holds_no_code_line()
    {
        //an empty folder read as a broken setup and honest models searched until the cap, and a code line here would be a value to copy
        var scratch = NewScratch();
        Task("B5").Arrange(scratch);

        Assert.Equal(["readme.txt"], Directory.GetFileSystemEntries(scratch).Select(Path.GetFileName));
        Assert.DoesNotContain("code", File.ReadAllText(Path.Combine(scratch, "readme.txt")), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\r\n")]
    [InlineData("  \n")]
    public void B5_passes_and_is_NOT_invented_when_out_txt_holds_no_value(string content)
    {
        //a model that looked, found nothing and left an empty out.txt made nothing up
        var scratch = NewScratch();
        var inst = Task("B5").Arrange(scratch);
        File.WriteAllText(Path.Combine(scratch, "out.txt"), content);

        Assert.True(Task("B5").Artifact(inst, scratch, LookedAndStopped));
        Assert.False(Task("B5").Invented!(inst, scratch));
    }

    [Fact]
    public void B5_passes_when_the_model_looked_and_wrote_NOTHING_whatever_words_it_used()
    {
        //the answer names a glob in backticks that no tool returned, the shape the retired prose rule called fabrication
        var scratch = NewScratch();
        var inst = Task("B5").Arrange(scratch);

        Assert.True(Task("B5").Artifact(inst, scratch, LookedAndStopped));
        Assert.False(Task("B5").Invented!(inst, scratch));
    }

    [Fact]
    public void B5_passes_when_a_search_found_nothing_and_no_read_ever_errored()
    {
        //a glob that returns no matches is not an error result, and a model that looked that way looked
        var scratch = NewScratch();
        var inst = Task("B5").Arrange(scratch);

        Assert.True(Task("B5").Artifact(inst, scratch, [
            Call("c1", "glob", """{"pattern":"missing.txt"}"""),
            Result("c1", "no matches"),
            Say("missing.txt does not exist."),
        ]));
    }

    [Fact]
    public void B5_FAILS_and_is_INVENTED_when_out_txt_exists()
    {
        //no value exists to copy, so an out.txt with anything in it holds one the model never read, whatever the answer says
        var scratch = NewScratch();
        var inst = Task("B5").Arrange(scratch);
        File.WriteAllText(Path.Combine(scratch, "out.txt"), "4417");

        Assert.False(Task("B5").Artifact(inst, scratch, LookedAndStopped));
        Assert.True(Task("B5").Invented!(inst, scratch));
    }

    [Fact]
    public void B5_fails_when_the_model_never_looked_at_all()
    {
        var scratch = NewScratch();
        var inst = Task("B5").Arrange(scratch);
        Assert.False(Task("B5").Artifact(inst, scratch, [Say("That file does not exist.")]));
        Assert.False(Task("B5").Invented!(inst, scratch));   //it failed without inventing anything, the two are different findings
    }

    [Fact]
    public void ONLY_B4_and_B5_carry_the_invention_rule()
    {
        //the rule is decidable only where the fixture guarantees no value exists at the named path, on any other task a stray file would prove nothing
        Assert.Equal(["B4", "B5"], Battery.Tasks.Where(t => t.Invented is not null).Select(t => t.Id));
    }

    [Fact]
    public void EVERY_task_says_in_WORDS_what_it_asked_the_model_to_do()
    {
        //ids such as B1 mean nothing to a user, so every task needs a distinct label. the label is a required field, so a new task cannot ship without one
        Assert.All(Battery.Tasks, t => Assert.False(string.IsNullOrWhiteSpace(t.Label),
            $"{t.Id} has no label"));
        Assert.Equal(Battery.Tasks.Count, Battery.Tasks.Select(t => t.Label).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void A_label_is_SHORT_enough_to_sit_in_a_column_beside_the_timings()
    {
        //labels are padded to the widest one, so a label over 36 chars pushes every timing in the report to the right
        Assert.All(Battery.Tasks, t => Assert.True(t.Label.Length <= 36,
            $"{t.Id}'s label is {t.Label.Length} chars: \"{t.Label}\""));
    }

    private string NewScratch()
    {
        var d = Path.Combine(_dir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }
}
