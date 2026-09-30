using Gatto.Core.Client;
using Gatto.Core.Loop;
using Gatto.Core.Tools;
using Gatto.Repl.Input;
using Gatto.Repl.Render;
using Gatto.Repl.Term;
using Gatto.Terminal;
using Gatto.Tests.Fakes;
using Xunit;

namespace Gatto.Tests.Render;

//highlighting reaches assistant code fences with a C# or PowerShell tag, the shell command on a tool row and a code file's write preview
public sealed class SyntaxHighlightRenderTests
{
    private static readonly Theme T = new(new TermCaps(true, true));
    private static readonly Theme T256 = new(new TermCaps(true, false));

    private static string Visible(string row) => TermText.StripAnsiForWidth(row);

    private static string Rgb(RgbColor c) => $"{c.R};{c.G};{c.B}";

    //every visible char of a painted row, with the truecolour foreground in force when it was written
    private static List<(char Ch, string? Fg)> Cells(string row)
    {
        var cells = new List<(char Ch, string? Fg)>();
        string? fg = null;
        for (var i = 0; i < row.Length; i++)
        {
            if (row[i] != (char)27)
            {
                cells.Add((row[i], fg));
                continue;
            }
            if (i + 1 >= row.Length || row[i + 1] != '[') continue;
            var end = i + 2;
            while (end < row.Length && (row[end] < '@' || row[end] > '~')) end++;
            var body = row[(i + 2)..Math.Min(end, row.Length)];
            if (end < row.Length && row[end] == 'm')
            {
                if (body is "0" or "") fg = null;
                else if (body.StartsWith("38;2;", StringComparison.Ordinal)) fg = body[5..];
            }
            i = end;
        }
        return cells;
    }

    private static List<string> Fence(Theme theme, int width, params string[] lines) =>
        ItemRender.ProseRun(lines, theme, "generalist", width, GlyphSet.Unicode);

    [Fact]
    public void A_csharp_fence_colours_its_keyword_its_number_and_its_comment()
    {
        var row = Fence(T, 80, "```csharp", "var total = 42; // sum", "```").Single(r => Visible(r).Contains("var total", StringComparison.Ordinal));

        Assert.Contains(Ansi.Bg(Theme.CodeBlockBg, true), row, StringComparison.Ordinal);
        Assert.Contains(Ansi.Fg(Theme.VsKeyword, true) + "var", row, StringComparison.Ordinal);
        Assert.Contains(Ansi.Fg(Theme.VsNumber, true) + "42", row, StringComparison.Ordinal);
        Assert.Contains(Ansi.Fg(Theme.VsComment, true) + "// sum", row, StringComparison.Ordinal);
    }

