using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Gatto.Core.Home;

namespace Gatto.Tests;

//the secret must be readable only by its owner, a silent acl failure would leave it readable through inheritance
public sealed class SecretFileTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gatto-secret-").FullName;

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string Path_(string name) => Path.Combine(_dir, name);

    [Fact]
    public void Write_RoundTripsTheContent()
    {
        var p = Path_("api-key.txt");

        SecretFile.Write(p, "s3cr3t-value\n");

        Assert.Equal("s3cr3t-value\n", File.ReadAllText(p));
    }

    [Fact]
    public void Write_ReplacesAnExistingFileWholesale()
    {
        //the file is rewritten on every serve start. a shorter new key must not leave a tail of the old one.
        var p = Path_("api-key.txt");
        SecretFile.Write(p, "a-very-long-previous-key-value");

        SecretFile.Write(p, "short");

        Assert.Equal("short", File.ReadAllText(p));
    }

    [Fact]
    public void Write_CreatesMissingDirectories()
    {
        var p = Path.Combine(_dir, "models", "some-model", "api-key.txt");

        SecretFile.Write(p, "k");

        Assert.True(File.Exists(p));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void Write_DropsInheritedAccess_SoTheFileIsNotReadableByEveryone()
    {
        if (!OperatingSystem.IsWindows()) return;

        //the file must not inherit the directory's acl. inherited entries typically include users:read, which would make the protection cosmetic.
        var p = Path_("api-key.txt");

        SecretFile.Write(p, "s3cr3t-value");

        var acl = new FileInfo(p).GetAccessControl();
        Assert.True(acl.AreAccessRulesProtected, "the file must not inherit the directory's ACL");

        var rules = acl.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>().ToList();
        Assert.NotEmpty(rules);

        var allowed = rules
            .Where(r => r.AccessControlType == AccessControlType.Allow)
            .Select(r => (SecurityIdentifier)r.IdentityReference)
            .ToList();

        //only the current user, system, and administrators may reach the file. any other principal means another account can read the credential.
        var permitted = new List<SecurityIdentifier> { WindowsIdentity.GetCurrent().User! };
        permitted.Add(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
        permitted.Add(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));

        foreach (var sid in allowed)
            Assert.True(permitted.Contains(sid), $"unexpected principal allowed on the secret file: {sid}");

        foreach (var wellKnown in new[]
                 {
                     WellKnownSidType.WorldSid,                    //the everyone sid must never be allowed here
                     WellKnownSidType.AuthenticatedUserSid,
                     WellKnownSidType.BuiltinUsersSid,
                 })
            Assert.DoesNotContain(new SecurityIdentifier(wellKnown, null), allowed);
    }

    [Fact]
    public void Write_LeavesNoFileBehind_WhenTheContentCannotBeWritten()
    {
        //a failed write must leave no readable remnant. a half-written credential nobody owns is worse than none.
        var p = Path_("locked.txt");
        SecretFile.Write(p, "first");

        using (var hold = new FileStream(p, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Throws<GattoConfigException>(() => SecretFile.Write(p, "second"));
        }

        //the failed write leaves the original file intact, with no partial or new readable file in its place
        Assert.Equal("first", File.ReadAllText(p));
    }
}
