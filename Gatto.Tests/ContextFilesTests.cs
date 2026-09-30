using Gatto.Core.Home;

namespace Gatto.Tests;

//the climb reaches the real drive root, so real ancestor files may sit above the fixture, and a test asserts only what it wrote
public class ContextFilesTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("gatto-ctx-").FullName;

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private string Dir(params string[] segments) => Path.Combine([_tempDir, .. segments]);

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public void Collect_returns_root_first_nearest_last()
    {
        var a = Dir("a");
        var c = Dir("a", "b", "c");
        Directory.CreateDirectory(c);
        Write(Path.Combine(a, "GATTO.md"), "root context");
        Write(Path.Combine(c, "GATTO.md"), "nearest context");

        var result = ContextFiles.Collect(c, compat: false, homeGattoMdPath: null);

        var aIndex = IndexOfPath(result, Path.Combine(a, "GATTO.md"));
        var cIndex = IndexOfPath(result, Path.Combine(c, "GATTO.md"));
        Assert.True(aIndex >= 0 && cIndex >= 0 && aIndex < cIndex, "expected root entry before nearest entry");
        Assert.Equal("root context", result[aIndex].Content);
        Assert.Equal("nearest context", result[cIndex].Content);
    }

    [Fact]
    public void Collect_skips_directories_with_no_candidate_file()
    {
        var c = Dir("a", "b", "c");
        Directory.CreateDirectory(c);
        var gattoPath = Path.Combine(c, "GATTO.md");
        Write(gattoPath, "only c has one");
        //the middle directories deliberately hold no context file.

        var result = ContextFiles.Collect(c, compat: false, homeGattoMdPath: null);

        Assert.DoesNotContain(result, e => e.Path == Path.Combine(Dir("a"), "GATTO.md"));
        Assert.DoesNotContain(result, e => e.Path == Path.Combine(Dir("a", "b"), "GATTO.md"));
        Assert.Contains(result, e => e.Path == gattoPath && e.Content == "only c has one");
    }

    [Fact]
    public void Collect_ignores_AGENTS_md_when_compat_false()
    {
        var c = Dir("a", "b", "c");
        Directory.CreateDirectory(c);
        var agentsPath = Path.Combine(c, "AGENTS.md");
        Write(agentsPath, "agents context");

        var result = ContextFiles.Collect(c, compat: false, homeGattoMdPath: null);

        Assert.DoesNotContain(result, e => e.Path == agentsPath);
    }

    [Fact]
    public void Collect_ignores_CLAUDE_md_when_compat_false()
    {
        var c = Dir("a", "b", "c");
        Directory.CreateDirectory(c);
        var claudePath = Path.Combine(c, "CLAUDE.md");
        Write(claudePath, "claude context");

        var result = ContextFiles.Collect(c, compat: false, homeGattoMdPath: null);

        Assert.DoesNotContain(result, e => e.Path == claudePath);
    }

    [Fact]
    public void Collect_compat_true_prefers_AGENTS_over_CLAUDE()
    {
        var c = Dir("a", "b", "c");
        Directory.CreateDirectory(c);
        var agentsPath = Path.Combine(c, "AGENTS.md");
        Write(agentsPath, "agents context");
        Write(Path.Combine(c, "CLAUDE.md"), "claude context");

        var result = ContextFiles.Collect(c, compat: true, homeGattoMdPath: null);

        Assert.DoesNotContain(result, e => e.Path == Path.Combine(c, "CLAUDE.md"));
        Assert.Contains(result, e => e.Path == agentsPath && e.Content == "agents context");
    }

    [Fact]
    public void Collect_compat_true_prefers_GATTO_over_AGENTS()
    {
        var c = Dir("a", "b", "c");
        Directory.CreateDirectory(c);
        var gattoPath = Path.Combine(c, "GATTO.md");
        Write(gattoPath, "gatto context");
        Write(Path.Combine(c, "AGENTS.md"), "agents context");

        var result = ContextFiles.Collect(c, compat: true, homeGattoMdPath: null);

        Assert.DoesNotContain(result, e => e.Path == Path.Combine(c, "AGENTS.md"));
        Assert.Contains(result, e => e.Path == gattoPath && e.Content == "gatto context");
    }

    [Fact]
    public void Collect_compat_true_falls_back_to_CLAUDE_when_only_CLAUDE_present()
    {
        var c = Dir("a", "b", "c");
        Directory.CreateDirectory(c);
        var claudePath = Path.Combine(c, "CLAUDE.md");
        Write(claudePath, "claude context");

        var result = ContextFiles.Collect(c, compat: true, homeGattoMdPath: null);

        Assert.Contains(result, e => e.Path == claudePath && e.Content == "claude context");
    }

    [Fact]
    public void Collect_default_path_that_does_not_exist_returns_empty()
    {
        var c = Dir("a", "b", "c");
        Directory.CreateDirectory(c);
        var missingDefault = Path.Combine(_tempDir, "no-such-default.md");

        var result = ContextFiles.Collect(c, compat: false, homeGattoMdPath: missingDefault);

        Assert.DoesNotContain(result, e => e.Path == missingDefault);
    }

    [Fact]
    public void Collect_home_file_is_kept_and_ordered_first_when_a_real_context_file_was_found()
    {
        var c = Dir("a", "b", "c");
        Directory.CreateDirectory(c);
        var gattoPath = Path.Combine(c, "GATTO.md");
        Write(gattoPath, "real context");
        var homePath = Path.Combine(_tempDir, "home-GATTO.md");
        File.WriteAllText(homePath, "home content");

        var result = ContextFiles.Collect(c, compat: false, homeGattoMdPath: homePath);

        Assert.Equal(homePath, result[0].Path);
        Assert.Equal("home content", result[0].Content);
        Assert.True(IndexOfPath(result, gattoPath) > IndexOfPath(result, homePath),
            "the project file is later, so it overrides the home layer where they disagree");
        Assert.Contains(result, e => e.Path == gattoPath && e.Content == "real context");
    }

    [Fact]
    public void Collect_nonexistent_cwd_does_not_throw()
    {
        var missing = Dir("does", "not", "exist");

        var result = ContextFiles.Collect(missing, compat: false, homeGattoMdPath: null);

        //the invariant is that no exception is thrown. content depends on real ancestor folders, so nothing more fixture-scoped can be asserted.
        Assert.NotNull(result);
    }

    [Fact]
    public void Collect_skips_GATTO_md_that_is_actually_a_directory()
    {
        var c = Dir("a", "b", "c");
        var gattoMdAsDir = Path.Combine(c, "GATTO.md");
        Directory.CreateDirectory(gattoMdAsDir);

        var result = ContextFiles.Collect(c, compat: false, homeGattoMdPath: null);

        Assert.DoesNotContain(result, e => e.Path == gattoMdAsDir);
    }

    [Fact]
    public void Collect_directory_named_GATTO_md_falls_through_to_compat_fallback()
    {
        var c = Dir("a", "b", "c");
        Directory.CreateDirectory(Path.Combine(c, "GATTO.md"));
        var agentsPath = Path.Combine(c, "AGENTS.md");
        Write(agentsPath, "agents context");

        var result = ContextFiles.Collect(c, compat: true, homeGattoMdPath: null);

        Assert.Contains(result, e => e.Path == agentsPath && e.Content == "agents context");
    }

    [Fact]
    public void Collect_includes_empty_GATTO_md_as_an_entry_with_empty_content()
    {
        //presence decides whether a directory contributes, so an empty GATTO.md still claims its one slot
        var c = Dir("a", "b", "c");
        Directory.CreateDirectory(c);
        var gattoPath = Path.Combine(c, "GATTO.md");
        File.WriteAllText(gattoPath, "");

        var result = ContextFiles.Collect(c, compat: false, homeGattoMdPath: null);

        Assert.Contains(result, e => e.Path == gattoPath && e.Content == "");
    }

    [Fact]
    public void Collect_skips_unreadable_file_without_throwing()
    {
        var c = Dir("a", "b", "c");
        Directory.CreateDirectory(c);
        var path = Path.Combine(c, "GATTO.md");
        File.WriteAllText(path, "locked");

        using var handle = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        //a locked file must be skipped, and its sharing-violation IOException must not escape

        var result = ContextFiles.Collect(c, compat: false, homeGattoMdPath: null);

        Assert.DoesNotContain(result, e => e.Path == path);
    }

    [Fact]
    public void Collect_paths_are_absolute()
    {
        var c = Dir("a", "b", "c");
        Directory.CreateDirectory(c);
        Write(Path.Combine(c, "GATTO.md"), "content");

        var result = ContextFiles.Collect(c, compat: false, homeGattoMdPath: null);

        Assert.True(Path.IsPathRooted(result[0].Path));
    }

    private static int IndexOfPath(IReadOnlyList<(string Path, string Content)> result, string path)
    {
        for (var i = 0; i < result.Count; i++)
            if (result[i].Path == path)
                return i;
        return -1;
    }

    [Fact]
    public void PerDirectoryCompat_ProjectRulingDoesNotEscapeUpward()
    {
        //a per-directory compat override governs its own directory and everything below it, and the global default still governs above it
        var root = Dir("root");
        var proj = Dir("root", "proj");
        var sub = Dir("root", "proj", "sub");
        Directory.CreateDirectory(sub);
        Write(Path.Combine(root, "CLAUDE.md"), "root claude");
        Write(Path.Combine(proj, ".gatto.json"), """{"context_files":{"compat":false}}""");
        Write(Path.Combine(proj, "CLAUDE.md"), "proj claude");
        Write(Path.Combine(sub, "CLAUDE.md"), "sub claude");

        var result = ContextFiles.Collect(sub, compat: true, homeGattoMdPath: null);

        Assert.Contains(result, e => e.Path == Path.Combine(root, "CLAUDE.md") && e.Content == "root claude");
        Assert.DoesNotContain(result, e => e.Path == Path.Combine(proj, "CLAUDE.md"));
        Assert.DoesNotContain(result, e => e.Path == Path.Combine(sub, "CLAUDE.md"));
    }

    [Fact]
    public void EffectiveCompatAtCwd_ReflectsTheDeepestRuling_NotTheConfiguredDefault()
    {
        //the doctor command reports the effective compat, which a project's .gatto.json can flip away from the configured default
        var proj = Dir("proj");
        Directory.CreateDirectory(proj);
        Write(Path.Combine(proj, ".gatto.json"), """{"context_files":{"compat":true}}""");
        Write(Path.Combine(proj, "CLAUDE.md"), "proj claude");

        var result = ContextFiles.Collect(proj, compat: false, homeGattoMdPath: null, out var effective);

        Assert.True(effective);
        Assert.Contains(result, e => e.Path == Path.Combine(proj, "CLAUDE.md"));
    }

    [Fact]
    public void EffectiveCompatAtCwd_IsTheConfiguredValue_WhenNoRulingApplies()
    {
        var proj = Dir("proj");
        Directory.CreateDirectory(proj);
        Write(Path.Combine(proj, "GATTO.md"), "proj context");

        ContextFiles.Collect(proj, compat: false, homeGattoMdPath: null, out var off);
        ContextFiles.Collect(proj, compat: true, homeGattoMdPath: null, out var on);

        Assert.False(off);
        Assert.True(on);
    }

    [Fact]
    public void DeeperGattoJson_OverridesShallower()
    {
        //the deeper .gatto.json wins from its own directory down, so the shallower compat ruling stops there
        var proj = Dir("proj");
        var sub = Dir("proj", "sub");
        Directory.CreateDirectory(sub);
        Write(Path.Combine(proj, ".gatto.json"), """{"context_files":{"compat":false}}""");
        Write(Path.Combine(sub, ".gatto.json"), """{"context_files":{"compat":true}}""");
        Write(Path.Combine(proj, "CLAUDE.md"), "proj claude");
        Write(Path.Combine(sub, "CLAUDE.md"), "sub claude");

        var result = ContextFiles.Collect(sub, compat: false, homeGattoMdPath: null);

        Assert.DoesNotContain(result, e => e.Path == Path.Combine(proj, "CLAUDE.md"));
        Assert.Contains(result, e => e.Path == Path.Combine(sub, "CLAUDE.md") && e.Content == "sub claude");
    }

    [Fact]
    public void UnreadableGattoJson_SkippedNotFatal()
    {
        var proj = Dir("proj");
        Directory.CreateDirectory(proj);
        var gattoJsonPath = Path.Combine(proj, ".gatto.json");
        File.WriteAllText(gattoJsonPath, """{"context_files":{"compat":true}}""");
        Write(Path.Combine(proj, "CLAUDE.md"), "proj claude");

        using var handle = new FileStream(gattoJsonPath, FileMode.Open, FileAccess.Read, FileShare.None);
        //a locked .gatto.json reads as absent, so the compat passed in still governs instead of throwing

        var result = ContextFiles.Collect(proj, compat: false, homeGattoMdPath: null);

        Assert.DoesNotContain(result, e => e.Path == Path.Combine(proj, "CLAUDE.md"));
    }

    [Fact]
    public void NoGattoJson_ByteIdenticalToBefore()
    {
        //the no-op path must keep the old output, so assert the order of the fixture's own entries. a count would break on a stray file in a real ancestor directory
        var a = Dir("a");
        var c = Dir("a", "b", "c");
        Directory.CreateDirectory(c);
        Write(Path.Combine(a, "GATTO.md"), "root context");
        Write(Path.Combine(c, "GATTO.md"), "nearest context");

        var result = ContextFiles.Collect(c, compat: false, homeGattoMdPath: null);

        var aIndex = IndexOfPath(result, Path.Combine(a, "GATTO.md"));
        var cIndex = IndexOfPath(result, Path.Combine(c, "GATTO.md"));
        Assert.True(aIndex >= 0 && cIndex >= 0 && aIndex < cIndex, "expected root entry before nearest entry");
        Assert.Equal("root context", result[aIndex].Content);
        Assert.Equal("nearest context", result[cIndex].Content);
    }

    [Fact]
    public void Home_file_is_layer_zero_even_when_the_walk_found_files()
    {
        var homeMd = Dir("home", "GATTO.md");
        Write(homeMd, "# global");
        var proj = Dir("proj");
        Write(Path.Combine(proj, "GATTO.md"), "# project");

        var result = ContextFiles.Collect(proj, false, homeMd);

        Assert.Equal(homeMd, result[0].Path);
        Assert.Equal("# global", result[0].Content);
        Assert.True(IndexOfPath(result, Path.Combine(proj, "GATTO.md")) > 0,
            "the project file must follow the home layer, so it overrides where they disagree");
    }

    [Fact]
    public void Home_file_appears_once_when_it_is_also_an_ancestor_of_cwd()
    {
        //home can also be an ancestor of cwd, and then one path must give one block at position zero
        var homeDir = Dir("anc");
        var homeMd = Path.Combine(homeDir, "GATTO.md");
        Write(homeMd, "# global");
        var deep = Path.Combine(homeDir, "work", "proj");
        Directory.CreateDirectory(deep);

        var result = ContextFiles.Collect(deep, false, homeMd);

        Assert.Equal(homeMd, result[0].Path);
        Assert.Equal(1, result.Count(f => string.Equals(f.Path, homeMd, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Home_layer_off_means_the_walk_only_even_when_the_file_exists()
    {
        var homeMd = Dir("off-home", "GATTO.md");
        Write(homeMd, "# global");
        var proj = Dir("off-proj");
        Write(Path.Combine(proj, "GATTO.md"), "# project");

        var result = ContextFiles.Collect(proj, false, null);

        Assert.DoesNotContain(result, f => string.Equals(f.Path, homeMd, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Unreadable_home_file_is_skipped_and_the_walk_still_returns()
    {
        var homeMd = Dir("lock-home", "GATTO.md");
        Write(homeMd, "# global");
        var proj = Dir("lock-proj");
        Write(Path.Combine(proj, "GATTO.md"), "# project");

        using var hold = new FileStream(homeMd, FileMode.Open, FileAccess.Read, FileShare.None);
        var result = ContextFiles.Collect(proj, false, homeMd);

        Assert.DoesNotContain(result, f => string.Equals(f.Path, homeMd, StringComparison.OrdinalIgnoreCase));
        Assert.True(IndexOfPath(result, Path.Combine(proj, "GATTO.md")) >= 0, "the walk still returns");
    }

    [Theory]
    [InlineData("a<!-- note -->b", "ab")]
    [InlineData("a<!--\nmulti\nline\n-->b", "ab")]
    [InlineData("a<!-- one -->b<!-- two -->c", "abc")]
    [InlineData("a<!-- unterminated b", "a<!-- unterminated b")]
    [InlineData("keep --> alone", "keep --> alone")]
    [InlineData("no comments here", "no comments here")]
    public void StripComments_removes_block_comments_and_nothing_else(string input, string expected)
        => Assert.Equal(expected, ContextFiles.StripComments(input));

    [Fact]
    public void StripComments_does_not_respect_a_fence_and_that_is_the_promise()
    {
        //the promise covers block-level comments only, so a fenced comment is stripped too. making the stripper fence-aware must be a deliberate change
        Assert.Equal("```\n\n```", ContextFiles.StripComments("```\n<!-- x -->\n```"));
    }

    [Fact]
    public void A_file_that_is_all_comments_is_absent_from_the_set()
    {
        var proj = Dir("conly");
        Write(Path.Combine(proj, "GATTO.md"), "<!-- notes to myself -->\n\n");

        var result = ContextFiles.Collect(proj, false, null);

        Assert.DoesNotContain(result, f => f.Path.StartsWith(proj, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void An_already_empty_file_keeps_its_entry_because_presence_is_the_rule()
    {
        //a file emptied by stripping is dropped, an empty labelled block wastes the prefix. a file that was already empty keeps its entry under the presence rule.
        var proj = Dir("emptykeep");
        Write(Path.Combine(proj, "GATTO.md"), "");

        var result = ContextFiles.Collect(proj, false, null);

        Assert.Contains(result, f => f.Path == Path.Combine(proj, "GATTO.md") && f.Content == "");
    }

    [Fact]
    public void Text_either_side_of_a_comment_survives_byte_for_byte()
    {
        var proj = Dir("keep");
        Write(Path.Combine(proj, "GATTO.md"), "# Title\n<!-- why: ask the owner -->\nrun tests with x\n");

        var result = ContextFiles.Collect(proj, false, null);

        var entry = result[IndexOfPath(result, Path.Combine(proj, "GATTO.md"))];
        Assert.Equal("# Title\n\nrun tests with x\n", entry.Content);
    }

    [Fact]
    public void The_home_layer_is_stripped_too()
    {
        var homeMd = Dir("striphome", "GATTO.md");
        Write(homeMd, "global<!-- private note -->rules");
        var proj = Dir("stripproj");
        Write(Path.Combine(proj, "GATTO.md"), "project");

        var result = ContextFiles.Collect(proj, false, homeMd);

        Assert.Equal("globalrules", result[0].Content);
    }
}
