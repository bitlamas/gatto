using System.Runtime.InteropServices;

namespace Gatto.Terminal;

//write plain text to the system clipboard, and TrySet returns false rather than throwing into the render loop when another process holds the clipboard
public interface IClipboard { bool TrySet(string text); }

//the clipboard calls, seamed so the retry and ownership logic is tested against a fake, and a successful Set hands the handle to the system
public interface IClipboardNative
{
    bool Open();          //the Win32 OpenClipboard call
    void Empty();         //the Win32 EmptyClipboard call
    nint Alloc(string s); //a GlobalAlloc block holding the UTF-16 text and its NUL, or 0 when the allocation fails
    bool Set(nint h);     //the Win32 SetClipboardData call with CF_UNICODETEXT
    void Close();         //the Win32 CloseClipboard call
    void Free(nint h);    //the Win32 GlobalFree call
}

//writes UTF-16 through SetClipboardData, retrying a failed OpenClipboard a few times and then failing soft. the handle is freed before the system takes it
public sealed class Win32Clipboard(IClipboardNative native) : IClipboard
{
    private const int MaxOpenAttempts = 5;

    public Win32Clipboard() : this(new Win32ClipboardNative()) { }

    public bool TrySet(string text)
    {
        for (var attempt = 0; attempt < MaxOpenAttempts; attempt++)
        {
            if (!native.Open()) { Thread.Sleep(10); continue; }   //another process holds the clipboard, so back off briefly
            nint h = 0;
            try
            {
                native.Empty();
                h = native.Alloc(text);
                if (h == 0) return false;                 //the allocation failed, so there's nothing to free
                if (!native.Set(h)) { native.Free(h); return false; }   //the set failed, so free the handle here
                return true;                              //the system owns h now, so don't free it
            }
            catch { if (h != 0) native.Free(h); return false; }   //the failure stops here so it can't reach the render loop
            finally { native.Close(); }
        }
        return false;   //gatto never got the clipboard open, so the copy fails soft
    }
}

//the real clipboard, over user32 and kernel32
public sealed class Win32ClipboardNative : IClipboardNative
{
    private const uint CF_UNICODETEXT = 13, GMEM_MOVEABLE = 0x0002;

    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(nint h);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetClipboardData(uint fmt, nint mem);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint GlobalLock(nint h);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalUnlock(nint h);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint GlobalFree(nint h);

    public bool Open() => OpenClipboard(0);
    public void Empty() => EmptyClipboard();

    public nint Alloc(string s)
    {
        var bytes = (nuint)((s.Length + 1) * 2);   //one two-byte unit per character, plus one for the NUL terminator
        var h = GlobalAlloc(GMEM_MOVEABLE, bytes);
        if (h == 0) return 0;
        var p = GlobalLock(h);
        if (p == 0) { GlobalFree(h); return 0; }
        Marshal.Copy(s.ToCharArray(), 0, p, s.Length);
        Marshal.WriteInt16(p, s.Length * 2, 0);    //the NUL terminator after the text
        GlobalUnlock(h);
        return h;
    }

    public bool Set(nint h) => SetClipboardData(CF_UNICODETEXT, h) != 0;
    public void Close() => CloseClipboard();
    public void Free(nint h) => GlobalFree(h);
}
