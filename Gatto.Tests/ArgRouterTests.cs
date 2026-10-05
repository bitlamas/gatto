using Gatto.Cli;

namespace Gatto.Tests;

public class ArgRouterTests
{
    [Fact]
    public void Bare_gatto_is_generalist_launch()
    {
        var p = ArgRouter.Parse(Array.Empty<string>());
        Assert.Equal("launch", p.Command);
        Assert.Equal("generalist", p.Role);
    }

    [Fact]
    public void Mode_model_prompt_continue_parse()
    {
        var p = ArgRouter.Parse(new[] { "coder", "-m", "qwen3.6-35b", "-p", "fix it", "--continue" });
        Assert.Equal("coder", p.Role);
        Assert.Equal("qwen3.6-35b", p.Model);
        Assert.Equal("fix it", p.Prompt);
        Assert.True(p.Continue);
    }

    [Fact]
    public void Serve_start_is_foreground_by_default_and_detach_is_opt_in()
    {
        Assert.False(ArgRouter.Parse(new[] { "serve", "start", "deepseek-v4-flash" }).Detach);
        Assert.True(ArgRouter.Parse(new[] { "serve", "start", "deepseek-v4-flash", "--detach" }).Detach);
        Assert.True(ArgRouter.Parse(new[] { "serve", "start", "deepseek-v4-flash", "-d" }).Detach);

        //don't read --detach as the model target
        var p = ArgRouter.Parse(new[] { "serve", "start", "deepseek-v4-flash", "--detach" });
        Assert.Equal("start", p.Subcommand);
        Assert.Equal("deepseek-v4-flash", p.Target);
    }

    [Fact]
    public void Reserved_commands_route()
    {
        Assert.Equal("serve", ArgRouter.Parse(new[] { "serve", "start" }).Command);
        Assert.Equal("doctor", ArgRouter.Parse(new[] { "doctor" }).Command);
    }

    //the probe spawns gatto with this word, so it must stay reserved (a role file named after it would hijack that launch)
    [Fact]
    public void The_probe_word_is_reserved_and_never_a_role_name()
    {
        var a = ArgRouter.Parse(new[] { Gatto.Core.Hardware.HardwareProbe.ChildWord });
        Assert.Equal(Gatto.Core.Hardware.HardwareProbe.ChildWord, a.Command);
        Assert.Equal("generalist", a.Role);
    }

    //roles are user-editable files, so a bare unknown token parses as a role name rather than a usage error
    [Fact]
    public void Bare_unknown_token_becomes_role_name()
    {
        var p = ArgRouter.Parse(new[] { "frobnicate" });
        Assert.Equal("launch", p.Command);
        Assert.Equal("frobnicate", p.Role);
    }

    [Fact]
    public void Yes_and_auto_flags_parse()
    {
        var p = ArgRouter.Parse(new[] { "coder", "--yes", "--auto", "-p", "go" });
        Assert.True(p.Yes);
        Assert.True(p.Auto);
        Assert.Equal("go", p.Prompt);
    }

    [Fact]
    public void Yes_and_auto_default_false()
    {
        var p = ArgRouter.Parse(new[] { "coder", "-p", "go" });
        Assert.False(p.Yes);
        Assert.False(p.Auto);
    }

    [Theory]
    [InlineData(new[] { "-p" }, "missing value")]
    [InlineData(new[] { "coder", "--wat" }, "unknown option")]
    public void Bad_input_throws(string[] argv, string expect)
    {
        var ex = Assert.Throws<ArgumentException>(() => ArgRouter.Parse(argv));
        Assert.Contains(expect, ex.Message);
    }

    //the prompt flag takes the prompt right after it, so an option there is a usage error rather than a prompt, and the real prompt never reads as an option
    [Theory]
    [InlineData("-p")]
    [InlineData("--prompt")]
    public void A_prompt_flag_followed_by_an_option_is_a_usage_error(string flag)
    {
        var ex = Assert.Throws<ArgumentException>(() => ArgRouter.Parse([flag, "--yes", "list the files"]));
        Assert.Contains($"{flag} needs the prompt right after it", ex.Message);
        Assert.Contains($"{flag}=", ex.Message);
    }

    //the joined form carries a prompt that starts with two dashes, and the rest of the line still parses
    [Theory]
    [InlineData("-p=--dry-run explained", "--dry-run explained")]
    [InlineData("--prompt=--dry-run explained", "--dry-run explained")]
    [InlineData("-p=plain words", "plain words")]
    public void The_joined_form_passes_any_prompt(string arg, string expected)
    {
        var p = ArgRouter.Parse([arg, "--yes"]);

        Assert.Equal(expected, p.Prompt);
        Assert.True(p.Yes);
    }

