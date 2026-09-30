using Gatto.Core;
using Gatto.Core.Client;
using Gatto.Core.Tools;

namespace Gatto.Repl.Input;

//what one message may attach, a count that asks above the threshold and a byte ceiling that no grant waives
public readonly record struct AttachLimits(int PromptAboveImages, long MaxTotalBytes)
{
    public static AttachLimits Default => new(PromptAboveImages: 10, MaxTotalBytes: 20L * 1024 * 1024);
}

//find image files named in a user's message, pure and cwd-relative, with the attached shas passed in by the caller
public static class ImageAttach
{
    //why a notice exists, so a caller can act on it without matching the wording of the sentence
    public enum NoticeKind
    {
        //a claim that something was attached, so it must never be committed on a refusal
        Attached,
        //one candidate did not make it, a duplicate or one already in this conversation, and the rest still travels
        Skipped,
        //gatto refused what the message is about, so the message does not go and the dispatcher acts on the kind
        Blocked,
    }

    public readonly record struct Notice(NoticeKind Kind, string Text);

    //what one message's scan produced, and NeedsConfirmation means a question is owed while every image still sits in Attached
    public readonly record struct Result(IReadOnlyList<ImageRef> Attached, IReadOnlyList<Notice> Notices,
        bool NeedsConfirmation = false);

    //what a scan of one message found, plus notices about a name that nearly resolved
    public readonly record struct Scan(IReadOnlyList<ImageRef> Found, IReadOnlyList<Notice> Notices);

    //a rough per-image token estimate, crude on purpose, so anything shown from it must read as an approximation
    public const int EstimatedTokensPerImage = 280;

    //punctuation a filename picks up in a sentence, stripped only on the second attempt so a real name survives
    private const string TrailingPunctuation = "?!.,;:)'\"`*";
    private const string LeadingPunctuation = "(['\"`*";

