//the submit path joins detection and serialisation, so a typed filename becomes an image reference on the user message
using Gatto.Core.Client;
using Gatto.Tests.Fakes;
//the widget types are in Gatto.Repl, a name this file's namespace cannot reach, so they are aliased explicitly
using SelectSpec = Gatto.Repl.SelectSpec;
using SelectOutcome = Gatto.Repl.SelectOutcome;

namespace Gatto.Tests.Repl;

[Collection("e2e")]
public class VisionAttachWiringTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gatto-vwire-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private static byte[] Jpeg() => new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46 };

    [Fact]
    public async Task A_filename_typed_into_the_composer_becomes_an_image_on_the_user_message()
    {
        File.WriteAllBytes(Path.Combine(_dir, "image.jpg"), Jpeg());
        var h = new RichReplHarness(_dir);
        h.Client.EnqueueTurn(new StreamEvent.TextDelta("a picture"), new StreamEvent.Finished("stop", null));

        //the fixture uses natural phrasing: a bare filename inside a sentence, no sigil.
        h.Keys.Line("There's a file named image.jpg in the project folder. Describe it.");
        h.Keys.Line("/quit");
        await h.RunAsync();

        var user = h.Convo.Messages.Last(m => m.Role == "user");
        var img = Assert.Single(user.Images!);
        Assert.Equal("image.jpg", Path.GetFileName(img.Path));
        Assert.Equal("image/jpeg", img.MediaType);

        //the attach must announce itself with its cost.
        Assert.Contains(h.Warnings.Concat(new[] { h.ScreenText() }),
            t => t.Contains("image.jpg", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_message_naming_no_image_carries_none()
    {
        var h = new RichReplHarness(_dir);
        h.Client.EnqueueTurn(new StreamEvent.TextDelta("sure"), new StreamEvent.Finished("stop", null));
        h.Keys.Line("just a normal question");
        h.Keys.Line("/quit");
        await h.RunAsync();

        Assert.All(h.Convo.Messages, m => Assert.Null(m.Images));
    }

    [Fact]
    public void The_image_unavailable_notice_is_WIRED_to_a_sink_by_the_composition()
    {
        //the image-unavailable notice cannot be raised by the fake client, so its sink wiring is pinned from source text
        var composition = File.ReadAllText(Path.Combine(RepoRoot(), "Gatto", "Cli", "GattoApp.cs"));
        Assert.Contains("client.OnImageUnavailable =", composition, StringComparison.Ordinal);
    }

    [Fact]
    public void The_client_raises_the_notice_when_an_attached_file_has_changed()
    {
        //a changed file must produce a stub and a warning, or the model sees different pixels under the same history
        var path = Path.Combine(_dir, "shot.jpg");
        File.WriteAllBytes(path, Jpeg());
        var stale = ImageRef.FromFile(path);
        File.WriteAllBytes(path, Jpeg().Concat(new byte[] { 0x99 }).ToArray());

        var warnings = new List<string>();
        var json = RequestJson.SerialiseMessage(
            new ChatMessage("user", "here is a picture", Images: new[] { stale }),
            new ImageCache(), warnings.Add);

        Assert.DoesNotContain("image_url", json, StringComparison.Ordinal);
        Assert.Contains(warnings, w => w.Contains("shot.jpg", StringComparison.Ordinal));
    }

    //the files this class reads sit at the repo root, so search upward from the test binary for Gatto.sln
    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "Gatto.sln"))) d = d.Parent;
        Assert.NotNull(d);
        return d!.FullName;
    }

    //the blind-model refusal and the count question are rows of the theory below, so the rule has one copy

    //distinct names matter here, since session dedupe collapses a repeated batch to nothing and the test would then check nothing
    private string Images(string prefix, int n)
    {
        var names = new List<string>();
        for (var i = 0; i < n; i++)
        {
            var name = $"{prefix}{i}.png";
            File.WriteAllBytes(Path.Combine(_dir, name),
                new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A,
                             (byte)prefix[0], (byte)i });
            names.Add(name);
        }
        return string.Join(' ', names);
    }

    private string ElevenImages() => Images("i", 11);

    [Fact]
    public async Task Yes_sends_every_image()
    {
        var text = ElevenImages();
        var h = new RichReplHarness(_dir);
        h.Client.EnqueueTurn(new StreamEvent.TextDelta("ok"), new StreamEvent.Finished("stop", null));
        h.OnConfirmAttach = _ => new SelectOutcome.Chosen(0);

        h.Keys.Line(text);
        h.Keys.Line("/quit");
        await h.RunAsync();

        var user = h.Convo.Messages.Last(m => m.Role == "user");
        Assert.Equal(11, user.Images!.Count);
    }

    [Fact]
    public async Task A_batch_at_or_under_the_threshold_is_never_asked_about()
    {
        //ten sits at the threshold, and only counts above it may ask. if this test ever fails with a question, the guard has become a nag.
        for (var i = 0; i < 10; i++)
            File.WriteAllBytes(Path.Combine(_dir, $"j{i}.png"),
                new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, (byte)i });
        var h = new RichReplHarness(_dir);
        h.Client.EnqueueTurn(new StreamEvent.TextDelta("ok"), new StreamEvent.Finished("stop", null));
        var askedCount = 0;
        h.OnConfirmAttach = _ => { askedCount++; return new SelectOutcome.Chosen(0); };

        h.Keys.Line(string.Join(' ', Enumerable.Range(0, 10).Select(i => $"j{i}.png")));
        h.Keys.Line("/quit");
        await h.RunAsync();

        Assert.Equal(0, askedCount);
        Assert.Equal(10, h.Convo.Messages.Last(m => m.Role == "user").Images!.Count);
    }

    [Fact]
    public async Task The_grant_stops_the_asking_for_this_project()
    {
        //the two batches must differ, or session dedupe collapses the second to nothing and the test passes without the grant
        var first = Images("a", 11);
        var second = Images("b", 11);
        var h = new RichReplHarness(_dir);
        h.Client.EnqueueTurn(new StreamEvent.TextDelta("ok"), new StreamEvent.Finished("stop", null));
        h.Client.EnqueueTurn(new StreamEvent.TextDelta("ok"), new StreamEvent.Finished("stop", null));
        var askedCount = 0;
        h.OnConfirmAttach = _ => { askedCount++; return new SelectOutcome.Chosen(1); };

        h.Keys.Line(first);
        h.Keys.Line(second);
        h.Keys.Line("/quit");
        await h.RunAsync();

        Assert.Equal(1, askedCount);   //the grant must stop all later asking.
        Assert.Equal(2, h.Convo.Messages.Count(m => m.Role == "user"));   //both messages must actually be sent.
        Assert.All(h.Convo.Messages.Where(m => m.Role == "user"), m => Assert.Equal(11, m.Images!.Count));
        Assert.True(File.Exists(Gatto.Core.Home.ProjectSettings.PathFor(_dir)));
    }

    //the four ways gatto can refuse to attach what a message is about.
    public enum Door { BlindModel, ByteCeiling, NearMiss, CountDeclined }

    //a refused attach must block the send and keep the message in the composer, and the exit dump proves it was never echoed
    [Theory]
    [InlineData(Door.BlindModel)]
    [InlineData(Door.ByteCeiling)]
    [InlineData(Door.NearMiss)]
    [InlineData(Door.CountDeclined)]
    public async Task A_refusal_blocks_the_send_says_its_own_reason_and_never_echoes_the_message(Door door)
    {
        //each row asserts its own signature phrase and the absence of the other rows' phrases.
        var phrase = new Dictionary<Door, string>
        {
            [Door.BlindModel] = "can't see images",
            [Door.ByteCeiling] = "too large",
            [Door.NearMiss] = "did you mean",
            [Door.CountDeclined] = "",       //the prompt itself is the sentence, so nothing further is committed.
        };

        string message;
        RichReplHarness h;
        SelectSpec? asked = null;
        switch (door)
        {
            case Door.BlindModel:
                //one small image with its exact name, innocent of the other causes.
                File.WriteAllBytes(Path.Combine(_dir, "shot.jpg"), Jpeg());
                h = new RichReplHarness(_dir, visionAvailable: false, dumpOnExit: true);
                message = "describe shot.jpg";
                break;
            case Door.ByteCeiling:
                //one image over the byte ceiling, still under the count threshold, named exactly, against a seeing model.
                File.WriteAllBytes(Path.Combine(_dir, "huge.png"), BigPng(21));
                h = new RichReplHarness(_dir, dumpOnExit: true);
                message = "describe huge.png";
                break;
            case Door.NearMiss:
                //two sibling files make the meant picture ambiguous, so nothing attaches. that keeps this row innocent of the blind-model and ceiling branches.
                File.WriteAllBytes(Path.Combine(_dir, "sopa.png"), Png(1));
                File.WriteAllBytes(Path.Combine(_dir, "sopa.jpg"), Jpeg());
                h = new RichReplHarness(_dir, dumpOnExit: true);
                message = "what is in sopa.jpeg";
                break;
            default:
                //eleven tiny files pass the count threshold and stay far under the byte ceiling.
                h = new RichReplHarness(_dir, dumpOnExit: true);
                h.OnConfirmAttach = spec => { asked = spec; return new SelectOutcome.Chosen(2); };
                message = Images("k", 11);
                break;
        }

        //no quit is scripted, since the refused message returns to the composer and a quit would race that restore
        h.Keys.Line(message);
        await h.RunUntilAsync(() => door == Door.CountDeclined ? asked is not null : h.Saw(phrase[door]));

        var transcript = h.TranscriptDump();

        //the refusal must keep the message from the model.
        Assert.DoesNotContain(h.Convo.Messages, m => m.Role == "user");

        //nothing may reach the transcript either. without the echo, no turn ever opened over the message.
        Assert.DoesNotContain("❯ " + message, transcript, StringComparison.Ordinal);

        //the row must give its own reason and no other row's reason.
        if (phrase[door].Length > 0)
            Assert.Contains(phrase[door], transcript, StringComparison.OrdinalIgnoreCase);
        else
            Assert.NotNull(asked);
        foreach (var (other, text) in phrase)
            if (other != door && text.Length > 0)
                Assert.DoesNotContain(text, transcript, StringComparison.OrdinalIgnoreCase);

        //no refusal path may announce an attach that did not happen.
        Assert.DoesNotContain("tok est.", transcript, StringComparison.Ordinal);

        //each refusal path also owes wording of its own, checked here rather than in near-duplicate tests.
        if (door == Door.BlindModel)
        {
            Assert.Contains("not sent", transcript, StringComparison.OrdinalIgnoreCase);
            //the refusal sentence is about the answering model, so internal file names stay out of it
            Assert.DoesNotContain("mmproj", transcript, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("profile.json", transcript, StringComparison.OrdinalIgnoreCase);
        }
        if (door == Door.CountDeclined)
            Assert.Contains("11 images", asked!.Question!.Text, StringComparison.Ordinal);
    }

    //a png header padded with zeroes so the file crosses the byte backstop.
    private static byte[] BigPng(int megabytes)
    {
        var bytes = new byte[megabytes * 1024 * 1024];
        new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
        return bytes;
    }

    private static byte[] Png(byte tag) =>
        new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, tag };

    [Fact]
    public async Task A_slash_command_never_attaches_even_when_its_argument_names_a_real_image()
    {
        //a command that falls through to a model turn keeps the user's words, which makes it the sharp case for the no-attach gate
        File.WriteAllBytes(Path.Combine(_dir, "shot.jpg"), Jpeg());
        var h = new RichReplHarness(_dir);
        h.Client.EnqueueTurn(new StreamEvent.TextDelta("banked"), new StreamEvent.Finished("stop", null));

        h.Keys.Line("/remember look at shot.jpg");
        h.Keys.Line("/quit");
        await h.RunAsync();

        var user = h.Convo.Messages.Last(m => m.Role == "user");
        Assert.Contains("shot.jpg", user.Content);   //the file name stays as words in the message.
        Assert.Null(user.Images);
    }

    //the gate reuses the dispatcher's classification, so both halves are pinned and the undeclared half is the one a prefix check fails

    public static IEnumerable<object[]> EveryCommand() =>
        Gatto.Repl.SlashCommands.All.Select(c => new object[] { c.Name });

    [Theory]
    [MemberData(nameof(EveryCommand))]
    public void Every_declared_command_is_classified_as_one(string name)
    {
        Assert.True(Gatto.Repl.Repl.IsSlashCommand(name));
        Assert.True(Gatto.Repl.Repl.IsSlashCommand($"{name} shot.png"));
        Assert.True(Gatto.Repl.Repl.IsSlashCommand($"  {name}  "));
    }

    [Fact]
    public void An_undeclared_slash_line_is_not_a_command()
    {
        //these lines match no command, so they reach the model as text and must be able to attach, where a prefix check fails
        Assert.False(Gatto.Repl.Repl.IsSlashCommand("/zzz shot.png"));
        Assert.False(Gatto.Repl.Repl.IsSlashCommand("/models shot.png"));   //this line nearly names a real command but is not declared.
        Assert.False(Gatto.Repl.Repl.IsSlashCommand("look at /srv/shot.png"));
    }

    //an escaped slash line must still attach, so the gate reads the literal flag rather than the stripped text
    [Fact]
    public async Task An_escaped_slash_line_still_attaches()
    {
        File.WriteAllBytes(Path.Combine(_dir, "shot.jpg"), Jpeg());
        var h = new RichReplHarness(_dir);
        h.Client.EnqueueTurn(new StreamEvent.TextDelta("ok"), new StreamEvent.Finished("stop", null));

        h.Keys.Line("//help what is in shot.jpg");
        h.Keys.Line("/quit");
        await h.RunAsync();

        var user = h.Convo.Messages.Last(m => m.Role == "user");
        Assert.Equal("/help what is in shot.jpg", user.Content);   //the line must be sent as text with its single slash kept.
        Assert.Single(user.Images!);
    }

    [Fact]
    public async Task An_undeclared_slash_line_still_attaches()
    {
        //the fixture starts with a slash but is not command-shaped, so neither a prefix check nor the refusal regex matches it
        File.WriteAllBytes(Path.Combine(_dir, "shot.jpg"), Jpeg());
        var h = new RichReplHarness(_dir);
        h.Client.EnqueueTurn(new StreamEvent.TextDelta("ok"), new StreamEvent.Finished("stop", null));

        Assert.False(Gatto.Repl.Repl.GateSlash("/srv/pics what is in shot.jpg").Refused);
        h.Keys.Line("/srv/pics what is in shot.jpg");
        h.Keys.Line("/quit");
        await h.RunAsync();

        var user = h.Convo.Messages.Last(m => m.Role == "user");
        Assert.Single(user.Images!);
    }

    [Fact]
    public async Task Mentioning_the_same_image_twice_attaches_it_once()
    {
        //the derived dedupe set is exercised through the real loop, where the second mention sees the first attach in the live conversation
        File.WriteAllBytes(Path.Combine(_dir, "shot.jpg"), Jpeg());
        var h = new RichReplHarness(_dir);
        h.Client.EnqueueTurn(new StreamEvent.TextDelta("ok"), new StreamEvent.Finished("stop", null));
        h.Client.EnqueueTurn(new StreamEvent.TextDelta("ok again"), new StreamEvent.Finished("stop", null));

        h.Keys.Line("look at shot.jpg");
        h.Keys.Line("now crop shot.jpg");
        h.Keys.Line("/quit");
        await h.RunAsync();

        var withImages = h.Convo.Messages.Count(m => m.Images is { Count: > 0 });
        Assert.Equal(1, withImages);
    }
}
