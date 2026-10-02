using System.Security.Cryptography;
using System.Text;

namespace Gatto.Core.Home;

//the key a project folder is stored under in the home, for its sessions and its grants alike
public static class ProjectKey
{
    //the full path lowercased, through GetFullPath only. a junction, a subst drive or a short name spells another folder, and resolving links would orphan every stored session
    public static string PathOf(string folder) => Path.GetFullPath(folder).ToLowerInvariant();

    //twelve hex characters of the path's SHA-256, a file name and nothing more: a store that keys on it checks the path it records
    public static string Of(string folder) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(PathOf(folder))))[..12].ToLowerInvariant();
}
