using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Gatto.Core.Home;

//DPAPI for the current user: another account or a copied disk cannot unseal, a process running as this user can
[SupportedOSPlatform("windows")]
public sealed class DpapiSeal : ISecretSeal
{
    private const int UiForbidden = 1;   //the UI-forbidden flag, a terminal tool never raises a prompt

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, string? description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr mem);

    public byte[] Seal(byte[] plain) =>
        Run(plain, (ref DataBlob i, out DataBlob o) => CryptProtectData(ref i, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out o),
            err => new GattoConfigException($"cannot seal a secret: {err}"));

    public byte[] Unseal(byte[] @sealed, string what) =>
        Run(@sealed, (ref DataBlob i, out DataBlob o) => CryptUnprotectData(ref i, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out o),
            err => new GattoConfigException($"cannot unseal {what}: {err}"));

    private delegate bool Crypt(ref DataBlob input, out DataBlob output);

    //the input is pinned for the call and the output is the API's own buffer, copied out and freed
    private static byte[] Run(byte[] input, Crypt call, Func<string, GattoConfigException> fail)
    {
        var handle = GCHandle.Alloc(input, GCHandleType.Pinned);
        try
        {
            var inBlob = new DataBlob { Size = input.Length, Data = handle.AddrOfPinnedObject() };
            if (!call(ref inBlob, out var outBlob))
                throw fail(new Win32Exception(Marshal.GetLastWin32Error()).Message);
            try
            {
                var result = new byte[outBlob.Size];
                Marshal.Copy(outBlob.Data, result, 0, outBlob.Size);
                return result;
            }
            finally { LocalFree(outBlob.Data); }
        }
        finally { handle.Free(); }
    }
}
