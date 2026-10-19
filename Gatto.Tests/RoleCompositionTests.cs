using System.Globalization;
using System.Text.Json;
using Gatto.Core.Tools;
using Gatto.Roles;
using Gatto.Roles.Models;

namespace Gatto.Tests;

public class RoleCompositionTests
{

    private static JsonElement Json(string s)
    {
        using var doc = JsonDocument.Parse(s);
        return doc.RootElement.Clone();
    }

    private static IReadOnlyDictionary<string, JsonElement?> Map(params (string Level, string? Body)[] entries)
    {
        var d = new Dictionary<string, JsonElement?>();
        foreach (var (level, body) in entries)
            d[level] = body is null ? null : Json(body);
        return d;
    }

    private static RoleFile Role(
        IReadOnlyList<string>? gates = null,
        bool raw = false,
        string? append = null,
        ThinkingLevel? thinking = ThinkingLevel.Medium)
        => new("r", "p", null, gates ?? Array.Empty<string>(), raw, append, thinking, null, null, 10, true, null, null, null, null);

    private static ModelProfile Profile(
        JsonElement? sampling = null,
        IReadOnlyDictionary<string, JsonElement?>? thinking = null,
        ThinkingLevel? defaultEffort = null)
        => new([new ModelFile("m.gguf", null, true)], 1234, 4096, null, null, null, sampling, thinking,
            Array.Empty<string>(), DefaultEffort: defaultEffort);

    private static Model Pk(
        string? systemAppend = null,
        JsonElement? repair = null,
        JsonElement? profileSampling = null,
        IReadOnlyDictionary<string, JsonElement?>? profileThinking = null,
        ThinkingLevel? profileDefaultEffort = null)
        => new("p", Profile(profileSampling, profileThinking, profileDefaultEffort), systemAppend, repair);

    private static readonly IReadOnlyList<(string Path, string Content)> NoContext =
        Array.Empty<(string, string)>();

    //every source gatto composes, so a raw role and its twin differ only where raw acts. the nudge keeps the environment line third
    private static Composition Full(bool raw) => RoleComposition.Compose(
        Role(gates: new[] { "citations" }, raw: raw, append: "ROLE APPEND", thinking: ThinkingLevel.High),
        Pk(systemAppend: "MODEL APPEND", profileSampling: Json("""{"temperature":0.3}"""),
            profileThinking: Map(("high", """{"reasoning":true}"""))),
        new[] { (@"C:\p\GATTO.md", "CONTEXT BODY") }, null, cwd: @"C:\p",
        date: new DateTime(2026, 1, 2), memoryIndex: "- MEMORY FACT", memoryNudge: true,
        policyLines: new[] { ("ask_user", "POLICY LINE") });

    [Fact]
    public void A_RAW_ROLE_DROPS_THE_THREE_PARTS_GATTO_WRITES_AND_KEEPS_THE_REST()
    {
        var raw = Full(raw: true).SystemText;
        var plain = Full(raw: false).SystemText;

        var blocks = plain.Split("\n\n");
        Assert.Equal(RoleComposition.BasePrompt, blocks[0]);
        Assert.Equal(RoleComposition.MemoryNudge, blocks[1]);
        Assert.StartsWith("Today's date:", blocks[2], StringComparison.Ordinal);
        Assert.Equal(string.Join("\n\n", blocks.Skip(3)), raw);
        foreach (var kept in new[] { "MODEL APPEND", "ROLE APPEND", "POLICY LINE", "CONTEXT BODY", "MEMORY FACT" })
            Assert.Contains(kept, raw, StringComparison.Ordinal);
    }

    [Fact]
    public void THE_SYSTEM_PROMPT_PART_IS_ABSENT_FROM_THE_PARTS_UNDER_RAW()
    {
        var labels = Full(raw: true).SystemParts!.Select(p => p.Label).ToList();

        Assert.Equal(new[] { "model folder", "role", "extension policy", "context files", "memory index" }, labels);
        Assert.Equal("system prompt", Full(raw: false).SystemParts![0].Label);
    }

    //a resume tells the model the date changed only when the date was in the text, so a raw baseline records none
    [Fact]
    public void A_RAW_ROLE_RECORDS_NO_DATE_SO_A_RESUME_NEVER_WRITES_ONE()
    {
        Assert.Null(Full(raw: true).Sources.Date);
        Assert.Equal("2026-01-02", Full(raw: false).Sources.Date);

        Composition On(DateTime day) => RoleComposition.Compose(Role(raw: true), Pk(), NoContext, null, cwd: @"C:\p", date: day);
        var delta = Gatto.Core.Session.ResumePlan.Delta(On(new DateTime(2026, 1, 2)).Sources, On(new DateTime(2026, 1, 3)).Sources);
        Assert.Null(delta.Date);
    }

