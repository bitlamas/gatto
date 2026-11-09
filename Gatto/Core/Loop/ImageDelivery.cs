using Gatto.Core.Client;
using Gatto.Core.Session;

namespace Gatto.Core.Loop;

//the record a round's tool-read pictures reach the model on, one user-role line naming each, the duplicates and the overflow said in it
internal static class ImageDelivery
{
    //the tool that read them, written to the record so a reader tells it from the user's words
    public const string By = "read_file";

    //the shas the next request still carries: every typed record's, and a tool's record only at or past the frontier, since elision cuts the ones before it
    public static HashSet<string> CarriedShas(IReadOnlyList<ChatMessage> messages, int elidedThrough = 0)
    {
        var shas = new HashSet<string>(StringComparer.Ordinal);
        for (var at = 0; at < messages.Count; at++)
            if (messages[at].Images is { } imgs && (messages[at].By is null || at >= elidedThrough))
                foreach (var i in imgs) shas.Add(i.Sha256);
        return shas;
    }

    //drops what the request carries or the round repeats, then what would pass the round's ceiling, and names both in the line
    public static ChatMessage Build(Conversation convo, IReadOnlyList<ImageRef> read, HashSet<string> carried)
    {
        var shown = new List<ImageRef>();
        var already = new List<string>();
        var over = new List<string>();
        long total = 0;
        foreach (var img in read)
        {
            if (carried.Contains(img.Sha256)) { already.Add(img.Name); continue; }
            if (total + img.Bytes > ImageRef.MaxBytes) { over.Add(img.Name); continue; }
            carried.Add(img.Sha256);
            total += img.Bytes;
            shown.Add(img);
        }
        var parts = new List<string>();
        if (shown.Count > 0) parts.Add("images read this round: " + string.Join(", ", shown.Select(i => $"{i.Name} ({i.Size})")));
        if (already.Count > 0) parts.Add("already shown: " + string.Join(", ", already));
        if (over.Count > 0) parts.Add($"not shown, over the round's {ImageRef.MaxBytes / (1024 * 1024)} MB: " + string.Join(", ", over));
        return convo.ImageRecord(string.Join("; ", parts), shown.Count > 0 ? shown : null, By);
    }
}
