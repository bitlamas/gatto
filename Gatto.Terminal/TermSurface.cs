namespace Gatto.Terminal;

public interface ITermSurface
{
    //terminal columns, 0 when unknown, and the repaint math has to cope with that
    int Width { get; }
    //terminal rows, 0 when unknown, and the repaint math then uses the 8-row floor
    int Height { get; }
    void Write(string s);

    //whether the surface can be resized while a screen waits for a key, so the screen should re-read its size between keys
    bool ReportsResize => false;
}

public sealed class ConsoleSurface : ITermSurface
{
    public int Width
    {
        get { try { return Console.BufferWidth; } catch (Exception) { return 0; } }
    }
    public int Height
    {
        get { try { return Console.WindowHeight; } catch (Exception) { return 0; } }
    }
    public void Write(string s) => Console.Write(s);

    //the real console can be resized while a key is awaited
    public bool ReportsResize => true;
}
