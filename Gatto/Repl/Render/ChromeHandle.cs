namespace Gatto.Repl.Render;

//a painter with no renderer would paint a prompt that leaves no transcript, so set and clear the two together
public sealed class ChromeHandle
{
    //the live painter, and null or a torn-down one both count as absent, every prompter checks Alive before painting
    public volatile ChromePainter? Painter;

    //commits the answered prompt block to scrollback, set and cleared together with Painter
    public volatile StreamRenderer? Renderer;
}
