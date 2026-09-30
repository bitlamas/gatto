using System.Text;

namespace Gatto.Tests.Fakes;

//replay cursor and erase escapes, since a byte log holds rows the terminal erased. one shared copy, since two emulators drift when either learns a new escape.
public static class TerminalReplay
{
    //colour and cursor-visibility escapes change nothing in the grid, so the replay drops them, and paint assertions match them in the escape runs
    public static string Screen(string written)
    {
        var rows = new List<StringBuilder> { new() };
        int row = 0, col = 0;

        for (var i = 0; i < written.Length; i++)
        {
            var ch = written[i];
            if (ch == '\x1b' && i + 1 < written.Length && written[i + 1] == '[')
            {
                var j = i + 2;
                while (j < written.Length && !char.IsLetter(written[j])) j++;
                if (j >= written.Length) break;
                var body = written[(i + 2)..j];
                switch (written[j])
                {
                    case 'A':                                    //the A escape moves the cursor up
                        row = Math.Max(0, row - (int.TryParse(body, out var n) ? n : 1));
                        break;
                    case 'K':                                    //the K escape erases from the cursor to the end of the line
                        if (rows[row].Length > col) rows[row].Length = col;
                        break;
                }
                i = j;
                continue;
            }
            if (ch == '\r') { col = 0; continue; }
            if (ch == '\n')
            {
                row++;
                col = 0;
                while (rows.Count <= row) rows.Add(new StringBuilder());
                continue;
            }
            while (rows[row].Length < col) rows[row].Append(' ');
            if (col < rows[row].Length) rows[row][col] = ch; else rows[row].Append(ch);
            col++;
        }
        return string.Join("\n", rows.Select(r => r.ToString().TrimEnd()));
    }

    //the same replay, but a finite width and deferred wrap: the last column stays pending, so a following ESC[K erases it. use it for full-width assertions.
    public static string ScreenAt(string written, int width)
    {
        if (width <= 0) return Screen(written);

        var rows = new List<char[]>();
        void Grow(int to) { while (rows.Count <= to) { var r = new char[width]; Array.Fill(r, ' '); rows.Add(r); } }
        Grow(0);

        int row = 0, col = 0;
        var pending = false;   //pending means the cursor sits on the last column with the wrap deferred.

        for (var i = 0; i < written.Length; i++)
        {
            var ch = written[i];
            if (ch == '\x1b' && i + 1 < written.Length && written[i + 1] == '[')
            {
                var j = i + 2;
                while (j < written.Length && !char.IsLetter(written[j])) j++;
                if (j >= written.Length) break;
                var body = written[(i + 2)..j];
                switch (written[j])
                {
                    case 'A':
                        row = Math.Max(0, row - (int.TryParse(body, out var n) ? n : 1));
                        pending = false;
                        break;
                    case 'K':
                        //erase runs from the cursor, and in the pending state that erases the character that caused the pending.
                        for (var c = col; c < width; c++) rows[row][c] = ' ';
                        break;
                }
                i = j;
                continue;
            }
            if (ch == '\r') { col = 0; pending = false; continue; }
            if (ch == '\n') { row++; Grow(row); col = 0; pending = false; continue; }

            if (pending) { row++; Grow(row); col = 0; pending = false; }
            rows[row][col] = ch;
            if (col == width - 1) pending = true; else col++;
        }
        return string.Join("\n", rows.Select(r => new string(r).TrimEnd()));
    }

    //strip the chrome a row opens with, hide-cursor, cursor-up from a repaint and the homing carriage return. an erase stays, since a guard asserts it is there
    public static string StripRowOpener(string row) =>
        System.Text.RegularExpressions.Regex.Replace(
            row, @"^(?:\x1b\[\?25[lh]|\x1b\[\d*[AB]|\r)+", "");

    //drops every escape run from the replayed screen, so use it for layout assertions while a paint assertion reads the escape runs
    public static string Plain(string written)
    {
        var sb = new StringBuilder();
        var screen = Screen(written);
        for (var i = 0; i < screen.Length; i++)
        {
            if (screen[i] == '\x1b' && i + 1 < screen.Length && screen[i + 1] == '[')
            {
                var j = i + 2;
                while (j < screen.Length && !char.IsLetter(screen[j])) j++;
                i = j;
                continue;
            }
            sb.Append(screen[i]);
        }
        return sb.ToString();
    }
}