    [Fact]
    public void Serve_start_with_model_target_parses_subcommand_and_target()
    {
        var p = ArgRouter.Parse(new[] { "serve", "start", "qwen3.6-35b" });
        Assert.Equal("serve", p.Command);
        Assert.Equal("start", p.Subcommand);
        Assert.Equal("qwen3.6-35b", p.Target);
    }

    [Fact]
    public void Serve_stop_has_subcommand_and_no_target()
    {
        var p = ArgRouter.Parse(new[] { "serve", "stop" });
        Assert.Equal("stop", p.Subcommand);
        Assert.Null(p.Target);
    }

    [Fact]
    public void Serve_status_parses()
    {
        var p = ArgRouter.Parse(new[] { "serve", "status" });
        Assert.Equal("status", p.Subcommand);
    }

    [Fact]
    public void Serve_with_no_subcommand_defaults_to_status()
    {
        var p = ArgRouter.Parse(new[] { "serve" });
        Assert.Equal("serve", p.Command);
        Assert.Equal("status", p.Subcommand);
        Assert.Null(p.Target);
    }

    [Fact]
    public void Serve_unknown_subcommand_is_a_usage_error_listing_valid_ones()
    {
        var ex = Assert.Throws<ArgumentException>(() => ArgRouter.Parse(new[] { "serve", "frobnicate" }));
        Assert.Contains("frobnicate", ex.Message);
        Assert.Contains("start", ex.Message);
        Assert.Contains("stop", ex.Message);
        Assert.Contains("status", ex.Message);
    }

    [Fact]
    public void Serve_status_json_flag_parses()
    {
        var p = ArgRouter.Parse(new[] { "serve", "status", "--json" });
        Assert.Equal("status", p.Subcommand);
        Assert.True(p.Json);
    }

    [Fact]
    public void Serve_status_json_short_flag_parses()
    {
        Assert.True(ArgRouter.Parse(new[] { "serve", "status", "-j" }).Json);
    }

    [Fact]
    public void Serve_status_bare_leaves_json_false()
    {
        Assert.False(ArgRouter.Parse(new[] { "serve", "status" }).Json);
        Assert.False(ArgRouter.Parse(new[] { "serve" }).Json);
    }

    [Fact]
    public void Parse_ModelNew_WithPath()
    {
        var args = ArgRouter.Parse(["model", "new", @"C:\models\a.gguf"]);
        Assert.Equal("model", args.Command);
        Assert.Equal("new", args.Subcommand);
        Assert.Equal(@"C:\models\a.gguf", args.Target);
    }

    [Fact]
    public void Parse_ModelNew_BareIsAllowed()
    {
        var args = ArgRouter.Parse(["model", "new"]);
        Assert.Equal("new", args.Subcommand);
        Assert.Null(args.Target);
    }

    //the router parses every token after model, and the runner accepts only new alone (which opens the shelf)


    [Fact]
    public void Parse_ModelNew_WithAPath()
    {
        var args = ArgRouter.Parse(["model", "new", @"C:\models\a.gguf", "qwen3.6-27b"]);
        Assert.Equal("model", args.Command);
        Assert.Equal("new", args.Subcommand);
        Assert.Equal(@"C:\models\a.gguf", args.Target);
    }

    [Fact]
    public void Continue_bare_has_null_id()
    {
        var p = ArgRouter.Parse(["--continue"]);
        Assert.True(p.Continue);
        Assert.Null(p.ContinueId);
    }

    [Fact]
    public void Continue_with_id_captures_it()
    {
        var p = ArgRouter.Parse(["--continue", "abc123"]);
        Assert.True(p.Continue);
        Assert.Equal("abc123", p.ContinueId);
    }

    [Fact]
    public void Continue_does_not_swallow_a_following_option_as_the_id()
    {
        var p = ArgRouter.Parse(["--continue", "-p", "hi"]);
        Assert.True(p.Continue);
        Assert.Null(p.ContinueId);
        Assert.Equal("hi", p.Prompt);
    }

    [Fact]
    public void Continue_as_the_last_token_has_null_id()
    {
        var p = ArgRouter.Parse(["-p", "hi", "--continue"]);
        Assert.True(p.Continue);
        Assert.Null(p.ContinueId);
        Assert.Equal("hi", p.Prompt);
    }

