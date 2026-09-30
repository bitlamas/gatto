using Gatto.Repl.Render;

namespace Gatto.Tests.Render;

//these tests pin what the argument preview after a tool name shows: identifiers and labelled modifiers, no bare value and no file body
public class CompactArgsTests
{
    private const string Cwd = @"C:\proj";

    [Fact]
    public void EditFile_shows_the_path_and_never_a_fragment_of_the_edit()
    {
        var row = ItemRender.CompactArgs(
            """{"path":"C:\\proj\\temp\\game.html","old_string":"// Input handling\nconst keys = {};","new_string":"// Input handling v2"}""",
            Cwd);

        Assert.Equal(@"temp\game.html", row);
        Assert.DoesNotContain("Input handling", row);
    }

    [Fact]
    public void WriteFile_never_previews_the_content_it_is_about_to_write()
    {
        //file content can hold secrets, so no fragment of it may reach the preview row
        var row = ItemRender.CompactArgs(
            """{"path":"C:\\proj\\.env","content":"API_KEY=super-secret-value"}""", Cwd);

        Assert.Equal(".env", row);
        Assert.DoesNotContain("super-secret-value", row);
    }

    [Fact]
    public void A_path_under_the_working_directory_renders_relative_to_it()
    {
        var row = ItemRender.CompactArgs("""{"path":"C:\\proj\\src\\Repl.cs"}""", Cwd);
        Assert.Equal(@"src\Repl.cs", row);
    }

    [Fact]
    public void A_path_outside_the_working_directory_stays_absolute()
    {
        //the row must never place a file somewhere it is not.
        var row = ItemRender.CompactArgs("""{"path":"C:\\Windows\\System32\\drivers\\etc\\hosts"}""", Cwd);
        Assert.Equal(@"C:\Windows\System32\drivers\etc\hosts", row);
    }

    [Fact]
    public void A_relative_path_is_left_exactly_as_the_model_wrote_it()
    {
        var row = ItemRender.CompactArgs("""{"path":"src/Repl.cs"}""", Cwd);
        Assert.Equal("src/Repl.cs", row);
    }

    [Fact]
    public void Shell_leads_with_the_command()
    {
        var row = ItemRender.CompactArgs("""{"command":"git status --short"}""", Cwd);
        Assert.Equal("git status --short", row);
    }

    //a path inside a command is shortened too (the permission prompt shows the exact string, so approval never comes from this row)

    [Fact]
    public void Shell_relativizes_an_absolute_path_inside_the_command()
    {
        var row = ItemRender.CompactArgs(
            """{"command":"Select-String -Path \"C:\\proj\\repo\\Gatto.Tests\\SlashCommandsTests.cs\" -Pattern \"x\""}""",
            Cwd);

        Assert.Equal(@"Select-String -Path ""repo\Gatto.Tests\SlashCommandsTests.cs"" -Pattern ""x""", row);
    }

    [Fact]
    public void Shell_relativizes_the_forward_slash_spelling_a_model_writes_just_as_often()
    {
        var row = ItemRender.CompactArgs("""{"command":"Get-Content C:/proj/repo/x.cs"}""", Cwd);
        Assert.Equal("Get-Content repo/x.cs", row);
    }

    [Fact]
    public void Shell_leaves_a_path_outside_the_project_absolute()
    {
        //the path rule is the same whether the path is an argument or sits inside a command
        var row = ItemRender.CompactArgs("""{"command":"Get-Content C:\\Windows\\hosts"}""", Cwd);
        Assert.Equal(@"Get-Content C:\Windows\hosts", row);
    }

    [Fact]
    public void Shell_leaves_a_sibling_whose_name_merely_starts_with_the_project_name()
    {
        //only drop the root when a separator follows it, a string replace would also cut a sibling folder's name
        var row = ItemRender.CompactArgs("""{"command":"cat C:\\proj-extra\\x.txt"}""", Cwd);
        Assert.Equal(@"cat C:\proj-extra\x.txt", row);
    }