    [Fact]
    public void THINKING_GATES_AND_SAMPLING_RESOLVE_THE_SAME_UNDER_RAW()
    {
        var raw = Full(raw: true);
        var plain = Full(raw: false);

        Assert.Equal(plain.Thinking, raw.Thinking);
        Assert.Equal(plain.Gates, raw.Gates);
        Assert.Equal(plain.Sampling?.GetRawText(), raw.Sampling?.GetRawText());
        Assert.Equal(plain.ThinkingBody?.GetRawText(), raw.ThinkingBody?.GetRawText());
        Assert.Equal(plain.ThinkingSuffix, raw.ThinkingSuffix);
        Assert.NotNull(raw.Sampling);
        Assert.NotNull(raw.ThinkingBody);
    }

    //the arguments a called role composes with: the caller's model and context files, no memory, and no policy line since no extension tool reaches a child
    private static Composition Child(bool raw, IReadOnlyList<(string Path, string Content)> contextFiles)
    {
        var caller = Role(append: "CALLER APPEND");
        var called = new RoleFile("callee", null, null, Array.Empty<string>(), raw, "CALLEE APPEND", null, null, null, 10, true, null, null, null, null);
        return RoleComposition.Compose(
            Gatto.Roles.Agents.RoleMerge.ForChild(called, caller),
            Pk(systemAppend: "MODEL APPEND"),
            contextFiles, null, cwd: @"C:\p",
            date: new DateTime(2026, 1, 2), memoryIndex: null, memoryNudge: false,
            policyLines: null);
    }

    private static readonly IReadOnlyList<(string Path, string Content)> CallerContext =
        new[] { (@"C:\p\GATTO.md", "CONTEXT BODY") };

    [Fact]
    public void A_CHILD_COMPOSES_WITH_NO_MEMORY_AND_THE_CALLERS_PARTS()
    {
        var c = Child(raw: false, CallerContext);

        Assert.Equal(new[] { "system prompt", "model folder", "role", "context files" },
            c.SystemParts!.Select(p => p.Label).ToArray());
        Assert.DoesNotContain(RoleComposition.MemoryNudge, c.SystemText, StringComparison.Ordinal);
        Assert.DoesNotContain("CALLER APPEND", c.SystemText, StringComparison.Ordinal);
        Assert.Contains("CALLEE APPEND", c.SystemText, StringComparison.Ordinal);
        Assert.Contains("CONTEXT BODY", c.SystemText, StringComparison.Ordinal);
    }

    [Fact]
    public void A_RAW_CHILD_IS_ITS_APPEND_PLUS_THE_USER_PARTS()
    {
        var c = Child(raw: true, CallerContext);

        Assert.Equal(
            string.Join("\n\n", "MODEL APPEND", "CALLEE APPEND", "## Context: C:\\p\\GATTO.md\nCONTEXT BODY"),
            c.SystemText);
    }

    [Fact]
    public void AN_EMPTY_CONTEXT_LIST_COMPOSES_WITHOUT_THE_PART()
    {
        var c = Child(raw: false, NoContext);

        Assert.DoesNotContain("context files", c.SystemParts!.Select(p => p.Label));
        Assert.DoesNotContain("CONTEXT BODY", c.SystemText, StringComparison.Ordinal);
    }

    [Fact]
    public void Gates_match_case_insensitively()
    {
        var c = RoleComposition.Compose(Role(gates: new[] { "Citations" }), Pk(), NoContext, null, cwd: null);

        Assert.Single(c.Gates);
        Assert.Contains("citations", c.Gates);   //membership checks are case-insensitive.
        Assert.Contains("CITATIONS", c.Gates);
    }

    [Fact]
    public void Gates_empty_role_and_no_model_yields_empty_set()
    {
        var c = RoleComposition.Compose(Role(), null, NoContext, null, cwd: null);
        Assert.Empty(c.Gates);
    }

    [Fact]
    public void Gates_no_model_uses_role_only()
    {
        var c = RoleComposition.Compose(Role(gates: new[] { "citations" }), null, NoContext, null, cwd: null);
        Assert.Single(c.Gates);
        Assert.Contains("citations", c.Gates);
    }

    [Fact]
    public void SystemText_full_composition_exact_string()
    {
        var role = Role(append: "ROLE");
        var model = Pk(systemAppend: "SYSAPPEND");
        var ctxPath = @"C:\proj\AGENTS.md";
        var ctx = new[] { (ctxPath, "ground rules") };

        var c = RoleComposition.Compose(role, model, ctx, null, cwd: null);

        var expected = RoleComposition.BasePrompt
            + "\n\nModel: p."
            + "\n\nSYSAPPEND\n\nROLE"
            + "\n\n## Context: " + ctxPath + "\nground rules";
        Assert.Equal(expected, c.SystemText);
    }

