using System.Text;

namespace Gatto.Terminal;

//one wrapped visual row: its text and the source chars it consumed, including spaces dropped at a wrap, summing to the source length
public readonly record struct WrapSeg(string Text, int SourceChars);

//the one word-wrap policy for every REPL surface: cell-aware, breaks at spaces, keeps the first row's indent and hard-breaks a long word
public static class SoftWrap
{
    public static IReadOnlyList<WrapSeg> Wrap(string text, int firstBudget, int contBudget)
    {
        if (firstBudget <= 0 || contBudget <= 0 || text.Length == 0)
            return new[] { new WrapSeg(text, text.Length) };

        var segs = new List<WrapSeg>();
        var row = new StringBuilder(); var rowCells = 0; var rowChars = 0;
        var word = new StringBuilder(); var wordCells = 0;
        var budget = firstBudget;

        void EndRow(bool wrapped)
        {
            //a wrapped row drops its trailing space from the text but still counts it in SourceChars, and the input's own end keeps it
            var t = row.ToString();
            if (wrapped && t.Length > 0 && t[^1] == ' ') t = t[..^1];
            segs.Add(new WrapSeg(t, rowChars));
            row.Clear(); rowCells = 0; rowChars = 0;
            budget = contBudget;
        }

        void CommitWord()
        {
            if (word.Length == 0) return;
            if (rowCells > 0 && rowCells + wordCells > budget) EndRow(wrapped: true);
            if (wordCells > budget)
            {   //a word longer than one row is hard-broken rather than left to overflow
                foreach (var r in word.ToString().EnumerateRunes())
                {
                    var rw = UnicodeWidth.OfRune(r);
                    if (rowCells + rw > budget && rowCells > 0) EndRow(wrapped: true);
                    row.Append(r.ToString()); rowCells += rw; rowChars += r.Utf16SequenceLength;
                }
            }
            else { row.Append(word); rowCells += wordCells; rowChars += word.Length; }
            word.Clear(); wordCells = 0;
        }

        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value == ' ')
            {
                CommitWord();
                if (rowCells + 1 > budget) { rowChars++; EndRow(wrapped: true); }  //the space that falls on the boundary is dropped
                else if (rowCells == 0 && segs.Count > 0)                          //a space that opens a wrapped row is dropped
                    segs[^1] = segs[^1] with { SourceChars = segs[^1].SourceChars + 1 };  //the dropped space still counts, charged to the row it trailed
                else { row.Append(' '); rowCells++; rowChars++; }                  //every other space is kept, including the first row's indent
            }
            else { word.Append(rune.ToString()); wordCells += UnicodeWidth.OfRune(rune); }
        }
        CommitWord();
        if (row.Length > 0 || rowChars > 0 || segs.Count == 0) EndRow(wrapped: false);
        return segs;
    }

    //the row and cell column a source char index renders at
    public static (int Row, int Cells) MapCursor(IReadOnlyList<WrapSeg> segs, string text, int cursorChars)
    {
        cursorChars = Math.Clamp(cursorChars, 0, text.Length);
        var start = 0;
        for (var i = 0; i < segs.Count; i++)
        {
            var end = start + segs[i].SourceChars;
            if (cursorChars < end || i == segs.Count - 1)
            {
                var into = Math.Clamp(cursorChars - start, 0, segs[i].Text.Length);
                return (i, UnicodeWidth.Of(segs[i].Text[..into]));
            }
            start = end;
        }
        return (0, 0);
    }
}
