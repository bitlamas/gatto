namespace Gatto.Core.Client;

//per-session base64 keyed by sha256, encoded once so the wire bytes stay stable (add a lock if tool calls ever run in parallel)
public sealed class ImageCache
{
    private readonly Dictionary<string, string> _bySha = new(StringComparer.Ordinal);

    //images already reported as unavailable, so the notice is said once per image per session
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);

    //the data: URI for this reference, null when the file has moved or changed (sending other pixels would lie about what the model saw)

    //report an unavailable image at most once per session, keyed on the sha recorded at attach time so a replacement reports again
    private void Report(ImageRef img, string message, Action<string>? onUnavailable)
    {
        if (_reported.Add(img.Sha256)) onUnavailable?.Invoke(message);
    }

    public string? DataUri(ImageRef img, Action<string>? onUnavailable = null)
    {
        if (_bySha.TryGetValue(img.Sha256, out var cached)) return cached;

        byte[] bytes;
        try { bytes = File.ReadAllBytes(img.Path); }
        catch (Exception)
        {
            Report(img, $"{img.Name} is no longer readable — the model cannot see it any more", onUnavailable);
            return null;
        }

        if (!string.Equals(ImageRef.ShaOf(bytes), img.Sha256, StringComparison.Ordinal))
        {
            Report(img, $"{img.Name} has changed since it was attached — sending it now would misrepresent what the model saw", onUnavailable);
            return null;
        }

        var uri = "data:" + img.MediaType + ";base64," + Convert.ToBase64String(bytes);
        _bySha[img.Sha256] = uri;
        return uri;
    }
}
