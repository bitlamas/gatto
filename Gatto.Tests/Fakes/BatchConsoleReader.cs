using Gatto.Terminal;

namespace Gatto.Tests.Fakes;

//serves scripted batches: each Read hands one as a burst arrives, Peek sees the next one only, and an empty batch is a pause the first Peek spends
internal sealed class BatchConsoleReader(params INPUT_RECORD[][] batches) : IConsoleInputReader
{
    private readonly List<INPUT_RECORD[]> _batches = [.. batches];
    private const uint MouseMoved = 0x0001, MouseWheeled = 0x0004, BtnLeft = 0x0001;

    public int Batches => _batches.Count;

    public int Read(INPUT_RECORD[] buf)
    {
        while (_batches.Count > 0 && _batches[0].Length == 0) _batches.RemoveAt(0);
        if (_batches.Count == 0) return 0;
        var n = Peek(buf);
        //a batch longer than the buffer leaves its tail for the next read, as the console does
        if (n == _batches[0].Length) _batches.RemoveAt(0); else _batches[0] = _batches[0][n..];
        return n;
    }

    public int Peek(INPUT_RECORD[] buf)
    {
        if (_batches.Count == 0) return 0;
        if (_batches[0].Length == 0) { _batches.RemoveAt(0); return 0; }
        var b = _batches[0];
        var n = Math.Min(b.Length, buf.Length);
        for (var i = 0; i < n; i++) buf[i] = b[i];
        return n;
    }

    public static INPUT_RECORD KeyRec(char ch, ushort rep, bool down = true, ushort vk = 0x41) => new()
    {
        EventType = NativeInput.EventKey,
        KeyEvent = new KEY_EVENT_RECORD { bKeyDown = down ? 1 : 0, wVirtualKeyCode = vk, UnicodeChar = ch, wRepeatCount = rep },
    };

    //a key down whose virtual key is the key's own, so Enter and Escape answer as keys do
    public static INPUT_RECORD Key(char ch, ConsoleKey key = 0) =>
        KeyRec(ch, 1, vk: key != 0 ? (ushort)key : char.IsAsciiLetterOrDigit(ch) ? char.ToUpperInvariant(ch) : (ushort)0x41);

    public static INPUT_RECORD MouseRec(uint buttons) => new()
    {
        EventType = NativeInput.EventMouse,
        MouseEvent = new MOUSE_EVENT_RECORD { dwButtonState = buttons },
    };

    //the left button down. the source detects a press against the last button state, so a second press needs a release before it
    public static INPUT_RECORD Press(int x, int y) => Mouse(x, y, BtnLeft, 0);

    public static INPUT_RECORD Release(int x, int y) => Mouse(x, y, 0, 0);

    public static INPUT_RECORD Move(int x, int y, uint buttons = 0) => Mouse(x, y, buttons, MouseMoved);

    //the high word of the button state carries the signed delta
    public static INPUT_RECORD Wheel(int x, int y, int delta) => Mouse(x, y, (uint)(delta << 16), MouseWheeled);

    private static INPUT_RECORD Mouse(int x, int y, uint state, uint flags) => new()
    {
        EventType = NativeInput.EventMouse,
        MouseEvent = new MOUSE_EVENT_RECORD
        {
            dwButtonState = state, dwEventFlags = flags,
            dwMousePosition = new COORD { X = (short)x, Y = (short)y },
        },
    };
}
