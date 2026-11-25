using Gatto.Core.Loop.Permissions;

namespace Gatto.Tests;

//the composer and the turn both write the grants file, so two writers at once must lose no grant and leave a file that loads
public class PermissionStoreTwoWritersTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "gatto-two-writers-" + Guid.NewGuid().ToString("N"));
    private readonly string _project = Path.Combine(Path.GetTempPath(), "gatto-two-writers-project-" + Guid.NewGuid().ToString("N"));

    public PermissionStoreTwoWritersTests()
    {
        Directory.CreateDirectory(_home);
        Directory.CreateDirectory(_project);
    }

    public void Dispose()
    {
        Directory.Delete(_home, recursive: true);
        Directory.Delete(_project, recursive: true);
    }

    [Fact]
    public async Task TWO_WRITERS_AT_ONCE_LOSE_NO_GRANT_AND_THE_FILE_LOADS()
    {
        const int rounds = 150;
        var store = PermissionStore.Load(_home, _project, out _);
        using var start = new ManualResetEventSlim();

        var turn = Task.Run(() =>
        {
            start.Wait();
            for (var i = 0; i < rounds; i++) store.GrantShellPrefix("tool" + i, persist: true);
        });
        var composer = Task.Run(() =>
        {
            start.Wait();
            for (var i = 0; i < rounds; i++) store.SetWild(i % 2 == 0, persist: true);
            store.SetWild(true, persist: true);
        });
        start.Set();
        await Task.WhenAll(turn, composer);

        var loaded = PermissionStore.Load(_home, _project, out var warning);
        Assert.Null(warning);
        Assert.True(loaded.Wild);
        Assert.Equal(rounds, loaded.ListEntries().Count);
    }
}
