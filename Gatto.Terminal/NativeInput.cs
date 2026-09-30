using System.Runtime.InteropServices;

namespace Gatto.Terminal;

//this struct follows the windows layout: EventType is a WORD at offset 0, the union begins at offset 4

[StructLayout(LayoutKind.Sequential)]
public struct COORD { public short X; public short Y; }

//this struct must declare CharSet.Unicode, a DllImport's CharSet doesn't reach nested fields, without it WCHAR marshals one byte
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct KEY_EVENT_RECORD
{
    public int bKeyDown;            //the native type is BOOL, int width
    public ushort wRepeatCount;
    public ushort wVirtualKeyCode;
    public ushort wVirtualScanCode;
    public char UnicodeChar;        //the native type is WCHAR
    public uint dwControlKeyState;
}

[StructLayout(LayoutKind.Sequential)]
public struct MOUSE_EVENT_RECORD
{
    public COORD dwMousePosition;
    public uint dwButtonState;      //the low word is the buttons, the high word the signed wheel delta for WHEELED
    public uint dwControlKeyState;
    public uint dwEventFlags;
}

[StructLayout(LayoutKind.Explicit)]
public struct INPUT_RECORD
{
    [FieldOffset(0)] public ushort EventType;
    [FieldOffset(4)] public KEY_EVENT_RECORD KeyEvent;
    [FieldOffset(4)] public MOUSE_EVENT_RECORD MouseEvent;
}

//converters from console records to gatto's model. its skip rules are ported verbatim from .NET's console: skip all key-up but Alt-up with a synthesized char
public static class NativeInput
{
    public const ushort EventKey = 0x0001, EventMouse = 0x0002;

    private const ushort VkMenu = 0x12;          //the Alt virtual key
    //control key-state bits
    private const uint RightAlt = 0x0001, LeftAlt = 0x0002, RightCtrl = 0x0004, LeftCtrl = 0x0008, Shift = 0x0010;
    //mouse event flags
    private const uint MouseMoved = 0x0001, DoubleClick = 0x0002, MouseWheeled = 0x0004, MouseHWheeled = 0x0008;
    //button-state bits (low word)
    private const uint BtnLeft = 0x0001, BtnRight = 0x0002, BtnMiddle = 0x0004;

    //translate one key record, null if it isn't a read-key event. wRepeatCount is expanded at the source, a held key yields N events
    public static ConsoleKeyInfo? TranslateKey(in KEY_EVENT_RECORD r)
    {
        //drop key-up except an Alt-up with a synthesized char, that char is emitted through the gate below
        if (r.bKeyDown == 0 && r.wVirtualKeyCode != VkMenu) return null;
        //drops a charless modifier press, so a bare Shift/Ctrl/Alt press never surfaces as a ghost key
        if (r.UnicodeChar == '\0' && IsModKey(r.wVirtualKeyCode)) return null;
        //skip Alt+numpad composition key-downs, the composed char arrives on the Alt-up. otherwise Alt+0233 types "0233é"
        if (r.bKeyDown != 0 && IsAltComposition(r)) return null;

        var s = r.dwControlKeyState;
        return new ConsoleKeyInfo(
            r.UnicodeChar,
            (ConsoleKey)r.wVirtualKeyCode,
            shift: (s & Shift) != 0,
            alt: (s & (LeftAlt | RightAlt)) != 0,
            control: (s & (LeftCtrl | RightCtrl)) != 0);
    }

    //the same set .NET checks: Shift/Ctrl/Alt plus the Caps, Num and Scroll locks
    private static bool IsModKey(ushort vk) => vk is (>= 0x10 and <= 0x12) or 0x14 or 0x90 or 0x91;

    //with Alt down these key-downs are composition strokes, .NET skips them too, AltGr included
    private static bool IsAltComposition(in KEY_EVENT_RECORD r)
    {
        if ((r.dwControlKeyState & (LeftAlt | RightAlt)) == 0) return false;
        var vk = r.wVirtualKeyCode;
        return (vk is >= 0x60 and <= 0x69) || vk == 0x0C || vk == 0x2D || (vk is >= 0x21 and <= 0x28);
    }

    //null for horizontal wheel and no-change records. presses and releases edge-detect against prevButtons so a held button fires once
    public static MouseEvent? TranslateMouse(in MOUSE_EVENT_RECORD r, ref uint prevButtons)
    {
        var mods = Mods(r.dwControlKeyState);

        if ((r.dwEventFlags & MouseWheeled) != 0)
            return new MouseEvent(r.dwMousePosition.X, r.dwMousePosition.Y, MouseKind.Wheel,
                MouseButton.None, (short)(r.dwButtonState >> 16), mods);   //the wheel record must not disturb the held-button state
        if ((r.dwEventFlags & MouseHWheeled) != 0)
            return null;   //horizontal wheel is ignored

        var state = r.dwButtonState & 0xFFFF;                 //low word is the buttons
        var changed = state ^ (prevButtons & 0xFFFF);
        prevButtons = state;

        //a button edge beats a fused move, losing the edge is worse than skipping the hover. one button changes per record
        if (changed != 0)
        {
            MouseButton btn;
            if ((changed & BtnLeft) != 0) btn = MouseButton.Left;
            else if ((changed & BtnRight) != 0) btn = MouseButton.Right;
            else if ((changed & BtnMiddle) != 0) btn = MouseButton.Middle;
            else return null;                                //changed, but no button we track
            var pressed = (state & ButtonBit(btn)) != 0;
            var clicks = pressed && (r.dwEventFlags & DoubleClick) != 0 ? 2 : 1;   //the OS double-click flag sets 2
            return new MouseEvent(r.dwMousePosition.X, r.dwMousePosition.Y,
                pressed ? MouseKind.Press : MouseKind.Release, btn, 0, mods) { Clicks = clicks };
        }

        //no transition: a pointer move, or a stray held-button record with no edge
        if ((r.dwEventFlags & MouseMoved) != 0)
            return new MouseEvent(r.dwMousePosition.X, r.dwMousePosition.Y, MouseKind.Move, MouseButton.None, 0, mods);
        return null;
    }

    private static uint ButtonBit(MouseButton b) => b switch
    {
        MouseButton.Left => BtnLeft, MouseButton.Right => BtnRight, MouseButton.Middle => BtnMiddle, _ => 0,
    };

    private static ConsoleModifiers Mods(uint state)
    {
        ConsoleModifiers m = 0;
        if ((state & Shift) != 0) m |= ConsoleModifiers.Shift;
        if ((state & (LeftAlt | RightAlt)) != 0) m |= ConsoleModifiers.Alt;
        if ((state & (LeftCtrl | RightCtrl)) != 0) m |= ConsoleModifiers.Control;
        return m;
    }
}
