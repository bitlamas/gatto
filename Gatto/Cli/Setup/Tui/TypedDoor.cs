namespace Gatto.Cli.Setup.Tui;

//what the user typed, told apart by shape, with no mode key to say which it is
internal enum DoorInput
{
    //a file or a folder, and a folder is swept. the engine door looks inside it for llama-server.exe, a model door takes every file at once
    Path,
    //org/name on the Hub
    RepoId,
    //a huggingface.co URL, whose grammar is parsed elsewhere
    Url,
    //a server to talk to, from the connect screen
    Address,
    //a context size, with commas accepted since the screens print them
    Number,
    //anything else, which on the shelf is a search
    Search,
}

//the one typed row every screen that takes text shows, told apart by shape. the repo-id test is the Hub client's own
internal static class TypedDoor
{
    //what the door will do with the text, trimmed first so a pasted trailing space changes nothing
    public static DoorInput Classify(string? text)
    {
        var s = (text ?? "").Trim();
        if (s.Length == 0) return DoorInput.Search;

        //a number, commas and all, so the door accepts what the screens print
        if (s.All(c => char.IsAsciiDigit(c) || c == ',') && s.Any(char.IsAsciiDigit))
            return DoorInput.Number;

        if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            //the host decides that a Hub URL is a model and any other http address is a server to talk to
            return Uri.TryCreate(s, UriKind.Absolute, out var u)
                && u.Host.EndsWith("huggingface.co", StringComparison.OrdinalIgnoreCase)
                ? DoorInput.Url
                : DoorInput.Address;
        }

        if (LooksLikePath(s)) return DoorInput.Path;

        //host:port with no scheme is still somewhere to talk to
        if (System.Text.RegularExpressions.Regex.IsMatch(s, @"^[A-Za-z0-9.\-]+:\d{2,5}$"))
            return DoorInput.Address;

        if (Gatto.Core.Acquire.HubClient.LooksLikeRepoId(s)) return DoorInput.RepoId;

        return DoorInput.Search;
    }

    //only Windows shapes count, so a bare org/name stays a repo id and a forward slash alone proves nothing
    private static bool LooksLikePath(string s) =>
        s.Contains('\\', StringComparison.Ordinal)
        || (s.Length >= 2 && char.IsAsciiLetter(s[0]) && s[1] == ':')
        || s.StartsWith('.')
        || s.StartsWith('~')
        || s.Contains(".gguf", StringComparison.OrdinalIgnoreCase)
        || s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

    //the draft scrolls to keep the caret visible, anchored at the end. no ellipsis and no second row, since the footer keeps one row
    public static string Window(string draft, int cells)
    {
        if (cells <= 0) return "";
        if (Gatto.Terminal.UnicodeWidth.Of(draft) <= cells) return draft;

        //count back from the end until the slice fits, since the caret is there and that is what the user must see
        var start = draft.Length;
        var used = 0;
        while (start > 0)
        {
            var w = Gatto.Terminal.UnicodeWidth.Of(draft[(start - 1)].ToString());
            if (used + w > cells) break;
            used += w;
            start--;
        }
        return draft[start..];
    }
}
