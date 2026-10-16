using Gatto.Core.Tools;

namespace Gatto.Tests;

//a tool's path is read against the working folder, and a leading tilde is the user's folder as the shell reads it
public class ToolArgsTests
{
    private static readonly string Cwd = Path.Combine(Path.GetTempPath(), "gatto-args-5c1e", "proj");
    private static readonly string Profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    [Theory]
    [InlineData("~", "")]
    [InlineData("~\\a\\b.txt", "a\\b.txt")]
    [InlineData("~/a/b.txt", "a\\b.txt")]
    public void A_LEADING_TILDE_IS_THE_USERS_FOLDER(string path, string rest) =>
        Assert.Equal(Path.GetFullPath(Path.Combine(Profile, rest)), ToolArgs.Resolve(Cwd, path));

    [Theory]
    [InlineData("~a\\b.txt")]
    [InlineData("a\\~\\b.txt")]
    [InlineData("a~")]
    public void A_TILDE_ANYWHERE_ELSE_IS_A_NAME_IN_THE_WORKING_FOLDER(string path) =>
        Assert.Equal(Path.GetFullPath(Path.Combine(Cwd, path)), ToolArgs.Resolve(Cwd, path));

    [Fact]
    public void A_ROOTED_PATH_STAYS_AS_GIVEN()
    {
        var rooted = Path.Combine(Path.GetTempPath(), "x.txt");
        Assert.Equal(Path.GetFullPath(rooted), ToolArgs.Resolve(Cwd, rooted));
    }
}
