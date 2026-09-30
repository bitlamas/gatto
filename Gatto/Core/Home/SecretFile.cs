using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Gatto.Core.Home;

//a file only its owner can read, with the ACL imposed rather than preserved. a failure here throws, since a silently default ACL leaves a plaintext secret
public static class SecretFile
{
    //write content to path, with reading restricted to the current user. any failure throws, and the file is deleted so no readable secret is left behind
    public static void Write(string path, string content)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        try
        {
            //create the file empty and lock it down before the secret goes in, or the credential would sit under the inherited ACL
            File.WriteAllText(path, string.Empty);
            Restrict(path);
            File.WriteAllText(path, content);
        }
        catch (GattoConfigException)
        {
            TryDelete(path);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(path);
            throw new GattoConfigException($"could not write {path}: {ex.Message}");
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { } //best effort
    }

    private static void Restrict(string path)
    {
        if (OperatingSystem.IsWindows()) RestrictWindows(path);
        else RestrictPosix(path);
    }

    [SupportedOSPlatform("windows")]
    private static void RestrictWindows(string path)
    {
        try
        {
            var info = new FileInfo(path);
            var acl = info.GetAccessControl();

            //drop the inherited rules, since keeping them would leave a read rule for Users and make the restriction cosmetic
            acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            foreach (var rule in acl.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>())
                acl.RemoveAccessRuleSpecific(rule);

            var current = WindowsIdentity.GetCurrent().User;
            if (current is not null)
                acl.AddAccessRule(new FileSystemAccessRule(current, FileSystemRights.FullControl, AccessControlType.Allow));

            //the local system account and the administrators group too, an admin can read the file anyway and shutting them out only breaks admin tooling
            foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
                acl.AddAccessRule(new FileSystemAccessRule(
                    new SecurityIdentifier(sid, null), FileSystemRights.FullControl, AccessControlType.Allow));

            info.SetAccessControl(acl);
        }
        catch (Exception ex)
        {
            throw new GattoConfigException(
                $"could not restrict permissions on {path}: {ex.Message} — refusing to leave a credential readable");
        }
    }

    [UnsupportedOSPlatform("windows")]
    private static void RestrictPosix(string path)
    {
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);   //0600
        }
        catch (Exception ex)
        {
            throw new GattoConfigException(
                $"could not restrict permissions on {path}: {ex.Message} — refusing to leave a credential readable");
        }
    }
}
