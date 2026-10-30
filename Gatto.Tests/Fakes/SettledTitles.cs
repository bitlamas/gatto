using Gatto.Terminal;

namespace Gatto.Tests.Fakes;

//the titles a session settled on: a turn's box frames and the cat put back after them dropped, since a slow turn ticks and a quick one does not
public static class SettledTitles
{
    public static List<string> Of(IEnumerable<string> applied)
    {
        var settled = new List<string>();
        var afterFrame = false;
        foreach (var title in applied)
        {
            if (TerminalTitle.BusyFrames.Any(f => title.Contains(f, StringComparison.Ordinal)))
            {
                afterFrame = true;
                continue;
            }
            if (!(afterFrame && settled.Count > 0 && settled[^1] == title)) settled.Add(title);
            afterFrame = false;
        }
        return settled;
    }
}