    [Theory]
    [InlineData(@"C:\proj\")]
    [InlineData(@"cd C:\proj\")]
    [InlineData(@"cd C:\proj\ ; ls")]
    [InlineData(@"Set-Location ""C:\proj\""")]
    [InlineData(@"cd C:\proj\;ls")]
    [InlineData(@"Get-ChildItem C:\proj\|Measure-Object")]
    [InlineData(@"C:\proj")]
    [InlineData(@"cd C:\proj")]
    [InlineData(@"Set-Location ""C:\proj""")]
    [InlineData(@"Get-ChildItem C:\proj -Force")]
    public void Shell_keeps_the_root_itself_whole(string command)
    {
        //dropping the root from the root itself leaves an empty argument that names no target.
        var json = System.Text.Json.JsonSerializer.Serialize(new { command });
        Assert.Equal(command, ItemRender.CompactArgs(json, Cwd));
    }

    [Theory]
    [InlineData(@"cat \\?\C:\proj\x.txt", Cwd)]
    [InlineData(@"cat D:\mirror\C:\proj\x.txt", Cwd)]
    [InlineData("cat /mnt/home/u/proj/x.txt", "/home/u/proj")]
    public void Shell_keeps_a_root_that_sits_inside_another_path(string command, string cwd)
    {
        //when the root sits inside a longer path it belongs to that path, so dropping it invents one
        var json = System.Text.Json.JsonSerializer.Serialize(new { command });
        Assert.Equal(command, ItemRender.CompactArgs(json, cwd));
    }

    [Theory]
    [InlineData(@"Copy-Item C:\proj\a.txt -Destination=c:/PROJ/b.txt", "Copy-Item a.txt -Destination=b.txt")]
    [InlineData(@"Get-ChildItem C:\proj\*.cs", "Get-ChildItem *.cs")]
    public void Shell_relativizes_every_path_that_starts_with_the_root(string command, string expected)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new { command });
        Assert.Equal(expected, ItemRender.CompactArgs(json, Cwd));
    }

    [Fact]
    public void A_shortened_command_still_carries_its_labelled_modifiers()
    {
        var row = ItemRender.CompactArgs("""{"command":"C:\\proj\\build.ps1","timeout_ms":500}""", Cwd);
        Assert.Equal("build.ps1 (500ms)", row);
    }

    [Theory]
    [InlineData("git status --short")]
    [InlineData("dotnet test --filter ThemeTests")]
    [InlineData("ls -la ./repo")]                          //an already relative path stays exactly as written.
    public void A_command_with_no_absolute_path_is_untouched(string command)
        => Assert.Equal(command, ItemRender.CompactArgs(
            $$"""{"command":"{{command}}"}""", Cwd));

    //a path under the user profile and outside the working directory reads from ~
    private const string Profile = @"C:\Users\user";

    [Fact]
    public void A_path_under_the_profile_reads_from_tilde()
        => Assert.Equal(@"~\notes\a.txt",
            ItemRender.CompactArgs("""{"path":"C:\\Users\\user\\notes\\a.txt"}""", Cwd, profile: Profile));

    [Fact]
    public void An_appdata_path_reads_from_tilde_with_no_rule_of_its_own()
        => Assert.Equal(@"~\AppData\Local\gatto\x.log",
            ItemRender.CompactArgs("""{"path":"C:\\Users\\user\\AppData\\Local\\gatto\\x.log"}""", Cwd, profile: Profile));

    [Fact]
    public void A_path_under_a_working_directory_inside_the_profile_stays_relative()
        => Assert.Equal(@"src\a.cs",
            ItemRender.CompactArgs("""{"path":"C:\\Users\\user\\proj\\src\\a.cs"}""", @"C:\Users\user\proj", profile: Profile));

    [Theory]
    [InlineData(@"cd ""C:\Users\user\work 仕事\projects\gatto\docs\spike""; $x = 1", @"cd ""~\work 仕事\projects\gatto\docs\spike""; $x = 1")]
    [InlineData("Get-Content c:/users/USER/notes/a.txt", "Get-Content ~/notes/a.txt")]
    [InlineData(@"Copy-Item C:\Users\user\a.txt C:\Users\user\b.txt", @"Copy-Item ~\a.txt ~\b.txt")]
    [InlineData(@"Get-ChildItem C:\Users\user -Force", "Get-ChildItem ~ -Force")]
    [InlineData(@"cd ""C:\Users\user""", @"cd ""~""")]
    [InlineData(@"cd C:\Users\user", "cd ~")]
    public void Shell_writes_every_path_under_the_profile_from_tilde(string command, string expected)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new { command });
        Assert.Equal(expected, ItemRender.CompactArgs(json, Cwd, profile: Profile));
    }

    [Theory]
    [InlineData(@"cat C:\Users\user2\x.txt")]
    [InlineData(@"cat C:\Users\user.bak\x.txt")]
    [InlineData(@"cat D:\mirror\C:\Users\user\x.txt")]
    public void Shell_keeps_a_path_that_only_looks_like_it_is_under_the_profile(string command)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new { command });
        Assert.Equal(command, ItemRender.CompactArgs(json, Cwd, profile: Profile));
    }

