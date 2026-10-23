using System.Runtime.Versioning;
using Gatto.Core.Sandbox;

namespace Gatto.Tests.Sandbox;

[SupportedOSPlatform("windows")]
public sealed class SandboxTokenTests
{
    [Fact]
    public void THE_TOKEN_IS_LOW_INTEGRITY_UNRESTRICTED_AND_OTHERWISE_THE_PROCESS_TOKEN()
    {
        using var token = SandboxToken.Create();
        Assert.Equal(SandboxToken.Low, SandboxToken.IntegrityLevel(token));
        Assert.False(SandboxNative.IsTokenRestricted(token));

        Assert.True(SandboxNative.OpenProcessToken(SandboxNative.GetCurrentProcess(), SandboxNative.TokenAllAccess, out var own));
        using (own)
        {
            Assert.NotEqual(SandboxToken.Low, SandboxToken.IntegrityLevel(own));
            //the integrity level travels as a group, the one the copy is meant to change
            static IEnumerable<string> Groups(Microsoft.Win32.SafeHandles.SafeAccessTokenHandle t) =>
                SandboxToken.Groups(t, SandboxNative.TokenGroups)
                    .Where(g => (g.Attributes & SandboxNative.SeGroupIntegrity) == 0).Select(g => g.Sid.Value).Order();
            Assert.Equal(Groups(own), Groups(token));
            Assert.Equal(SandboxToken.Privileges(own), SandboxToken.Privileges(token));
        }
    }
}