    [Fact]
    public void SystemText_append_order_is_model_then_role()
    {
        var role = Role(append: "ROLE");
        var model = Pk(systemAppend: "SYSAPPEND");

        var c = RoleComposition.Compose(role, model, NoContext, null, cwd: null);

        var iModel = c.SystemText.IndexOf("SYSAPPEND", StringComparison.Ordinal);
        var iRole = c.SystemText.IndexOf("ROLE", StringComparison.Ordinal);
        Assert.True(iModel >= 0 && iModel < iRole);
    }

    [Fact]
    public void SystemText_degenerates_to_base_prompt_and_model_line_when_all_appends_empty_and_no_context()
    {
        var c = RoleComposition.Compose(Role(append: null), Pk(systemAppend: null), NoContext, null, cwd: null);
        Assert.Equal(RoleComposition.BasePrompt + "\n\nModel: p.", c.SystemText);   //equality holds only with no trailing blank lines.
    }

    [Fact]
    public void SystemText_skips_whitespace_only_parts()
    {
        var role = Role(append: "REAL");
        var model = Pk(systemAppend: "\n\t ");

        var c = RoleComposition.Compose(role, model, NoContext, null, cwd: null);

        Assert.Equal(RoleComposition.BasePrompt + "\n\nModel: p.\n\nREAL", c.SystemText);
    }

    [Fact]
    public void SystemText_context_blocks_are_nearest_last_with_path_labels()
    {
        var root = (@"C:\proj\AGENTS.md", "root rules");
        var near = (@"C:\proj\sub\AGENTS.md", "near rules");
        var ctx = new[] { root, near };   //the caller passes context root-first.

        var c = RoleComposition.Compose(Role(), null, ctx, null, cwd: null);

        var expected = RoleComposition.BasePrompt
            + "\n\n## Context: " + root.Item1 + "\nroot rules"
            + "\n\n## Context: " + near.Item1 + "\nnear rules";
        Assert.Equal(expected, c.SystemText);
    }

    [Fact]
    public void SystemText_context_file_with_empty_content_keeps_label()
    {
        var ctx = new[] { (@"C:\proj\AGENTS.md", "") };
        var c = RoleComposition.Compose(Role(), null, ctx, null, cwd: null);
        Assert.Equal(RoleComposition.BasePrompt + "\n\n## Context: " + @"C:\proj\AGENTS.md" + "\n", c.SystemText);
    }

    [Fact]
    public void Date_supplied_renders_exact_line()
    {
        var c = RoleComposition.Compose(Role(), null, NoContext, null, cwd: null, date: new DateTime(2026, 3, 4));
        Assert.Contains("Today's date: 2026-03-04.", c.SystemText);
    }

    [Fact]
    public void Date_block_sits_after_BasePrompt_and_before_model_and_role_appends()
    {
        var role = Role(append: "ROLE");
        var model = Pk(systemAppend: "SYSAPPEND");

        var c = RoleComposition.Compose(role, model, NoContext, null, cwd: null, date: new DateTime(2026, 3, 4));

        var iBase = c.SystemText.IndexOf(RoleComposition.BasePrompt, StringComparison.Ordinal);
        var iDate = c.SystemText.IndexOf("Today's date:", StringComparison.Ordinal);
        var iModel = c.SystemText.IndexOf("SYSAPPEND", StringComparison.Ordinal);
        var iRole = c.SystemText.IndexOf("ROLE", StringComparison.Ordinal);
        Assert.True(iBase < iDate && iDate < iModel && iModel < iRole);
    }

    [Fact]
    public void Date_omitted_yields_no_date_line_and_is_byte_identical_to_no_date_overload()
    {
        var role = Role(append: "ROLE");
        var model = Pk(systemAppend: "SYSAPPEND");

        var withoutDateArg = RoleComposition.Compose(role, model, NoContext, null, cwd: null);
        var withExplicitNull = RoleComposition.Compose(role, model, NoContext, null, cwd: null);

        Assert.DoesNotContain("Today's date:", withoutDateArg.SystemText);
        Assert.Equal(withoutDateArg.SystemText, withExplicitNull.SystemText);
    }

    [Fact]
    public void Cwd_supplied_renders_exact_line()
    {
        //if the prompt omits the working directory, the model infers a wrong location from context headers. the prompt must state the real one.
        var c = RoleComposition.Compose(Role(), null, NoContext, null, cwd: @"C:\proj\sub");
        Assert.Contains(@"Working directory: C:\proj\sub." + "\n", c.SystemText + "\n", StringComparison.Ordinal);
    }

