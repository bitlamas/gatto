using System.Runtime.InteropServices;
using Gatto.Terminal;

namespace Gatto.Tests;

public class NativeInputTranslateTests
{
    private const ushort VK_A = 0x41, VK_SHIFT = 0x10, VK_MENU = 0x12;
    private const uint RIGHT_ALT = 0x0001, LEFT_ALT = 0x0002, LEFT_CTRL = 0x0008, SHIFT = 0x0010;

    private static KEY_EVENT_RECORD Key(bool down, ushort vk, char ch, uint ctrl, ushort rep = 1) => new()
    {
        bKeyDown = down ? 1 : 0, wVirtualKeyCode = vk, UnicodeChar = ch, dwControlKeyState = ctrl, wRepeatCount = rep,
    };

    [Fact] public void Letter_keydown_translates()
    {
        var k = NativeInput.TranslateKey(Key(true, VK_A, 'a', 0));
        Assert.NotNull(k);
        Assert.Equal(ConsoleKey.A, k!.Value.Key);
        Assert.Equal('a', k.Value.KeyChar);
    }

    [Fact] public void Plain_keyup_is_dropped()
        => Assert.Null(NativeInput.TranslateKey(Key(false, VK_A, 'a', 0)));

    [Fact] public void Alt_up_with_synthesized_char_is_kept()   //a key-up with only a synthesized char comes from Alt plus numpad entry or from paste.
    {
        var k = NativeInput.TranslateKey(Key(false, VK_MENU, 'é', 0));
        Assert.NotNull(k);
        Assert.Equal('é', k!.Value.KeyChar);
    }

    [Fact] public void Alt_up_without_char_is_dropped()
        => Assert.Null(NativeInput.TranslateKey(Key(false, VK_MENU, '\0', 0)));

    [Fact] public void Bare_shift_keydown_is_dropped()          //a bare Shift must not translate, or a ghost key reaches the composer.
        => Assert.Null(NativeInput.TranslateKey(Key(true, VK_SHIFT, '\0', SHIFT)));

    [Fact] public void AltGr_is_ctrl_plus_alt()                 //the AltGr combination reports as Ctrl+Alt, and users type or paste accented chars with it.
    {
        var k = NativeInput.TranslateKey(Key(true, VK_A, 'ä', LEFT_CTRL | RIGHT_ALT));
        Assert.NotNull(k);
        Assert.True(k!.Value.Modifiers.HasFlag(ConsoleModifiers.Control));
        Assert.True(k.Value.Modifiers.HasFlag(ConsoleModifiers.Alt));
    }

    [Fact] public void Alt_numpad_composition_keydowns_are_dropped()
    {
        Assert.Null(NativeInput.TranslateKey(Key(true, 0x62, '\0', RIGHT_ALT)));   //the virtual key 0x62 with right Alt is NumPad2 with NumLock on
        Assert.Null(NativeInput.TranslateKey(Key(true, 0x21, '\0', LEFT_ALT)));    //with NumLock off, NumPad2 arrives as the PageUp key 0x21
    }

    [Fact] public void KeyEvent_UnicodeChar_marshals_as_UTF16_not_ANSI()   //the record is built from raw bytes, so the test runs without a console.
    {
        //offset 10 of the record holds UnicodeChar, written as little-endian U+8A9E
        var bytes = new byte[16];
        bytes[0] = 1;                       //offset 0 is bKeyDown
        bytes[10] = 0x9E; bytes[11] = 0x8A;
        var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            var rec = Marshal.PtrToStructure<KEY_EVENT_RECORD>(pin.AddrOfPinnedObject());
            Assert.Equal('語', rec.UnicodeChar);              //under ANSI marshaling the char becomes U+FFFD, so this assert pins UTF-16.
            Assert.Equal(16, Marshal.SizeOf<KEY_EVENT_RECORD>());
        }
        finally { pin.Free(); }
    }

    private static MOUSE_EVENT_RECORD M(uint buttons = 0, uint flags = 0, short x = 0, short y = 0) => new()
    {
        dwButtonState = buttons, dwEventFlags = flags, dwMousePosition = new COORD { X = x, Y = y },
    };

    [Fact] public void Wheel_up_is_positive_delta()
    {
        uint prev = 0;
        var m = NativeInput.TranslateMouse(M(buttons: 0x00780000, flags: 0x0004), ref prev); //the flag 0x0004 means WHEELED
        Assert.Equal(MouseKind.Wheel, m!.Kind);
        Assert.Equal(120, m.WheelDelta);
    }

    [Fact] public void Wheel_down_is_negative_delta()   //measured hardware sent a magnitude of 128 for wheel-down, so the delta is not fixed at 120.
    {
        uint prev = 0;
        var m = NativeInput.TranslateMouse(M(buttons: 0xFF800000, flags: 0x0004), ref prev);
        Assert.Equal(-128, m!.WheelDelta);
    }

    [Fact] public void Horizontal_wheel_is_ignored()
    {
        uint prev = 0;
        Assert.Null(NativeInput.TranslateMouse(M(buttons: 0x00780000, flags: 0x0008), ref prev)); //the flag 0x0008 means HWHEELED
    }

    [Fact] public void Left_press_fires_on_down_edge_only()
    {
        uint prev = 0;
        var down = NativeInput.TranslateMouse(M(buttons: 0x0001, x: 5, y: 7), ref prev);
        Assert.Equal(MouseKind.Press, down!.Kind);
        Assert.Equal(MouseButton.Left, down.Button);
        Assert.Equal((5, 7), (down.X, down.Y));

        var held = NativeInput.TranslateMouse(M(buttons: 0x0001), ref prev);   //no button transition happened between the two records.
        Assert.True(held is null || held.Kind != MouseKind.Press);              //while the button is held, no press may be emitted.

        var up = NativeInput.TranslateMouse(M(buttons: 0x0000), ref prev);      //the button went from down to up, so this record is a release edge.
        Assert.Equal(MouseKind.Release, up!.Kind);
    }

    [Fact] public void Move_translates_to_a_Move()
    {
        uint prev = 0;
        var m = NativeInput.TranslateMouse(M(flags: 0x0001, x: 2, y: 3), ref prev); //the flag 0x0001 means MOUSE_MOVED
        Assert.Equal(MouseKind.Move, m!.Kind);
    }
}
