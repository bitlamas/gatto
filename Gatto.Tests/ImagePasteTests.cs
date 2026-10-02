//these tests cover the paste acquisition path and its refusals, no bitmap encoder exists since the sources seen so far hand over registered PNG
using Gatto.Core.Client;
using Gatto.Repl.Input;
using Gatto.Terminal;

namespace Gatto.Tests;

public class ImagePasteTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gatto-paste-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private string BlobDir => Path.Combine(_dir, "blobs");
    private static readonly IReadOnlySet<string> NoShas = new HashSet<string>();
    private static readonly AttachLimits Limits = AttachLimits.Default;

    private static byte[] Png(byte seed) =>
        new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, seed };

    private sealed class FakeClipboard : IClipboardImageNative
    {
        public ClipboardImageKind Kind = ClipboardImageKind.None;
        public byte[]? Bytes;
        public IReadOnlyList<string> Files = Array.Empty<string>();
        public int OpensPerformed;

        public ClipboardImageKind Available() => Kind;
        public byte[]? ReadPng() { OpensPerformed++; return Bytes; }
        public IReadOnlyList<string> ReadFileDrop() { OpensPerformed++; return Files; }
    }

    //a paste names the blob by its content and writes nothing, so a paste that is never sent leaves no file behind
    [Fact]
    public void A_pasted_png_is_named_by_its_content_and_written_only_when_saved()
    {
        var cb = new FakeClipboard { Kind = ClipboardImageKind.Png, Bytes = Png(0x01) };
        var r = ImagePaste.Paste(cb, BlobDir);

        var img = Assert.Single(r.Candidates);
        Assert.Equal("image/png", img.MediaType);
        Assert.StartsWith(img.Sha256, Path.GetFileNameWithoutExtension(img.Path), StringComparison.Ordinal);
        Assert.Null(r.Notice);   //acquisition stays silent, the guard pass at submit does the announcing
        Assert.False(Directory.Exists(BlobDir));

        ImagePaste.Save(img.Path, r.Unsaved![img.Path]);
        Assert.True(File.Exists(img.Path));
    }

    [Fact]
    public void Saving_the_same_image_twice_writes_ONE_blob()
    {
        //the blob path derives from the sha of the bytes, so the same image always gets the same path
        var cb = new FakeClipboard { Kind = ClipboardImageKind.Png, Bytes = Png(0x02) };
        var first = ImagePaste.Paste(cb, BlobDir);
        var second = ImagePaste.Paste(cb, BlobDir);
        Assert.Equal(first.Candidates[0].Path, second.Candidates[0].Path);

        ImagePaste.Save(first.Candidates[0].Path, first.Unsaved![first.Candidates[0].Path]);
        ImagePaste.Save(second.Candidates[0].Path, second.Unsaved![second.Candidates[0].Path]);
        Assert.Single(Directory.GetFiles(BlobDir));
    }

    //a save that fails keeps the bytes, so the message handed back can be sent again with its picture
    [Fact]
    public void A_failed_save_keeps_the_bytes_and_the_next_send_writes_them()
    {
        var cb = new FakeClipboard { Kind = ClipboardImageKind.Png, Bytes = Png(0x03) };
        var r = ImagePaste.Paste(cb, BlobDir);
        var unsaved = new Dictionary<string, byte[]>(r.Unsaved!);
        File.WriteAllText(BlobDir, "a file where the folder should be");

        Assert.ThrowsAny<IOException>(() => ImagePaste.SaveSent(r.Candidates, unsaved));
        Assert.Single(unsaved);

        File.Delete(BlobDir);
        ImagePaste.SaveSent(r.Candidates, unsaved);
        Assert.Empty(unsaved);
        Assert.True(File.Exists(r.Candidates[0].Path));
    }

    [Fact]
    public void A_pasted_JPEG_is_labelled_as_a_JPEG_even_though_the_clipboard_format_is_called_PNG()
    {
        //the clipboard format name is not the media type, the bytes decide (a mislabelled data URI leaves the model without pixels)
        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46 };
        var cb = new FakeClipboard { Kind = ClipboardImageKind.Png, Bytes = jpeg };

        var img = Assert.Single(ImagePaste.Paste(cb, BlobDir).Candidates);

        Assert.Equal("image/jpeg", img.MediaType);
        Assert.EndsWith(".jpg", img.Path, StringComparison.Ordinal);
    }

    [Fact]
    public void Clipboard_data_that_is_not_an_image_at_all_is_refused_not_written()
    {
        var cb = new FakeClipboard
        {
            Kind = ClipboardImageKind.Png,
            Bytes = System.Text.Encoding.UTF8.GetBytes("this is not an image"),
        };

        var r = ImagePaste.Paste(cb, BlobDir);

        Assert.Empty(r.Candidates);
        Assert.NotNull(r.Notice);
        Assert.False(Directory.Exists(BlobDir), "a blob we cannot name must never be written");
    }

    [Fact]
    public void Paste_does_NOT_dedupe_or_cap_by_itself()
    {
        //acquisition stays dumb on purpose, dedupe and caps live in one guard at submit and a paste-time answer would go stale
        var cb = new FakeClipboard { Kind = ClipboardImageKind.Png, Bytes = Png(0x03) };
        Assert.Single(ImagePaste.Paste(cb, BlobDir).Candidates);
        Assert.Single(ImagePaste.Paste(cb, BlobDir).Candidates);
    }

    [Fact]
    public void A_copied_FILE_goes_down_the_path_road_with_no_blob()
    {
        var file = Path.Combine(_dir, "shot.png");
        File.WriteAllBytes(file, Png(0x04));
        var cb = new FakeClipboard { Kind = ClipboardImageKind.FileDrop, Files = new[] { file } };

        var r = ImagePaste.Paste(cb, BlobDir);

        Assert.Equal(file, Assert.Single(r.Candidates).Path);   //a dropped file keeps its own path, so no blob copy is written
        Assert.False(Directory.Exists(BlobDir), "a file that already has a path needs no blob");
    }

    [Fact]
    public void A_copied_NON_image_file_is_not_attached()
    {
        var file = Path.Combine(_dir, "notes.txt");
        File.WriteAllText(file, "hello");
        var cb = new FakeClipboard { Kind = ClipboardImageKind.FileDrop, Files = new[] { file } };

        var r = ImagePaste.Paste(cb, BlobDir);
        Assert.Empty(r.Candidates);
        Assert.NotNull(r.Notice);
    }

    [Fact]
    public void A_bitmap_with_no_PNG_is_refused_with_an_actionable_message()
    {
        //no bitmap encoder exists since no measured source needs one, so the refusal says what to do instead
        var cb = new FakeClipboard { Kind = ClipboardImageKind.DibOnly };
        var r = ImagePaste.Paste(cb, BlobDir);

        Assert.Empty(r.Candidates);
        Assert.Contains("bitmap", r.Notice!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("copying the image file", r.Notice!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_empty_clipboard_says_so_and_points_at_the_text_paste_key()
    {
        //the Alt+V binding is image-only, so an empty clipboard says so and points at Ctrl+V
        var r = ImagePaste.Paste(new FakeClipboard(), BlobDir);

        Assert.Empty(r.Candidates);
        Assert.Contains("nothing to paste", r.Notice!, StringComparison.Ordinal);
        Assert.Contains("Ctrl+V", r.Notice!, StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_opens_the_clipboard_when_there_is_nothing_to_read()
    {
        //the availability probe must not open the clipboard, opening it locks other apps out and makes polling unsafe
        var cb = new FakeClipboard();
        ImagePaste.Paste(cb, BlobDir);
        Assert.Equal(0, cb.OpensPerformed);
    }
}
