using Gatto.Extensions;
using Gatto.Roles;

namespace Gatto.Tests;

//a shipped role loaded through RoleFile.Load from a temp copy, so a test reads the file a home receives
internal static class ShippedRoleFiles
{
    public static RoleFile Load(string name)
    {
        var dir = Directory.CreateTempSubdirectory("gatto-shipped-role-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, $"{name}.json"), ShippedExtensions.Files[$"roles/{name}.json"]);
            return RoleFile.Load(dir, name);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
