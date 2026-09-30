using Gatto.Core.Client;
using Gatto.Terminal;

namespace Gatto.Repl.Input;

//turns clipboard images into ImageRef attachments, the shape every later stage already handles. bound to Alt+V, Windows Terminal swallows Ctrl+V
public static class ImagePaste
{
    //raw candidates, no dedupe or caps (paste and typed names share one guard pass at submit). the Notice field explains why Alt+V did nothing
    public readonly record struct Result(IReadOnlyList<ImageRef> Candidates, string? Notice);

    //no conversation and no limits here, dedupe and caps happen once at submit. blobDir holds the pasted bytes, addressed by content so a repeat writes one file
    public static Result Paste(IClipboardImageNative clipboard, string blobDir)
    {
        switch (clipboard.Available())
        {
            case ClipboardImageKind.Png:
                var bytes = clipboard.ReadPng();
                if (bytes is null || bytes.Length == 0)
                    return new Result(Array.Empty<ImageRef>(), "could not read the image from the clipboard");
                try { return new Result(new[] { Blob(bytes, blobDir) }, null); }
                catch (Exception)
                {
                    //a message the user can act on beats writing a blob that can't be sent
                    return new Result(Array.Empty<ImageRef>(),
                        "the clipboard's image data isn't in a format gatto can send — try copying the image file itself");
                }

            case ClipboardImageKind.FileDrop:
                //a copied file already has a path, so no blob is written and it resolves like a typed filename
                var refs = new List<ImageRef>();
                foreach (var path in clipboard.ReadFileDrop())
                {
                    try { refs.Add(ImageRef.FromFile(path)); }
                    catch (Exception) { } //not an image, so skip it in silence
                }
                return refs.Count > 0
                    ? new Result(refs, null)
                    : new Result(Array.Empty<ImageRef>(), "nothing on the clipboard gatto can attach");

            case ClipboardImageKind.DibOnly:
                //refused on purpose (no measured source needs a DIB to PNG encoder, and naming it turns one that does into a report)
                return new Result(Array.Empty<ImageRef>(),
                    "the clipboard holds a bitmap gatto can't read — try copying the image file itself, or re-copy it from a browser");

            default:
                //pastes images only, so nothing to paste says so and points at Ctrl+V rather than adding a second text path
                return new Result(Array.Empty<ImageRef>(), "nothing to paste — Ctrl+V pastes text");
        }
    }

    //the blob is written before the caps run, so an over-cap paste leaves one file nothing ever references (content addressing keeps repeats free)

    //the media type by magic number, the same decider typed filenames use. anything unrecognised throws instead of sending a blob with a label that fits nothing
    private static (string Ext, string Media) Describe(byte[] bytes) =>
        Gatto.Core.Tools.BinarySniff.Describe(bytes) switch
        {
            "PNG image" => (".png", "image/png"),
            "JPEG image" => (".jpg", "image/jpeg"),
            "GIF image" => (".gif", "image/gif"),
            "BMP image" => (".bmp", "image/bmp"),
            var other => throw new InvalidOperationException(
                $"the clipboard offered PNG data that is actually {other ?? "not an image"}"),
        };

    private static ImageRef Blob(byte[] bytes, string blobDir)
    {
        //sniff before anything touches the filesystem, so a refusal here leaves no directory and no orphan blob behind
        var sha = ImageRef.ShaOf(bytes);

        //sniff the bytes instead of trusting the clipboard's label. a wrong media type reaches the server and the image never decodes, though the message still lists it
        var (ext, media) = Describe(bytes);
        Directory.CreateDirectory(blobDir);
        var path = Path.Combine(blobDir, sha + ext);
        //the path comes from the content, so pasting the same screenshot twice writes one file
        if (!File.Exists(path)) File.WriteAllBytes(path, bytes);
        return new ImageRef(path, bytes.LongLength, sha, media);
    }
}
