using System.Text.Json;
using Gatto.Core.Tools;

namespace Gatto.Tests;

public sealed class ShellOutputTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gatto-test-").FullName;
    private IToolContext Ctx => new TestToolContext(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch (Exception) { } }

    private async Task<ToolResult> Run(string command) =>
        await new ShellTool().ExecuteAsync(JsonSerializer.SerializeToElement(new { command }), Ctx, default);

    //the round trips run the real tool, so the CRLF line ends it writes are what the parser meets
    [Fact]
    public async Task Stdout_only_parses_to_stdout_lines()
    {
        var o = ShellOutput.Parse((await Run("'one'; 'two'")).Text);
        Assert.Equal(new[] { "one", "two" }, o.Stdout);
        Assert.Empty(o.Stderr);
        Assert.Empty(o.Harness);
    }

    [Fact]
    public async Task Stdout_and_stderr_split_at_the_marker_line()
    {
        var r = await Run("'out'; [Console]::Error.WriteLine('err one'); [Console]::Error.WriteLine('err two'); exit 1");
        Assert.Contains("\r\n" + ShellTool.StderrMarker + "\r\n", r.Text, StringComparison.Ordinal);
        var o = ShellOutput.Parse(r.Text);
        Assert.Equal(new[] { "out" }, o.Stdout);
        Assert.Equal(new[] { "err one", "err two" }, o.Stderr);
        Assert.Equal("err two", o.LastStderr);
        Assert.Equal(1, ShellOutput.ExitCodeOf(r.Gloss));
    }

    //the result row shows the message of a PowerShell error record, trimmed, and not the record's indented id lines
    [Fact]
    public void The_error_line_skips_a_PowerShell_record_and_trims()
    {
        var o = new ShellOutput(Array.Empty<string>(), new[]
        {
            "Get-Item : Cannot find path 'C:\\x' because it does not exist.",
            "At line:1 char:1",
            "+ Get-Item C:\\x",
            "+ ~~~~~~~~~~~~~",
            "    + CategoryInfo          : ObjectNotFound: (C:\\x:String) [Get-Item], ItemNotFoundException",
            "    + FullyQualifiedErrorId : PathNotFound,Microsoft.PowerShell.Commands.GetItemCommand",
            "",
        }, Array.Empty<string>());
        Assert.Equal("Get-Item : Cannot find path 'C:\\x' because it does not exist.", o.ErrorLine);
        Assert.Equal("boom", new ShellOutput(Array.Empty<string>(), new[] { "   boom  " }, Array.Empty<string>()).ErrorLine);
        Assert.Equal("+ only", new ShellOutput(Array.Empty<string>(), new[] { "  + only" }, Array.Empty<string>()).ErrorLine);
        Assert.Null(ShellOutput.Empty.ErrorLine);
    }

    //the same through the real tool, so the record lines are the ones this machine's PowerShell writes
    [Fact]
    public async Task A_real_PowerShell_error_gives_its_message_line()
    {
        var o = ShellOutput.Parse((await Run("Get-Item C:\\no_such_dir_for_gatto_tests\\x")).Text);
        Assert.NotNull(o.ErrorLine);
        Assert.False(o.ErrorLine!.StartsWith('+'), o.ErrorLine);
        Assert.False(o.ErrorLine.StartsWith("At line", StringComparison.Ordinal), o.ErrorLine);
        Assert.Equal(o.ErrorLine.Trim(), o.ErrorLine);
    }

    [Fact]
    public async Task Stderr_only_leaves_a_blank_stdout()
    {
        var o = ShellOutput.Parse((await Run("[Console]::Error.WriteLine('boom'); exit 2")).Text);
        Assert.True(o.StdoutBlank);
        Assert.Equal(new[] { "boom" }, o.Stderr);
    }

    [Fact]
    public async Task A_non_ascii_line_survives_the_round_trip()
    {
        var o = ShellOutput.Parse((await Run("'caf' + [char]0xE9")).Text);
        Assert.Equal("caf\u00e9", o.Stdout.Single());
    }

    [Fact]
    public void The_exit_line_is_removed_and_the_harness_lines_are_kept()
    {
        var text = "out\r\n" + ShellTool.StderrMarker + "\r\nerr\r\n" + ShellTool.PipeHeldNote + "\r\n(exit code 3)";
        var o = ShellOutput.Parse(text);
        Assert.Equal(new[] { "out" }, o.Stdout);
        Assert.Equal(new[] { "err" }, o.Stderr);
        Assert.Equal(new[] { ShellTool.PipeHeldNote }, o.Harness);
    }

    [Fact]
    public void A_head_capped_text_keeps_its_truncation_line_as_harness()
    {
        var o = ShellOutput.Parse("line one\r\nline tw\r\n" + ToolArgs.TruncatedMarker);
        Assert.Equal(new[] { "line one", "line tw" }, o.Stdout);
        Assert.Equal(new[] { ToolArgs.TruncatedMarker }, o.Harness);
    }

    [Fact]
    public void A_marker_printed_by_stdout_splits_there()
    {
        var o = ShellOutput.Parse("a\n" + ShellTool.StderrMarker + "\nb");
        Assert.Equal(new[] { "a" }, o.Stdout);
        Assert.Equal(new[] { "b" }, o.Stderr);
    }

    [Fact]
    public void A_text_with_no_marker_is_all_stdout()
    {
        var o = ShellOutput.Parse("rewritten by a hook\nsecond line");
        Assert.Equal(2, o.Stdout.Count);
        Assert.Empty(o.Stderr);
    }

    [Theory]
    [InlineData("exit 0", 0)]
    [InlineData("exit 1", 1)]
    [InlineData("exit -1073741510", -1073741510)]
    public void The_exit_code_comes_from_the_gloss(string gloss, int code) => Assert.Equal(code, ShellOutput.ExitCodeOf(gloss));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("exit")]
    [InlineData("exited 1")]
    [InlineData("1 match")]
    public void A_missing_or_foreign_gloss_has_no_exit_code(string? gloss) => Assert.Null(ShellOutput.ExitCodeOf(gloss));

    [Theory]
    [InlineData("bash: line 13: /dev/null\r: Permission denied", "bash: line 13: /dev/null: Permission denied")]
    [InlineData("  0%\r 10%\r100%", "100%")]
    [InlineData("downloading 10%\rdownloading 100%\rdone", "downloading 10%downloading 100%done")]
    [InlineData("plain", "plain")]
    [InlineData("a\r", "a")]
    public void A_cr_inside_a_line_resolves_to_a_redraw_or_a_join(string line, string expected) =>
        Assert.Equal(expected, ShellOutput.ResolveCr(line));
}