    [Fact]
    public void THE_ENDPOINT_FLAG_TAKES_A_NAME_IN_BOTH_FORMS()
    {
        var p = ArgRouter.Parse(["-e", "openrouter", "-m", "nex-agi/nex-n2.5-pro:free", "-p", "hi"]);
        Assert.Equal("openrouter", p.Endpoint);
        Assert.Equal("nex-agi/nex-n2.5-pro:free", p.Model);
        Assert.Equal("hi", p.Prompt);
        Assert.Equal("local", ArgRouter.Parse(["coder", "--endpoint", "local"]).Endpoint);
        Assert.Null(ArgRouter.Parse(["-p", "hi"]).Endpoint);
    }

    //the flag has no bare form, so a missing name is a usage error like -m without a model
    [Theory]
    [InlineData("-e")]
    [InlineData("--endpoint")]
    public void THE_ENDPOINT_FLAG_WITHOUT_A_NAME_IS_A_USAGE_ERROR(string flag)
    {
        var ex = Assert.Throws<ArgumentException>(() => ArgRouter.Parse(["-p", "hi", flag]));
        Assert.Equal($"missing value for {flag}", ex.Message);
    }

    //the level parses wherever the flag sits on the line, and no flag leaves the role's level in charge
    [Fact]
    public void THE_EFFORT_FLAG_TAKES_A_LEVEL_BEFORE_OR_AFTER_THE_PROMPT()
    {
        Assert.Equal(Gatto.Roles.ThinkingLevel.High, ArgRouter.Parse(["--effort", "high", "-p", "hi"]).Effort);
        Assert.Equal(Gatto.Roles.ThinkingLevel.Max, ArgRouter.Parse(["-e", "openrouter", "-p", "hi", "--effort", "max"]).Effort);
        Assert.Null(ArgRouter.Parse(["-p", "hi"]).Effort);
    }

    [Fact]
    public void THE_EFFORT_FLAG_WITHOUT_A_LEVEL_IS_A_USAGE_ERROR()
    {
        var ex = Assert.Throws<ArgumentException>(() => ArgRouter.Parse(["-p", "hi", "--effort"]));
        Assert.Equal("missing value for --effort", ex.Message);
    }

    //a level the thinking map cannot name is refused before anything launches, with the valid names in the message
    [Fact]
    public void AN_UNKNOWN_EFFORT_LEVEL_IS_A_USAGE_ERROR()
    {
        var ex = Assert.Throws<ArgumentException>(() => ArgRouter.Parse(["--effort", "extreme", "-p", "hi"]));
        Assert.Contains("invalid thinking level 'extreme'", ex.Message);
    }

    //the REPL already has /effort, so the flag is for one-shot runs only and a launch without -p must say so
    [Fact]
    public void THE_EFFORT_FLAG_WITHOUT_A_PROMPT_IS_A_USAGE_ERROR()
    {
        var ex = Assert.Throws<ArgumentException>(() => ArgRouter.Parse(["--effort", "high"]));
        Assert.Contains("-p", ex.Message);
        Assert.Contains("/effort", ex.Message);
    }

    //bare words act and flags modify or inform, so replacing the binary is a bare command
    [Fact]
    public void UPDATE_IS_A_COMMAND_not_a_role()
        => Assert.Equal("update", ArgRouter.Parse(["update"]).Command);

    //the bare command is the only update form, the --update flag must throw
    [Fact]
    public void DASH_DASH_UPDATE_IS_NOT_A_COMMAND()
        => Assert.Throws<ArgumentException>(() => ArgRouter.Parse(["--update"]));

    //a non-reserved bare token becomes a role name, so update must stay in Reserved (a role file named update.json would otherwise launch the REPL)
    [Fact]
    public void UPDATE_CANNOT_BE_SHADOWED_BY_A_ROLE_FILE()
        => Assert.NotEqual("launch", ArgRouter.Parse(["update"]).Command);

    //a help flag after a reserved command prints help, running the command instead costs minutes on audition
    [Theory]
    [InlineData("audition", "--help")]
    [InlineData("audition", "-h")]
    [InlineData("serve", "--help")]
    [InlineData("doctor", "-h")]
    public void A_HELP_FLAG_AFTER_A_RESERVED_COMMAND_IS_HELP(string command, string flag)
        => Assert.Equal("help", ArgRouter.Parse([command, flag]).Command);

    [Fact]
    public void A_HELP_FLAG_AFTER_A_TARGET_IS_STILL_HELP()
        => Assert.Equal("help", ArgRouter.Parse(["audition", "some-model", "--help"]).Command);
}
