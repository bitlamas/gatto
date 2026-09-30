using Gatto.Core.Acquire;
using Gatto.Roles;

namespace Gatto.Tests;

//the supported-architecture set, generated from the release it ships beside, which can only answer that a model will not load
public class SupportedArchitecturesTests
{
    //the embedded resource

    [Fact]
    public void The_embedded_architecture_set_loads_dated_from_the_pinned_release()
    {
        var set = SupportedArchitectures.Load();

        Assert.Equal("b11071", set.Release);
        Assert.Equal("2026-09-21", set.Generated);

        //the count is evidence the generator read a real registry, so an empty file would show up here
        Assert.Equal(152, set.Architectures.Count);

        //spot checks on the registry the generator read: three architectures gatto's own models run
        Assert.Contains("llama", set.Architectures);
        Assert.Contains("qwen3", set.Architectures);
        Assert.Contains("minimax-m2", set.Architectures);

        //these architectures arrived after an older pin, so a set generated from an old registry fails here
        Assert.Contains("bailingmoe3", set.Architectures);
        Assert.Contains("qwen4exp", set.Architectures);
        Assert.Contains("bailingmoe", set.Architectures);
        Assert.Contains("bailingmoe2", set.Architectures);

        //the (unknown) sentinel is not an architecture anything ships, so it must stay out of the set
        Assert.DoesNotContain("(unknown)", set.Architectures);
    }

    //the set names one llama.cpp release, so this test is the only thing that fails when the pin moves without a regenerated set
    [Fact]
    public void THE_ARCHITECTURE_SET_AND_THE_RELEASE_PIN_NAME_THE_SAME_BUILD()
    {
        Assert.Equal(LlamaAssetSteering.PinnedRelease, SupportedArchitectures.Load().Release);
    }

    //the predicate is honest in one direction

    [Fact]
    public void AN_ARCHITECTURE_THE_BUILD_DOES_NOT_KNOW_will_not_load()
    {
        Assert.True(SupportedArchitectures.Load().WillNotLoad("futurearch"));
    }

    [Fact]
    public void A_KNOWN_ARCHITECTURE_ANSWERS_FALSE_which_is_the_absence_of_a_claim()
    {
        //false means only that there is no evidence it will fail, so the type exposes no positive predicate to render
        Assert.False(SupportedArchitectures.Load().WillNotLoad("llama"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AN_ARCHITECTURE_WE_WERE_NOT_TOLD_is_never_flagged(string? arch)
    {
        //a listing with no gguf expand tells us nothing, so a missing architecture must never flag the row
        Assert.False(SupportedArchitectures.Load().WillNotLoad(arch));
    }

    [Fact]
    public void AN_EMPTY_SET_FLAGS_NOTHING_rather_than_flagging_everything()
    {
        //an empty set must flag nothing, so the guard is tested against a constructed one the shipped set can never be
        var empty = new SupportedArchitectures("b10076", "2026-08-13", new HashSet<string>());

        Assert.False(empty.WillNotLoad("futurearch"));
        Assert.False(empty.WillNotLoad("llama"));
    }

    [Fact]
    public void MATCHING_IS_ORDINAL_because_an_arch_string_is_a_machine_identifier()
    {
        //the arch string comes from a converter, so the match is ordinal and LLAMA is a different name
        Assert.True(SupportedArchitectures.Load().WillNotLoad("LLAMA"));
    }

    //the type exposes one public bool predicate and it is the negative one, so no screen can ask whether an architecture is supported
    [Fact]
    public void THERE_IS_NO_WAY_TO_ASK_WHETHER_AN_ARCHITECTURE_IS_SUPPORTED()
    {
        var methods = typeof(SupportedArchitectures)
            .GetMethods(System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static)
            .Where(m => m.ReturnType == typeof(bool) && !m.IsSpecialName)
            .Select(m => m.Name)
            .Where(n => n != nameof(object.Equals))
            .ToList();

        Assert.Equal([nameof(SupportedArchitectures.WillNotLoad)], methods);
    }
}
