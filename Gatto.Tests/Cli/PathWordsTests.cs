using Gatto.Cli;

namespace Gatto.Tests.Cli;

//the fold is display only, so each case is the words a screen prints for one path
public class PathWordsTests
{
    private const string Profile = @"C:\Users\you";

    //a child keeps its trailing separator, a folder line still reads as a place
    [Theory]
    [InlineData(@"C:\Users\you", "~")]
    [InlineData(@"C:\Users\you\", @"~\")]
    [InlineData(@"C:\Users\you\.gatto\gatto.json", @"~\.gatto\gatto.json")]
    [InlineData(@"C:\Users\you\.gatto\weights\qwen3.5-9b\", @"~\.gatto\weights\qwen3.5-9b\")]
    [InlineData(@"c:\users\YOU\Downloads\gatto.exe", @"~\Downloads\gatto.exe")]
    public void A_PATH_UNDER_THE_USERS_FOLDER_READS_AS_A_TILDE(string path, string shown) =>
        Assert.Equal(shown, PathWords.Home(path, Profile));

    //another drive, another user, a folder whose name only starts with the user's, and nothing at all all print whole
    [Theory]
    [InlineData(@"D:\m\qwen.gguf")]
    [InlineData(@"C:\Users\someone\x")]
    [InlineData(@"C:\Users\you2\x")]
    [InlineData(@"C:\Users")]
    [InlineData("")]
    public void ANY_OTHER_PATH_PRINTS_WHOLE(string path) =>
        Assert.Equal(path, PathWords.Home(path, Profile));

    //a profile given with its trailing separator folds the same paths
    [Fact]
    public void A_PROFILE_WITH_A_TRAILING_SEPARATOR_FOLDS_THE_SAME() =>
        Assert.Equal(@"~\.gatto\", PathWords.Home(@"C:\Users\you\.gatto\", Profile + @"\"));
}
