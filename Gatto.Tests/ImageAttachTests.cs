//auto-detect attaches without being asked, so every attach is named, big batches are confirmed, content decides an image and sha decides a duplicate
using Gatto.Core.Client;
using Gatto.Repl.Input;

namespace Gatto.Tests;

public class ImageAttachTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gatto-attach-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private static readonly IReadOnlySet<string> NoShas = new HashSet<string>();
    private static readonly AttachLimits Limits = AttachLimits.Default;

    private static byte[] Png(byte seed) =>
        new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, seed };
    private static byte[] Jpeg() => new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46 };

    private void Write(string name, byte[] bytes) => File.WriteAllBytes(Path.Combine(_dir, name), bytes);

    private ImageAttach.Result Detect(string text, IReadOnlySet<string>? seen = null, AttachLimits? lim = null) =>
        ImageAttach.Detect(text, _dir, seen ?? NoShas, lim ?? Limits);

    [Fact]
    public void A_bare_filename_in_a_sentence_attaches()
    {
        //a real user message, kept verbatim as the fixture
        Write("image.jpg", Jpeg());
        var r = Detect("There's a file named image.jpg in the project folder. Describe it if you can.");
        Assert.Single(r.Attached);
        Assert.Equal("image.jpg", Path.GetFileName(r.Attached[0].Path));
    }

    [Fact]
    public void Trailing_punctuation_does_not_defeat_detection()
    {
        //the raw token keeps the punctuation and resolves to nothing, so the retry without it is what works
        Write("foo.png", Png(0x01));
        Assert.Single(Detect("should I delete foo.png?").Attached);
        Assert.Single(Detect("look at foo.png, then tell me").Attached);
        Assert.Single(Detect("(foo.png)").Attached);
        Assert.Single(Detect("see foo.png.").Attached);
    }

    [Fact]
    public void Markdown_formatting_around_a_filename_does_not_defeat_detection()
    {
        //a token ending in a backtick fails the name prefilter, so strip the markdown edges or the file is missed silently
        Write("cow.png", Png(0x02));
        Assert.Single(Detect("mix `cow.png` with the other one").Attached);
        Assert.Single(Detect("what about **cow.png**?").Attached);
        Assert.Single(Detect("the file *cow.png* is a cow").Attached);
    }

    [Fact]
    public void A_filename_that_really_starts_with_a_backtick_still_resolves()
    {
        //an exact match is tried before any trimming, a backtick can start a real filename and trimming only touches the edges
        Write("`weird.png", Png(0x03));
        Assert.Single(Detect("look at `weird.png please").Attached);
    }

    [Fact]
    public void A_file_that_is_not_really_an_image_is_not_attached()
    {
        //the bytes decide whether a file is an image, an extension proves nothing
        Write("notes.txt", System.Text.Encoding.UTF8.GetBytes("hello"));
        Write("liar.png", System.Text.Encoding.UTF8.GetBytes("this is not a png"));
        var r = Detect("read notes.txt and liar.png");
        Assert.Empty(r.Attached);
    }

    [Fact]
    public void A_path_that_does_not_exist_is_left_as_plain_text()
    {
        var r = Detect("what about missing.png");
        Assert.Empty(r.Attached);
        Assert.Empty(r.Notices);   //a name that resolves to no file is plain text, so there is nothing to report
    }

    [Fact]
    public void Every_attach_produces_a_notice_naming_the_file_and_its_cost()
    {
        //an attach is never silent, the notice says which file went and what it costs
        Write("image.jpg", Jpeg());
        var r = Detect("describe image.jpg");
        var notice = Assert.Single(r.Notices);
        Assert.Equal(ImageAttach.NoticeKind.Attached, notice.Kind);
        Assert.Contains("image.jpg", notice.Text, StringComparison.Ordinal);
        Assert.Contains("tok", notice.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_same_image_mentioned_again_does_not_re_attach()
    {
        //a name recurs across a conversation, so without dedupe each mention attaches another permanent copy
        Write("image.jpg", Jpeg());
        var first = Detect("what about image.jpg");
        var sha = first.Attached[0].Sha256;

        var second = Detect("ok now crop image.jpg", new HashSet<string> { sha });

        Assert.Empty(second.Attached);
        Assert.Contains(second.Notices, n => n.Text.Contains("already attached", StringComparison.Ordinal));
    }

    [Fact]
    public void A_changed_file_re_attaches_because_dedupe_is_keyed_on_bytes()
    {
        //dedupe keys on the sha of the bytes, so a file whose bytes changed is worth another look
        Write("shot.png", Png(0x01));
        var sha1 = Detect("see shot.png").Attached[0].Sha256;
        Write("shot.png", Png(0x02));
        Assert.Single(Detect("see shot.png again", new HashSet<string> { sha1 }).Attached);
    }

    [Fact]
    public void Collapsing_a_duplicate_within_one_message_is_ANNOUNCED()
    {
        //a silent collapse leaves two tokens in the message and one image in the model's context, so it must be announced
        var img = Ref("dup.png", 0x77);
        var r = ImageAttach.ApplyGuards(new[] { img, img }, NoShas, Limits);

        Assert.Single(r.Attached);
        Assert.Contains(r.Notices, n => n.Text.Contains("twice in one message", StringComparison.Ordinal));
    }

    [Fact]
    public void The_same_file_named_twice_in_ONE_message_attaches_once()
    {
        //the seen set only holds what was attached before, so it cannot stop a repeat inside one message
        Write("a.png", Png(0x05));
        Assert.Single(Detect("compare a.png with a.png").Attached);
    }

    //the count threshold asks the user and the byte backstop never yields, small images stay under the byte limit and still add prefix tokens

    private string[] WriteMany(int n)
    {
        var names = new string[n];
        for (var i = 0; i < n; i++) { names[i] = $"i{i}.png"; Write(names[i], Png((byte)i)); }
        return names;
    }

    [Fact]
    public void At_the_threshold_nothing_is_asked()
    {
        var r = Detect(string.Join(' ', WriteMany(10)));
        Assert.Equal(10, r.Attached.Count);
        Assert.False(r.NeedsConfirmation);
    }

    [Fact]
    public void Above_the_threshold_the_batch_needs_confirmation()
    {
        var r = Detect(string.Join(' ', WriteMany(11)));
        //above the threshold the batch only waits, so nothing is dropped or sent before the user answers.
        Assert.Equal(11, r.Attached.Count);
        Assert.True(r.NeedsConfirmation);
    }

    [Fact]
    public void The_ordinary_message_is_never_asked_about()
    {
        //the ordinary message is pinned from below, so a change that asks about every single image fails here
        Write("solo.png", Png(0x40));
        Assert.False(Detect("look at solo.png").NeedsConfirmation);
        Assert.False(Detect("no images here at all").NeedsConfirmation);
    }

    [Fact]
    public void The_count_is_taken_AFTER_dedupe()
    {
        //dedupe first, then count, then ask, so the number offered matches what will be sent
        var names = WriteMany(9);
        var text = string.Join(' ', names) + " " + names[0] + " " + names[1] + " " + names[2];
        var r = Detect(text);
        Assert.Equal(9, r.Attached.Count);
        Assert.False(r.NeedsConfirmation);
    }

    [Fact]
    public void The_byte_backstop_refuses_outright_and_never_asks()
    {
        //the byte backstop refuses without asking, asking permission for a refusal would be a lie
        Write("big.png", Png(0x01).Concat(new byte[5000]).ToArray());
        var r = Detect("look at big.png", lim: new AttachLimits(PromptAboveImages: 10, MaxTotalBytes: 1000));
        Assert.Empty(r.Attached);
        Assert.False(r.NeedsConfirmation);
        Assert.Contains(r.Notices, n => n.Text.Contains("too large", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_filename_containing_SPACES_is_found()
    {
        Write("play the part.jpeg", Jpeg());
        var r = Detect("Can you tell me what is wrong with the image play the part.jpeg?");
        Assert.Equal("play the part.jpeg", Path.GetFileName(Assert.Single(r.Attached).Path));
    }

    [Fact]
    public void The_wrong_image_extension_finds_the_right_file_and_says_it_substituted()
    {
        //the notice names the typed name and the resolved file, so the substitution is visible
        Write("play the part.jpeg", Jpeg());
        var r = Detect("what is wrong with the image play the part.jpg?");

        Assert.Equal("play the part.jpeg", Path.GetFileName(Assert.Single(r.Attached).Path));
        Assert.Contains(r.Notices, n => n.Text.Contains("play the part.jpg", StringComparison.Ordinal)
                                     && n.Text.Contains("play the part.jpeg", StringComparison.Ordinal));
    }

    [Fact]
    public void A_substitution_the_guards_then_skip_does_not_claim_it_attached()
    {
        //the scan's notice says only which file it resolved to, the guards report what became of it, or the two lines contradict each other
        Write("logo.jpeg", Jpeg());
        var already = new HashSet<string> { ImageRef.FromFile(Path.Combine(_dir, "logo.jpeg")).Sha256 };

        var r = Detect("and logo.jpg?", already);

        Assert.Empty(r.Attached);
        Assert.Contains(r.Notices, n => n.Text.Contains("using logo.jpeg", StringComparison.Ordinal));
        Assert.Contains(r.Notices, n => n.Text.Contains("already attached", StringComparison.Ordinal));
        Assert.DoesNotContain(r.Notices, n => n.Text.Contains("attached logo.jpeg instead", StringComparison.Ordinal));
    }

    [Fact]
    public void An_AMBIGUOUS_near_miss_attaches_nothing_and_lists_the_candidates()
    {
        //when two files share the stem, gatto attaches nothing and lists both instead of guessing
        Write("shot.jpeg", Jpeg());
        Write("shot.png", Png(0x01));
        var r = Detect("look at shot.jpg");

        Assert.Empty(r.Attached);
        Assert.Contains(r.Notices, n => n.Text.Contains("shot.jpeg", StringComparison.Ordinal)
                                     && n.Text.Contains("shot.png", StringComparison.Ordinal));
    }

    [Fact]
    public void A_genuinely_missing_image_is_still_silent()
    {
        //with no file and no same-stem sibling, the word was never a file mention, and guessing would search every name.
        var r = Detect("what about nothing-here.jpg");
        Assert.Empty(r.Attached);
        Assert.Empty(r.Notices);
    }

    [Fact]
    public void A_spaced_name_does_not_swallow_the_words_around_it()
    {
        //only token runs ending in an image extension are tried and the shortest match wins, so the sentence is not swallowed
        Write("part.jpg", Png(0x02));
        var r = Detect("tell me about the image part.jpg please");
        Assert.Equal("part.jpg", Path.GetFileName(Assert.Single(r.Attached).Path));
    }

    [Fact]
    public void A_pasted_image_is_announced_by_its_LABEL_not_its_content_hash()
    {
        //a blob path is a content hash, so a pasted image is announced by its display label
        var blob = new ImageRef(@"C:lobs\995f71cf.png", 100, "995f71cf", "image/png",
                                DisplayName: "pasted image 1");
        var r = ImageAttach.ApplyGuards(new[] { blob }, NoShas, Limits);

        var notice = Assert.Single(r.Notices);
        Assert.Contains("pasted image 1", notice.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("995f71cf", notice.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_the_user_NAMED_is_still_announced_by_its_filename()
    {
        //the label is for nameless images only, a typed file already has the name the user gave it
        Write("image.jpg", Jpeg());
        var r = Detect("describe image.jpg");
        Assert.Contains("image.jpg", Assert.Single(r.Notices).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Several_images_produce_exactly_ONE_notice()
    {
        Write("a.png", Png(0x01)); Write("b.png", Png(0x02)); Write("c.png", Png(0x03));
        var r = Detect("look at a.png and b.png and c.png");

        Assert.Equal(3, r.Attached.Count);
        var notice = Assert.Single(r.Notices);
        Assert.Contains("3 images", notice.Text, StringComparison.Ordinal);
        Assert.Contains("a.png", notice.Text, StringComparison.Ordinal);   //one notice, but it still names the files that were attached
        Assert.Contains("c.png", notice.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_cost_reads_as_an_estimate_never_as_a_measurement()
    {
        Write("a.png", Png(0x01)); Write("b.png", Png(0x02));
        var notice = Assert.Single(Detect("a.png b.png").Notices);

        Assert.Contains("est.", notice.Text, StringComparison.Ordinal);
        Assert.Contains("~", notice.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Many_names_collapse_to_a_count_rather_than_wrapping()
    {
        //past a readable width the names give way to a count, so the line stays on one row
        for (var i = 0; i < 4; i++) Write($"a-rather-long-image-name-{i}.png", Png((byte)i));
        var notice = Assert.Single(
            Detect("a-rather-long-image-name-0.png a-rather-long-image-name-1.png "
                 + "a-rather-long-image-name-2.png a-rather-long-image-name-3.png").Notices);

        Assert.Contains("4 images", notice.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("a-rather-long-image-name-3.png", notice.Text, StringComparison.Ordinal);
        Assert.True(notice.Text.Length < 60, $"the line must stay short; was {notice.Text.Length}");
    }

    private static ImageRef Blob(int n) =>
        new($@"C:lobs\{n}.png", 100, $"sha{n}", "image/png", DisplayName: $"pasted image {n}");

    [Fact]
    public void Only_images_whose_placeholder_SURVIVED_are_attached()
    {
        var pasted = new Dictionary<int, ImageRef> { [1] = Blob(1), [2] = Blob(2) };

        //dropping a placeholder drops the image with it and leaves the rest attached
        var kept = ImageAttach.ReferencedIn("look at [Image #1] and tell me", pasted);

        Assert.Equal("pasted image 1", Assert.Single(kept).DisplayName);
    }

    [Fact]
    public void Deleting_every_placeholder_detaches_everything()
    {
        var pasted = new Dictionary<int, ImageRef> { [1] = Blob(1) };
        Assert.Empty(ImageAttach.ReferencedIn("never mind, forget the picture", pasted));
    }

    [Fact]
    public void Placeholders_attach_in_the_order_they_appear_in_the_sentence()
    {
        //attachments follow the sentence's order, so the model maps each image to its clause
        var pasted = new Dictionary<int, ImageRef> { [1] = Blob(1), [2] = Blob(2) };
        var order = ImageAttach.ReferencedIn("this logo [Image #2] beside this shot [Image #1]", pasted);

        Assert.Equal(new[] { "pasted image 2", "pasted image 1" }, order.Select(i => i.DisplayName));
    }

    [Fact]
    public void A_placeholder_with_no_attachment_is_just_text()
    {
        //a placeholder with no attachment is a reference to nothing
        Assert.Empty(ImageAttach.ReferencedIn("see [Image #9]", new Dictionary<int, ImageRef> { [1] = Blob(1) }));
    }

    [Fact]
    public void The_same_placeholder_twice_attaches_once()
    {
        var pasted = new Dictionary<int, ImageRef> { [1] = Blob(1) };
        Assert.Single(ImageAttach.ReferencedIn("[Image #1] and again [Image #1]", pasted));
    }

    //both candidate routes pass one guard pass, so the tests assert on the guard rather than on a feeder

    private ImageRef Ref(string name, byte seed)
    {
        Write(name, Png(seed));
        return ImageRef.FromFile(Path.Combine(_dir, name));
    }

    [Fact]
    public void The_same_bytes_pasted_twice_before_sending_attach_ONCE()
    {
        //the same screenshot pasted twice attaches one part, so the token cost is paid once
        var img = Ref("shot.png", 0x01);
        var r = ImageAttach.ApplyGuards(new[] { img, img }, NoShas, Limits);
        Assert.Single(r.Attached);
    }

    [Fact]
    public void A_pasted_image_and_the_same_file_NAMED_attach_once()
    {
        //a pasted candidate and a typed one naming the same file dedupe together in one message
        var img = Ref("shot.png", 0x02);
        var candidates = new[] { img }.Concat(ImageAttach.Candidates("look at shot.png", _dir).Found).ToList();
        Assert.Single(ImageAttach.ApplyGuards(candidates, NoShas, Limits).Attached);
    }

    [Fact]
    public void The_byte_cap_applies_to_pasted_candidates_too()
    {
        //the byte cap applies to pasted candidates as well as to typed ones, the count cap alone lets a 60 MB paste through
        Write("big.png", Png(0x03).Concat(new byte[5000]).ToArray());
        var big = ImageRef.FromFile(Path.Combine(_dir, "big.png"));
        var r = ImageAttach.ApplyGuards(new[] { big }, NoShas, new AttachLimits(PromptAboveImages: 10, MaxTotalBytes: 1000));
        Assert.Empty(r.Attached);
        Assert.Contains(r.Notices, n => n.Text.Contains("too large", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void One_duplicate_in_a_batch_skips_only_itself_and_the_rest_still_attach()
    {
        //a duplicate skips only itself, the rest of the batch still attaches
        var seen = Ref("seen.png", 0x04);
        var fresh1 = Ref("a.png", 0x05);
        var fresh2 = Ref("b.png", 0x06);

        var r = ImageAttach.ApplyGuards(new[] { seen, fresh1, fresh2 },
            new HashSet<string> { seen.Sha256 }, Limits);

        Assert.Equal(2, r.Attached.Count);
        Assert.Contains(r.Notices, n => n.Text.Contains("already attached", StringComparison.Ordinal));
    }

    [Fact]
    public void After_a_RESTORE_an_attached_image_is_still_deduped()
    {
        //the accumulated set is empty after a restore, so the shas come from restored history
        Write("image.jpg", Jpeg());
        var img = Detect("here is image.jpg").Attached[0];

        //a resumed session still has the image in history, so the restored list holds it
        var restored = new[] { new ChatMessage("user", "here is image.jpg", Images: new[] { img }) };

        var r = Detect("what about image.jpg now", ImageAttach.ShasIn(restored));
        Assert.Empty(r.Attached);
        Assert.Contains(r.Notices, n => n.Text.Contains("already attached", StringComparison.Ordinal));
    }

    [Fact]
    public void After_a_COMPACTION_the_same_image_CAN_be_attached_again()
    {
        //compaction drops images from history, so claiming already attached would lie about what the model can see
        Write("image.jpg", Jpeg());
        var img = Detect("here is image.jpg").Attached[0];
        Assert.NotEmpty(ImageAttach.ShasIn(new[] { new ChatMessage("user", "x", Images: new[] { img }) }));

        //the compacted history is a text summary, so it yields no hashes and the image can be attached again.
        var compacted = new[] { new ChatMessage("system", "summary of the session so far") };

        Assert.Empty(ImageAttach.ShasIn(compacted));
        Assert.Single(Detect("look at image.jpg again", ImageAttach.ShasIn(compacted)).Attached);
    }

    [Fact]
    public void A_directory_named_like_an_image_is_not_attached()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "shots.png"));
        Assert.Empty(Detect("open shots.png").Attached);
    }
}
