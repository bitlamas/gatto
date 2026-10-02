using Gatto.Core.Home;

namespace Gatto.Tests;

public class ProjectKeyTests
{
    //the literal keys the session files were stored under before the helper existed, so moving the hash to one home orphans no session
    [Theory]
    [InlineData(@"C:\proj\a", "8d64dc32f086")]
    [InlineData(@"C:\PROJ\A", "8d64dc32f086")]
    [InlineData(@"C:\Users\x\Some Project", "63184ec53bff")]
    public void The_key_is_the_lowercased_full_path_hashed(string folder, string key) =>
        Assert.Equal(key, ProjectKey.Of(folder));

    //the session store names its files with the same key, the one spelling of it
    [Fact]
    public void Session_files_take_the_project_key()
    {
        var home = Directory.CreateTempSubdirectory("gatto-key-").FullName;
        try
        {
            var store = new SessionStore(home, @"C:\proj\a");
            store.WriteLines(new[] { "{}" });
            var file = Assert.Single(Directory.GetFiles(Path.Combine(home, "sessions"), "*.jsonl"));
            Assert.StartsWith("8d64dc32f086-", Path.GetFileName(file), StringComparison.Ordinal);
        }
        finally { Directory.Delete(home, recursive: true); }
    }
}
