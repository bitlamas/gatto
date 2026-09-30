using System.Text;
using Gatto.Terminal;

namespace Gatto.Repl.Render;

//wraps styled spans into rows with SoftWrap's policy and slices each span back out, a link can't cross a newline
public static class SpanWrap
{
    private const char Nbsp = ' ';   //no-break space (one cell wide), so a chip wraps as one word

    public static IReadOnlyList<IReadOnlyList<StyledSpan>> Wrap(
        IReadOnlyList<StyledSpan> spans, int firstBudget, int contBudget)
    {
        //join the span texts, turning a chip's spaces into Nbsp so the chip stays one word
        var concat = new StringBuilder();
        var starts = new int[spans.Count];
        for (var k = 0; k < spans.Count; k++)
        {
            starts[k] = concat.Length;
            var t = spans[k].Text;
            if (spans[k].Flags.HasFlag(SpanFlags.Chip))
                t = t.Replace(' ', Nbsp);
            concat.Append(t);
        }
        var text = concat.ToString();

        //wrap the joined text with the shared policy, the chips are plain Nbsp words by now
        var segs = WrapCells(text, firstBudget, contBudget);

        //slice the spans back out per row, a row starts at its source position and its dropped chars are trailing spaces
        var rows = new List<IReadOnlyList<StyledSpan>>(segs.Count);
        var pos = 0;
        foreach (var seg in segs)
        {
            var visStart = pos;
            var visEnd = pos + seg.Text.Length;   //the dropped chars run from visEnd to the row's source end
            pos += seg.SourceChars;

            var row = new List<StyledSpan>();
            for (var k = 0; k < spans.Count; k++)
            {
                var sStart = starts[k];
                var sEnd = sStart + spans[k].Text.Length;
                var oStart = Math.Max(visStart, sStart);
                var oEnd = Math.Min(visEnd, sEnd);
                if (oEnd <= oStart) continue;   //no overlap, or a span with no text, so skip it
                var piece = text.Substring(oStart, oEnd - oStart);
                if (spans[k].Flags.HasFlag(SpanFlags.Chip)) piece = piece.Replace(Nbsp, ' ');
                row.Add(new StyledSpan(piece, spans[k].Flags, spans[k].LinkUrl));
            }
            rows.Add(row);
        }
        return rows;
    }

    //the greedy wrap policy of SoftWrap.Wrap, byte-identical output, kept as its own copy only to drive the per-span slicing
    private static IReadOnlyList<WrapSeg> WrapCells(string text, int firstBudget, int contBudget)
    {
        if (firstBudget <= 0 || contBudget <= 0 || text.Length == 0)
            return new[] { new WrapSeg(text, text.Length) };

        var segs = new List<WrapSeg>();
        var row = new StringBuilder(); var rowCells = 0; var rowChars = 0;
        var word = new StringBuilder(); var wordCells = 0;
        var budget = firstBudget;

        void EndRow(bool wrapped)
        {
            var t = row.ToString();
            if (wrapped && t.Length > 0 && t[^1] == ' ') t = t[..^1];
            segs.Add(new WrapSeg(t, rowChars));
            row.Clear(); rowCells = 0; rowChars = 0;
            budget = contBudget;
        }

        void CommitWord()
        {
            if (word.Length == 0) return;
            var cost = wordCells;
            if (rowCells > 0 && rowCells + cost > budget) EndRow(wrapped: true);
            if (cost > budget)
            {   //a word wider than a full row is hard-broken rune by rune
                foreach (var r in word.ToString().EnumerateRunes())
                {
                    var rw = UnicodeWidth.OfRune(r);
                    if (rowCells + rw > budget && rowCells > 0) EndRow(wrapped: true);
                    row.Append(r.ToString()); rowCells += rw; rowChars += r.Utf16SequenceLength;
                }
            }
            else { row.Append(word); rowCells += cost; rowChars += word.Length; }
            word.Clear(); wordCells = 0;
        }

        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value == ' ')   //only a real U+0020 breaks a word, a chip's Nbsp spaces stay inside it
            {
                CommitWord();
                if (rowCells + 1 > budget) { rowChars++; EndRow(wrapped: true); }  //the boundary space is dropped from the row
                else if (rowCells == 0 && segs.Count > 0)                          //a space at the start of a wrapped row is dropped and charged to the row before it
                    segs[^1] = segs[^1] with { SourceChars = segs[^1].SourceChars + 1 };
                else { row.Append(' '); rowCells++; rowChars++; }
            }
            else
            {
                word.Append(rune.ToString()); wordCells += UnicodeWidth.OfRune(rune);
            }
        }
        CommitWord();
        if (row.Length > 0 || rowChars > 0 || segs.Count == 0) EndRow(wrapped: false);
        return segs;
    }
}
