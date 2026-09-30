using System.Text;
using Gatto.Terminal;

namespace Gatto.Tests.Render;

//the background in force at each visible cell of a painted row, read from its SGR codes, null where the terminal's own shows
internal static class GroundWalker
{
    //an erase to the end of the line fills the cells from the cursor to the width with the ground then in force
    public static List<string?> Of(string row, int width = 0)
    {
        var cells = new List<string?>();
        string? bg = null;
        var at = 0;
        var i = 0;
        while (i < row.Length)
        {
            if (row[i] == (char)0x1B && i + 1 < row.Length && row[i + 1] == '[')
            {
                var j = i + 2;
                while (j < row.Length && !(row[j] >= '@' && row[j] <= '~')) j++;
                if (j < row.Length && row[j] == 'K' && row[(i + 2)..j] is "" or "0")
                    for (var c = at; c < width; c++) Put(cells, c, bg);
                if (j < row.Length && row[j] == 'm')
                {
                    var ps = row[(i + 2)..j].Split(';');
                    for (var k = 0; k < ps.Length; k++)
                    {
                        if (ps[k] is "" or "0" or "49") bg = null;
                        else if (ps[k] == "48" && k + 1 < ps.Length && ps[k + 1] == "2" && k + 4 < ps.Length) { bg = $"2;{ps[k + 2]};{ps[k + 3]};{ps[k + 4]}"; k += 4; }
                        else if (ps[k] == "48" && k + 2 < ps.Length && ps[k + 1] == "5") { bg = $"5;{ps[k + 2]}"; k += 2; }
                        else if (ps[k] == "38" && k + 1 < ps.Length) k += ps[k + 1] == "2" ? 4 : 2;
                    }
                }
                i = j + 1;
                continue;
            }
            if (!Rune.TryGetRuneAt(row, i, out var rune)) { i++; continue; }
            for (var c = 0; c < UnicodeWidth.OfRune(rune); c++) Put(cells, at++, bg);
            i += rune.Utf16SequenceLength;
        }
        return cells;
    }

    private static void Put(List<string?> cells, int at, string? bg)
    {
        while (cells.Count <= at) cells.Add(null);
        cells[at] = bg;
    }

    //the walker's name for a colour, built from the colour's own values and not from the paint under test
    public static string Code(RgbColor c, bool trueColor) => trueColor ? $"2;{c.R};{c.G};{c.B}" : $"5;{c.Index256}";
}
