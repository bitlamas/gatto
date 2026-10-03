using System;
using System.Collections.Generic;
using System.Linq;
using Gatto.Core.Loop.Permissions;
using Gatto.Repl;
using Xunit;

namespace Gatto.Tests;

//these check the prompt words through pure oracles, and a probe uses StringComparison.Ordinal, since collation treats a control byte as weightless
public class PromptTitlesTests
{
    private const string Cwd = @"C:\proj";

    //flattens the structured spans back to plain words, so every oracle below reads what the panel says
    private static (string Title, string Question, IReadOnlyList<string> Detail) For(
        PermissionRequest r, string? provider = null)
    {
        var (title, question, detail) = PromptTitles.For(r, provider, Cwd);
        return (title.Text + title.Code, question.Text + question.Code + question.After,
            detail.Select(d => (d.Gutter ?? "") + d.Text).ToList());
    }

    [Fact]
    public void WriteFile_TitleAndPreview_CarryTheirSpans()
    {
        //pin the path as a code span, the preview numbers as a dim gutter and the elision row as dim throughout
        var (title, _, detail) = PromptTitles.For(new PermissionRequest(
            "write_file", @"C:\proj\sub\a.txt (+42 lines)", @"C:\proj",
            PreviewLines: new[] { "a", "b" }, PreviewTotalLines: 42), null, Cwd);

        Assert.Equal("Write new file ", title.Text);
        Assert.Equal(@"sub\a.txt", title.Code);
        Assert.Equal("1  ", detail[0].Gutter);
        //the question shows the file name as a code span too.
        var (_, question, _) = PromptTitles.For(new PermissionRequest(
            "write_file", @"C:\proj\sub\a.txt (+42 lines)", @"C:\proj",
            PreviewLines: new[] { "a" }, PreviewTotalLines: 42), null, Cwd);
        Assert.Equal("Do you want to write new file ", question.Text);
        Assert.Equal("a.txt", question.Code);
        Assert.Equal("?", question.After);
        Assert.False(detail[0].Dim);
        Assert.True(detail[^1].Dim);
        Assert.Equal("… +40 lines", detail[^1].Text);
    }

    //a write over a file says overwrite and puts what it replaces in warn ink above the preview
    [Fact]
    public void A_write_over_a_file_says_overwrite_and_what_it_replaces()
    {
        var request = new PermissionRequest("write_file", @"C:\proj\sub\a.txt (+2 lines)", @"C:\proj",
            PreviewLines: new[] { "x", "y" }, PreviewTotalLines: 2, Existing: new ExistingFile(40, 900));
        var (title, question, detail) = PromptTitles.For(request, null, Cwd);

        Assert.Equal(("Overwrite file ", @"sub\a.txt"), (title.Text, title.Code));
        Assert.Equal(("Do you want to overwrite ", "a.txt", "?"), (question.Text, question.Code, question.After));
        Assert.Equal("replaces 40 lines", detail[0].Text);
        Assert.True(detail[0].Warn);
        Assert.Equal(("1  ", "x"), (detail[1].Gutter, detail[1].Text));

        var binary = PromptTitles.For(request with { Existing = new ExistingFile(null, 1) }, null, Cwd).Detail;
        Assert.Equal("replaces 1 byte", binary[0].Text);
        var unread = PromptTitles.For(request with { Existing = new ExistingFile(null, null) }, null, Cwd);
        Assert.Equal("Overwrite file ", unread.Title.Text);
        Assert.Equal("1  ", unread.Detail[0].Gutter);
    }

    //with room for one detail row an overwrite keeps its warn row, the fact that the file is replaced outranks the count of hidden lines
    [Fact]
    public void A_one_row_cap_keeps_the_overwrite_warning()
    {
        var request = new PermissionRequest("write_file", @"C:\proj\a.txt (+8 lines)", @"C:\proj",
            PreviewLines: new[] { "a", "b", "c", "d", "e" }, PreviewTotalLines: 8, Existing: new ExistingFile(40, 900));
        var detail = PromptTitles.For(request, null, Cwd).Detail;
        var one = PromptTitles.DetailAt(request, detail)(1);
        Assert.Equal(new[] { "replaces 40 lines" }, one.Select(d => d.Text));
        var three = PromptTitles.DetailAt(request, detail)(3);
        Assert.Equal(new[] { "replaces 40 lines", "a", "… +7 lines" }, three.Select(d => d.Text));
    }

    private static readonly char Esc = Convert.ToChar(0x1B);

    [Fact]
    public void WriteFile_TitleAndQuestion()
    {
        var (title, question, detail) = For(new PermissionRequest(
            "write_file", @"C:\proj\sub\a.txt (+2 lines)", @"C:\proj",
            PreviewLines: new[] { "using System;", "" }, PreviewTotalLines: 2));

        Assert.Equal(@"Write new file sub\a.txt", title);
        Assert.Equal("Do you want to write new file a.txt?", question);
        Assert.Equal(new[] { "1  using System;", "2  " }, detail);
    }