    [Fact]
    public void A_powershell_fence_colours_its_command_its_parameter_its_variable_and_its_number()
    {
        var row = Fence(T, 80, "```powershell", "Get-ChildItem -Path $env:TEMP | Select-Object -First 3", "```")
            .Single(r => Visible(r).Contains("Get-ChildItem", StringComparison.Ordinal));

        Assert.Contains(Ansi.Fg(Theme.CampbellBrightYellow, true) + "Get-ChildItem", row, StringComparison.Ordinal);
        Assert.Contains(Ansi.Fg(Theme.CampbellBrightBlack, true) + "-Path", row, StringComparison.Ordinal);
        Assert.Contains(Ansi.Fg(Theme.CampbellBrightGreen, true) + "$env:TEMP", row, StringComparison.Ordinal);
        Assert.Contains(Ansi.Fg(Theme.CampbellBrightWhite, true) + "3", row, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("```cs", "var x = 1;", "VsKeyword")]
    [InlineData("```C#", "var x = 1;", "VsKeyword")]
    [InlineData("```csharp Program.cs", "var x = 1;", "VsKeyword")]
    [InlineData("```ps1", "Get-Date", "CampbellBrightYellow")]
    [InlineData("```pwsh", "Get-Date", "CampbellBrightYellow")]
    public void Every_spelling_of_the_two_tags_is_highlighted(string opening, string body, string token)
    {
        var colour = (RgbColor)typeof(Theme).GetField(token)!.GetValue(null)!;
        var rows = Fence(T, 80, opening, body, "```");
        Assert.Contains(rows, r => r.Contains(Ansi.Fg(colour, true), StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("```")]
    [InlineData("```text")]
    [InlineData("```markdown")]
    public void A_fence_without_a_highlighted_language_stays_literal(string opening)
    {
        var rows = Fence(T, 80, opening, "var x = 1; // note", "```");

        Assert.Contains(rows, r => r.Contains(T.PaintBgLine("var x = 1; // note", Theme.CodeBlockFg, Theme.CodeBlockBg), StringComparison.Ordinal));
        Assert.DoesNotContain(rows, r => r.Contains(Ansi.Fg(Theme.VsKeyword, true), StringComparison.Ordinal));
    }

    [Fact]
    public void Below_truecolour_a_tagged_fence_renders_exactly_like_a_literal_one()
    {
        var rows = Fence(T256, 80, "```csharp", "var x = 1; // note", "```");
        Assert.Contains(rows, r => r.Contains(T256.PaintBgLine("var x = 1; // note", Theme.CodeBlockFg, Theme.CodeBlockBg), StringComparison.Ordinal));
    }

    [Fact]
    public void A_code_fence_the_user_typed_stays_literal()
    {
        var rows = ItemRender.UserEchoRows(new[] { "```csharp", "var x = 1;", "```" }, T, 80, GlyphSet.Unicode);
        Assert.Contains(rows, r => Visible(r).Contains("var x = 1;", StringComparison.Ordinal));
        Assert.DoesNotContain(rows, r => r.Contains(Ansi.Fg(Theme.VsKeyword, true), StringComparison.Ordinal));
    }

    [Fact]
    public void A_half_streamed_fence_is_highlighted_without_its_closing_line()
    {
        var rows = Fence(T, 80, "```csharp", "var s = \"still arriving");
        Assert.Contains(rows, r => r.Contains(Ansi.Fg(Theme.VsKeyword, true) + "var", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("csharp", @"var message = ""a long string literal that has to wrap across several rows""; // and a trailing comment", "VsString", "//", "VsComment")]
    [InlineData("powershell", @"$message = ""a long string literal that has to wrap across several rows"" # and a trailing comment", "CampbellCyan", "#", "CampbellGreen")]
    [InlineData("json", @"""a long string literal that has to wrap across several rows"" // and a trailing comment", "VsString", "//", "VsComment")]
    [InlineData("javascript", @"const message = ""a long string literal that has to wrap across several rows""; // and a trailing comment", "VsString", "//", "VsComment")]
    [InlineData("typescript", @"const message: string = ""a long string literal that has to wrap across several rows""; // and a trailing comment", "VsString", "//", "VsComment")]
    [InlineData("python", @"message = ""a long string literal that has to wrap across several rows"" # and a trailing comment", "VsString", "#", "VsComment")]
    [InlineData("xml", @"<item name=""a long string literal that has to wrap across several rows""/> <!-- and a trailing comment -->", "VsString", "<!--", "VsComment")]
    [InlineData("html", @"<p title=""a long string literal that has to wrap across several rows""></p> <!-- and a trailing comment -->", "VsString", "<!--", "VsComment")]
    [InlineData("rust", @"let message = ""a long string literal that has to wrap across several rows""; // and a trailing comment", "VsString", "//", "VsComment")]
    [InlineData("bash", @"echo ""a long string literal that has to wrap across several rows"" # and a trailing comment", "CampbellCyan", "#", "CampbellGreen")]
    public void A_highlighted_line_wraps_like_a_literal_one_and_keeps_its_roles_across_every_break(string tag, string body, string stringColour, string commentMarker, string commentColour)
    {
        var literalStart = body.IndexOf('"');
        var literalEnd = body.LastIndexOf('"');
        var commentStart = body.IndexOf(commentMarker, StringComparison.Ordinal);
        var stringRgb = Rgb((RgbColor)typeof(Theme).GetField(stringColour)!.GetValue(null)!);
        var commentRgb = Rgb((RgbColor)typeof(Theme).GetField(commentColour)!.GetValue(null)!);
        var head = body[..6];

        for (var width = 16; width <= 70; width++)
        {
            //a long tag can wrap its own row, so both renders are compared from the first row of the body
            var highlighted = Fence(T, width, "```" + tag, body, "```").SkipWhile(r => !Visible(r).Contains(head, StringComparison.Ordinal)).ToList();
            var literal = Fence(T, width, "```text", body, "```").SkipWhile(r => !Visible(r).Contains(head, StringComparison.Ordinal)).ToList();
            Assert.NotEmpty(highlighted);
            Assert.Equal(literal.Select(Visible), highlighted.Select(Visible));

            var at = 0;
            foreach (var row in highlighted)
                foreach (var (ch, fg) in Cells(row).Skip(GutterWrap.Hang.Length))
                {
                    while (at < body.Length && body[at] != ch && body[at] == ' ') at++;
                    Assert.True(at < body.Length && body[at] == ch, $"{tag} at width {width}: the rows stop following the source at {at}");
                    if (ch != ' ' && at >= literalStart && at <= literalEnd) Assert.True(stringRgb == fg, $"{tag} at width {width}: char {at} is not the string colour");
                    if (ch != ' ' && at >= commentStart) Assert.True(commentRgb == fg, $"{tag} at width {width}: char {at} is not the comment colour");
                    at++;
                }
            Assert.Equal(body.Length, at);
        }
    }

    [Theory]
    [InlineData("```python", "def load(path):", "load", "VsMethod")]
    [InlineData("```json", @"{""port"": 1235}", "1235", "VsNumber")]
    [InlineData("```bash", "ls -la | grep foo", "ls", "CampbellBrightYellow")]
    [InlineData("```sh", "echo $HOME", "$HOME", "CampbellBrightGreen")]
    [InlineData("```rust", "fn main() {}", "fn", "VsKeyword")]
    [InlineData("```html", @"<a href=""x"">", @"""x""", "VsString")]
    [InlineData("```xml", @"<item/>", "item", "VsKeyword")]
    [InlineData("```ts", "interface User {}", "User", "VsType")]
    [InlineData("```js", "const re = /ab+c/g;", "/ab+c/g", "VsString")]
    public void A_fallback_fence_paints_in_the_colours_of_its_language(string opening, string line, string probe, string token)
    {
        var colour = (RgbColor)typeof(Theme).GetField(token)!.GetValue(null)!;
        var row = Fence(T, 80, opening, line, "```").Single(r => Visible(r).Contains(line, StringComparison.Ordinal));
        Assert.Contains(Ansi.Fg(colour, true) + probe, row, StringComparison.Ordinal);
    }

    [Fact]
    public void A_python_file_preview_is_highlighted_by_its_extension()
    {
        var language = SyntaxHighlight.LanguageOfToolCall("write_file", @"{""path"":""tools/gen.py"",""content"":""x""}");
        var block = new ToolBlockItem("write_file", "gen.py", "ok", true, "generalist")
        {
            Collapsed = false,
            WriteContent = "def load(path):",
            FileLanguage = language,
        };

        Assert.Equal(CodeLanguage.Python, language);
        Assert.Contains(block.Render(80, T, GlyphSet.Unicode), r => r.Contains(T.Paint("def", Theme.VsKeyword), StringComparison.Ordinal));
    }

    [Fact]
    public void The_shell_command_on_a_committed_tool_row_is_highlighted()
    {
        var header = ItemRender.ToolRows("shell", "Get-ChildItem -Path $env:TEMP", "ok", true, T, "generalist", glyphs: GlyphSet.Unicode)[0];

        Assert.Contains(T.Paint("shell", Theme.ToolName), header, StringComparison.Ordinal);
        Assert.Contains(T.Paint("Get-ChildItem", Theme.CampbellBrightYellow), header, StringComparison.Ordinal);
        Assert.Contains(T.Paint("-Path", Theme.CampbellBrightBlack), header, StringComparison.Ordinal);
        Assert.Contains(T.Paint("$env:TEMP", Theme.CampbellBrightGreen), header, StringComparison.Ordinal);
    }

    [Fact]
    public void The_shell_command_on_the_live_tool_row_is_highlighted()
    {
        var s = new VtScreenSurface(80, 20);
        var status = new StatusInfo(@"C:\Users\user\projects\gatto", "model", "generalist", new CtxState(), @"C:\Users\user");
        var painter = new ChromePainter(s, T, new object())
        {
            Frame = new InputFrame(s, T, "generalist", status, glyphs: GlyphSet.Unicode),
        };
        painter.State.Composer = new EditorView(new List<string> { "" }, 0, 0);
        painter.State.Tool = ("shell", "git status --short");

        var row = painter.ComposeChromeBlock(80, 20).Rows
            .Single(r => r.Region == ChromeRegion.Tool && r.Visible.Contains("shell", StringComparison.Ordinal));

        Assert.Contains(T.Paint("git", Theme.CampbellBrightYellow), row.Rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Below_truecolour_the_shell_command_keeps_the_one_argument_colour()
    {
        var header = ItemRender.ToolRows("shell", "Get-ChildItem -Path $env:TEMP", "ok", true, T256, "generalist", glyphs: GlyphSet.Unicode)[0];
        Assert.Contains(T256.Paint("Get-ChildItem -Path $env:TEMP", Theme.ToolArgs), header, StringComparison.Ordinal);
    }

    private static ToolBlockItem WriteBlock(CodeLanguage language) =>
        new("write_file", "a.cs", "ok", true, "generalist")
        {
            Collapsed = false,
            WriteContent = "var x = 1;",
            FileLanguage = language,
        };

    [Fact]
    public void A_code_file_preview_is_highlighted()
    {
        var rows = WriteBlock(CodeLanguage.CSharp).Render(80, T, GlyphSet.Unicode);
        Assert.Contains(rows, r => r.Contains(T.Paint("var", Theme.VsKeyword), StringComparison.Ordinal));
    }

    [Fact]
    public void A_preview_of_any_other_file_stays_one_dim_colour()
    {
        var rows = WriteBlock(CodeLanguage.None).Render(80, T, GlyphSet.Unicode);
        Assert.Contains(rows, r => r.Contains(T.Paint("var x = 1;", Theme.CodeBlockFg), StringComparison.Ordinal));
        Assert.DoesNotContain(rows, r => r.Contains(Ansi.Fg(Theme.VsKeyword, true), StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("write_file", "{\"path\":\"src/a.cs\",\"content\":\"x\"}", CodeLanguage.CSharp)]
    [InlineData("edit_file", "{\"path\":\"C:\\\\proj\\\\build.PS1\",\"new_string\":\"x\"}", CodeLanguage.PowerShell)]
    [InlineData("write_file", "{\"path\":\"notes.txt\",\"content\":\"x\"}", CodeLanguage.None)]
    [InlineData("write_file", @"{""path"":""tools/gen.py"",""content"":""x""}", CodeLanguage.Python)]
    [InlineData("write_file", @"{""path"":""build/App.csproj"",""content"":""x""}", CodeLanguage.Xml)]
    [InlineData("edit_file", @"{""path"":""deploy.sh"",""content"":""x""}", CodeLanguage.Bash)]
    [InlineData("write_file", @"{""path"":""site/INDEX.HTML"",""content"":""x""}", CodeLanguage.Html)]
    [InlineData("read_file", "{\"path\":\"src/a.cs\"}", CodeLanguage.None)]
    [InlineData("write_file", "{not json", CodeLanguage.None)]
    public void A_tool_call_names_its_preview_language_by_the_path_it_writes(string tool, string json, CodeLanguage expected)
        => Assert.Equal(expected, SyntaxHighlight.LanguageOfToolCall(tool, json));

    private static (StreamRenderer R, ChromePainter P) Wire()
    {
        var s = new VtScreenSurface(80, 20);
        var gate = new object();
        var model = new TranscriptModel("coder");
        var painter = new ChromePainter(s, T, gate, model)
        {
            Frame = new InputFrame(s, T, "coder", new StatusInfo(@"C:\proj", "qwen", "coder", new CtxState(), @"C:\Users\x"), glyphs: GlyphSet.Unicode),
            RoleForTint = "coder",
        };
        painter.State.Composer = new EditorView(new List<string> { "" }, 0, 0);
        var ticker = new ChromeTicker(painter, gate);
        var renderer = new StreamRenderer(painter, s, T, "coder", ticker, gate, model: model, convoTail: () => null);
        painter.AltScreen.Enter();
        painter.Repaint();
        return (renderer, painter);
    }

    [Fact]
    public void The_live_path_gives_a_written_code_file_its_preview_language()
    {
        var (r, p) = Wire();
        var call = new ToolCall("c1", "write_file", "{\"path\":\"src/a.cs\",\"content\":\"var x = 1;\"}");
        r.BeginTurn();
        r.OnToolCallStart(call);
        r.OnToolResult(call, new ToolResult("wrote 1 line"));
        r.EndTurn();

        var item = Assert.Single(p.Model.Items.OfType<ToolBlockItem>());
        Assert.Equal(CodeLanguage.CSharp, item.FileLanguage);
    }

    [Fact]
    public void Replay_gives_a_written_code_file_its_preview_language()
    {
        var convo = new Conversation("sys");
        convo.AddUser("write it");
        convo.Load(new[]
        {
            new ChatMessage("assistant", null, new[] { new ToolCall("c1", "write_file", "{\"path\":\"src/a.ps1\",\"content\":\"Get-Date\"}") }),
            new ChatMessage("tool", "wrote 1 line", ToolCallId: "c1"),
        });

        var (_, rebuilt) = TranscriptStore.Rebuild(TranscriptStore.BuildLines(new TranscriptModel("coder"), convo), T, "coder", glyphs: GlyphSet.Unicode);

        var item = Assert.Single(rebuilt.Items.OfType<ToolBlockItem>());
        Assert.Equal(CodeLanguage.PowerShell, item.FileLanguage);
    }

    [Theory]
    [InlineData("csharp", CodeLanguage.CSharp)]
    [InlineData(" CS ", CodeLanguage.CSharp)]
    [InlineData("powershell", CodeLanguage.PowerShell)]
    [InlineData("ps1", CodeLanguage.PowerShell)]
    [InlineData("bash", CodeLanguage.Bash)]
    [InlineData("markdown", CodeLanguage.None)]
    [InlineData("JSON", CodeLanguage.Json)]
    [InlineData("py", CodeLanguage.Python)]
    [InlineData("rs", CodeLanguage.Rust)]
    [InlineData("htm", CodeLanguage.Html)]
    [InlineData("svg", CodeLanguage.Xml)]
    [InlineData("jsx", CodeLanguage.None)]
    [InlineData("json5", CodeLanguage.None)]
    [InlineData("", CodeLanguage.None)]
    public void A_fence_tag_names_its_language(string tag, CodeLanguage expected)
        => Assert.Equal(expected, SyntaxHighlight.LanguageOfTag(tag));

    [Fact]
    public void Every_code_colour_has_a_light_theme_pair()
    {
        var light = new Theme(new TermCaps(true, true), ThemeMode.Light);
        var colours = new HashSet<RgbColor>();
        foreach (var language in Enum.GetValues<CodeLanguage>())
            foreach (var role in Enum.GetValues<SpanRole>())
                if (Theme.RoleColor(language, role) is { } dark) colours.Add(dark);

        Assert.Equal(14, colours.Count);
        Assert.All(colours, dark => Assert.NotEqual(dark, light.Map(dark)));
    }

    [Fact]
    public void Each_line_of_a_fence_takes_the_roles_of_its_own_text()
    {
        var row = Fence(T, 80, "```csharp", "// a first line", "var x = 1;", "```").Single(r => Visible(r).Contains("var x", StringComparison.Ordinal));
        Assert.Contains(Ansi.Fg(Theme.VsKeyword, true) + "var", row, StringComparison.Ordinal);
        Assert.Contains(Ansi.Fg(Theme.VsNumber, true) + "1", row, StringComparison.Ordinal);
    }

    [Fact]
    public void Each_line_of_a_preview_takes_the_roles_of_its_own_text()
    {
        var block = new ToolBlockItem("write_file", "a.cs", "ok", true, "generalist")
        {
            Collapsed = false,
            WriteContent = "// a first line\nvar x = 1;",
            FileLanguage = CodeLanguage.CSharp,
        };
        var row = block.Render(80, T, GlyphSet.Unicode).Single(r => Visible(r).Contains("var x", StringComparison.Ordinal));
        Assert.Contains(T.Paint("var", Theme.VsKeyword), row, StringComparison.Ordinal);
        Assert.Contains(T.Paint("1", Theme.VsNumber), row, StringComparison.Ordinal);
    }
}
