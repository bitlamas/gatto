namespace Gatto.Tests.Census;

//a product project missing from SourceTree.ProductProjects leaves its files out of every source census, and each of those censuses still passes
public class ProductProjectsCensusTests
{
    [Fact]
    public void EVERY_PRODUCT_PROJECT_IS_A_CENSUS_FOLDER()
    {
        var root = SourceTree.RepoRoot();
        var onDisk = Directory.EnumerateDirectories(root)
            .Select(d => Path.GetFileName(d))
            .Where(d => File.Exists(Path.Combine(root, d, d + ".csproj")) && !d.EndsWith(".Tests", StringComparison.Ordinal))
            .OrderBy(d => d, StringComparer.Ordinal)
            .ToList();

        //a reader that found nothing would agree with an empty list
        Assert.Contains("Gatto", onDisk);
        Assert.Equal(onDisk, SourceTree.ProductProjects.OrderBy(d => d, StringComparer.Ordinal).ToList());
    }

    //a test project missing from SourceTree.TestProjects leaves its tests out of every census that reads test sources
    [Fact]
    public void EVERY_TEST_PROJECT_IS_A_CENSUS_FOLDER()
    {
        var root = SourceTree.RepoRoot();
        var onDisk = Directory.EnumerateDirectories(root)
            .Select(d => Path.GetFileName(d))
            .Where(d => File.Exists(Path.Combine(root, d, d + ".csproj")) && d.EndsWith(".Tests", StringComparison.Ordinal))
            .OrderBy(d => d, StringComparer.Ordinal)
            .ToList();

        Assert.Contains("Gatto.Tests", onDisk);
        Assert.Equal(onDisk, SourceTree.TestProjects.OrderBy(d => d, StringComparer.Ordinal).ToList());

        //a project left out of the solution passes by never running, so each one must be in it
        var sln = File.ReadAllText(Path.Combine(root, "Gatto.sln"));
        Assert.All(SourceTree.TestProjects, p => Assert.Contains($"{p}\\{p}.csproj", sln, StringComparison.Ordinal));
    }
}