    [Fact]
    public void EditFile_TitleAndQuestion()
    {
        var (title, question, _) = For(new PermissionRequest(
            "edit_file", @"C:\proj\sub\a.txt (340 bytes)", @"C:\proj",
            PreviewLines: new[] { "x" }, PreviewTotalLines: 1));

        Assert.Equal(@"Edit file sub\a.txt", title);
        Assert.Equal("Do you want to edit a.txt?", question);
    }

    //a count of 1 gives the singular noun, so the suffix must still come off a one-line write and a 1-byte edit
    [Theory]
    [InlineData("write_file", @"C:\proj\sub\a.txt (+1 line)", @"Write new file sub\a.txt", "Do you want to write new file a.txt?")]
    [InlineData("edit_file", @"C:\proj\sub\a.txt (1 byte)", @"Edit file sub\a.txt", "Do you want to edit a.txt?")]
    public void A_singular_sizing_suffix_stays_out_of_the_title(string tool, string summary, string wantTitle, string wantQuestion)
    {
        var (title, question, _) = For(new PermissionRequest(tool, summary, @"C:\proj"));

        Assert.Equal(wantTitle, title);
        Assert.Equal(wantQuestion, question);
    }

    //strip the sizing suffix only when the text really ends with one, so a plain path keeps its title
    [Fact]
    public void WriteFile_NoSizingSuffix_TitleIsStillJustThePath()
    {
        var (title, question, _) = For(new PermissionRequest(
            "write_file", @"C:\proj\a.txt", @"C:\proj"));

        Assert.Equal("Write new file a.txt", title);
        Assert.Equal("Do you want to write new file a.txt?", question);
    }

    //the tail row reads … +N lines, where N counts only the lines the user cannot see and the total comes from the request
    [Fact]
    public void WriteFile_PreviewIsNumbered_WithAnElisionTail()
    {
        var (_, _, detail) = For(new PermissionRequest(
            "write_file", @"C:\proj\a.txt (+42 lines)", @"C:\proj",
            PreviewLines: new[] { "a", "b", "c", "d", "e" }, PreviewTotalLines: 42));

        Assert.Equal(new[] { "1  a", "2  b", "3  c", "4  d", "5  e", "… +37 lines" }, detail);
    }

    [Fact]
    public void WriteFile_PreviewCoversTheWholeFile_NoElisionTail()
    {
        var (_, _, detail) = For(new PermissionRequest(
            "write_file", @"C:\proj\a.txt (+3 lines)", @"C:\proj",
            PreviewLines: new[] { "a", "b", "c" }, PreviewTotalLines: 3));

        Assert.Equal(new[] { "1  a", "2  b", "3  c" }, detail);
    }

    //with no preview the detail falls back to the summary, since core never fabricates a preview for a write with no sizing argument
    [Fact]
    public void WriteFile_NoPreview_FallsBackToTheSummaryDetail()
    {
        var (_, _, detail) = For(new PermissionRequest(
            "write_file", @"C:\proj\a.txt", @"C:\proj"));

        Assert.Equal(new[] { @"C:\proj\a.txt" }, detail);
    }

    //preview lines arrive as raw bytes, so a trailing CR or an ESC must be gone before the rows leave here
    [Fact]
    public void WriteFile_PreviewCarryingRawCrAndEsc_IsSanitized()
    {
        var (_, _, detail) = For(new PermissionRequest(
            "write_file", @"C:\proj\a.txt (+2 lines)", @"C:\proj",
            PreviewLines: new[] { "first line\r", Esc + "[31mred" }, PreviewTotalLines: 2));

        Assert.Equal(new[] { "1  first line", "2  [31mred" }, detail);
        Assert.DoesNotContain(detail, row => row.Contains('\r'));
        Assert.DoesNotContain(detail, row => row.Contains(Esc));
    }

    //an elided part of a shell command would be exactly the part being authorized, so the title must never elide.
    [Fact]
    public void Shell_TitleNeverElides()
    {
        var command = string.Join("\n", Enumerable.Range(1, 18).Select(i => $"line-{i}"));
        var (title, question, detail) = For(new PermissionRequest(
            "shell", command, "line"));

        Assert.Equal("shell command", title);
        Assert.Equal("Do you want to proceed?", question);
        Assert.Equal(18, detail.Count);
        Assert.DoesNotContain(detail, row => row.Contains('⋯'));
        Assert.Equal("line-18", detail[^1]);   //check the last line too, since a hidden payload sits at the tail
    }

    //shell skips elision and keeps the display cap, since a heredoc without it would make the painter clamp and eat the choices
    [Fact]
    public void Shell_PastTheDisplayCap_ShowsTwentyLinesAndAMarker_StillNoHeadTailElision()
    {
        var command = string.Join("\n", Enumerable.Range(1, 50).Select(i => $"line-{i}"));
        var (_, _, detail) = For(new PermissionRequest("shell", command, null));

        Assert.Equal(21, detail.Count);
        Assert.Equal("line-1", detail[0]);
        Assert.Equal("line-20", detail[19]);
        Assert.Equal("… +30 lines", detail[20]);   //the marker counts what the cap hides, a command is never cut silently
        Assert.DoesNotContain(detail, row => row.Contains('⋯'));
    }

