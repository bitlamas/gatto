using Gatto.Core.Acquire;

namespace Gatto.Tests;

//one home answers where a model file lives and where it should go, so the doctor and the wizard can't drift apart
public class ModelLocationTests
{

    //the shell's known Downloads folder wins over the profile path, which can name a folder no download reaches
    [Fact]
    public void A_RELOCATED_DOWNLOADS_WINS_over_the_profile_concatenation()
    {
        var resolved = ModelLocation.Resolve(@"D:\somewhere else\downloads", @"C:\Users\me");

        Assert.Equal(@"D:\somewhere else\downloads", resolved);
        Assert.NotEqual(Path.Combine(@"C:\Users\me", "Downloads"), resolved);
    }

    //with no known folder from the shell, fall back to the profile path (a wrong guess only reads as not found)
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void A_SHELL_THAT_WILL_NOT_ANSWER_FALLS_BACK_to_the_profile(string? knownFolder)
    {
        Assert.Equal(
            Path.Combine(@"C:\Users\me", "Downloads"),
            ModelLocation.Resolve(knownFolder, @"C:\Users\me"));
    }

    //the resolved folder is a rooted path and never blank
    [Fact]
    public void THE_RESOLVED_DOWNLOADS_IS_A_ROOTED_PATH()
    {
        var dir = ModelLocation.DownloadsDir;

        Assert.False(string.IsNullOrWhiteSpace(dir));
        Assert.True(Path.IsPathRooted(dir));
    }

    [Theory]
    [InlineData(@"C:\Users\me\Downloads\model.gguf", true)]
    [InlineData(@"C:\Users\me\Downloads\sub\model.gguf", true)]
    [InlineData(@"C:\Users\me\Documents\model.gguf", false)]
    [InlineData(@"D:\models\model.gguf", false)]
    public void CONTAINMENT_IS_BY_PATH_not_by_string_prefix(string path, bool inside)
    {
        Assert.Equal(inside, ModelLocation.IsInside(@"C:\Users\me\Downloads", path));
    }

    [Fact]
    public void A_SIBLING_FOLDER_WITH_THE_SAME_PREFIX_IS_NOT_INSIDE()
    {
        //a prefix test would count Downloads-old as inside Downloads, so compare by path relation
        Assert.False(ModelLocation.IsInside(@"C:\Users\me\Downloads", @"C:\Users\me\Downloads-old\m.gguf"));
    }

    [Fact]
    public void THE_FOLDER_ITSELF_IS_NOT_INSIDE_ITSELF()
    {
        Assert.False(ModelLocation.IsInside(@"C:\Users\me\Downloads", @"C:\Users\me\Downloads"));
    }

    [Fact]
    public void A_MALFORMED_PATH_ANSWERS_FALSE_rather_than_throwing()
    {
        //a malformed path from a foreign listing answers false rather than throwing (a crash here takes the doctor and the wizard down together)
        Assert.False(ModelLocation.IsInside(@"C:\Users\me\Downloads", "\0not a path"));
    }

    [Fact]
    public void THE_SUGGESTION_DEFERS_TO_A_CONFIGURED_MODELS_DIR()
    {
        //a configured models dir is the user's own settled decision, so don't suggest another folder
        Assert.Equal(@"D:\weights", ModelLocation.SuggestedDir(@"C:\home", @"D:\weights"));
    }

    [Fact]
    public void WITHOUT_ONE_IT_SUGGESTS_A_FOLDER_UNDER_HOME()
    {
        //the models folder holds the setups, so the suggestion is a weights folder beside it. only a typed folder sets weights_root
        Assert.Equal(Path.Combine(@"C:\home", "weights"), ModelLocation.SuggestedDir(@"C:\home", null));
        Assert.Equal(Path.Combine(@"C:\home", "weights"), ModelLocation.SuggestedDir(@"C:\home", ""));
    }

    [Fact]
    public void SAME_ROOT_IS_A_RENAME_and_a_different_root_is_a_copy()
    {
        Assert.False(ModelLocation.IsCrossVolume(@"C:\a\m.gguf", @"C:\b"));
        Assert.True(ModelLocation.IsCrossVolume(@"C:\a\m.gguf", @"D:\b"));
    }

    [Fact]
    public void AN_UNREADABLE_PATH_ASSUMES_THE_EXPENSIVE_ANSWER()
    {
        //an unreadable path counts as cross-volume, so a false warning costs a sentence instead of a silent 30 gb copy
        Assert.True(ModelLocation.IsCrossVolume("\0bad", @"D:\b"));
    }
}