    [Theory]
    [InlineData("""{"path":"C:\\Users\\user\\notes\\a.txt"}""", @"C:\Users\user\notes\a.txt")]
    [InlineData("""{"command":"ls \\temp"}""", @"ls \temp")]
    public void An_unknown_profile_changes_nothing(string json, string expected)
        => Assert.Equal(expected, ItemRender.CompactArgs(json, Cwd, profile: ""));

    [Fact]
    public void EarlyPreview_reads_a_path_under_the_profile_from_tilde()
        => Assert.Equal(@"~\a.txt",
            ItemRender.EarlyArgsPreview("""{"path":"C:\\Users\\user\\a.txt","x":1""", profile: Profile));

    [Fact]
    public void ReadFile_turns_offset_and_limit_into_the_line_range_they_mean()
    {
        //a read window is one-based and inclusive (Skip(offset - 1).Take(limit)), so 240 with 50 is lines 240 to 289
        var row = ItemRender.CompactArgs(
            """{"path":"C:\\proj\\temp\\game.html","offset":240,"limit":50}""", Cwd);

        Assert.Equal(@"temp\game.html (lines 240–289)", row);
    }

    [Theory]
    [InlineData("""{"path":"a.txt","offset":10}""", "a.txt (from line 10)")]
    [InlineData("""{"path":"a.txt","limit":20}""", "a.txt (first 20 lines)")]
    public void A_half_specified_window_still_says_what_it_means(string json, string expected)
        => Assert.Equal(expected, ItemRender.CompactArgs(json, Cwd));

    [Fact]
    public void WebSearch_labels_the_result_count_instead_of_trailing_a_bare_number()
    {
        var row = ItemRender.CompactArgs(
            """{"query":"latest terrorist attack Germany 2026","count":5}""", Cwd);

        Assert.Equal("latest terrorist attack Germany 2026 (×5)", row);
    }

    [Fact]
    public void An_unknown_key_is_labelled_rather_than_dropped_or_left_bare()
    {
        //an extension can register any tool, so every unknown key still gets its name beside the value
        var row = ItemRender.CompactArgs("""{"query":"cats","safe_mode":true,"lang":"fr"}""", Cwd);

        Assert.Equal("cats (safe_mode true, lang fr)", row);
    }

    [Fact]
    public void Grep_labels_its_root_and_glob()
    {
        var row = ItemRender.CompactArgs(
            """{"pattern":"TODO","root":"C:\\proj\\src","glob":"*.cs"}""", Cwd);

        Assert.Equal(@"TODO (in src, glob *.cs)", row);
    }

