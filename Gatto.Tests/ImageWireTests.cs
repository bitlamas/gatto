//these two guards hold the request prefix stable, and each one can fail against a broken implementation
using System.Text.Json;
using Gatto.Core.Client;

namespace Gatto.Tests;

public class ImageWireTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gatto-imgwire-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    //enough of a PNG for sniffing to name it and for the seed to change the hash, no code path decodes it
    private static byte[] Png(byte seed) =>
        new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, seed };

    private string Write(string name, byte[] bytes)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllBytes(p, bytes);
        return p;
    }

    [Fact]
    public void An_image_free_message_serialises_byte_identically_to_today()
    {
        //an image-free message must serialise byte-identically, and the expected literal is a real capture rather than an invented one
        var json = RequestJson.SerialiseMessage(new ChatMessage("user", "read a.txt please"));

        Assert.Equal("""{"role":"user","content":"read a.txt please"}""", json);
        Assert.DoesNotContain("image_url", json, StringComparison.Ordinal);
    }

    [Fact]
    public void A_null_content_assistant_with_tool_calls_is_still_emitted_as_it_was()
    {
        //this shape comes from the same real capture, pinned for the same reason
        var m = new ChatMessage("assistant", null,
            ToolCalls: new[] { new ToolCall("call_1", "read_file", """{"path":"a.txt"}""") });
        var json = RequestJson.SerialiseMessage(m);

        Assert.DoesNotContain("\"content\"", json, StringComparison.Ordinal);   //the content field is omitted when it is null
        Assert.Contains("""{"role":"assistant","tool_calls":[""", json, StringComparison.Ordinal);
    }

    [Fact]
    public void An_image_bearing_message_reserialises_byte_stably_when_the_file_changes_on_disk()
    {
        //the cache keyed by the sha keeps the serialisation stable, or editing a file on disk rewrites the prefix and forces a full re-prefill
        var path = Write("shot.png", Png(0x01));
        var cache = new ImageCache();
        var m = new ChatMessage("user", "look", Images: new[] { ImageRef.FromFile(path) });

        var first = RequestJson.SerialiseMessage(m, cache);
        File.WriteAllBytes(path, Png(0xFE));
        var second = RequestJson.SerialiseMessage(m, cache);

        Assert.Equal(first, second);
        Assert.Contains("image_url", first, StringComparison.Ordinal);
    }

    //a label must sit directly before its image part, or the model binds a name to the wrong image and nothing shows it

    [Fact]
    public void Every_image_part_is_preceded_by_its_own_label()
    {
        var a = Write("a.png", Png(0x10));
        var b = Write("b.png", Png(0x20));
        var m = new ChatMessage("user", "compare these",
            Images: new[] { ImageRef.FromFile(a), ImageRef.FromFile(b) });

        using var doc = JsonDocument.Parse(RequestJson.SerialiseMessage(m, new ImageCache()));
        var parts = doc.RootElement.GetProperty("content").EnumerateArray().ToList();

        //the parts are user text, then one label and one image per file.
        Assert.Equal(5, parts.Count);
        Assert.Equal("compare these", parts[0].GetProperty("text").GetString());
        Assert.Equal("image 1 of 2: a.png", parts[1].GetProperty("text").GetString());
        Assert.Equal("image_url", parts[2].GetProperty("type").GetString());
        Assert.Equal("image 2 of 2: b.png", parts[3].GetProperty("text").GetString());
        Assert.Equal("image_url", parts[4].GetProperty("type").GetString());
    }

    [Fact]
    public void A_single_image_is_labelled_too()
    {
        //labelling is uniform and never depends on the count, a single-image binding failure is unobserved rather than absent
        var m = new ChatMessage("user", "look", Images: new[] { ImageRef.FromFile(Write("solo.png", Png(0x11))) });
        using var doc = JsonDocument.Parse(RequestJson.SerialiseMessage(m, new ImageCache()));
        var parts = doc.RootElement.GetProperty("content").EnumerateArray().ToList();

        Assert.Equal("image 1 of 1: solo.png", parts[1].GetProperty("text").GetString());
    }

    [Fact]
    public void The_label_repeats_the_token_the_user_typed()
    {
        //the label repeats the user's own token, a second name would only add confusion
        var blob = new ImageRef(@"C:/blobs/deadbeef.png", 9, "deadbeef", "image/png",
                                DisplayName: "[Image #4]");
        var m = new ChatMessage("user", "what is [Image #4]", Images: new[] { blob });

        var json = RequestJson.SerialiseMessage(m, new ImageCache());
        Assert.Contains("image 1 of 1: [Image #4]", json, StringComparison.Ordinal);
        Assert.DoesNotContain("deadbeef", json.Replace("data:image", ""), StringComparison.Ordinal);
    }

    [Fact]
    public void An_unavailable_image_keeps_its_labelled_SLOT()
    {
        //an unavailable image keeps its labelled slot, or the manifest count and the model's count diverge
        var present = Write("here.png", Png(0x30));
        var gone = Write("gone.png", Png(0x31));
        var m = new ChatMessage("user", "both",
            Images: new[] { ImageRef.FromFile(present), ImageRef.FromFile(gone) });
        File.Delete(gone);

        using var doc = JsonDocument.Parse(RequestJson.SerialiseMessage(m, new ImageCache()));
        var parts = doc.RootElement.GetProperty("content").EnumerateArray().ToList();

        Assert.Equal(4, parts.Count);                                   //the parts are text, one label and image, and the stub's label.
        Assert.Equal("image 1 of 2: here.png", parts[1].GetProperty("text").GetString());
        Assert.Equal("image_url", parts[2].GetProperty("type").GetString());
        var stub = parts[3].GetProperty("text").GetString()!;
        Assert.StartsWith("image 2 of 2: gone.png", stub, StringComparison.Ordinal);
        Assert.Contains("unavailable", stub, StringComparison.Ordinal);
    }

    [Fact]
    public void An_image_bearing_message_becomes_a_parts_array()
    {
        var path = Write("a.png", Png(0x10));
        var m = new ChatMessage("user", "describe this", Images: new[] { ImageRef.FromFile(path) });

        using var doc = JsonDocument.Parse(RequestJson.SerialiseMessage(m, new ImageCache()));
        var content = doc.RootElement.GetProperty("content");

        Assert.Equal(JsonValueKind.Array, content.ValueKind);
        Assert.Equal(3, content.GetArrayLength());   //one message holds three parts: text, label, image.
        Assert.Equal("text", content[0].GetProperty("type").GetString());
        Assert.Equal("describe this", content[0].GetProperty("text").GetString());
        Assert.Equal("image 1 of 1: a.png", content[1].GetProperty("text").GetString());
        Assert.Equal("image_url", content[2].GetProperty("type").GetString());
        Assert.StartsWith("data:image/png;base64,",
            content[2].GetProperty("image_url").GetProperty("url").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Two_images_become_two_parts_in_order()
    {
        var a = Write("a.png", Png(0x10));
        var b = Write("b.png", Png(0x20));
        var m = new ChatMessage("user", "compare",
            Images: new[] { ImageRef.FromFile(a), ImageRef.FromFile(b) });

        using var doc = JsonDocument.Parse(RequestJson.SerialiseMessage(m, new ImageCache()));
        var content = doc.RootElement.GetProperty("content");
        Assert.Equal(5, content.GetArrayLength());   //five parts: text, then one label and one image per file.
        Assert.NotEqual(content[2].GetProperty("image_url").GetProperty("url").GetString(),
                        content[4].GetProperty("image_url").GetProperty("url").GetString());
    }

    [Fact]
    public void ImageRef_carries_the_sha_and_size_of_the_bytes_on_disk()
    {
        var bytes = Png(0x33);
        var r = ImageRef.FromFile(Write("x.png", bytes));

        Assert.Equal(bytes.Length, r.Bytes);
        Assert.Equal(
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant(),
            r.Sha256);
        Assert.Equal("image/png", r.MediaType);
    }

    [Fact]
    public void A_non_image_cannot_become_an_ImageRef()
    {
        //the BinarySniff check alone decides image content, so an extension alone never produces an ImageRef
        var p = Write("liar.png", System.Text.Encoding.UTF8.GetBytes("this is not a png"));
        Assert.Throws<InvalidOperationException>(() => ImageRef.FromFile(p));
    }
}
