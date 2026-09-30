using Gatto.Extensions;

namespace Gatto.Tests;

public class ExtensionDiscoveryTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gatto-ext-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string WriteFile(string relativePath, string content)
    {
        var path = Path.Combine(_dir, relativePath);
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Discover_finds_single_file_and_folder_extensions_sorted_by_name()
    {
        WriteFile("zeta.csx", "// zeta");
        WriteFile("alpha.csx", "// alpha");
        WriteFile(Path.Combine("beta", "main.csx"), "// beta main");

        var sources = ExtensionDiscovery.Discover(_dir);

        Assert.Equal(new[] { "alpha", "beta", "zeta" }, sources.Select(s => s.Name));
    }

    [Fact]
    public void Discover_single_file_extension_has_one_file_and_matching_entry_path()
    {
        var path = WriteFile("alpha.csx", "// alpha");

        var sources = ExtensionDiscovery.Discover(_dir);
        var alpha = Assert.Single(sources);

        Assert.Equal("alpha", alpha.Name);
        Assert.Equal(Path.GetFullPath(path), alpha.EntryPath);
        Assert.Equal(new[] { Path.GetFullPath(path) }, alpha.Files);
    }

    [Fact]
    public void Discover_folder_extension_entry_path_is_main_csx_and_includes_nested_files()
    {
        WriteFile(Path.Combine("beta", "main.csx"), "// beta main");
        WriteFile(Path.Combine("beta", "helpers", "util.csx"), "// util");

        var sources = ExtensionDiscovery.Discover(_dir);
        var beta = Assert.Single(sources);

        Assert.Equal("beta", beta.Name);
        Assert.Equal(Path.GetFullPath(Path.Combine(_dir, "beta", "main.csx")), beta.EntryPath);
        Assert.Equal(2, beta.Files.Count);
    }

    [Fact]
    public void Discover_folder_files_sorted_by_relative_path_ordinal()
    {
        WriteFile(Path.Combine("beta", "main.csx"), "// beta main");
        WriteFile(Path.Combine("beta", "zz.csx"), "// zz");
        WriteFile(Path.Combine("beta", "helpers", "util.csx"), "// util");
        WriteFile(Path.Combine("beta", "aardvark.csx"), "// aardvark");

        var sources = ExtensionDiscovery.Discover(_dir);
        var beta = Assert.Single(sources);

        var expectedOrder = new[] { "aardvark.csx", "helpers", "main.csx", "zz.csx" }
            .OrderBy(s => s, StringComparer.Ordinal);
        var relPaths = beta.Files
            .Select(f => Path.GetRelativePath(Path.Combine(_dir, "beta"), f))
            .ToList();
        var sortedCopy = relPaths.OrderBy(p => p, StringComparer.Ordinal).ToList();

        Assert.Equal(sortedCopy, relPaths); //discovery order must equal the same list sorted ordinal by relative path.
    }

    [Fact]
    public void Discover_folder_without_main_csx_is_skipped_with_diagnostic()
    {
        WriteFile(Path.Combine("broken", "other.csx"), "// not main");

        var diagnostics = new List<string>();
        var sources = ExtensionDiscovery.Discover(_dir, diagnostics.Add);

        Assert.Empty(sources);
        Assert.Contains("extension folder 'broken' has no main.csx — skipped", diagnostics);
    }

    [Fact]
    public void Discover_ignores_cache_dir_and_dot_prefixed_entries()
    {
        WriteFile(Path.Combine(".cache", "main.csx"), "// cache main");
        WriteFile(".hidden.csx", "// hidden file");
        WriteFile(Path.Combine(".hiddenfolder", "main.csx"), "// hidden folder main");
        WriteFile("visible.csx", "// visible");

        var sources = ExtensionDiscovery.Discover(_dir);

        Assert.Equal(new[] { "visible" }, sources.Select(s => s.Name));
    }

    [Fact]
    public void Discover_missing_directory_returns_empty_list()
    {
        var sources = ExtensionDiscovery.Discover(Path.Combine(_dir, "does-not-exist"));

        Assert.Empty(sources);
    }

    [Fact]
    public void Discover_ignores_non_csx_top_level_files()
    {
        WriteFile("readme.txt", "not an extension");
        WriteFile("valid.csx", "// valid");

        var sources = ExtensionDiscovery.Discover(_dir);

        Assert.Equal(new[] { "valid" }, sources.Select(s => s.Name));
    }

    [Fact]
    public void ComputeHash_and_ContentHash_change_when_a_helper_file_is_edited()
    {
        WriteFile(Path.Combine("beta", "main.csx"), "// beta main");
        WriteFile(Path.Combine("beta", "helpers", "util.csx"), "// util v1");
        var before = ExtensionDiscovery.Discover(_dir).Single();
        var beforeCache = ExtensionDiscovery.ComputeHash(before, "host-1", "roslyn-1", "bin-1");
        var beforeContent = ExtensionDiscovery.ContentHash(before);

        WriteFile(Path.Combine("beta", "helpers", "util.csx"), "// util v2 — edited");
        var after = ExtensionDiscovery.Discover(_dir).Single();
        var afterCache = ExtensionDiscovery.ComputeHash(after, "host-1", "roslyn-1", "bin-1");
        var afterContent = ExtensionDiscovery.ContentHash(after);

        Assert.NotEqual(beforeCache, afterCache);
        Assert.NotEqual(beforeContent, afterContent);
    }

    [Fact]
    public void ComputeHash_changes_with_version_salt_but_ContentHash_does_not()
    {
        WriteFile(Path.Combine("beta", "main.csx"), "// beta main");
        var src = ExtensionDiscovery.Discover(_dir).Single();

        var hashV1 = ExtensionDiscovery.ComputeHash(src, "host-1", "roslyn-1", "bin-1");
        var hashV2 = ExtensionDiscovery.ComputeHash(src, "host-2", "roslyn-1", "bin-1");
        var contentA = ExtensionDiscovery.ContentHash(src);
        var contentB = ExtensionDiscovery.ContentHash(src);

        Assert.NotEqual(hashV1, hashV2);
        Assert.Equal(contentA, contentB);
    }

    [Fact]
    public void ComputeHash_changes_with_host_binary_version_salt()
    {
        //a change in the host assembly version must invalidate the cache, or a stale dll can't bind
        WriteFile(Path.Combine("beta", "main.csx"), "// beta main");
        var src = ExtensionDiscovery.Discover(_dir).Single();

        var hashV1 = ExtensionDiscovery.ComputeHash(src, "host-1", "roslyn-1", "1.0.0.0");
        var hashV2 = ExtensionDiscovery.ComputeHash(src, "host-1", "roslyn-1", "0.0.1.0");

        Assert.NotEqual(hashV1, hashV2);
    }

    [Fact]
    public void ComputeHash_is_lowercase_hex_sha256_length()
    {
        WriteFile("alpha.csx", "// alpha");
        var src = ExtensionDiscovery.Discover(_dir).Single();

        var hash = ExtensionDiscovery.ComputeHash(src, "host", "roslyn", "bin");

        Assert.Equal(64, hash.Length);
        Assert.Equal(hash, hash.ToLowerInvariant());
        Assert.DoesNotContain(hash, c => !Uri.IsHexDigit(c));
    }

    [Fact]
    public void ComputeHash_is_deterministic_for_same_inputs()
    {
        WriteFile("alpha.csx", "// alpha");
        var src = ExtensionDiscovery.Discover(_dir).Single();

        var hash1 = ExtensionDiscovery.ComputeHash(src, "host", "roslyn", "bin");
        var hash2 = ExtensionDiscovery.ComputeHash(src, "host", "roslyn", "bin");

        Assert.Equal(hash1, hash2);
    }
}
