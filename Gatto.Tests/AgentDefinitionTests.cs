using Gatto.Extensions;
using Gatto.Roles.Agents;

namespace Gatto.Tests;

public sealed class AgentDefinitionTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-agents-home-").FullName;
    private readonly string _project = Directory.CreateTempSubdirectory("gatto-agents-proj-").FullName;

    public void Dispose()
    {
        Directory.Delete(_home, recursive: true);
        Directory.Delete(_project, recursive: true);
    }

    private static readonly ISet<string> Builtins =
        new HashSet<string>(StringComparer.Ordinal) { "read_file", "write_file", "edit_file", "glob", "grep", "shell" };

    private void WriteHome(string name, string text) => Write(_home, name, text);
    private void WriteProject(string name, string text) => Write(_project, name, text);

    private static void Write(string dir, string name, string text)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"{name}.md"), text);
    }

    private IReadOnlyList<AgentDefinition> Load(List<string>? diagnostics = null)
    {
        diagnostics ??= new List<string>();
        return AgentDefinitions.LoadAll(_home, _project, Builtins, diagnostics.Add);
    }

    private const string HappyPath = """
        ---
        description: A test agent for happy-path parsing.
        tools: read_file, glob
        max_turns: 5
        ---
        You are a test agent. Do the thing.
        """;

    [Fact]
    public void Frontmatter_happy_path_parses_every_field()
    {
        WriteHome("tester", HappyPath);

        var defs = Load();

        var def = Assert.Single(defs);
        Assert.Equal("tester", def.Name);
        Assert.Equal("A test agent for happy-path parsing.", def.Description);
        Assert.Equal(new[] { "read_file", "glob" }, def.Tools);
        Assert.Equal(5, def.MaxTurns);
        Assert.Equal("You are a test agent. Do the thing.", def.SystemPrompt);
    }

    [Fact]
    public void Duplicate_tool_name_is_deduped_with_diagnostic()
    {
        //report the duplicate as a diagnostic at load time and keep the definition (every run_agent call throws the duplicate tool name error)
        WriteHome("dupes", """
            ---
            description: An agent with a duplicated tool.
            tools: read_file, read_file, glob
            ---
            Do the thing.
            """);
        var diags = new List<string>();

        var defs = Load(diags);

        var def = Assert.Single(defs);
        Assert.Equal(new[] { "read_file", "glob" }, def.Tools);
        Assert.Contains(diags, d => d.Contains("read_file") && d.Contains("duplicate"));
    }

    [Fact]
    public void Max_turns_defaults_to_ten_when_key_absent()
    {
        WriteHome("tester", """
            ---
            description: no max_turns key here.
            tools: read_file
            ---
            Body text.
            """);

        var def = Assert.Single(Load());
        Assert.Equal(10, def.MaxTurns);
    }

    [Fact]
    public void Project_overrides_home_by_name()
    {
        WriteHome("tester", HappyPath);
        WriteProject("tester", """
            ---
            description: The project version wins.
            tools: grep
            max_turns: 3
            ---
            Project body.
            """);

        var def = Assert.Single(Load());
        Assert.Equal("The project version wins.", def.Description);
        Assert.Equal(new[] { "grep" }, def.Tools);
        Assert.Equal(3, def.MaxTurns);
        Assert.Equal("Project body.", def.SystemPrompt);
    }

    [Fact]
    public void A_PROJECT_OVERRIDE_is_announced_never_silent()
    {
        //opening a project can swap the agent the user wrote at home, so the swap is said at launch with the file that made it
        WriteHome("tester", HappyPath);
        WriteProject("tester", HappyPath);
        WriteProject("other", HappyPath);
        var diagnostics = new List<string>();

        Load(diagnostics);

        var said = Assert.Single(diagnostics);
        Assert.Equal(
            $"agent 'tester' at {Path.Combine(_project, "tester.md")} overrides the home agent of the same name", said);
    }

    [Fact]
    public void Distinct_names_from_home_and_project_both_survive()
    {
        WriteHome("alpha", HappyPath);
        WriteProject("beta", HappyPath);

        var defs = Load();

        Assert.Equal(2, defs.Count);
        Assert.Contains(defs, d => d.Name == "alpha");
        Assert.Contains(defs, d => d.Name == "beta");
    }

    [Fact]
    public void Missing_description_skips_file_with_diagnostic()
    {
        WriteHome("bad", """
            ---
            tools: read_file
            max_turns: 5
            ---
            Body text.
            """);

        var diagnostics = new List<string>();
        var defs = Load(diagnostics);

        Assert.Empty(defs);
        Assert.Contains(diagnostics, d => d.Contains("bad") && d.Contains("description"));
    }

    [Fact]
    public void Blank_description_skips_file_with_diagnostic()
    {
        WriteHome("bad", """
            ---
            description:
            tools: read_file
            ---
            Body text.
            """);

        var diagnostics = new List<string>();
        var defs = Load(diagnostics);

        Assert.Empty(defs);
        Assert.Contains(diagnostics, d => d.Contains("bad"));
    }

    [Fact]
    public void Missing_tools_skips_file_with_diagnostic()
    {
        WriteHome("bad", """
            ---
            description: no tools key.
            max_turns: 5
            ---
            Body text.
            """);

        var diagnostics = new List<string>();
        var defs = Load(diagnostics);

        Assert.Empty(defs);
        Assert.Contains(diagnostics, d => d.Contains("bad") && d.Contains("tools"));
    }

    [Fact]
    public void All_unknown_tools_yields_empty_surviving_list_and_skips_with_diagnostic()
    {
        WriteHome("bad", """
            ---
            description: only unknown tools.
            tools: run_agent, some_extension
            ---
            Body text.
            """);

        var diagnostics = new List<string>();
        var defs = Load(diagnostics);

        Assert.Empty(defs);
        Assert.Contains(diagnostics, d => d.Contains("run_agent"));
        Assert.Contains(diagnostics, d => d.Contains("some_extension"));
        Assert.Contains(diagnostics, d => d.Contains("bad") && d.Contains("no valid tools"));
    }

    [Fact]
    public void Unknown_tool_among_known_ones_is_dropped_but_definition_survives()
    {
        WriteHome("mixed", """
            ---
            description: a mix of known and unknown tools.
            tools: read_file, run_agent, grep
            ---
            Body text.
            """);

        var diagnostics = new List<string>();
        var defs = Load(diagnostics);

        var def = Assert.Single(defs);
        Assert.Equal(new[] { "read_file", "grep" }, def.Tools);
        Assert.Contains(diagnostics, d => d.Contains("run_agent") && d.Contains("dropped"));
    }

    [Fact]
    public void Run_agent_in_tools_line_is_dropped()
    {
        WriteHome("selfref", """
            ---
            description: tries to call itself.
            tools: read_file, run_agent
            ---
            Body text.
            """);

        var def = Assert.Single(Load());
        Assert.DoesNotContain("run_agent", def.Tools);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("not-a-number")]
    public void Bad_max_turns_skips_file_with_diagnostic(string badValue)
    {
        WriteHome("bad", $"""
            ---
            description: bad max_turns.
            tools: read_file
            max_turns: {badValue}
            ---
            Body text.
            """);

        var diagnostics = new List<string>();
        var defs = Load(diagnostics);

        Assert.Empty(defs);
        Assert.Contains(diagnostics, d => d.Contains("bad") && d.Contains("max_turns"));
    }

    [Fact]
    public void Empty_body_skips_file_with_diagnostic()
    {
        WriteHome("bad", """
            ---
            description: an empty body.
            tools: read_file
            ---
            """);

        var diagnostics = new List<string>();
        var defs = Load(diagnostics);

        Assert.Empty(defs);
        Assert.Contains(diagnostics, d => d.Contains("bad"));
    }

    [Fact]
    public void Whitespace_only_body_skips_file_with_diagnostic()
    {
        WriteHome("bad", "---\ndescription: whitespace body.\ntools: read_file\n---\n   \n\t\n");

        var diagnostics = new List<string>();
        var defs = Load(diagnostics);

        Assert.Empty(defs);
        Assert.Contains(diagnostics, d => d.Contains("bad"));
    }

    [Fact]
    public void No_frontmatter_skips_file_with_diagnostic()
    {
        WriteHome("bad", "Just a plain markdown file, no frontmatter at all.\n");

        var diagnostics = new List<string>();
        var defs = Load(diagnostics);

        Assert.Empty(defs);
        Assert.Contains(diagnostics, d => d.Contains("bad"));
    }

    [Fact]
    public void Missing_closing_delimiter_skips_file_with_diagnostic()
    {
        WriteHome("bad", """
            ---
            description: never closes.
            tools: read_file
            Body text with no closing delimiter.
            """);

        var diagnostics = new List<string>();
        var defs = Load(diagnostics);

        Assert.Empty(defs);
        Assert.Contains(diagnostics, d => d.Contains("bad"));
    }

    [Fact]
    public void Missing_directories_yield_empty_list_without_throwing()
    {
        var defs = AgentDefinitions.LoadAll(
            Path.Combine(_home, "nosuch"), Path.Combine(_project, "nosuch"), Builtins, _ => { });

        Assert.Empty(defs);
    }

    [Fact]
    public void Tolerates_key_colon_space_spacing()
    {
        WriteHome("spaced", "---\ndescription : spaced colon.\ntools : read_file , grep\nmax_turns : 7\n---\nBody.\n");

        var def = Assert.Single(Load());
        Assert.Equal("spaced colon.", def.Description);
        Assert.Equal(new[] { "read_file", "grep" }, def.Tools);
        Assert.Equal(7, def.MaxTurns);
    }

    public static IEnumerable<object[]> ShippedAgentPaths =>
        new[] { "spec-review", "plan-review", "code-review", "security-review" }
            .Select(n => new object[] { $"agents/{n}.md" });

    [Theory]
    [MemberData(nameof(ShippedAgentPaths))]
    public void Shipped_reviewer_text_parses_clean_with_readonly_tools_subset(string relPath)
    {
        Assert.True(ShippedExtensions.Files.ContainsKey(relPath));
        var text = ShippedExtensions.Files[relPath];

        var name = Path.GetFileNameWithoutExtension(relPath);
        WriteHome(name, text);

        var diagnostics = new List<string>();
        var defs = Load(diagnostics);

        Assert.Empty(diagnostics);
        var def = Assert.Single(defs);
        Assert.Equal(name, def.Name);
        Assert.NotEmpty(def.Description);
        Assert.NotEmpty(def.Tools);
        Assert.True(def.Tools.All(t => t is "read_file" or "glob" or "grep"));
        Assert.Equal(10, def.MaxTurns);
        Assert.NotEmpty(def.SystemPrompt);
    }

    [Fact]
    public void Four_shipped_agent_files_are_registered_in_ShippedExtensions()
    {
        foreach (var n in new[] { "spec-review", "plan-review", "code-review", "security-review" })
            Assert.True(ShippedExtensions.Files.ContainsKey($"agents/{n}.md"));
    }
}
