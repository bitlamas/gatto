namespace Gatto.Core.Home;

//seals the bytes of an extension's secrets for this user. what names the file in a failure, since the seal never sees a path
public interface ISecretSeal
{
    byte[] Seal(byte[] plain);
    byte[] Unseal(byte[] @sealed, string what);
}

//the seal this OS offers, and no plaintext fallback where it offers none
public static class SecretSeal
{
    public static ISecretSeal ForThisOs() =>
        OperatingSystem.IsWindows() ? new DpapiSeal() : new UnsupportedSeal();
}

//an OS with no data protection API keeps no secret at all, rather than one in the clear
public sealed class UnsupportedSeal : ISecretSeal
{
    private const string Message = "secrets need the Windows data protection API; this OS has no seal";

    public byte[] Seal(byte[] plain) => throw new GattoConfigException(Message);
    public byte[] Unseal(byte[] @sealed, string what) => throw new GattoConfigException(Message);
}