    //show even a short command whole, since the panel replaces the composer and no line is duplicated
    [Fact]
    public void Shell_ShortCommand_IsStillShownInFull()
    {
        var (_, _, detail) = For(new PermissionRequest(
            "shell", "git status --short", "git status"));

        Assert.Equal(new[] { "git status --short" }, detail);
    }

    [Fact]
    public void WebSearch_ProviderAndQuery()
    {
        var (title, question, detail) = For(
            new PermissionRequest("web_search", """{"query":"strix halo vulkan crash","count":8}""",
                "web_search"),
            provider: "ddg");

        Assert.Equal("Web search using ddg", title);
        Assert.Equal("Do you want to proceed?", question);
        Assert.Equal(new[] { "\"strix halo vulkan crash\" · 8 results" }, detail);
    }

    [Fact]
    public void WebSearch_NoProvider_FallsBackToTheBareTitle()
    {
        var (title, _, detail) = For(
            new PermissionRequest("web_search", """{"query":"q"}""", "web_search"));

        Assert.Equal("Web search", title);
        Assert.Equal(new[] { "\"q\"" }, detail);   //the results clause appears only when the request has a count argument.
    }

    //the summary is args JSON read opportunistically, so unparseable input falls back to the generic shape
    [Theory]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    [InlineData("""{"quer":"typo'd key"}""")]
    [InlineData("""{"query":42}""")]
    public void WebSearch_MalformedJson_FallsBackGeneric(string summary)
    {
        var (title, question, detail) = For(
            new PermissionRequest("web_search", summary, "web_search"),
            provider: "ddg");

        Assert.Equal("web_search", title);
        Assert.Equal("Do you want to proceed?", question);
        Assert.Equal(new[] { summary }, detail);
    }

    [Fact]
    public void WebFetch_ShowsTheUrl()
    {
        var (title, question, detail) = For(new PermissionRequest(
            "web_fetch", """{"url":"https://example.com/a"}""", "web_fetch"));

        Assert.Equal("Web fetch", title);
        Assert.Equal("Do you want to proceed?", question);
        Assert.Equal(new[] { "https://example.com/a" }, detail);
    }

    [Fact]
    public void WebFetch_MalformedJson_FallsBackGeneric()
    {
        var (title, _, detail) = For(new PermissionRequest(
            "web_fetch", "{oops", "web_fetch"));

        Assert.Equal("web_fetch", title);
        Assert.Equal(new[] { "{oops" }, detail);
    }

    [Fact]
    public void Generic_UsesRequestLines()
    {
        var (title, question, detail) = For(new PermissionRequest(
            "frobnicate", "{\"x\":1}", "frobnicate"));

        Assert.Equal("frobnicate", title);
        Assert.Equal("Do you want to proceed?", question);
        Assert.Equal(new[] { "{\"x\":1}" }, detail);
    }

    //checkpoint is not worded specially, so it must take the generic path with the tool name as the title.
    [Fact]
    public void Checkpoint_TakesTheGenericShape_TitledByTool()
    {
        var (title, question, detail) = For(new PermissionRequest(
            "checkpoint", "M  a.cs\n?? b.cs", null));

        Assert.Equal("checkpoint", title);
        Assert.Equal("Do you want to proceed?", question);
        Assert.Equal(new[] { "M  a.cs", "?? b.cs" }, detail);
    }

    //elide to head and tail around a visible marker, since a smuggled payload hides at the end of a long argument
    [Fact]
    public void Generic_PastTheRowCap_ElidesToHeadAndTail()
    {
        var summary = string.Join("\n", Enumerable.Range(1, 18).Select(i => $"line-{i}"));
        var (_, _, detail) = For(new PermissionRequest("run_agent", summary, null));

        Assert.Equal(14, detail.Count);
        Assert.Equal("line-1", detail[0]);
        Assert.Equal("line-10", detail[9]);
        Assert.Equal("⋯ +5 lines ⋯", detail[10]);
        Assert.Equal("line-16", detail[11]);
        Assert.Equal("line-18", detail[13]);
    }

    [Fact]
    public void Generic_AtTheRowCap_IsNotElided()
    {
        var summary = string.Join("\n", Enumerable.Range(1, 14).Select(i => $"line-{i}"));
        var (_, _, detail) = For(new PermissionRequest("run_agent", summary, null));

        Assert.Equal(14, detail.Count);
        Assert.DoesNotContain(detail, row => row.Contains('⋯'));
    }

    //a model-invented tool name reaches this class unfiltered and shows up in a bold title row, so it must arrive sanitized
    [Fact]
    public void HostileToolNameAndSummary_AreSanitized()
    {
        var (title, _, detail) = For(new PermissionRequest(
            Esc + "[31mevil", "a" + Esc + "[0m\rforged", null));

        Assert.Equal("[31mevil", title);
        Assert.Equal(new[] { "a[0mforged" }, detail);
        Assert.DoesNotContain(Esc, title);
        Assert.DoesNotContain(detail, row => row.Contains(Esc) || row.Contains('\r'));
    }
}
