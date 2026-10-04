using System.Runtime.Versioning;
using System.Text.Json;
using Gatto.Core.Tools;

namespace Gatto.Tests;

public class SearchToolsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gatto-test-").FullName;
    private IToolContext Ctx => new TestToolContext(_dir);
    private static JsonElement Args(object o) => JsonSerializer.SerializeToElement(o);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private void Seed()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "src", "deep"));
        Directory.CreateDirectory(Path.Combine(_dir, ".git"));
        File.WriteAllText(Path.Combine(_dir, "src", "a.cs"), "class A { }");
        File.WriteAllText(Path.Combine(_dir, "src", "deep", "b.cs"), "class B { // findme\n}");
        File.WriteAllText(Path.Combine(_dir, "readme.md"), "# findme");
        File.WriteAllText(Path.Combine(_dir, ".git", "junk.cs"), "class Junk { }");
    }

    //the cap is the number of rows delivered: 200 matches come back whole and unmarked, a 201st is the one that is cut and marked
    [Theory]
    [InlineData(200, 200, false)]
    [InlineData(201, 200, true)]
    [InlineData(450, 200, true)]
    public async Task Grep_delivers_at_most_its_cap_and_marks_only_a_real_cut(int matches, int rows, bool marked)
    {
        File.WriteAllText(Path.Combine(_dir, "many.txt"), string.Join("\n", Enumerable.Range(1, matches).Select(i => $"hit {i}")));
        var r = await new GrepTool().ExecuteAsync(Args(new { pattern = "hit" }), Ctx, default);
        var lines = r.Text.Split(Environment.NewLine);

        Assert.Equal(rows, lines.Count(l => l.StartsWith("many.txt:", StringComparison.Ordinal)));
        Assert.Equal(marked, r.Text.Contains("[capped at 200 matches]", StringComparison.Ordinal));
        Assert.Equal($"{rows} matches", r.Gloss);
    }

    //the character budget is a ceiling too: the row that would cross it is not delivered
    [Fact]
    public async Task Grep_never_delivers_more_characters_than_its_budget()
    {
        var line = "hit " + new string('x', 290);
        File.WriteAllText(Path.Combine(_dir, "wide.txt"), string.Join("\n", Enumerable.Repeat(line, 199)));
        var r = await new GrepTool().ExecuteAsync(Args(new { pattern = "hit" }), Ctx, default);
        var rows = r.Text.Split(Environment.NewLine).Where(l => l.StartsWith("wide.txt:", StringComparison.Ordinal)).ToList();

        Assert.True(rows.Sum(x => x.Length) <= GrepTool.MaxTotalChars, $"{rows.Sum(x => x.Length)} chars delivered");
        Assert.Contains("[capped at 50000 chars", r.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Glob_double_star_crosses_directories_and_skips_git()
    {
        Seed();
        var r = await new GlobTool().ExecuteAsync(Args(new { pattern = "**/*.cs" }), Ctx, default);
        var lines = r.Text.Split(Environment.NewLine);
        Assert.Contains(Path.Combine("src", "a.cs"), lines);
        Assert.Contains(Path.Combine("src", "deep", "b.cs"), lines);
        Assert.DoesNotContain(lines, l => l.Contains("junk"));
    }

    [Fact]
    public async Task Glob_star_lists_the_folders_of_one_level_too_each_marked_as_a_folder()
    {
        //a listing with no folders reads as an empty folder when all it holds is folders
        Seed();
        var r = await new GlobTool().ExecuteAsync(Args(new { pattern = "*" }), Ctx, default);
        Assert.Equal(["readme.md", "src" + Path.DirectorySeparatorChar], r.Text.Split(Environment.NewLine));
        Assert.Equal("2 entries", r.Gloss);
    }

    [Fact]
    public async Task Glob_star_never_crosses_a_separator_and_double_star_reaches_folders_at_any_depth()
    {
        Seed();
        var one = await new GlobTool().ExecuteAsync(Args(new { pattern = "*.cs" }), Ctx, default);
        Assert.Equal("no matches", one.Text);

        var deep = await new GlobTool().ExecuteAsync(Args(new { pattern = "**/deep" }), Ctx, default);
        Assert.Equal(Path.Combine("src", "deep") + Path.DirectorySeparatorChar, deep.Text);
    }

    [Fact]
    public async Task Glob_with_only_files_matched_still_counts_files()
    {
        Seed();
        var r = await new GlobTool().ExecuteAsync(Args(new { pattern = "**/*.cs" }), Ctx, default);
        Assert.Equal("2 files", r.Gloss);
    }

    [Theory]
    [InlineData("src/[ab].cs", "a.cs", true)]
    [InlineData("src/[!b].cs", "a.cs", true)]
    [InlineData("src/[b-z].cs", "a.cs", false)]
    [InlineData("src/[a-c].cs", "a.cs", true)]
    public async Task Glob_reads_a_bracket_class_as_one_character_of_the_set(string pattern, string file, bool matches)
    {
        Seed();
        var r = await new GlobTool().ExecuteAsync(Args(new { pattern }), Ctx, default);
        Assert.Equal(matches, r.Text.Split(Environment.NewLine).Contains(Path.Combine("src", file)));
    }

    [Fact]
    public async Task Glob_still_finds_a_folder_whose_name_is_written_in_brackets()
    {
        //route folders are named this way, a pattern that spells one must reach it
        Directory.CreateDirectory(Path.Combine(_dir, "app", "[id]"));
        File.WriteAllText(Path.Combine(_dir, "app", "[id]", "page.tsx"), "x");
        var r = await new GlobTool().ExecuteAsync(Args(new { pattern = "app/[id]/page.tsx" }), Ctx, default);
        Assert.Equal(Path.Combine("app", "[id]", "page.tsx"), r.Text);
    }

    [Fact]
    public async Task Glob_no_matches_says_so()
    {
        Seed();
        var r = await new GlobTool().ExecuteAsync(Args(new { pattern = "**/*.zig" }), Ctx, default);
        Assert.Equal("no matches", r.Text);
    }

    [Fact]
    public async Task Grep_returns_path_line_and_content()
    {
        Seed();
        var r = await new GrepTool().ExecuteAsync(Args(new { pattern = "findme" }), Ctx, default);
        Assert.Contains($"{Path.Combine("src", "deep", "b.cs")}:1:", r.Text);
        Assert.Contains("readme.md:1:", r.Text);
    }

    [Fact]
    public async Task Grep_invalid_regex_throws()
    {
        Seed();
        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => new GrepTool().ExecuteAsync(Args(new { pattern = "(" }), Ctx, default));
        Assert.Contains("invalid regex", ex.Message);
    }

    [Fact]
    public async Task Grep_preserves_leading_whitespace()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "indent.txt"), "    indented findme");
        var r = await new GrepTool().ExecuteAsync(Args(new { pattern = "findme" }), Ctx, default);
        Assert.Contains("indent.txt:1:     indented findme", r.Text);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task Glob_survives_unreadable_subdirectory()
    {
        Seed();
        var locked = Path.Combine(_dir, "locked");
        Directory.CreateDirectory(locked);
        File.WriteAllText(Path.Combine(locked, "hidden.cs"), "x");
        var dirInfo = new DirectoryInfo(locked);
        var security = dirInfo.GetAccessControl();
        security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
            System.Security.Principal.WindowsIdentity.GetCurrent().Name,
            System.Security.AccessControl.FileSystemRights.ListDirectory,
            System.Security.AccessControl.AccessControlType.Deny));
        dirInfo.SetAccessControl(security);
        try
        {
            var r = await new GlobTool().ExecuteAsync(Args(new { pattern = "**/*.cs" }), Ctx, default);
            Assert.Contains(Path.Combine("src", "a.cs"), r.Text.Split(Environment.NewLine));  //the denied folder must not hide the files outside it
        }
        finally
        {
            security.RemoveAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                System.Security.Principal.WindowsIdentity.GetCurrent().Name,
                System.Security.AccessControl.FileSystemRights.ListDirectory,
                System.Security.AccessControl.AccessControlType.Deny));
            dirInfo.SetAccessControl(security);
        }
    }

    [Fact]
    public async Task Glob_GlossIsFileCount()
    {
        Seed();
        var r = await new GlobTool().ExecuteAsync(Args(new { pattern = "**/*.cs" }), Ctx, default);
        Assert.Equal("2 files", r.Gloss);
    }

    //the fixture holds one match, so the gloss must read '1 match'
    [Fact]
    public async Task Grep_GlossIsMatchCount()
    {
        File.WriteAllText(Path.Combine(_dir, "one.txt"), "findme once");
        var r = await new GrepTool().ExecuteAsync(Args(new { pattern = "findme" }), Ctx, default);
        Assert.Equal("1 match", r.Gloss);
    }

    [Fact]
    public async Task Glob_NonexistentRoot_Throws()
    {
        var args = Args(new { pattern = "*.cs", root = "Z:\\no\\such\\dir" });
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => new GlobTool().ExecuteAsync(args, Ctx, default));
        Assert.Contains("root not found", ex.Message);
    }

    [Fact]
    public async Task Grep_NonexistentRoot_Throws()
    {
        var args = Args(new { pattern = "x", root = "Z:\\no\\such\\dir" });
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => new GrepTool().ExecuteAsync(args, Ctx, default));
        Assert.Contains("root not found", ex.Message);
    }

    [Fact]
    public async Task Grep_SlashlessGlob_MatchesNestedFiles()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "sub"));
        File.WriteAllText(Path.Combine(_dir, "sub", "inner.cs"), "needle here");
        var args = Args(new { pattern = "needle", root = _dir, glob = "*.cs" });
        var r = await new GrepTool().ExecuteAsync(args, Ctx, default);
        Assert.Contains("inner.cs:1", r.Text);
    }

    [Fact]
    public async Task Grep_SlashlessGlob_MatchesRootLevelFiles()
    {
        File.WriteAllText(Path.Combine(_dir, "top.cs"), "needle here");
        var args = Args(new { pattern = "needle", root = _dir, glob = "*.cs" });
        var r = await new GrepTool().ExecuteAsync(args, Ctx, default);
        Assert.Contains("top.cs:1", r.Text);
    }

    //grep must bound its output three ways: never read binary files, clip each line, and keep a byte budget.

    [Fact]
    public async Task Grep_skips_binary_files()
    {
        File.WriteAllText(Path.Combine(_dir, "real.cs"), "needle in source");
        //a source-shaped regex matches inside a binary, so the fixture plants a needle in a fake dll. the nul bytes mark it as not text.
        File.WriteAllBytes(Path.Combine(_dir, "build.dll"),
            System.Text.Encoding.UTF8.GetBytes("MZ\0\0needle in a binary\0\0"));

        var r = await new GrepTool().ExecuteAsync(Args(new { pattern = "needle" }), Ctx, default);

        Assert.Contains("real.cs:1", r.Text);
        Assert.DoesNotContain("build.dll", r.Text);
    }

    [Fact]
    public async Task Grep_still_reads_utf16_text_that_carries_a_bom()
    {
        //utf-16 text contains nul bytes but has a bom. the binary sniff must not swallow it.
        File.WriteAllText(Path.Combine(_dir, "wide.cs"), "needle in utf16", System.Text.Encoding.Unicode);

        var r = await new GrepTool().ExecuteAsync(Args(new { pattern = "needle" }), Ctx, default);

        Assert.Contains("wide.cs:1", r.Text);
    }

    [Fact]
    public async Task Grep_clips_a_very_long_matching_line()
    {
        File.WriteAllText(Path.Combine(_dir, "long.txt"), "needle " + new string('x', 20_000));

        var r = await new GrepTool().ExecuteAsync(Args(new { pattern = "needle" }), Ctx, default);

        Assert.Single(r.Text.Split(Environment.NewLine));
        Assert.True(r.Text.Length < 500, $"emitted line was {r.Text.Length} chars");
        Assert.Contains("needle", r.Text);
        Assert.EndsWith("…", r.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Grep_stops_at_the_total_output_budget()
    {
        //many matches with long lines trip the byte budget before the 200-match cap
        for (var i = 0; i < 200; i++)
            File.WriteAllText(Path.Combine(_dir, $"f{i}.txt"), "needle " + new string('y', 4_000));

        var r = await new GrepTool().ExecuteAsync(Args(new { pattern = "needle" }), Ctx, default);

        Assert.True(r.Text.Length <= 60_000, $"grep returned {r.Text.Length} chars");
        Assert.Contains("chars", r.Text);   //the message must say the byte budget tripped
        Assert.DoesNotContain("matches]", r.Text);
    }

    [Fact]
    public async Task Walk_skips_the_tmp_build_output_directory()
    {
        //build output under tmp must be skipped even when the inner folder is named testbin
        Directory.CreateDirectory(Path.Combine(_dir, "tmp", "testbin"));
        File.WriteAllText(Path.Combine(_dir, "tmp", "testbin", "leftover.cs"), "needle in build output");
        File.WriteAllText(Path.Combine(_dir, "kept.cs"), "needle in source");

        var r = await new GrepTool().ExecuteAsync(Args(new { pattern = "needle" }), Ctx, default);

        Assert.Contains("kept.cs:1", r.Text);
        Assert.DoesNotContain("leftover", r.Text);
    }
}
