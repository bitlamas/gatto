using Gatto.Cli.Setup.Tui;
using Gatto.Terminal;

namespace Gatto.Tests.Fakes;

//scripted mouse input for the rig: a press on a target's first cell, after the double-click window has passed unless the test asks for a double click
internal static class RigMouse
{
    //the rig turns this into an action that moves its clock past the window
    internal sealed record PastTheWindow;

    public static MouseEvent Press(int x, int y, MouseButton button = MouseButton.Left) =>
        new(x, y, MouseKind.Press, button, 0, 0);

    public static object[] On(HitTarget t, bool @double = false) =>
        @double ? [Press(t.FirstCol, t.Row)] : [new PastTheWindow(), Press(t.FirstCol, t.Row)];

    public static MouseEvent Wheel(HitTarget t, int delta) => new(t.FirstCol, t.Row, MouseKind.Wheel, MouseButton.None, delta, 0);
}