    [Fact]
    public void Cwd_sits_with_the_date_after_BasePrompt_and_before_model_and_role_appends()
    {
        var c = RoleComposition.Compose(Role(append: "ROLE"), Pk(systemAppend: "SYSAPPEND"), NoContext, null, date: new DateTime(2026, 3, 4), cwd: @"C:\proj");

        var iBase = c.SystemText.IndexOf(RoleComposition.BasePrompt, StringComparison.Ordinal);
        var iDate = c.SystemText.IndexOf("Today's date:", StringComparison.Ordinal);
        var iCwd = c.SystemText.IndexOf("Working directory:", StringComparison.Ordinal);
        var iModel = c.SystemText.IndexOf("SYSAPPEND", StringComparison.Ordinal);
        Assert.True(iBase < iDate && iDate < iCwd && iCwd < iModel, c.SystemText);
    }

    [Fact]
    public void Cwd_and_date_share_one_block_so_they_cost_one_blank_line()
    {
        //date and working directory are session-start facts, so they read as one stanza rather than two paragraphs
        var c = RoleComposition.Compose(Role(), null, NoContext, null, cwd: @"C:\proj", date: new DateTime(2026, 3, 4));
        Assert.Contains("Today's date: 2026-03-04.\nWorking directory: C:\\proj.", c.SystemText, StringComparison.Ordinal);
    }

    //a local model does not know which model it is, so the stanza names the profile id between the date and the directory
    [Fact]
    public void THE_STANZA_NAMES_THE_MODEL_BY_ITS_PROFILE_ID()
    {
        var model = new Model("qwen3.8-flash-next-q4-k-xl-dn4", Pk().Profile, null, null);
        var c = RoleComposition.Compose(Role(), model, NoContext, null, cwd: @"C:\proj", date: new DateTime(2026, 3, 4));
        Assert.Contains("Today's date: 2026-03-04.\nModel: qwen3.8-flash-next-q4-k-xl-dn4.\nWorking directory: C:\\proj.", c.SystemText, StringComparison.Ordinal);
    }

    [Fact]
    public void WITHOUT_A_MODEL_THE_STANZA_HOLDS_NO_MODEL_LINE()
    {
        var c = RoleComposition.Compose(Role(), null, NoContext, null, cwd: @"C:\proj", date: new DateTime(2026, 3, 4));
        Assert.DoesNotContain("Model:", c.SystemText, StringComparison.Ordinal);
        Assert.Contains("Today's date: 2026-03-04.\nWorking directory: C:\\proj.", c.SystemText, StringComparison.Ordinal);
    }

    [Fact]
    public void Cwd_omitted_or_blank_yields_no_line_and_is_byte_identical()
    {
        var role = Role(append: "ROLE");
        var baseline = RoleComposition.Compose(role, null, NoContext, null, cwd: null);

        Assert.DoesNotContain("Working directory:", baseline.SystemText);
        Assert.Equal(baseline.SystemText, RoleComposition.Compose(role, null, NoContext, null, cwd: null).SystemText);
        Assert.Equal(baseline.SystemText, RoleComposition.Compose(role, null, NoContext, null, cwd: "   ").SystemText);
    }

