using System.Runtime.InteropServices;

namespace Gatto.Terminal;

//what the clipboard offers right now, answered without opening it
public enum ClipboardImageKind
{
    //nothing gatto can attach
    None,
    //the registered PNG format, offered by both Win+Shift+S and a browser copy image
    Png,
    //a copied file (CF_HDROP), which gives a path instead of bytes
    FileDrop,
    //a bitmap with no PNG beside it, refused with a message because encoding a DIB by hand is real work
    DibOnly,
}

//reading images off the clipboard, seamed so the decision logic is tested against a fake and the P/Invoke stays a thin edge
public interface IClipboardImageNative
{
    //what is on the clipboard right now, answered with IsClipboardFormatAvailable, which does not open it, so a keystroke or a poll can ask safely
    ClipboardImageKind Available();

    //the PNG bytes, or null. it opens the clipboard, so call it only when Available already said Png
    byte[]? ReadPng();

    //the paths from a CF_HDROP, or an empty list
    IReadOnlyList<string> ReadFileDrop();
}

//the real reader over user32 and kernel32, with no WinForms, System.Drawing or NuGet dependency
public sealed class Win32ClipboardImageNative : IClipboardImageNative
{
    private const uint CF_DIB = 8, CF_HDROP = 15, CF_DIBV5 = 17;

    [DllImport("user32.dll", SetLastError = true)] private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormatW(string name);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(nint h);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern nint GetClipboardData(uint fmt);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint GlobalLock(nint h);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalUnlock(nint h);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nuint GlobalSize(nint h);
    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern uint DragQueryFileW(nint hDrop, uint i, char[]? file, uint cch);

    //registered at runtime because the id is assigned per session and is not stable across boots
    private static readonly uint PngFormat = RegisterClipboardFormatW("PNG");

    public ClipboardImageKind Available()
    {
        //order matters: PNG first (both real sources offer it), then FileDrop (a path beats bytes), then DIB as the last resort
        if (PngFormat != 0 && IsClipboardFormatAvailable(PngFormat)) return ClipboardImageKind.Png;
        if (IsClipboardFormatAvailable(CF_HDROP)) return ClipboardImageKind.FileDrop;
        if (IsClipboardFormatAvailable(CF_DIB) || IsClipboardFormatAvailable(CF_DIBV5)) return ClipboardImageKind.DibOnly;
        return ClipboardImageKind.None;
    }

    public byte[]? ReadPng()
    {
        if (PngFormat == 0 || !OpenClipboard(0)) return null;
        try
        {
            var h = GetClipboardData(PngFormat);
            if (h == 0) return null;
            var p = GlobalLock(h);
            if (p == 0) return null;
            try
            {
                var size = (int)GlobalSize(h);
                if (size <= 0) return null;
                var buf = new byte[size];
                Marshal.Copy(p, buf, 0, size);
                return buf;
            }
            finally { GlobalUnlock(h); }
        }
        catch (Exception) { return null; }   //fail soft, a paste that cannot read should show a message rather than crash
        finally { CloseClipboard(); }
    }

    public IReadOnlyList<string> ReadFileDrop()
    {
        if (!OpenClipboard(0)) return Array.Empty<string>();
        try
        {
            var h = GetClipboardData(CF_HDROP);
            if (h == 0) return Array.Empty<string>();
            var count = DragQueryFileW(h, 0xFFFFFFFF, null, 0);
            var paths = new List<string>((int)count);
            for (uint i = 0; i < count; i++)
            {
                var len = DragQueryFileW(h, i, null, 0);
                if (len == 0) continue;
                var buf = new char[len + 1];
                if (DragQueryFileW(h, i, buf, len + 1) > 0) paths.Add(new string(buf, 0, (int)len));
            }
            return paths;
        }
        catch (Exception) { return Array.Empty<string>(); }
        finally { CloseClipboard(); }
    }
}
