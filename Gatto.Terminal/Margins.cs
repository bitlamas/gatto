namespace Gatto.Terminal;

//the frame's margins, one home. content ends two cells short of the right wall. rules keep the full width, they're edges rather than rows
public static class Margins
{
    //blank between right-aligned content and the edge. same number as the left indent, the complaint was asymmetry, pick it deliberately
    public const int Right = 2;

    //the width right-aligned content composes to. floored at 1: a terminal narrower than the margin is no reason to compute a negative width
    public static int Inside(int width) => System.Math.Max(1, width - Right);
}
