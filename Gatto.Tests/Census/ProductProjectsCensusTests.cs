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
}
