using Gatto.Core.Home;
using Gatto.Tests.Support;

namespace Gatto.Tests;

//each test mounts a temp tree as a drive root, so the ascent stops there. a leaked mapping survives until logout, so only isolation tests belong here
[Collection(SubstDriveCollection.Name)]
public class ContextFilesWalkTerminationTests : IDisposable
{
    //the mount mechanism lives once in SubstRoot, shared with other suites
    private readonly SubstRoot _substRoot = SubstRoot.Mount();

    private string _tempDir => _substRoot.Backing;
    private string _root => _substRoot.Root;

    public void Dispose() => _substRoot.Dispose();

    private string Dir(params string[] segments) => Path.Combine([_root, .. segments]);

    private static void Write(string path, string content) => SubstRoot.Write(path, content);

    [Fact]
    public void Collect_nothing_found_returns_default_file_as_single_entry()
    {
        var c = Dir("a", "b", "c");
        Directory.CreateDirectory(c);
        var defaultPath = Path.Combine(_tempDir, "default-GATTO.md");
        File.WriteAllText(defaultPath, "default content");

        var result = ContextFiles.Collect(c, compat: false, homeGattoMdPath: defaultPath);

        Assert.Single(result);
        Assert.Equal(defaultPath, result[0].Path);
        Assert.Equal("default content", result[0].Content);
    }

    [Fact]
    public void Collect_nothing_found_and_null_default_path_returns_empty()
    {
        var c = Dir("a", "b", "c");
        Directory.CreateDirectory(c);

        var result = ContextFiles.Collect(c, compat: false, homeGattoMdPath: null);

        Assert.Empty(result);
    }

    [Fact]
    public void Collect_cwd_is_drive_root_finds_only_that_directory()
    {
        Write(Path.Combine(_root, "GATTO.md"), "root only");

        var result = ContextFiles.Collect(_root, compat: false, homeGattoMdPath: null);

        Assert.Single(result);
        Assert.Equal(Path.Combine(_root, "GATTO.md"), result[0].Path);
    }
}
