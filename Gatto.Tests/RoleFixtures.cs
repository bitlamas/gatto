using Gatto.Roles;

namespace Gatto.Tests;

//a dressed role loaded through RoleFile.Load from a fixture, the avatar and tints the coder and the oracle once shipped with
internal static class RoleFixtures
{
    public static RoleFile Load(string name)
    {
        var dir = Directory.CreateTempSubdirectory("gatto-role-fixture-").FullName;
        try
        {
            File.Copy(Path.Combine(Census.SourceTree.RepoRoot(), "Gatto.Tests", "Fixtures", $"role-{name}-dress.json"), Path.Combine(dir, $"{name}.json"));
            return RoleFile.Load(dir, name);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
