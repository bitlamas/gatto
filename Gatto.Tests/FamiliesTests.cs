using System.Text.Json;
using Gatto.Core.Acquire;

namespace Gatto.Tests;

//check both ways. every arch a family names must exist in the engine list, and every engine arch named after a family must be classified or excluded
public class FamiliesTests
{
    private static readonly Families F = Families.Load();

    //return the architectures that the engine release can load, read from the same embedded file the family sets are curated against.
    private static IReadOnlySet<string> Engine()
    {
        using var s = typeof(SupportedArchitectures).Assembly
            .GetManifestResourceStream("Gatto.Core.Acquire.llama-architectures.json")
            ?? throw new InvalidOperationException("llama-architectures.json is not embedded");
        using var doc = JsonDocument.Parse(s);
        return doc.RootElement.GetProperty("architectures").EnumerateArray()
            .Select(x => x.GetString() ?? "")
            .Where(x => x.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    //a family that names an architecture the engine list lacks is a chip that silently matches nothing.
    [Fact]
    public void EVERY_ARCH_A_FAMILY_NAMES_EXISTS_IN_THE_ENGINES_OWN_LIST()
    {
        var engine = Engine();
        foreach (var (family, members) in F.Members)
            foreach (var arch in members.Keys)
                Assert.True(engine.Contains(arch),
                    $"family '{family}' names '{arch}', which llama-architectures.json does not have");

        //assert a name the engine list must not have, so a renamed JSON key fails here instead of leaving every assertion vacuously true
        Assert.False(engine.Contains("gemma9"),
            "the planted phantom is in the engine list, so this check can no longer discriminate");
    }

    //only the stems are checked, since checking every architecture would add hundreds of entries nobody reads
    [Fact]
    public void EVERY_ARCH_WEARING_A_FAMILYS_NAME_IS_EITHER_IN_IT_OR_EXCLUDED_WITH_A_REASON()
    {
        foreach (var arch in Engine().Where(Wears))
            Assert.True(F.FamilyOf(arch) is not null || F.Excluded.ContainsKey(arch),
                $"'{arch}' carries a family's naming stem and is neither in a family nor excluded. "
                + "Add it to families.json's family, or to `excluded` with a one-word reason "
                + "(embedding · ocr · superseded · tts). Silence is not an answer.");

        //assert one classified arch and one excluded arch from the engine list, so the loop cannot pass while seeing nothing
        Assert.Contains("qwen3moe", Engine());
        Assert.NotNull(F.FamilyOf("qwen3moe"));
        Assert.Contains("rwkv6qwen2", Engine());
        Assert.True(F.Excluded.ContainsKey("rwkv6qwen2"), "the awkward one must be answered, not skipped");
    }

    private static bool Wears(string arch) =>
        Families.Stems.Any(s => arch.Contains(s, StringComparison.OrdinalIgnoreCase));

    //the stems must equal the ladder names, a dropped stem hides a whole family. a known hit and a known miss prove the matcher sees its subject
    [Fact]
    public void THE_STEM_MATCHER_CAN_SEE_ITS_SUBJECT()
    {
        Assert.Equal([.. F.Ladder.Where(c => c != "all")], Families.Stems);

        Assert.True(Wears("qwen3moe"), "the matcher cannot see an ordinary family arch");
        Assert.True(Wears("rwkv6qwen2"), "the matcher cannot see a stem in the middle of a name");
        Assert.True(Wears("chatglm"), "the matcher cannot see a stem at the end of a name");
        Assert.False(Wears("llama"), "the matcher fires on an arch that belongs to no family");
        Assert.False(Wears("rwkv7"), "the matcher fires on ordinary text");
    }

    //a set curated against one engine release and shipped beside another is worse than no set at all
    [Fact]
    public void THE_FAMILY_SETS_NAME_THE_RELEASE_THEY_WERE_READ_AGAINST()
    {
        using var s = typeof(SupportedArchitectures).Assembly
            .GetManifestResourceStream("Gatto.Core.Acquire.llama-architectures.json")!;
        using var doc = JsonDocument.Parse(s);
        Assert.Equal(doc.RootElement.GetProperty("release").GetString(), F.ArchitecturesRelease);
    }

    //the ladder has one fixed order, and the expected list is a literal. a test that reads the list it asserts can never fail about the order
    [Fact]
    public void THE_LADDER_IS_THE_RULED_ORDER_AND_ALL_CLOSES_IT()
    {
        Assert.Equal(["gemma", "qwen", "deepseek", "glm", "mistral", "all"], F.Ladder);

        //the chip all lifts the filter rather than naming a family, so no architecture is a member of it
        Assert.DoesNotContain("all", F.Members.Keys);
        //the name llama is a leftover group rather than a chip, so it reaches all by having no family
        Assert.Null(F.FamilyOf("llama"));
        Assert.Null(F.FamilyOf("llama4"));
    }

    //a variant arch stays listed under its family row, gemma4-assistant is Gemma 4's decoding draft and excluding it would drop its builds
    [Fact]
    public void A_DRAFT_MODELS_ARCH_IS_IN_ITS_FAMILY_AND_MARKED_A_BUILD()
    {
        Assert.Equal("gemma", F.FamilyOf("gemma4-assistant"));
        Assert.Equal(ArchRole.Variant, F.RoleOf("gemma4-assistant"));

        //assert the standalone models too, so the test proves Variant is not the answer for every arch
        Assert.Equal(ArchRole.Standalone, F.RoleOf("gemma4"));
        Assert.Equal(ArchRole.Standalone, F.RoleOf("gemma3n"));

        //the field is nullable, since absence from every shelf is a third answer beside standalone and variant
        Assert.Null(F.RoleOf("gemma2"));
    }

    //a vision arch must sit in its family as a standalone model, or that model can never reach its shelf.
    [Fact]
    public void THE_VISION_ARCHS_ARE_ON_THE_QWEN_SHELF()
    {
        Assert.Equal("qwen", F.FamilyOf("qwen3vl"));
        Assert.Equal("qwen", F.FamilyOf("qwen3vlmoe"));
        Assert.Equal(ArchRole.Standalone, F.RoleOf("qwen3vl"));
    }

    //each exclusion names one of the allowed reasons, so the exclusions can be grouped by reason
    [Fact]
    public void EVERY_EXCLUSION_GIVES_ONE_OF_THE_REVIEWED_REASONS()
    {
        string[] allowed = ["embedding", "ocr", "superseded", "tts"];
        Assert.NotEmpty(F.Excluded);
        foreach (var (arch, reason) in F.Excluded)
            Assert.True(allowed.Contains(reason, StringComparer.Ordinal),
                $"'{arch}' is excluded for '{reason}', which is not one of: {string.Join(" · ", allowed)}");
    }

    //the other families split by arch, and llama.cpp mints no arch per minor release, so only the qwen name separates 3.5 from 3.6
    [Fact]
    public void ONLY_QWEN_CARRIES_A_DATED_TIER_PIN()
    {
        Assert.Equal("3.8", F.CurrentTier["qwen"]);
        foreach (var family in new[] { "gemma", "deepseek", "glm", "mistral" })
            Assert.False(F.CurrentTier.ContainsKey(family),
                $"'{family}' is arch-keyed and must not carry a name-parsed tier pin");
    }

    //an untiered arch falls into unversioned and hides from the default shelf, so the build fails instead of the shelf going quiet
    [Fact]
    public void EVERY_ARCH_ON_AN_ARCH_KEYED_SHELF_CARRIES_A_TIER()
    {
        var untiered = F.Ladder
            .Where(f => f != "all" && !F.CurrentTier.ContainsKey(f))
            .SelectMany(f => F.Members[f].Keys.Select(a => (Family: f, Arch: a)))
            .Where(x => !F.ArchTiers.ContainsKey(x.Arch))
            .ToList();

        Assert.True(untiered.Count == 0,
            "these arches sit on an arch-keyed shelf with no tier, so they fall to `unversioned` "
            + "- the bucket that goes last, reached only when the versioned tiers run out: "
            + string.Join(", ", untiered.Select(x => $"{x.Family}/{x.Arch}")));
    }

    //an arch tier must not name an arch no family owns, or a family whose tier is name-parsed. the arch map is read first, so it would win
    [Fact]
    public void NO_TIER_IS_WRITTEN_FOR_AN_ARCH_THAT_SHOULD_NOT_HAVE_ONE()
    {
        var owned = F.Ladder.Where(f => f != "all")
            .ToDictionary(f => f, f => F.Members[f].Keys.ToHashSet(StringComparer.OrdinalIgnoreCase));

        foreach (var arch in F.ArchTiers.Keys)
        {
            var family = owned.FirstOrDefault(kv => kv.Value.Contains(arch)).Key;
            Assert.True(family is not null, $"`{arch}` carries a tier and no family names it");
            Assert.False(F.CurrentTier.ContainsKey(family!),
                $"`{arch}` is in `{family}`, which is NAME-parsed - an arch tier would give one repo "
                + "two answers, and the arch map is read first, so it would silently win");
        }
    }

    //each count is pinned measured data, so a change must be stated here, and a filter that offers no choice needs a reason
    [Fact]
    public void THE_ARCH_TIERS_DISCRIMINATE_WHERE_THERE_IS_SOMETHING_TO_DISCRIMINATE()
    {
        var generations = F.Ladder
            .Where(f => f != "all" && !F.CurrentTier.ContainsKey(f))
            .ToDictionary(f => f, f => F.Members[f].Keys
                .Where(F.ArchTiers.ContainsKey).Select(a => F.ArchTiers[a]).Distinct().Count());

        Assert.Equal(2, generations["gemma"]);       //the two generations are gemma4 against gemma3 and gemma3n
        Assert.Equal(3, generations["deepseek"]);    //the three tiers are 4, 3.2 and 2.
        Assert.Equal(2, generations["mistral"]);     //the two tiers come from mistral4 and mistral3
        //one tier is a fact about the engine. no glm3 arch exists, so the glm shelf has one generation and its filter shows no choice
        Assert.Equal(1, generations["glm"]);
    }

    //a name-parsed family's pin moves with uploads and an arch-keyed one comes from its arches, so a second number for that fact would drift
    [Fact]
    public void EACH_FAMILY_OPENS_ON_ITS_CURRENT_GENERATION()
    {
        Assert.Equal("3.8", F.PinFor("qwen").Label);      //the qwen family is name-parsed, so its pin is a dated value in current_tier rather than the top of an arch table
        Assert.Equal("4", F.PinFor("gemma").Label);       //the expected label is the highest tier in gemma's arch table.
        Assert.Equal("4", F.PinFor("deepseek").Label);
        Assert.Equal("4", F.PinFor("mistral").Label);

        //an untiered family opens on no version, so the shelf never shows a guessed generation.
        Assert.False(F.PinFor("all").IsVersioned);
    }

    //the arch map is read first, so a row cannot get two tiers. only qwen has no arch tier, so its name is parsed.
    [Theory]
    [InlineData("gemma4", "unsloth/gemma-4-26B-A4B-it-GGUF", "4")]
    [InlineData("gemma3", "unsloth/gemma-3-27b-it-GGUF", "3")]
    [InlineData("gemma3n", "unsloth/gemma-3n-E4B-GGUF", "3")]
    [InlineData("deepseek32", "unsloth/DeepSeek-V3.2-GGUF", "3.2")]
    //a qwen arch deliberately has no tier entry, so the expected value can only come from the repo name.
    [InlineData("qwen35moe", "unsloth/Qwen3.6-35B-A3B-GGUF", "3.6")]
    public void A_ROWS_TIER_COMES_FROM_THE_RIGHT_MECHANISM(string arch, string repoId, string expected)
        => Assert.Equal(expected, F.TierFor(arch, repoId).Label);

    //the shipped file forbids both maps on one family, so a hand-built fixture shows which wins, and 9 against 3.6 tells them apart
    [Fact]
    public void AN_ARCH_TIER_BEATS_A_NAME_PARSED_PIN_FOR_THE_SAME_FAMILY()
    {
        var both = new Families("2026-08-31", "b10076", ["qwen", "all"],
            new Dictionary<string, IReadOnlyDictionary<string, ArchRole>>
            {
                ["qwen"] = new Dictionary<string, ArchRole> { ["qwen35moe"] = ArchRole.Standalone },
            },
            new Dictionary<string, string>(),
            new Dictionary<string, string> { ["qwen"] = "3.6" },
            new Dictionary<string, string> { ["qwen35moe"] = "9" });

        Assert.Equal("9", both.TierFor("qwen35moe", "unsloth/Qwen3.6-35B-A3B-GGUF").Label);
    }

    //an unknown arch reads unversioned, which says gatto does not know the generation rather than that the model is old
    [Fact]
    public void AN_UNKNOWN_ARCH_IS_UNVERSIONED_RATHER_THAN_OLD()
    {
        Assert.False(F.TierFor("llama", "meta/Llama-3-8B-GGUF").IsVersioned);
        Assert.False(F.TierFor(null, "someone/mystery-GGUF").IsVersioned);
    }

    //the file must hold a date, so the sets read as data due to be checked again. a blank date leaves no way to tell how old the sets are
    [Fact]
    public void THE_SETS_CARRY_THE_DAY_SOMEONE_READ_THEM() =>
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", F.Reviewed);

    //an engine bump brings new architectures and new models, so it fails here until someone re-reads the qwen tier pin and restamps it
    [Fact]
    public void THE_TIER_PIN_WAS_RE_READ_AT_THE_ENGINE_PIN() =>
        Assert.Equal(Gatto.Roles.LlamaAssetSteering.PinnedRelease, F.CurrentTierRelease);
}
