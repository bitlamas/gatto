using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using static Gatto.Core.Sandbox.SandboxNative;

namespace Gatto.Core.Sandbox;

//gatto's own token at low integrity: no-write-up refuses every write to an object labelled above low, reads are unchanged
[SupportedOSPlatform("windows")]
internal static class SandboxToken
{
    public static readonly SecurityIdentifier Low = new("S-1-16-4096");

    //a primary copy of the process token with the low mandatory label. no SID restricted or disabled, no privilege removed
    public static SafeAccessTokenHandle Create()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenAllAccess, out var own))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenProcessToken");
        using (own)
        {
            if (!DuplicateTokenEx(own, TokenAllAccess, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out var token))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "DuplicateTokenEx");
            try
            {
                SetLow(token);
                return token;
            }
            catch
            {
                token.Dispose();
                throw;
            }
        }
    }

    //the token's mandatory label, read back for the tests
    internal static SecurityIdentifier IntegrityLevel(SafeAccessTokenHandle token)
    {
        var buffer = Read(token, TokenIntegrityLevel, out _);
        try { return new SecurityIdentifier(Marshal.ReadIntPtr(buffer)); }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    //the token's SIDs of one TOKEN_GROUPS class, read back for the tests
    internal static IReadOnlyList<(SecurityIdentifier Sid, uint Attributes)> Groups(SafeAccessTokenHandle token, int infoClass)
    {
        var buffer = Read(token, infoClass, out _);
        try
        {
            var count = Marshal.ReadInt32(buffer);
            var size = Marshal.SizeOf<SidAndAttributes>();
            var list = new List<(SecurityIdentifier, uint)>(count);
            for (var i = 0; i < count; i++)
            {
                var entry = Marshal.PtrToStructure<SidAndAttributes>(buffer + IntPtr.Size + i * size);
                list.Add((new SecurityIdentifier(entry.Sid), entry.Attributes));
            }
            return list;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    //the TOKEN_PRIVILEGES block as bytes, so a test can hold it equal to the process token's
    internal static byte[] Privileges(SafeAccessTokenHandle token)
    {
        var buffer = Read(token, TokenPrivileges, out var length);
        try
        {
            var bytes = new byte[length];
            Marshal.Copy(buffer, bytes, 0, bytes.Length);
            return bytes;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static void SetLow(SafeAccessTokenHandle token)
    {
        var sidBytes = new byte[Low.BinaryLength];
        Low.GetBinaryForm(sidBytes, 0);
        var size = Marshal.SizeOf<SidAndAttributes>();
        //the TOKEN_MANDATORY_LABEL is one SID_AND_ATTRIBUTES, and the SID it points to travels in the same block after it
        var block = Marshal.AllocHGlobal(size + sidBytes.Length);
        try
        {
            Marshal.Copy(sidBytes, 0, block + size, sidBytes.Length);
            Marshal.StructureToPtr(new SidAndAttributes { Sid = block + size, Attributes = SeGroupIntegrity }, block, false);
            if (!SetTokenInformation(token, TokenIntegrityLevel, block, (uint)(size + sidBytes.Length)))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetTokenInformation");
        }
        finally { Marshal.FreeHGlobal(block); }
    }

    //the caller frees the block
    private static IntPtr Read(SafeAccessTokenHandle token, int infoClass, out uint length)
    {
        GetTokenInformation(token, infoClass, IntPtr.Zero, 0, out length);
        if (length == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "GetTokenInformation");
        var buffer = Marshal.AllocHGlobal((int)length);
        if (GetTokenInformation(token, infoClass, buffer, length, out _)) return buffer;
        var error = Marshal.GetLastWin32Error();
        Marshal.FreeHGlobal(buffer);
        throw new Win32Exception(error, "GetTokenInformation");
    }
}