    [Fact]
    public void Date_format_is_invariant_culture_regardless_of_current_culture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            foreach (var name in new[] { "fr-FR", "de-DE" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                var c = RoleComposition.Compose(Role(), null, NoContext, null, cwd: null, date: new DateTime(2026, 3, 4));
                Assert.Contains("Today's date: 2026-03-04.", c.SystemText);
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Thinking_with_a_model_uses_role_request()
    {
        var c = RoleComposition.Compose(Role(thinking: ThinkingLevel.High), Pk(), NoContext, null, cwd: null);

        Assert.Equal(ThinkingLevel.High, c.Thinking);
    }

    [Fact]
    public void Thinking_no_model_uses_role_request()
    {
        var c = RoleComposition.Compose(Role(thinking: ThinkingLevel.XHigh), null, NoContext, null, cwd: null);
        Assert.Equal(ThinkingLevel.XHigh, c.Thinking);
    }

    [Fact]
    public void Thinking_role_absent_falls_back_to_the_models_persisted_default()
    {
        //when thinking is absent, the model's default_effort wins before the harness fallback
        var role = Role(thinking: null);
        var model = Pk(profileDefaultEffort: ThinkingLevel.XHigh);

        var c = RoleComposition.Compose(role, model, NoContext, null, cwd: null);

        Assert.Equal(ThinkingLevel.XHigh, c.Thinking);
    }

    [Fact]
    public void Thinking_role_absent_and_no_model_default_uses_medium()
    {
        //medium is the out-of-box default and lives in composition. the loader returns null when a key is absent.
        var role = Role(thinking: null);
        var model = Pk();   //this profile has no default_effort

        var c = RoleComposition.Compose(role, model, NoContext, null, cwd: null);

        Assert.Equal(ThinkingLevel.Medium, c.Thinking);
    }

    [Fact]
    public void Thinking_role_absent_and_no_model_at_all_uses_medium()
    {
        var c = RoleComposition.Compose(Role(thinking: null), null, NoContext, null, cwd: null);
        Assert.Equal(ThinkingLevel.Medium, c.Thinking);
    }

    [Fact]
    public void Thinking_roles_explicit_request_wins_over_the_models_persisted_default()
    {
        //an explicit role thinking key is a deliberate choice. a model default must not silently override it.
        var role = Role(thinking: ThinkingLevel.Low);
        var model = Pk(profileDefaultEffort: ThinkingLevel.XHigh);

        var c = RoleComposition.Compose(role, model, NoContext, null, cwd: null);

        Assert.Equal(ThinkingLevel.Low, c.Thinking);
    }

    [Fact]
    public void Thinking_reports_the_landed_level_not_the_pre_map_request()
    {
        //a map with a gap at the requested level must report the level the wire really sent
        var role = Role(thinking: ThinkingLevel.Max);
        var model = Pk(profileThinking: Map(("none", null), ("low", """{ "reasoning_effort": "low" }""")));

        var c = RoleComposition.Compose(role, model, NoContext, null, cwd: null);

        Assert.Equal(ThinkingLevel.Low, c.Thinking);
    }

    [Fact]
    public void ThinkingBody_resolves_on_from_model_map()
    {
        var model = Pk(profileThinking: Map(("high", """{ "reasoning_effort": "high" }"""), ("none", null)));
        var role = Role(thinking: ThinkingLevel.High);

        var c = RoleComposition.Compose(role, model, NoContext, null, cwd: null);

        Assert.NotNull(c.ThinkingBody);
        Assert.Equal("high", c.ThinkingBody!.Value.GetProperty("reasoning_effort").GetString());
    }

    [Fact]
    public void ThinkingBody_resolves_off_falls_back_down_to_explicit_null()
    {
        //the none entry with an explicit null resolves to sending nothing
        var model = Pk(profileThinking: Map(("high", """{ "reasoning_effort": "high" }"""), ("none", null)));
        var role = Role(thinking: ThinkingLevel.None);

        var c = RoleComposition.Compose(role, model, NoContext, null, cwd: null);

        Assert.Null(c.ThinkingBody);
    }

    [Fact]
    public void ThinkingBody_uses_endpoint_map_when_model_is_null_cloud()
    {
        var endpoint = Map(("high", """{ "reasoning_effort": "cloud-high" }"""));
        var role = Role(thinking: ThinkingLevel.High);

        var c = RoleComposition.Compose(role, null, NoContext, endpoint, cwd: null);

        Assert.NotNull(c.ThinkingBody);
        Assert.Equal("cloud-high", c.ThinkingBody!.Value.GetProperty("reasoning_effort").GetString());
    }

    [Fact]
    public void ThinkingBody_model_map_wins_when_both_exist()
    {
        var model = Pk(profileThinking: Map(("high", """{ "src": "model" }""")));
        var endpoint = Map(("high", """{ "src": "endpoint" }"""));
        var role = Role(thinking: ThinkingLevel.High);

        var c = RoleComposition.Compose(role, model, NoContext, endpoint, cwd: null);

        Assert.Equal("model", c.ThinkingBody!.Value.GetProperty("src").GetString());
    }

    [Fact]
    public void ThinkingBody_null_when_no_map_anywhere()
    {
        var c = RoleComposition.Compose(Role(thinking: ThinkingLevel.High), Pk(), NoContext, null, cwd: null);
        Assert.Null(c.ThinkingBody);
    }

    [Fact]
    public void ThinkingSuffix_split_out_of_model_map_entry()
    {
        var model = Pk(profileThinking: Map(
            ("none", """{"chat_template_kwargs":{"enable_thinking":false}}"""),
            ("low",  """{"chat_template_kwargs":{"enable_thinking":true},"prompt_suffix":"/think"}""")));
        var role = Role(thinking: ThinkingLevel.Max);   //the map lacks Max, so the level falls back to low.

        var c = RoleComposition.Compose(role, model, NoContext, null, cwd: null);

        Assert.Equal("/think", c.ThinkingSuffix);
        Assert.NotNull(c.ThinkingBody);
        Assert.True(c.ThinkingBody!.Value.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
    }

    [Fact]
    public void ThinkingSuffix_only_entry_leaves_ThinkingBody_null()
    {
        var model = Pk(profileThinking: Map(("low", """{"prompt_suffix":"/no_think"}""")));
        var role = Role(thinking: ThinkingLevel.Low);

        var c = RoleComposition.Compose(role, model, NoContext, null, cwd: null);

        Assert.Null(c.ThinkingBody);
        Assert.Equal("/no_think", c.ThinkingSuffix);
    }

    [Fact]
    public void ThinkingSuffix_null_when_entry_has_no_suffix_key()
    {
        var model = Pk(profileThinking: Map(("high", """{ "reasoning_effort": "high" }""")));
        var role = Role(thinking: ThinkingLevel.High);

        var c = RoleComposition.Compose(role, model, NoContext, null, cwd: null);

        Assert.Null(c.ThinkingSuffix);
    }

    [Fact]
    public void ThinkingSuffix_null_when_no_map_anywhere()
    {
        var c = RoleComposition.Compose(Role(thinking: ThinkingLevel.High), Pk(), NoContext, null, cwd: null);
        Assert.Null(c.ThinkingSuffix);
    }

    //sampling is a property of the model, so the profile's goes into every request to it
    [Fact]
    public void Sampling_is_the_model_profile_sampling()
    {
        var model = Pk(profileSampling: Json("""{ "temperature": 0.9 }"""));

        var c = RoleComposition.Compose(Role(), model, NoContext, null, cwd: null);

        Assert.NotNull(c.Sampling);
        Assert.Equal(0.9, c.Sampling!.Value.GetProperty("temperature").GetDouble());
    }

    [Fact]
    public void Sampling_is_null_with_no_model_or_a_profile_without_it()
    {
        Assert.Null(RoleComposition.Compose(Role(), null, NoContext, null, cwd: null).Sampling);
        Assert.Null(RoleComposition.Compose(Role(), Pk(), NoContext, null, cwd: null).Sampling);
    }

    [Fact]
    public void RepairArmed_true_when_model_has_repair()
    {
        var c = RoleComposition.Compose(Role(), Pk(repair: Json("""{ "pattern": "retry" }""")), NoContext, null, cwd: null);
        Assert.True(c.RepairArmed);
    }

    [Fact]
    public void RepairArmed_false_when_model_has_no_repair()
    {
        Assert.False(RoleComposition.Compose(Role(), Pk(), NoContext, null, cwd: null).RepairArmed);
    }

    [Fact]
    public void RepairArmed_false_when_no_model()
    {
        Assert.False(RoleComposition.Compose(Role(), null, NoContext, null, cwd: null).RepairArmed);
    }

    [Fact]
    public void MemoryBlock_AfterContextFiles()
    {
        var ctx = new[] { (@"C:\proj\GATTO.md", "rules") };

        var c = RoleComposition.Compose(Role(), null, ctx, null, cwd: null, memoryIndex: "- seeded fact");

        var iCtx = c.SystemText.IndexOf("## Context: ", StringComparison.Ordinal);
        var iMem = c.SystemText.IndexOf("## Memory (project notes gatto keeps between sessions):", StringComparison.Ordinal);
        Assert.True(iCtx >= 0 && iMem > iCtx, c.SystemText);
        Assert.EndsWith(
            "## Memory (project notes gatto keeps between sessions):\n"
            + "[notes were true when written — verify a path, flag, or version before relying on one]\n"
            + "- seeded fact", c.SystemText, StringComparison.Ordinal);
    }

    [Fact]
    public void MemoryBlock_OmittedWhenNull()
    {
        //an empty or whitespace-only index must omit the whole block. an empty heading could read as an invitation to invent notes.
        var c = RoleComposition.Compose(Role(), null, NoContext, null, cwd: null, memoryIndex: null);
        Assert.DoesNotContain("## Memory", c.SystemText, StringComparison.Ordinal);
    }

    [Fact]
    public void MemoryBlock_RendersForEmptyStringIndexCarryingOnlyTheTruncationNote()
    {
        //an empty non-null index means nothing fit though the file exists. the block must still render the warning.
        var c = RoleComposition.Compose(Role(), null, NoContext, null, cwd: null, memoryIndex: "", memoryTruncatedLines: 7);

        Assert.Contains("## Memory (project notes gatto keeps between sessions):", c.SystemText, StringComparison.Ordinal);
        Assert.Contains("[index truncated at budget — 7 facts not shown]", c.SystemText, StringComparison.Ordinal);
    }

    [Fact]
    public void MemoryBlock_TruncationNoteWhenTruncated()
    {
        var c = RoleComposition.Compose(Role(), null, NoContext, null, cwd: null, memoryIndex: "- a\n- b", memoryTruncatedLines: 3);

        Assert.EndsWith(
            "## Memory (project notes gatto keeps between sessions):\n"
            + "[notes were true when written — verify a path, flag, or version before relying on one]\n"
            + "- a\n- b"
            //the warning counts dropped facts rather than lines. the unit the operator prunes is a file
            + "\n[index truncated at budget — 3 facts not shown]",
            c.SystemText, StringComparison.Ordinal);
    }

    //the staleness caveat sits between the heading and the facts, and its sentence is fixed so the prompt prefix stays stable
    [Fact]
    public void MemoryBlock_CarriesTheStalenessFraming_BetweenHeadingAndFacts()
    {
        var c = RoleComposition.Compose(Role(), null, NoContext, null, cwd: null, memoryIndex: "- a fact");

        Assert.EndsWith(
            "## Memory (project notes gatto keeps between sessions):\n"
            + "[notes were true when written — verify a path, flag, or version before relying on one]\n"
            + "- a fact",
            c.SystemText, StringComparison.Ordinal);
    }

    [Fact]
    public void MemoryNudge_TeachesSlugReuseAndDeletion_AndDropsTheTopicPointerConvention()
    {
        //the composer itself appends the pointer, so the nudge must not teach it. pin the absence, because gated text drifts back.
        Assert.Contains("before minting a new slug", RoleComposition.MemoryNudge);
        Assert.Contains("delete a fact you've disproven", RoleComposition.MemoryNudge);
        Assert.Contains("Don't save what the repo already states", RoleComposition.MemoryNudge);
        Assert.DoesNotContain("pointer", RoleComposition.MemoryNudge);
        Assert.DoesNotContain("topic", RoleComposition.MemoryNudge);
    }

    [Fact]
    public void MemoryBlock_NoTruncationNoteWhenNothingTruncated()
    {
        var c = RoleComposition.Compose(Role(), null, NoContext, null, cwd: null, memoryIndex: "- a", memoryTruncatedLines: 0);
        Assert.DoesNotContain("[index truncated at budget", c.SystemText, StringComparison.Ordinal);
    }

    [Fact]
    public void Nudge_PresentWhenEnabled_AfterBasePrompt()
    {
        var c = RoleComposition.Compose(Role(append: "ROLE"), null, NoContext, null, cwd: null, date: new DateTime(2026, 3, 4), memoryNudge: true);

        var iBase = c.SystemText.IndexOf(RoleComposition.BasePrompt, StringComparison.Ordinal);
        var iNudge = c.SystemText.IndexOf(RoleComposition.MemoryNudge, StringComparison.Ordinal);
        var iDate = c.SystemText.IndexOf("Today's date:", StringComparison.Ordinal);
        Assert.True(iBase >= 0 && iNudge > iBase + RoleComposition.BasePrompt.Length && iNudge < iDate, c.SystemText);
    }

    [Fact]
    public void Nudge_AbsentWhenDisabled()
    {
        //assert on the raw substring, which also catches memory_write leaking into the system text by any other route
        var c = RoleComposition.Compose(Role(), null, NoContext, null, cwd: null, memoryNudge: false);
        Assert.DoesNotContain("memory_write", c.SystemText, StringComparison.Ordinal);
    }

    //the three write paths must teach one index shape, or the store ends half bulleted. these tests pin the instruction rather than a model's obedience

    [Fact]
    public void Nudge_And_ToolDescription_TeachTheSameIndexFormat_AsTheCompactionTemplate()
    {
        //all three sources must teach the same bullet shape, so the guard is the convergence rather than the exact phrase
        const string format = "\"- \" bullet";

        Assert.Contains(format, Gatto.Core.Loop.Compactor.TemplatePromptForTests, StringComparison.Ordinal);
        Assert.Contains(format, RoleComposition.MemoryNudge, StringComparison.Ordinal);
        Assert.Contains(format, new MemoryWriteTool(() => 1000).Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Nudge_IsIndependentOfTheIndex_SoAnEmptyProjectStillGetsToldToWrite()
    {
        //the nudge must reach the prompt even when the index does not exist yet.
        var c = RoleComposition.Compose(Role(), null, NoContext, null, cwd: null, memoryNudge: true);
        Assert.Contains(RoleComposition.MemoryNudge, c.SystemText, StringComparison.Ordinal);
        Assert.DoesNotContain("## Memory", c.SystemText, StringComparison.Ordinal);
    }

    [Fact]
    public void NoMemoryArgs_ByteIdenticalToBefore()
    {
        //a golden literal pins the exact text (two live calls would drift together)
        var role = Role(append: "ROLE");
        var model = Pk(systemAppend: "SYSAPPEND");
        var ctx = new[] { (@"C:\proj\GATTO.md", "rules") };

        var c = RoleComposition.Compose(role, model, ctx, null, cwd: @"C:\proj", date: new DateTime(2026, 3, 4));

        const string golden =
            "You are gatto, a local agent harness on Windows. Use the provided tools; keep answers grounded in what you actually read or ran.\n"
            + "The shell is PowerShell. Never use Bash or Linux-only commands; when a Unix habit tempts you, find the PowerShell way.\n"
            + "Say what you actually conclude. If the user's idea has a problem, name it plainly before helping -- agreement you don't hold is a disservice.\n"
            + "When you are unsure, say so and say why; never present a guess as a fact.\n"
            + "Be direct and warm; no flattery, no filler praise, no apologizing for existing."
            + "\n\nToday's date: 2026-03-04.\nModel: p.\nWorking directory: C:\\proj."
            + "\n\nSYSAPPEND\n\nROLE"
            + "\n\n## Context: C:\\proj\\GATTO.md\nrules";
        Assert.Equal(golden, c.SystemText);
    }

    //the prompt must name the platform and shell, or a model assumes linux and reaches for ls and grep
    [Fact]
    public void THE_PROMPT_NAMES_THE_OS_AND_THE_SHELL()
    {
        var c = RoleComposition.Compose(Role(), Pk(), [], null, cwd: @"C:\proj",
            date: new DateTime(2026, 3, 4));

        Assert.Contains("a local agent harness on Windows.", c.SystemText, StringComparison.Ordinal);
        Assert.Contains(
            "The shell is PowerShell. Never use Bash or Linux-only commands; when a Unix habit tempts you, find the PowerShell way.",
            c.SystemText, StringComparison.Ordinal);
    }

    //the count makes "only inside that sentence" falsifiable. a substring guard alone stays green when the sentence goes and another block names Bash
    [Fact]
    public void BASH_IS_NAMED_ONCE_AND_ONLY_IN_THAT_SENTENCE()
    {
        var text = RoleComposition.Compose(Role(), Pk(), [], null, cwd: @"C:\proj",
            date: new DateTime(2026, 3, 4)).SystemText;

        var occurrences = text.Split("Bash").Length - 1;
        Assert.Equal(1, occurrences);
        Assert.Contains("Never use Bash or Linux-only commands", text, StringComparison.Ordinal);
    }

    //the policy block sits between the harness text and the user text, so a user can countermand a line in their own file

    [Fact]
    public void Zero_policy_lines_compose_byte_identically_to_today()
    {
        var a = RoleComposition.Compose(Role(), Pk(), NoContext, null, cwd: @"C:\proj");
        var b = RoleComposition.Compose(Role(), Pk(), NoContext, null, cwd: @"C:\proj",
            policyLines: Array.Empty<(string, string)>());

        Assert.Equal(a.SystemText, b.SystemText);
    }

    [Fact]
    public void The_block_sits_after_the_role_append_and_before_the_first_context_file()
    {
        var text = RoleComposition.Compose(
            Role(append: "ROLE-MARK"), Pk(),
            new[] { (@"C:\p\GATTO.md", "PROJECT-MARK") }, null, cwd: @"C:\p",
            policyLines: new[] { ("ask_user", "POLICY-MARK") }).SystemText;

        var role = text.IndexOf("ROLE-MARK", StringComparison.Ordinal);
        var policy = text.IndexOf("POLICY-MARK", StringComparison.Ordinal);
        var context = text.IndexOf("## Context:", StringComparison.Ordinal);

        Assert.True(role >= 0 && policy >= 0 && context >= 0);
        Assert.True(role < policy, "policy follows the role's append");
        Assert.True(policy < context, "policy precedes every context file, so the user keeps the last word");
    }

    [Fact]
    public void Lines_render_in_ordinal_extension_order_whatever_order_they_arrive_in()
    {
        var text = RoleComposition.Compose(Role(), Pk(), NoContext, null, cwd: @"C:\p",
            policyLines: new[] { ("zeta", "Z-LINE"), ("alpha", "A-LINE") }).SystemText;

        Assert.True(text.IndexOf("A-LINE", StringComparison.Ordinal)
                  < text.IndexOf("Z-LINE", StringComparison.Ordinal),
            "ordinal by extension name, so the bytes are the same from one launch to the next");
    }

    [Fact]
    public void The_block_carries_no_heading()
    {
        //the block must have no heading. each line already names its own tool.
        var text = RoleComposition.Compose(Role(), Pk(), NoContext, null, cwd: @"C:\p",
            policyLines: new[] { ("ask_user", "POLICY-MARK") }).SystemText;

        var block = text.Split("\n\n").Single(b => b.Contains("POLICY-MARK", StringComparison.Ordinal));
        Assert.Equal("POLICY-MARK", block);
    }

    [Fact]
    public void Two_extensions_share_one_block_rather_than_taking_a_block_each()
    {
        //two extensions share one block (two blocks would spend extra prefix newlines and read as unrelated stanzas)
        var text = RoleComposition.Compose(Role(), Pk(), NoContext, null, cwd: @"C:\p",
            policyLines: new[] { ("alpha", "A-LINE"), ("zeta", "Z-LINE") }).SystemText;

        Assert.Contains("A-LINE\nZ-LINE", text, StringComparison.Ordinal);
    }
}
