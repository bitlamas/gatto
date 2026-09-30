namespace Gatto.Core.Tools;

//the strings the tool replaced, after its alignment, and where the match starts in the file's text
public sealed record EditMatch(string Old, string New, int Index);

//the line the edit starts on, the rest of its first and last line, and up to three lines on each side, for display only
public sealed record EditView(int Start, string Head, string Tail, IReadOnlyList<string> Before, IReadOnlyList<string> After);

//the edit tool's match, shared with the permission gate so both find what the tool replaces, and the view of where it lands
public static class EditLocate
{
    public const int Neighbours = 3;
    public const int LineCells = 300;

    //the literal match first, the CRLF alignment only when it finds nothing, and a match only when the count is 1
    public static (EditMatch? Match, int Count) Find(string fileText, string oldString, string newString, CancellationToken ct)
    {
        if (oldString.Length == 0) return (null, 0);   //an empty needle matches everywhere and the count would never end
        var oldS = oldString;
        var newS = newString;
        var count = Count(fileText, oldS, ct);
        if (count == 0 && Align(fileText, oldS) is { } aligned && Count(fileText, aligned, ct) > 0)
        {
            oldS = aligned;
            newS = Align(fileText, newS) ?? newS;   //expand the replacement too, so the file keeps its endings
            count = Count(fileText, oldS, ct);
        }
        return (count == 1 ? new EditMatch(oldS, newS, fileText.IndexOf(oldS, StringComparison.Ordinal)) : null, count);
    }

    //null on any failure, since a view is display only and must never stop the tool or the gate
    public static EditView? View(string fileText, EditMatch match)
    {
        try
        {
            var start = match.Index;
            var end = start + match.Old.Length;
            var lineStart = start == 0 ? 0 : fileText.LastIndexOf('\n', start - 1) + 1;
            var head = Strip(fileText[lineStart..start]);
            string tail;
            int after;
            //both strings end a line only when both end in a newline, otherwise the new text joins the line that followed
            if (match.Old.EndsWith('\n') && match.New.EndsWith('\n'))
            {
                tail = "";
                after = end;
            }
            else
            {
                var nl = fileText.IndexOf('\n', end);
                tail = Strip(nl < 0 ? fileText[end..] : fileText[end..nl]);
                after = nl < 0 ? fileText.Length : nl + 1;
            }
            var line = 1;
            for (var i = 0; i < lineStart; i++) if (fileText[i] == '\n') line++;
            var before = lineStart == 0 ? Array.Empty<string>() : fileText[..(lineStart - 1)].Split('\n').TakeLast(Neighbours).Select(Cut).ToArray();
            var rest = fileText[after..];
            var next = rest.Length == 0 ? Array.Empty<string>() : rest.Split('\n');
            if (next.Length > 0 && rest.EndsWith('\n')) next = next[..^1];
            return new EditView(line, CutHead(head), Cut(tail), before, next.Take(Neighbours).Select(Cut).ToArray());
        }
        catch (Exception) { return null; }
    }

    //the lines the diff compares, whole lines when a view gives the rest of the first and the last line
    public static (IReadOnlyList<string> Old, IReadOnlyList<string> New) Lines(string oldString, string newString, EditView? view) =>
        (Split((view?.Head ?? "") + oldString + (view?.Tail ?? "")), Split((view?.Head ?? "") + newString + (view?.Tail ?? "")));

    private static IReadOnlyList<string> Split(string s)
    {
        if (s.Length == 0) return Array.Empty<string>();
        var lines = s.Split('\n').Select(Strip).ToList();
        if (s.EndsWith('\n')) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    private static string Strip(string line) => line.EndsWith('\r') ? line[..^1] : line;

    private static string Cut(string line)
    {
        var l = Strip(line);
        return l.Length <= LineCells ? l : l[..ToolArgs.SafeCut(l, LineCells)];
    }

    //the head keeps the cells nearest the match, where a long line is read from
    private static string CutHead(string head) =>
        head.Length <= LineCells ? head : head[(head.Length - LineCells + (char.IsLowSurrogate(head[head.Length - LineCells]) ? 1 : 0))..];

    private static int Count(string haystack, string needle, CancellationToken ct)
    {
        var n = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            n++;
        }
        return n;
    }

    //bare LFs become CRLF when the file is CRLF, and it applies to the replacement too, a mixed-endings file aligns nothing
    private static string? Align(string fileText, string s)
    {
        if (!fileText.Contains("\r\n", StringComparison.Ordinal)) return null;
        if (s.Contains("\r\n", StringComparison.Ordinal) || !s.Contains('\n')) return null;
        return s.Replace("\n", "\r\n");
    }
}
