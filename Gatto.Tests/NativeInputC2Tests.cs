using Gatto.Terminal;
using Xunit;

namespace Gatto.Tests;

//the double-click flag becomes Clicks 2, and a moved record whose button changed becomes a Press or Release edge (conhost needs the synthesis)
public class NativeInputC2Tests
{
    private const uint DOUBLE_CLICK = 0x0002, MOUSE_MOVED = 0x0001, BTN_LEFT = 0x0001;

    private static MOUSE_EVENT_RECORD M(uint buttons, uint flags = 0, short x = 0, short y = 0) => new()
    {
        dwButtonState = buttons, dwEventFlags = flags, dwMousePosition = new COORD { X = x, Y = y },
    };

    [Fact] public void Double_click_sets_Clicks_2()
    {
        uint prev = 0;
        var m = NativeInput.TranslateMouse(M(BTN_LEFT, DOUBLE_CLICK), ref prev);
        Assert.Equal(MouseKind.Press, m!.Kind);
        Assert.Equal(2, m.Clicks);
    }

    [Fact] public void Single_press_is_Clicks_1()
    {
        uint prev = 0;
        var m = NativeInput.TranslateMouse(M(BTN_LEFT), ref prev);
        Assert.Equal(1, m!.Clicks);
    }

    [Fact] public void Moved_with_button_down_transition_synthesizes_a_Press()
    {
        uint prev = 0;   //zero means the button was up before this record.
        var m = NativeInput.TranslateMouse(M(BTN_LEFT, MOUSE_MOVED, 4, 2), ref prev);
        Assert.Equal(MouseKind.Press, m!.Kind);   //the edge must be the synthesized Press (a Move would mean the transition was ignored)
        Assert.Equal((4, 2), (m.X, m.Y));
    }

    [Fact] public void Lost_release_heals_state0_record_becomes_a_Release()   //the real case is alt-tabbing out of a drag, which loses the release.
    {
        uint prev = BTN_LEFT;   //the button was down before this record, so a state-zero record means a lost release.
        var m = NativeInput.TranslateMouse(M(0, 0, 1, 1), ref prev);
        Assert.Equal(MouseKind.Release, m!.Kind);
    }

    [Fact] public void Pure_move_no_transition_stays_Move()
    {
        uint prev = 0;
        var m = NativeInput.TranslateMouse(M(0, MOUSE_MOVED, 3, 3), ref prev);
        Assert.Equal(MouseKind.Move, m!.Kind);
    }

    [Fact] public void Held_button_no_move_is_a_noop()   //translating a held button with no movement must produce nothing, so a hold never re-fires a press.
    {
        uint prev = BTN_LEFT;
        Assert.Null(NativeInput.TranslateMouse(M(BTN_LEFT), ref prev));
    }
}