    //extensions that make a token look like a picture name, a cheap prefilter since BinarySniff decides
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".bmp"];

    //the longest filename gatto assembles from consecutive words, far past any real name with spaces and it bounds the search
    private const int MaxWordsInFilename = 8;

    //every image file named in the text, raw, with runs of words tried for names with spaces and the shortest match winning
    public static Scan Candidates(string text, string cwd)
    {
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var found = new List<ImageRef>();
        var notices = new List<Notice>();

        for (var i = 0; i < words.Length; i++)
        {
            for (var len = 1; len <= MaxWordsInFilename && i + len <= words.Length; len++)
            {
                if (!LooksLikeImageName(words[i + len - 1])) continue;

                var (path, notice) = Resolve(string.Join(' ', words, i, len), cwd);
                if (path is null)
                {
                    if (notice is Notice miss) notices.Add(miss);
                    continue;
                }

                ImageRef img;
                try { img = ImageRef.FromFile(path); }
                catch (Exception) { continue; }   //the words were not a file after all, so keep scanning

                found.Add(img);
                if (notice is Notice sub) notices.Add(sub);
                i += len - 1;   //consume the run so the rest of the sentence is not part of the name
                break;
            }
        }
        return new Scan(found, notices);
    }

    //the guards applied once to the candidates: session and batch dedupe, the byte backstop, the count question, and the notices
    public static Result ApplyGuards(IReadOnlyList<ImageRef> candidates,
        IReadOnlySet<string> alreadyAttachedShas, AttachLimits limits)
    {
        var kept = new List<ImageRef>();
        var notices = new List<Notice>();
        var seenHere = new HashSet<string>(StringComparer.Ordinal);
        var reportedDuplicates = new HashSet<string>(StringComparer.Ordinal);

        foreach (var img in candidates)
        {
            //already in this conversation, so skip it and keep the batch going
            if (alreadyAttachedShas.Contains(img.Sha256))
            {
                if (reportedDuplicates.Add(img.Sha256))
                    notices.Add(new Notice(NoticeKind.Skipped,
                        $"{img.Name} — already attached, not sending it again"));
                continue;
            }
            //the same bytes twice in one message or one drop, skipped and announced so the user and the model agree on the count
            if (!seenHere.Add(img.Sha256))
            {
                if (reportedDuplicates.Add(img.Sha256))
                    notices.Add(new Notice(NoticeKind.Skipped,
                        $"{img.Name} — the same image twice in one message, sending it once"));
                continue;
            }
            kept.Add(img);
        }

        if (kept.Count == 0) return new Result(Array.Empty<ImageRef>(), notices);

        //the byte backstop, all or nothing and checked before the count question so no permission is asked for an attach already refused
        var total = kept.Sum(f => f.Bytes);
        if (total > limits.MaxTotalBytes)
        {
            notices.Add(new Notice(NoticeKind.Blocked,
                $"attachments too large ({total / (1024 * 1024)} MB, limit {limits.MaxTotalBytes / (1024 * 1024)} MB) — none attached"));
            return new Result(Array.Empty<ImageRef>(), notices);
        }

        //one attached line per message, the line is never silent, and a row per image pushed the conversation off screen
        notices.Add(new Notice(NoticeKind.Attached, AttachedLine(kept)));

        //the count question is decided here at the end, on what the dedupes left, so the number matches what is sent
        return new Result(kept, notices, NeedsConfirmation: kept.Count > limits.PromptAboveImages);
    }

    //the one attached line, names while they stay readable and a count past that, with the token figure marked approximate
    private static string AttachedLine(IReadOnlyList<ImageRef> kept)
    {
        var cost = $"~{Tokens(kept.Count * EstimatedTokensPerImage)} tok est.";
        if (kept.Count == 1) return $"attached {kept[0].Name} ({cost})";

        var names = string.Join(", ", kept.Select(k => k.Name));
        return names.Length <= 72
            ? $"attached {Plural.Of(kept.Count, "image")}: {names} ({cost})"
            : $"attached {Plural.Of(kept.Count, "image")} ({cost})";
    }

    private static string Tokens(int n) =>
        n >= 1000 ? (n / 1000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "k"
                  : n.ToString(System.Globalization.CultureInfo.InvariantCulture);

    //the text-only detect, find then guard, with nothing pasted
    public static Result Detect(string text, string cwd, IReadOnlySet<string> alreadyAttachedShas,
        AttachLimits limits) =>
        Detect(text, cwd, Array.Empty<ImageRef>(), alreadyAttachedShas, limits);

    //scan the text, put pasted images in front of the named ones, and run the whole list through one guard pass from here
    public static Result Detect(string text, string cwd, IReadOnlyList<ImageRef> pasted,
        IReadOnlySet<string> alreadyAttachedShas, AttachLimits limits)
    {
        var scan = Candidates(text, cwd);
        var candidates = pasted.Count == 0 ? scan.Found : pasted.Concat(scan.Found).ToList();
        var guarded = ApplyGuards(candidates, alreadyAttachedShas, limits);
        return scan.Notices.Count == 0
            ? guarded
            : guarded with { Notices = scan.Notices.Concat(guarded.Notices).ToList() };
    }

    //the shas of every image in this conversation, derived each time since a stored set would be wrong after a restore or a compaction
    public static IReadOnlySet<string> ShasIn(IEnumerable<ChatMessage> messages) =>
        messages.SelectMany(m => m.Images ?? (IReadOnlyList<ImageRef>)Array.Empty<ImageRef>())
                .Select(i => i.Sha256)
                .ToHashSet(StringComparer.Ordinal);

    //what Alt+V drops into the composer, so an attachment can be removed with backspace and the model learns where the image sits
    public static string Placeholder(int index) => $"[Image #{index}]";

    private static readonly System.Text.RegularExpressions.Regex PlaceholderPattern =
        new(@"\[Image #(\d+)\]", System.Text.RegularExpressions.RegexOptions.Compiled);

    //the pasted images whose placeholders still appear in the text, in the order they appear (delete [Image #2] and only image 2 is detached)
    public static IReadOnlyList<ImageRef> ReferencedIn(string text, IReadOnlyDictionary<int, ImageRef> pasted)
    {
        if (pasted.Count == 0) return Array.Empty<ImageRef>();
        var found = new List<ImageRef>();
        foreach (System.Text.RegularExpressions.Match m in PlaceholderPattern.Matches(text))
            if (int.TryParse(m.Groups[1].Value, out var n)
                && pasted.TryGetValue(n, out var img)
                && !found.Contains(img))
                found.Add(img);
        return found;
    }

    private static bool LooksLikeImageName(string token)
    {
        var t = Trim(token);
        foreach (var ext in ImageExtensions)
            if (t.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string Trim(string token) =>
        token.TrimStart(LeadingPunctuation.ToCharArray()).TrimEnd(TrailingPunctuation.ToCharArray());

    //resolve the name as written, then with punctuation trimmed, then another extension for the same stem. one sibling is announced, two or more attach nothing
    private static (string? Path, Notice? Notice) Resolve(string token, string cwd)
    {
        if (Try(token) is string exact) return (exact, null);
        var trimmed = Trim(token);
        if (trimmed.Length != token.Length && Try(trimmed) is string t) return (t, null);
        return NearMiss(trimmed, cwd);

        string? Try(string s)
        {
            if (s.Length == 0) return null;
            try
            {
                var full = Path.IsPathRooted(s) ? s : Path.Combine(cwd, s);
                return File.Exists(full) ? full : null;
            }
            catch (Exception) { return null; }   //a token that throws here is just a word
        }
    }

    private static (string? Path, Notice? Notice) NearMiss(string name, string cwd)
    {
        string dir, stem;
        try
        {
            var full = Path.IsPathRooted(name) ? name : Path.Combine(cwd, name);
            dir = Path.GetDirectoryName(full) ?? cwd;
            stem = Path.GetFileNameWithoutExtension(full);
            if (stem.Length == 0 || !Directory.Exists(dir)) return (null, null);
        }
        catch (Exception) { return (null, null); }

        List<string> siblings;
        try
        {
            siblings = ImageExtensions.Select(e => Path.Combine(dir, stem + e)).Where(File.Exists).ToList();
        }
        catch (Exception) { return (null, null); }

        return siblings.Count switch
        {
            //the notice says using: the scan only knows which file it resolved to, and a guard may still drop the image
            1 => (siblings[0], new Notice(NoticeKind.Attached,
                    $"{Path.GetFileName(name)} not found — using {Path.GetFileName(siblings[0])}")),
            //several siblings, so gatto lists them and picks none of them
            > 1 => (null, new Notice(NoticeKind.Blocked,
                    $"{Path.GetFileName(name)} not found — did you mean {string.Join(" or ", siblings.Select(Path.GetFileName))}?")),
            //no siblings, so stay silent (a notice here would make every filename mention a search)
            _ => (null, null),
        };
    }
}