    [Fact]
    public void A_tool_with_no_known_identifier_still_leads_with_something()
    {
        //with no known identifier key, the first scalar leads so the row is not just a parenthesised list
        var row = ItemRender.CompactArgs("""{"topic":"taxes","limit_words":50}""", Cwd);

        Assert.Equal("taxes (limit_words 50)", row);
    }

    [Fact]
    public void A_promoted_fallback_whose_label_does_not_end_in_its_value_does_not_crash_the_renderer()
    {
        //timeout_ms renders as 500ms, the one label that does not end in its own value. a search for the value would miss it, so track its index instead
        var row = ItemRender.CompactArgs("""{"timeout_ms":500}""", Cwd);

        Assert.NotNull(row);
        Assert.Contains("500", row);
    }

    [Fact]
    public void Nested_objects_and_arrays_are_dropped_rather_than_rendered_as_an_ellipsis()
    {
        var row = ItemRender.CompactArgs(
            """{"query":"cats","filters":{"a":1},"tags":["x","y"]}""", Cwd);

        Assert.Equal("cats", row);
    }

    [Fact]
    public void No_arguments_produces_an_empty_preview_not_a_stray_bracket()
    {
        Assert.Equal("", ItemRender.CompactArgs("{}", Cwd));
        Assert.Equal("", ItemRender.CompactArgs("", Cwd));
    }

    [Fact]
    public void Malformed_json_falls_back_to_the_raw_text_rather_than_throwing()
    {
        //a partially assembled or malformed tool call must never take the renderer down.
        var row = ItemRender.CompactArgs("""{"path":"a.txt",""", Cwd);
        Assert.Contains("a.txt", row);
    }

    [Fact]
    public void EarlyPreview_shows_the_path_as_soon_as_its_string_has_closed()
    {
        //the path arrives first even while a long tool call keeps streaming, so the preview can show it early.
        var row = ItemRender.EarlyArgsPreview("""{"path":"a.txt","old_string":"// Input han""");
        Assert.Equal("a.txt", row);
    }

    [Fact]
    public void EarlyPreview_says_nothing_while_the_path_is_still_arriving()
    {
        //a half-written path on the row is worse than no arguments at all.
        Assert.Equal("", ItemRender.EarlyArgsPreview("""{"path":"C:\\pro"""));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{")]                                  //the first fragment of a real streamed call
    [InlineData("""{"query":"cats"}""")]              //the input holds no path key.
    [InlineData("""{"path":""")]                      //the key is seen but its value has not started.
    [InlineData("""{"path":"a\\""")]                  //the value is cut off in the middle of an escape.
    public void EarlyPreview_is_silent_rather_than_wrong(string? partial)
        => Assert.Equal("", ItemRender.EarlyArgsPreview(partial));

    [Fact]
    public void EarlyPreview_decodes_escapes_so_a_windows_path_reads_correctly()
    {
        var row = ItemRender.EarlyArgsPreview("""{"path":"C:\\other\\game.html","x":1""");
        Assert.Equal(@"C:\other\game.html", row);
    }

    [Fact]
    public void A_long_command_keeps_its_whole_length_and_only_a_runaway_is_bounded()
    {
        //only the truncation at the window edge bounds the row, so a long command stays whole
        var command = string.Join(" ", Enumerable.Range(0, 40).Select(i => $"arg{i:D3}"));
        Assert.Equal(command, ItemRender.CompactArgs(System.Text.Json.JsonSerializer.Serialize(new { command }), Cwd));

        var runaway = ItemRender.CompactArgs($$"""{"command":"{{new string('x', 10000)}}"}""", Cwd);
        var cells = Gatto.Terminal.UnicodeWidth.Of(runaway);
        Assert.True(cells <= ItemRender.ArgsPreviewCells && cells > 1000, $"a runaway argument became {cells} cells");
    }
}
