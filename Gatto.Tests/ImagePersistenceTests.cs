//images persist by reference, and a moved or changed file shows a stub instead of different pixels
using System.Text.Json;
using Gatto.Core.Client;
using Gatto.Core.Session;

namespace Gatto.Tests;

public class ImagePersistenceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gatto-imgpersist-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private static byte[] Png(byte seed) =>
        new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, seed };

    private string Write(string name, byte[] bytes)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllBytes(p, bytes);
        return p;
    }

    private static ChatMessage RoundTrip(ChatMessage m) =>
        SessionStore.ParseChatElement(JsonDocument.Parse(SessionStore.ChatJson(m)).RootElement);

    [Fact]
    public void A_message_without_images_writes_no_images_field()
    {
        //a message with no images omits the field, so the json stays byte-identical to older files
        var line = SessionStore.ChatJson(new ChatMessage("user", "hi"));
        Assert.DoesNotContain("images", line, StringComparison.Ordinal);
        Assert.Equal("""{"role":"user","content":"hi"}""", line);
    }

    [Fact]
    public void Images_round_trip_by_reference_not_by_bytes()
    {
        var img = ImageRef.FromFile(Write("a.png", Png(0x40)));
        var line = SessionStore.ChatJson(new ChatMessage("user", "look", Images: new[] { img }));

        Assert.DoesNotContain("base64", line, StringComparison.Ordinal);   //the record holds a reference, so the bytes never go in the line
        Assert.True(line.Length < 600, $"a reference must stay small; was {line.Length}");

        var back = RoundTrip(new ChatMessage("user", "look", Images: new[] { img }));
        var r = Assert.Single(back.Images!);
        Assert.Equal(img.Sha256, r.Sha256);
        Assert.Equal(img.Path, r.Path);
        Assert.Equal(img.Bytes, r.Bytes);
        Assert.Equal(img.MediaType, r.MediaType);
    }

    [Fact]
    public void Two_images_round_trip_in_order()
    {
        var a = ImageRef.FromFile(Write("a.png", Png(0x01)));
        var b = ImageRef.FromFile(Write("b.png", Png(0x02)));
        var back = RoundTrip(new ChatMessage("user", "compare", Images: new[] { a, b }));

        Assert.Equal(2, back.Images!.Count);
        Assert.Equal(a.Sha256, back.Images[0].Sha256);
        Assert.Equal(b.Sha256, back.Images[1].Sha256);
    }

    [Fact]
    public void A_missing_file_on_restore_becomes_a_stub_and_says_so()
    {
        var path = Write("gone.png", Png(0x50));
        var m = new ChatMessage("user", "look", Images: new[] { ImageRef.FromFile(path) });
        File.Delete(path);

        var warnings = new List<string>();
        var json = RequestJson.SerialiseMessage(m, new ImageCache(), warnings.Add);

        Assert.DoesNotContain("image_url", json, StringComparison.Ordinal);   //an unreadable image sends a stub instead of an image_url part
        Assert.Contains("gone.png", json, StringComparison.Ordinal);
        Assert.Single(warnings);
    }

    [Fact]
    public void The_unavailable_notice_is_said_ONCE_per_session_not_once_per_request()
    {
        //the failure path latches into the cache like the success path, a warning on every request buries the turn it belongs to
        var path = Write("gone.png", Png(0x70));
        var m = new ChatMessage("user", "look", Images: new[] { ImageRef.FromFile(path) });
        File.Delete(path);

        var warnings = new List<string>();
        var cache = new ImageCache();                     //one cache is one session, so the latch lasts the session
        for (var i = 0; i < 5; i++)
            RequestJson.SerialiseMessage(m, cache, warnings.Add);

        Assert.Single(warnings);
    }

    [Fact]
    public void Every_request_still_carries_the_stub_even_after_the_notice_is_latched()
    {
        //the latch silences the notice only, every request still has to include the unavailable stub or the model describes an image it cannot see
        var path = Write("gone2.png", Png(0x71));
        var m = new ChatMessage("user", "look", Images: new[] { ImageRef.FromFile(path) });
        File.Delete(path);

        var cache = new ImageCache();
        var warnings = new List<string>();
        var first = RequestJson.SerialiseMessage(m, cache, warnings.Add);
        var second = RequestJson.SerialiseMessage(m, cache, warnings.Add);

        Assert.Single(warnings);
        //the stub merges into the labelled slot and tells the model not to describe it, dropping it would leave the count wrong
        Assert.Contains("unavailable, not shown", first, StringComparison.Ordinal);
        Assert.Contains("do not describe it", first, StringComparison.Ordinal);
        Assert.Contains("confuse it with any other image", first, StringComparison.Ordinal);
        Assert.Equal(first, second);
    }

    [Fact]
    public void A_file_whose_bytes_changed_since_it_was_attached_also_stubs()
    {
        //after a restore there is no cache, so the hash is the only evidence the file still matches what the model saw
        var path = Write("changed.png", Png(0x60));
        var m = new ChatMessage("user", "look", Images: new[] { ImageRef.FromFile(path) });
        File.WriteAllBytes(path, Png(0x61));

        var warnings = new List<string>();
        var json = RequestJson.SerialiseMessage(m, new ImageCache(), warnings.Add);

        Assert.DoesNotContain("image_url", json, StringComparison.Ordinal);
        Assert.Contains("changed", warnings[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_tool_image_record_round_trips_its_images_and_by()
    {
        var img = ImageRef.FromFile(Write("page.png", Png(0x30)));
        var line = SessionStore.ChatJson(new Conversation(null).ImageRecord("images read this round: page.png (9 bytes)", [img], "read_file"));
        Assert.Contains("\"by\":\"read_file\"", line, StringComparison.Ordinal);

        var back = SessionStore.ParseChatElement(JsonDocument.Parse(line).RootElement);
        Assert.Equal(("user", "read_file"), (back.Role, back.By));
        Assert.Equal(img.Sha256, Assert.Single(back.Images!).Sha256);
    }

    [Fact]
    public void A_typed_record_writes_no_by_field() =>
        Assert.DoesNotContain("\"by\"", SessionStore.ChatJson(new ChatMessage("user", "look")), StringComparison.Ordinal);

    [Fact]
    public void A_tool_image_record_writes_its_line_first_then_the_picture()
    {
        var img = ImageRef.FromFile(Write("p.png", Png(0x31)));
        var json = RequestJson.SerialiseMessage(new ChatMessage("user", "images read this round: p.png (9 bytes)", Images: [img], By: "read_file"));

        using var doc = JsonDocument.Parse(json);
        var parts = doc.RootElement.GetProperty("content");
        Assert.Equal("images read this round: p.png (9 bytes)", parts[0].GetProperty("text").GetString());
        Assert.Equal("image_url", parts[2].GetProperty("type").GetString());
    }

    [Fact]
    public void A_tool_read_file_that_changed_stubs_with_read_file_reads_it_again()
    {
        var path = Write("chart.png", Png(0x62));
        var m = new ChatMessage("user", "images read this round: chart.png (9 bytes)", Images: [ImageRef.FromFile(path)], By: "read_file");
        File.WriteAllBytes(path, Png(0x63));

        var json = RequestJson.SerialiseMessage(m, new ImageCache());

        Assert.Contains("read_file reads it again", json, StringComparison.Ordinal);
        Assert.DoesNotContain("attach it again", json, StringComparison.Ordinal);
    }
}
