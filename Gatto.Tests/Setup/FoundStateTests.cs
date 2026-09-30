using Gatto.Roles;
using Xunit;

namespace Gatto.Tests.Setup;

//a folder number that does not match the banner is a fork inside gatto's own llama root. elsewhere the number decides, and no suffixed build sorts by its number
public class FoundStateTests
{
    //the enum's name stands in as a string (a public test method can't take an internal type, and a wrong string would pass silently)
    [Theory]
    [InlineData("b11071", "Exact")]          //the pin itself, the build the s4-found golden draws
    [InlineData("b11137", "Newer")]          //a newer build, the one the s4-found-newer golden draws
    [InlineData("b9848", "Older")]           //the build the s4-found-older golden draws
    [InlineData("b10061-laguna", "Fork")]    //the fork case behind the s4-found-fork golden
    [InlineData(null, "Fork")]
    public void THE_FOUR_BUILDS_THE_SCREENS_DRAW_ARE_PLACED(string? build, string want) =>
        Assert.Equal(want, LlamaAssetSteering.Classify(build).ToString());

    //the classifier must never read a fork build's digits, or a digit scan calls a chosen build older and offers to replace it
    [Theory]
    [InlineData("b10061-laguna")]     //the fork number sits below the pin.
    [InlineData("b99999-laguna")]     //the fork number sits above the pin.
    [InlineData("b11071-laguna")]     //the fork names the pin's own number.
    public void A_FORK_IS_NEVER_SORTED_BY_THE_NUMBER_INSIDE_IT(string build) =>
        Assert.Equal("Fork", LlamaAssetSteering.Classify(build).ToString());

    //the server reports a bare number, while screens and folders write it with a b prefix. neither spelling may decide whether the pinned build is recognised
    [Theory]
    [InlineData("11071")]
    [InlineData("B11071")]
    public void THE_PIN_IS_RECOGNISED_IN_EITHER_SPELLING(string build) =>
        Assert.Equal("Exact", LlamaAssetSteering.Classify(build).ToString());

    //an unreadable version line must classify as a fork, or a zero reads as older and offers to replace the engine
    [Theory]
    [InlineData("")]
    [InlineData("b")]
    [InlineData("   ")]
    [InlineData("-1")]
    public void AN_UNREADABLE_BUILD_IS_A_FORK_NOT_A_ZERO(string build) =>
        Assert.Equal("Fork", LlamaAssetSteering.Classify(build).ToString());

    //a folder name that does not match its banner is a fork inside gatto's root, and older outside it, where there is no provenance
    [Fact]
    public void A_HAND_NAMED_FOLDER_IN_GATTOS_HOME_IS_THE_FORK_ARM()
    {
        Assert.Equal("Fork", LlamaAssetSteering.Classify("1", gattoFolder: "laguna-2bd55d0").ToString());
        Assert.Equal("Older", LlamaAssetSteering.Classify("1").ToString());
    }

    //a folder gatto named is gatto's own fetch, and the pairing matches it to the build by the banner's number
    [Theory]
    [InlineData("11071", "b11071", "Exact")]
    [InlineData("11137", "b11137", "Newer")]
    [InlineData("9848", "b9848", "Older")]
    public void A_FOLDER_GATTO_NAMED_IS_PLACED_BY_ITS_NUMBER(string build, string folder, string want) =>
        Assert.Equal(want, LlamaAssetSteering.Classify(build, folder).ToString());

    //a folder named after the pin must not make its build the pin, or any binary in that folder would be called the tested release
    [Fact]
    public void A_PINNED_FOLDER_NAME_DOES_NOT_MAKE_A_BUILD_THE_PIN() =>
        Assert.Equal("Fork", LlamaAssetSteering.Classify("1", gattoFolder: "b11071").ToString());

    //the pairing compares numbers, so the pin's own build is gatto's fetch however the banner spells it
    [Theory]
    [InlineData("11071")]
    [InlineData("b11071")]
    public void THE_PAIRING_COMPARES_NUMBERS_NOT_SPELLINGS(string build) =>
        Assert.Equal("Exact", LlamaAssetSteering.Classify(build, "b11071").ToString());

    //one place spells a build, so no screen prints a spelling the folder disagrees with. a fork keeps its own name, since a b prefix would invent a release
    [Theory]
    [InlineData("11071", "b11071")]
    [InlineData("9848", "b9848")]
    [InlineData("b11071", "b11071")]
    [InlineData("B11071", "B11071")]
    [InlineData("laguna-2bd55d0", "laguna-2bd55d0")]
    [InlineData("b10061-laguna", "b10061-laguna")]
    [InlineData("", "")]
    public void A_BUILD_IS_SPELLED_THE_WAY_GATTO_NAMES_ITS_OWN(string build, string want) =>
        Assert.Equal(want, LlamaAssetSteering.ReleaseName(build));
}
