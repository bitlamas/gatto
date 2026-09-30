using System.Collections.Generic;
using System.Linq;
using Gatto.Repl;
using Xunit;

namespace Gatto.Tests;

//the pure half of slash-command completion: no rendering, no keys, no state. the cycle position is passed in.
public class CommandHintTests
{
    private static CommandCompletion At(string line) =>
        CommandHint.For(new List<string> { line }, 0, line.Length);

    [Fact]
    public void Every_command_completes_from_a_prefix_that_is_unique()
    {
        //the hint is the rest of the name plus the table's own Args
        foreach (var cmd in SlashCommands.All)
        {
            var typed = cmd.Name[..2];
            var c = At(typed);
            Assert.Contains(cmd, c.Candidates);
            var i = c.Candidates.ToList().IndexOf(cmd);
            var expected = cmd.Args.Length == 0 ? cmd.Name[2..] : cmd.Name[2..] + " " + cmd.Args;
            Assert.Equal(expected, c.Hint(i));
            Assert.Equal(cmd.Name, c.Completion(i));         //the completion is the name only
        }
    }

    [Fact]
    public void A_fully_typed_name_still_answers_with_its_arguments()
    {
        var c = At("/model");
        var only = Assert.Single(c.Candidates);
        Assert.Equal("/model", only.Name);
        Assert.Equal(" [name|quant]", c.Hint(0));   //the hint is the empty name remainder then the args.
        Assert.Equal("/model", c.Completion(0));
    }

    [Fact]
    public void A_command_that_takes_nothing_hints_only_the_name_remainder()
    {
        var c = At("/qu");
        Assert.Equal("it", c.Hint(0));
        Assert.Equal("/quit", c.Completion(0));
    }

    //pin the order here, since a table edit could silently reorder what Tab shows
    [Fact]
    public void The_one_ambiguous_prefix_answers_both_in_table_order()
    {
        var c = At("/r");
        Assert.Equal(new[] { "/role", "/remember" }, c.Candidates.Select(x => x.Name).ToArray());
        Assert.Equal("ole [name]", c.Hint(0));
        Assert.Equal("emember <text>", c.Hint(1));
        Assert.Equal("/role", c.Completion(0));
        Assert.Equal("/remember", c.Completion(1));
    }

    [Fact]
    public void The_cycle_wraps_in_both_directions()
    {
        var c = At("/r");
        Assert.Equal("/role", c.Completion(2));       //index 2 wraps back to the first candidate.
        Assert.Equal("/remember", c.Completion(3));
        Assert.Equal("/remember", c.Completion(-1));  //a negative index wraps, so a reverse key can never fall out of range.
    }

    //a bare slash answers the whole table, with no special case. the first hint is how a user discovers the key exists.
    [Fact]
    public void A_bare_slash_answers_every_command()
    {
        var c = At("/");
        Assert.Equal(SlashCommands.All.Count, c.Candidates.Count);
        Assert.Equal(SlashCommands.All[0].Name, c.Completion(0));
    }

    //the escape hatch must never complete as a command. no name begins with it today, so this check guards a future matcher
    [Fact]
    public void The_escape_hatch_is_never_a_completion()
    {
        Assert.False(At("//").Any);
        Assert.False(At("//m").Any);
        Assert.Equal("", At("//m").Hint(0));
        Assert.Equal("", At("//m").Completion(0));   //there is nothing to insert, and LineEditor checks Any first
    }

    [Fact]
    public void Nothing_answers_once_the_line_is_past_the_name()
    {
        Assert.False(At("/model ").Any);            //after the name, the command handles its own arguments.
        Assert.False(At("/model add").Any);
    }

    [Fact]
    public void Nothing_answers_for_ordinary_text_or_an_unknown_command()
    {
        Assert.False(At("hello").Any);
        Assert.False(At("").Any);
        Assert.False(At("/zz").Any);
        Assert.False(At("/Model").Any);              //the dispatcher's matcher is ordinal, and so is this one.
    }

    //the cursor must be at the end. completing behind the caret would insert text whose end the user cannot see.
    [Fact]
    public void Nothing_answers_when_the_cursor_is_not_at_the_end()
    {
        Assert.False(CommandHint.For(new List<string> { "/model" }, 0, 3).Any);
        Assert.False(CommandHint.For(new List<string> { "/model" }, 0, 0).Any);
    }

    //only a one-line buffer completes, since a multi-line buffer is a message
    [Fact]
    public void Nothing_answers_for_a_multi_line_buffer()
    {
        Assert.False(CommandHint.For(new List<string> { "/help", "more" }, 0, 5).Any);
        Assert.False(CommandHint.For(new List<string> { "/help", "more" }, 1, 4).Any);
        Assert.False(CommandHint.For(null, 0, 0).Any);
    }
}
