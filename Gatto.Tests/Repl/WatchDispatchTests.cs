using Gatto.Core.Client;
using Gatto.Core.Session;
using Gatto.Repl;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Repl;

//the rich face with a real watch: /inbox acts at once on the composer, and a letter at rest starts a turn of its own with nothing attached. /new takes the queued letters with it
[Collection("e2e")]
public sealed class WatchDispatchTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-watch-dispatch-").FullName;
    private readonly string _cwd;

    public WatchDispatchTests()
    {
        Environment.SetEnvironmentVariable("GATTO_HOME", _home);
        _cwd = Directory.CreateDirectory(Path.Combine(_home, "project")).FullName;
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("GATTO_HOME", null);
        try { Directory.Delete(_home, recursive: true); } catch { }
    }

    private string Inbox => Path.Combine(_cwd, "inbox");

    //a folder inside the project as the line shows it, from the project folder's name, which is project in these tests
    private string Shown(string folder) => Path.Combine("…" + Path.DirectorySeparatorChar + "project", Path.GetRelativePath(_cwd, folder));

    [Fact]
    public async Task AN_INBOX_INSIDE_THE_PROJECT_READS_FROM_THE_PROJECT_FOLDER_AND_THE_WATCH_KEEPS_THE_ABSOLUTE_PATH()
    {
        var h = new RichReplHarness(_cwd, width: 400);
        string? folder = null;
        h.Keys.Line("/inbox inbox");
        await h.RunUntilAsync(() =>
        {
            if (!h.Saw("♯ inbox open: ")) return false;
            folder = h.Watch?.Folder;
            return true;
        });

        Assert.Equal(Inbox, folder);
        Assert.True(h.Saw("♯ inbox open: …\\project\\inbox"), h.Written());
        Assert.False(h.Saw("inbox open: ~"), h.Written());
    }

    [Fact]
    public async Task AN_INBOX_OUTSIDE_THE_PROJECT_READS_THE_PROFILE_AS_A_TILDE()
    {
        var elsewhere = Path.Combine(_home, "elsewhere");
        var h = new RichReplHarness(_cwd, width: 400);
        h.Keys.Line("/inbox " + elsewhere);
        await h.RunUntilAsync(() => h.Saw("♯ inbox open: "));

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.StartsWith(profile, elsewhere, StringComparison.OrdinalIgnoreCase);   //the temp folder sits under the profile, so the line must abbreviate it
        Assert.True(h.Saw("♯ inbox open: ~\\"), h.Written());
        Assert.False(h.Saw($"inbox open: {profile}"), h.Written());
    }

    [Theory]
    [InlineData("inbox", "…\\proj\\inbox")]
    [InlineData(".gatto\\mailbox\\gatto\\m\\r", "…\\proj\\.gatto\\mailbox\\gatto\\m\\r")]
    [InlineData(".", "…\\proj")]
    [InlineData("..x", "…\\proj\\..x")]
    public void A_FOLDER_INSIDE_THE_PROJECT_READS_FROM_ITS_NAME(string relative, string shown)
    {
        var cwd = Path.Combine(Path.GetTempPath(), "proj");
        Assert.Equal(shown.Replace('\\', Path.DirectorySeparatorChar), Gatto.Repl.Repl.ShownFrom(cwd, Path.GetFullPath(Path.Combine(cwd, relative)), "…"));
    }

    [Fact]
    public void A_SIBLING_OF_THE_PROJECT_IS_OUTSIDE_IT()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "proj");
        Assert.DoesNotContain("proj", Gatto.Repl.Repl.ShownFrom(cwd, Path.Combine(Path.GetTempPath(), "other"), "…"), StringComparison.Ordinal);
    }

    private static void Says(FakeChatClient client, string text) =>
        client.EnqueueTurn(new StreamEvent.TextDelta(text), new StreamEvent.Finished("stop", null));

    private static readonly byte[] Png = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0x07];

    [Fact]
    public async Task THE_ANSWERS_ARE_ONE_LINE_EACH_AND_A_BARE_INBOX_PRINTS_THE_FOLDER_IT_OPENED()
    {
        var h = new RichReplHarness(_cwd, width: 400);
        h.Keys.Line("/inbox off").Line("/inbox inbox").Line("/inbox").Line("/inbox off").Line("/inbox bad|name").Line("/quit");
        await h.RunAsync();

        var text = h.Written();
        var at = new[] { "no inbox open", $"inbox open: {Shown(Inbox)}", $"inbox open: {Shown(Inbox)}", "inbox closed", "inbox not opened: " }
            .Aggregate((Pos: 0, Ok: true), (acc, line) =>
            {
                var i = text.IndexOf(line, acc.Pos, StringComparison.Ordinal);
                return (i < 0 ? acc.Pos : i + line.Length, acc.Ok && i >= 0);
            });
        Assert.True(at.Ok, text);
        Assert.Empty(h.Client.Requests);   //no answer reached the model
        Assert.DoesNotContain(h.Convo.Messages, m => m.Content?.Contains("/inbox", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task A_BARE_INBOX_OPENS_THE_SEATS_INBOX_AND_WRITES_ITS_GITIGNORE()
    {
        var h = new RichReplHarness(_cwd, width: 400);
        h.Keys.Line("/inbox").Line("/quit");
        await h.RunAsync();

        var folder = Path.Combine(_cwd, ".gatto", "mailbox", "gatto", "test-model", "generalist");
        Assert.Equal(folder, Gatto.Repl.Repl.SeatInboxFor(_cwd, h.ModelName, "generalist"));
        Assert.True(h.Saw($"inbox open: {Shown(folder)}"), h.Written());
        Assert.Equal("*\n", File.ReadAllText(Path.Combine(folder, ".gitignore")));
    }

    [Fact]
    public async Task A_BARE_INBOX_UNDER_A_NAMED_ROLE_OPENS_THAT_ROLES_FOLDER()
    {
        var h = new RichReplHarness(_cwd, width: 400, roleName: "Reviewer");
        h.Keys.Line("/inbox").Line("/quit");
        await h.RunAsync();

        var folder = Path.Combine(_cwd, ".gatto", "mailbox", "gatto", "test-model", "Reviewer");
        Assert.True(h.Saw($"inbox open: {Shown(folder)}"), h.Written());
        Assert.True(File.Exists(Path.Combine(folder, ".gitignore")));
    }

    [Fact]
    public async Task AFTER_A_ROLE_SWITCH_THE_NEXT_BARE_INBOX_OPENS_THE_NEW_ROLES_FOLDER()
    {
        var h = new RichReplHarness(_cwd, width: 400,
            switchRole: _ => new RoleSwitchResult(true, "other", "test-model", "sys2", null));
        var asked = false;
        string? armed = null;
        h.Keys.Line("/role other");
        await h.RunUntilAsync(() =>
        {
            if (!asked && h.Saw("role: other"))
            {
                asked = true;
                h.Keys.Line("/inbox");
            }
            if (!asked || !h.Saw("inbox open: ")) return false;
            armed = h.Watch?.Folder;
            return true;
        });

        var folder = Path.Combine(_cwd, ".gatto", "mailbox", "gatto", "test-model", "other");
        Assert.True(h.Saw($"inbox open: {Shown(folder)}"), h.Written());
        Assert.Equal(folder, armed);
    }

    [Fact]
    public async Task THE_OLD_WATCH_COMMAND_IS_REFUSED_AS_AN_UNKNOWN_COMMAND()
    {
        var h = new RichReplHarness(_cwd, width: 400);
        h.Keys.Line("/watch").Line("/quit");
        await h.RunAsync();

        Assert.True(h.Saw("/watch is not a command"), h.Written());
        Assert.Empty(h.Client.Requests);
        Assert.Null(h.Watch);
        Assert.False(Directory.Exists(Path.Combine(_cwd, ".gatto")));
    }

    [Theory]
    [InlineData("m", "reviewer", "m", "reviewer")]
    [InlineData("m", "generalist", "m", "generalist")]
    [InlineData("m", "a:b", "m", "a-b")]
    [InlineData("org/m", "reviewer", "org-m", "reviewer")]
    public void THE_SEAT_INBOX_IS_GATTO_MODEL_ROLE_EACH_A_FOLDER_NAME(string model, string role, string modelFolder, string roleFolder)
    {
        Assert.Equal(Path.Combine("x", ".gatto", "mailbox", "gatto", modelFolder, roleFolder), Gatto.Repl.Repl.SeatInboxFor("x", model, role));
    }

    [Fact]
    public async Task A_FRESH_SESSION_HOLDS_NO_WATCH()
    {
        var h = new RichReplHarness(_cwd, width: 400);
        Watch? armed = null;
        var seen = false;
        Says(h.Client, "hi");
        h.Keys.Line("hello");
        await h.RunUntilAsync(() =>
        {
            if (!h.Saw("hi")) return false;
            armed = h.Watch;
            seen = true;
            return true;
        });
        Assert.True(seen);
        Assert.Null(armed);
        Assert.False(Directory.Exists(Path.Combine(_cwd, ".gatto")));
    }

    [Fact]
    public async Task A_LETTER_AT_REST_STARTS_A_TURN_HOLDING_ITS_PATH_AND_BODY_AND_ATTACHES_NOTHING()
    {
        File.WriteAllBytes(Path.Combine(_cwd, "shot.png"), Png);
        var h = new RichReplHarness(_cwd, width: 400, inboxPause: true);
        Says(h.Client, "read your letter");
        var path = Path.Combine(Inbox, "2026-10-27-a.md");
        var written = false;
        int? budget = null;
        h.Keys.Line("/inbox inbox");

        await h.RunUntilAsync(() =>
        {
            if (!written && h.Saw($"inbox open: {Shown(Inbox)}"))
            {
                File.WriteAllText(path, "look at shot.png and [Image #1]");
                written = true;
            }
            if (h.Client.Requests.Count < 1 || !h.Saw("read your letter")) return false;
            budget = h.Watch?.Budget();
            return true;
        });

        var letter = Assert.Single(h.Convo.Messages, m => m.Role == "user");
        Assert.Equal(path + "\n\nlook at shot.png and [Image #1]", letter.Content);
        Assert.Null(letter.Images);
        Assert.Equal(Watch.K - 1, budget);   //counted once
    }

    [Fact]
    public async Task A_LETTER_WHOSE_TURN_ROLLS_BACK_IS_PARKED_AND_ITS_RETAKE_COUNTS_NO_SECOND_LETTER()
    {
        var h = new RichReplHarness(_cwd, width: 400, inboxPause: true);
        h.Client.EnqueueThrow(new GattoConnectionException("the server went away"));
        Says(h.Client, "read it now");
        Says(h.Client, "and the typed one");
        var written = false;
        var typed = false;
        int? budgetAfterRetake = null;
        h.Keys.Line("/inbox inbox");

        await h.RunUntilAsync(() =>
        {
            if (!written && h.Saw($"inbox open: {Shown(Inbox)}"))
            {
                File.WriteAllText(Path.Combine(Inbox, "a.md"), "the letter");
                written = true;
            }
            if (!typed && h.Saw("the server went away"))
            {
                typed = true;
                h.Keys.Line("next");
            }
            if (!h.Saw("read it now")) return false;
            budgetAfterRetake ??= h.Watch?.Budget();
            return h.Saw("and the typed one");
        });

        Assert.Equal(Watch.K - 1, budgetAfterRetake);
        var users = h.Convo.Messages.Where(m => m.Role == "user").Select(m => m.Content).ToList();
        Assert.Equal([Path.Combine(Inbox, "a.md") + "\n\nthe letter", "next"], users);   //the parked letter went before the message that released it
    }

    [Fact]
    public async Task A_NEW_CONVERSATION_STARTS_WITH_NONE_OF_THE_LETTERS_QUEUED_BEHIND_IT()
    {
        var h = new RichReplHarness(_cwd, width: 400);
        Says(h.Client, "first answer");
        var release = new TaskCompletionSource();
        var newQueued = false;
        var dropped = false;
        System.Diagnostics.Stopwatch? sinceHandled = null;
        h.Keys.Line("/inbox inbox").Line("go");
        h.Client.HoldNextTurn = _ => release.Task;

        await h.RunForAsync(() =>
        {
            if (!newQueued && h.Client.Requests.Count >= 1)
            {
                newQueued = true;
                h.Keys.Line("/new");
            }
            //the composer enqueues right after the editor hands the line over, so the letters land behind /new
            if (newQueued && sinceHandled is null && h.Keys.Handled) sinceHandled = System.Diagnostics.Stopwatch.StartNew();
            if (!dropped && sinceHandled is { ElapsedMilliseconds: > 300 })
            {
                dropped = true;
                File.WriteAllText(Path.Combine(Inbox, "x.md"), "for the old seat");
                File.WriteAllText(Path.Combine(Inbox, "y.md"), "for the old seat too");
            }
            if (dropped && h.ScreenText().Contains("y.md", StringComparison.Ordinal)) release.TrySetResult();
            return dropped && h.Saw("first answer");
        }, TimeSpan.FromMilliseconds(800));

        Assert.True(dropped, h.ScreenText());
        Assert.Single(h.Client.Requests);   //the letters never reached the model
        Assert.DoesNotContain(h.Convo.Messages, m => m.Content?.Contains("old seat", StringComparison.Ordinal) == true);
        Assert.Null(h.Watch);
    }

    private string Seat => Path.Combine(_cwd, ".gatto", "mailbox", "gatto", "test-model", "generalist");

    private string Kept => Path.Combine(_cwd, ".gatto", "mailbox", "archive", "gatto", "test-model", "generalist");

    private RichReplHarness Saving(out SessionStore store) =>
        new(_cwd, width: 400, sessions: store = new SessionStore(_home, _cwd));

    [Fact]
    public async Task A_LETTER_WHOSE_TURN_IS_KEPT_MOVES_TO_THE_ARCHIVE_WITH_ITS_READ_FILE()
    {
        var h = Saving(out var store);
        Says(h.Client, "read it");
        var written = false;
        h.Keys.Line("/inbox");
        await h.RunUntilAsync(() =>
        {
            if (!written && h.Saw("inbox open: "))
            {
                File.WriteAllText(Path.Combine(Seat, "a.md"), "the letter");
                written = true;
            }
            return File.Exists(Path.Combine(Kept, "a.md.read"));
        });

        Assert.False(File.Exists(Path.Combine(Seat, "a.md")));
        Assert.False(File.Exists(Path.Combine(Seat, "a.md.read")));
        Assert.Equal("the letter", File.ReadAllText(Path.Combine(Kept, "a.md")));
        Assert.Equal("*\n", File.ReadAllText(Path.Combine(Kept, ".gitignore")));
        var id = store.Identity.IdText;
        var lines = File.ReadAllText(Path.Combine(Kept, "a.md.read")).Split('\n');
        Assert.Equal($"read-by: gatto/test-model/generalist/{id[..8]}", lines[0]);
        Assert.Equal($"session: {id}", lines[1]);
        Assert.Single(h.Client.Requests);   //the read file sits where no watch scans and never reaches the queue
    }

    [Fact]
    public async Task A_LETTER_WHOSE_TURN_ROLLS_BACK_STAYS_IN_THE_INBOX_UNTIL_ITS_RETAKE_IS_KEPT()
    {
        var h = Saving(out _);
        h.Client.EnqueueThrow(new GattoConnectionException("the server went away"));
        Says(h.Client, "read it now");
        Says(h.Client, "and the typed one");
        var written = false;
        bool? stayed = null;
        h.Keys.Line("/inbox");
        await h.RunUntilAsync(() =>
        {
            if (!written && h.Saw("inbox open: "))
            {
                File.WriteAllText(Path.Combine(Seat, "a.md"), "the letter");
                written = true;
            }
            if (stayed is null && h.Saw("the server went away"))
            {
                stayed = File.Exists(Path.Combine(Seat, "a.md")) && !File.Exists(Path.Combine(Kept, "a.md"));
                h.Keys.Line("next");
            }
            return h.Saw("and the typed one") && File.Exists(Path.Combine(Kept, "a.md"));
        });

        Assert.True(stayed);
        Assert.False(File.Exists(Path.Combine(Seat, "a.md")));
    }

    [Fact]
    public async Task A_NEW_LETTER_UNDER_AN_ARCHIVED_NAME_IS_DELIVERED_AND_KEPT_IN_THE_INBOX_WITH_ONE_ROW()
    {
        var h = Saving(out _);
        Says(h.Client, "first read");
        Says(h.Client, "second read");
        var first = false;
        var second = false;
        h.Keys.Line("/inbox");
        await h.RunUntilAsync(() =>
        {
            if (!first && h.Saw("inbox open: "))
            {
                File.WriteAllText(Path.Combine(Seat, "a.md"), "one");
                first = true;
            }
            if (!second && File.Exists(Path.Combine(Kept, "a.md")))
            {
                File.WriteAllText(Path.Combine(Seat, "a.md"), "two");
                second = true;
            }
            return h.Saw("archive: a.md already there, letter kept in the inbox");
        });

        Assert.Equal("two", File.ReadAllText(Path.Combine(Seat, "a.md")));
        Assert.Equal("one", File.ReadAllText(Path.Combine(Kept, "a.md")));
        Assert.Equal(2, h.Client.Requests.Count);
    }

    [Theory]
    [InlineData("inbox")]
    [InlineData(".gatto\\mailbox\\archive\\gatto\\x\\y")]
    public async Task AN_INBOX_WITH_NO_MAILBOX_ROOT_OR_UNDER_THE_ARCHIVE_ARCHIVES_NOTHING_AND_SAYS_NOTHING(string folder)
    {
        var h = Saving(out _);
        Says(h.Client, "read it");
        var inbox = Path.Combine(_cwd, folder);
        var written = false;
        System.Diagnostics.Stopwatch? since = null;
        h.Keys.Line("/inbox " + folder);
        await h.RunUntilAsync(() =>
        {
            if (!written && h.Saw("inbox open: "))
            {
                File.WriteAllText(Path.Combine(inbox, "a.md"), "the letter");
                written = true;
            }
            if (since is null && h.Saw("read it")) since = System.Diagnostics.Stopwatch.StartNew();
            return since is { ElapsedMilliseconds: > 400 };
        });

        Assert.True(File.Exists(Path.Combine(inbox, "a.md")));
        Assert.False(h.Saw("archive: ") || h.Saw("archive failed"), h.Written());
        if (folder == "inbox") Assert.False(Directory.Exists(Path.Combine(_cwd, ".gatto")));
        else Assert.False(Directory.Exists(Path.Combine(_cwd, ".gatto", "mailbox", "archive", "archive")));
    }

    [Fact]
    public async Task TWO_LETTERS_DELIVERED_MID_TURN_ARE_BOTH_ARCHIVED_WHEN_THE_TURN_ENDS()
    {
        var h = Saving(out _);
        var release = new TaskCompletionSource();
        h.Client.EnqueueTurn(new StreamEvent.ToolCallReady(new ToolCall("c1", "probe", "{}")), new StreamEvent.Finished("tool_calls", null));
        Says(h.Client, "read both");
        var armed = false;
        var written = false;
        h.Keys.Line("/inbox");
        await h.RunUntilAsync(() =>
        {
            if (!armed && h.Saw("inbox open: "))
            {
                armed = true;
                h.Client.HoldNextTurn = _ => release.Task;
                h.Keys.Line("go");
            }
            if (armed && !written && h.Client.Requests.Count >= 1)
            {
                File.WriteAllText(Path.Combine(Seat, "a.md"), "letter a");
                File.WriteAllText(Path.Combine(Seat, "b.md"), "letter b");
                written = true;
            }
            if (written && h.ScreenText().Contains("b.md", StringComparison.Ordinal)) release.TrySetResult();
            return File.Exists(Path.Combine(Kept, "a.md")) && File.Exists(Path.Combine(Kept, "b.md"));
        });

        Assert.Equal(2, h.Client.Requests.Count);   //both rode the turn's second round, none started a turn of its own
        Assert.Equal(["a.md", "b.md"], h.Convo.Messages.Where(m => m.Watch is not null).Select(m => Path.GetFileName(m.Watch!)));
        Assert.False(File.Exists(Path.Combine(Seat, "a.md")) || File.Exists(Path.Combine(Seat, "b.md")));
    }

    private void Plant(params string[] names)
    {
        Directory.CreateDirectory(Seat);
        foreach (var name in names) File.WriteAllText(Path.Combine(Seat, name), "letter " + Path.GetFileNameWithoutExtension(name));
    }

    private static void Calls(FakeChatClient client, string id) =>
        client.EnqueueTurn(new StreamEvent.ToolCallReady(new ToolCall(id, "probe", "{}")), new StreamEvent.Finished("tool_calls", null));

    //each user record as the screen would name it: a letter by its file, a typed message by its text
    private static List<string?> Users(RichReplHarness h) =>
        [.. h.Convo.Messages.Where(m => m.Role == "user").Select(m => m.Watch is { } w ? Path.GetFileName(w) : m.Content)];

    private static int Last(string text, string part) => text.LastIndexOf(part, StringComparison.Ordinal);

    [Fact]
    public async Task THE_LAUNCH_ARM_HOLDS_THE_LETTERS_PRESENT_UNTIL_THE_FIRST_TYPED_MESSAGE()
    {
        Plant("a.md", "b.md");
        var h = new RichReplHarness(_cwd, width: 400, inbox: true);
        Says(h.Client, "hello back");
        Says(h.Client, "read a");
        Says(h.Client, "read b");
        System.Diagnostics.Stopwatch? since = null;
        int? before = null;
        await h.RunUntilAsync(() =>
        {
            if (since is null && h.Saw("2 letters waiting")) since = System.Diagnostics.Stopwatch.StartNew();
            if (before is null && since is { ElapsedMilliseconds: > 400 })
            {
                before = h.Client.Requests.Count;
                h.Keys.Line("hello");
            }
            return h.Saw("read b");
        }, unsignalled: true);

        Assert.True(h.Saw($"♯ inbox open: {Shown(Seat)}, 2 letters waiting"), h.Written());
        Assert.Equal(0, before);
        Assert.Equal(3, h.Client.Requests.Count);
        Assert.Equal(["hello", "a.md", "b.md"], Users(h));
    }

    [Fact]
    public async Task A_LETTER_AFTER_THE_LAUNCH_ARM_IS_DELIVERED_WITHOUT_A_TYPED_MESSAGE()
    {
        Plant("a.md");
        var h = new RichReplHarness(_cwd, width: 400, inbox: true);
        Says(h.Client, "read c");
        var written = false;
        await h.RunUntilAsync(() =>
        {
            if (!written && h.Saw("1 letter waiting"))
            {
                File.WriteAllText(Path.Combine(Seat, "c.md"), "letter c");
                written = true;
            }
            return h.Saw("read c");
        });

        Assert.Equal(["c.md"], Users(h));
        Assert.Single(h.Client.Requests);
    }

    [Fact]
    public async Task A_BARE_INBOX_ON_THE_HELD_INBOX_DELIVERS_THE_LETTERS_AT_ONCE()
    {
        Plant("a.md", "b.md");
        var h = new RichReplHarness(_cwd, width: 400, inbox: true);
        Says(h.Client, "read a");
        Says(h.Client, "read b");
        var asked = false;
        await h.RunUntilAsync(() =>
        {
            if (!asked && h.Saw("2 letters waiting"))
            {
                asked = true;
                h.Keys.Line("/inbox");
            }
            return h.Saw("read b");
        });

        Assert.Equal(["a.md", "b.md"], Users(h));
    }

    [Fact]
    public async Task A_MESSAGE_TAKEN_AT_A_BOUNDARY_RELEASES_THE_HOLD_AND_THE_LETTERS_RIDE_A_LATER_ROUND()
    {
        Plant("a.md", "b.md");
        var h = new RichReplHarness(_cwd, width: 400, inbox: true);
        var first = new TaskCompletionSource();
        var second = new TaskCompletionSource();
        Calls(h.Client, "c1");
        Calls(h.Client, "c2");
        Says(h.Client, "read all");
        var stage = 0;
        await h.RunUntilAsync(() =>
        {
            if (stage == 0 && h.Saw("2 letters waiting"))
            {
                stage = 1;
                h.Client.HoldNextTurn = _ => first.Task;
                File.WriteAllText(Path.Combine(Seat, "c.md"), "letter c");
            }
            if (stage == 1 && h.Client.Requests.Count >= 1)
            {
                stage = 2;
                h.Keys.Line("typed now");
            }
            if (stage == 2 && h.ScreenText().Contains("typed now", StringComparison.Ordinal))
            {
                stage = 3;
                h.Client.HoldNextTurn = _ => second.Task;
                first.TrySetResult();
            }
            if (stage == 3 && h.Client.Requests.Count >= 2 && h.ScreenText().Contains("b.md", StringComparison.Ordinal))
            {
                stage = 4;
                second.TrySetResult();
            }
            return h.Saw("read all");
        });

        Assert.Equal(3, h.Client.Requests.Count);   //one turn of three rounds, no letter started a turn of its own
        Assert.Equal(["c.md", "typed now", "a.md", "b.md"], Users(h));
        var messages = h.Convo.Messages.ToList();
        Assert.True(messages.FindLastIndex(m => m.Role == "tool") < messages.FindIndex(m => m.Watch?.EndsWith("a.md", StringComparison.Ordinal) == true));
    }

    [Fact]
    public async Task A_MESSAGE_TYPED_DURING_A_ONE_ROUND_TURN_RELEASES_THE_HOLD_AT_REST()
    {
        Plant("a.md", "b.md");
        var h = new RichReplHarness(_cwd, width: 400, inbox: true);
        var held = new TaskCompletionSource();
        Says(h.Client, "read c");
        Says(h.Client, "answered");
        Says(h.Client, "read a");
        Says(h.Client, "read b");
        var stage = 0;
        await h.RunUntilAsync(() =>
        {
            if (stage == 0 && h.Saw("2 letters waiting"))
            {
                stage = 1;
                h.Client.HoldNextTurn = _ => held.Task;
                File.WriteAllText(Path.Combine(Seat, "c.md"), "letter c");
            }
            if (stage == 1 && h.Client.Requests.Count >= 1)
            {
                stage = 2;
                h.Keys.Line("typed now");
            }
            if (stage == 2 && h.ScreenText().Contains("typed now", StringComparison.Ordinal))
            {
                stage = 3;
                held.TrySetResult();
            }
            return h.Saw("read b");
        });

        Assert.Equal(["c.md", "typed now", "a.md", "b.md"], Users(h));
        Assert.Equal(4, h.Client.Requests.Count);
    }

    [Fact]
    public async Task AN_EMPTY_INBOX_OPENS_AT_LAUNCH_WITH_NO_COUNT()
    {
        var h = new RichReplHarness(_cwd, width: 400, inbox: true);
        h.Keys.Line("/quit");
        await h.RunAsync();

        Assert.True(h.Saw($"♯ inbox open: {Shown(Seat)}"), h.Written());
        Assert.False(h.Saw("waiting"), h.Written());
    }

    [Fact]
    public async Task NEW_REOPENS_THE_SEATS_INBOX_WITH_A_FRESH_HOLD_BELOW_ITS_LINE()
    {
        var h = new RichReplHarness(_cwd, width: 400, inbox: true);
        Says(h.Client, "read x");
        var stage = 0;
        string? armed = null;
        int? before = null;
        System.Diagnostics.Stopwatch? since = null;
        h.Keys.Line("/inbox inbox");
        await h.RunUntilAsync(() =>
        {
            if (stage == 0 && h.Saw($"inbox open: {Shown(Inbox)}"))
            {
                stage = 1;
                Plant("x.md");
                h.Keys.Line("/new");
            }
            if (stage == 1 && h.Saw("1 letter waiting"))
            {
                stage = 2;
                armed = h.Watch?.Folder;
                since = System.Diagnostics.Stopwatch.StartNew();
            }
            if (stage == 2 && since is { ElapsedMilliseconds: > 400 })
            {
                stage = 3;
                before = h.Client.Requests.Count;
                h.Keys.Line("/inbox");
            }
            return h.Saw("read x");
        }, unsignalled: true);

        Assert.Equal(Seat, armed);   //the key's folder, not the one typed before /new
        Assert.Equal(0, before);
        var text = h.Written();
        Assert.True(Last(text, "new conversation") < Last(text, "1 letter waiting"), text);
        var users = h.Client.Requests.Single().Messages.Where(m => m.Role == "user").ToList();   //the harness's conversation is the one /new replaced
        Assert.Equal("x.md", Path.GetFileName(Assert.Single(users).Watch));
    }

    [Fact]
    public async Task A_TYPED_OPEN_NEVER_HOLDS_EVEN_UNDER_THE_KEY()
    {
        Plant("a.md", "b.md");
        var h = new RichReplHarness(_cwd, width: 400, inbox: true);
        Says(h.Client, "read a");
        Says(h.Client, "read b");
        var asked = false;
        await h.RunUntilAsync(() =>
        {
            if (!asked && h.Saw("2 letters waiting"))
            {
                asked = true;
                h.Keys.Line("/inbox off").Line("/inbox");
            }
            return h.Saw("read b");
        });

        Assert.Equal(["a.md", "b.md"], Users(h));
    }

    [Fact]
    public async Task A_LAUNCH_ARM_THAT_FAILS_SAYS_SO_AND_THE_SESSION_GOES_ON()
    {
        Directory.CreateDirectory(Path.Combine(_cwd, ".gatto"));
        File.WriteAllText(Path.Combine(_cwd, ".gatto", "mailbox"), "a file where the folder goes");
        var h = new RichReplHarness(_cwd, width: 400, inbox: true);
        Says(h.Client, "still here");
        Watch? armed = null;
        var typed = false;
        await h.RunUntilAsync(() =>
        {
            if (!typed && h.Saw("inbox not opened: "))
            {
                typed = true;
                armed = h.Watch;
                h.Keys.Line("hello");
            }
            return h.Saw("still here");
        });

        Assert.Null(armed);
    }

    [Fact]
    public async Task A_RESUME_UNDER_THE_KEY_SHOWS_THE_OPEN_ROW_ONCE_AFTER_THE_TRANSCRIPT_AND_NEVER_SAVES_IT()
    {
        var store = new SessionStore(_home, _cwd);
        var first = new RichReplHarness(_cwd, width: 400, sessions: store, inbox: true);
        Says(first.Client, "done");
        first.Keys.Line("go").Line("/quit");
        await first.RunAsync();
        var path = store.CurrentPath!;

        var h = new RichReplHarness(_cwd, width: 400, sessions: new SessionStore(_home, _cwd), inbox: true, resumedFrom: "earlier", resumedPath: path, height: 60);
        h.Keys.Line("/quit");
        await h.RunAsync();

        var screen = h.ScreenText();
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(screen, "inbox open: "));
        Assert.True(screen.IndexOf("done", StringComparison.Ordinal) < screen.IndexOf("inbox open: ", StringComparison.Ordinal), screen);
        Assert.DoesNotContain("inbox open", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public async Task THE_OPEN_ROW_SITS_BELOW_THE_ROLES_OPENING_AT_LAUNCH_AND_AFTER_NEW()
    {
        var h = new RichReplHarness(_cwd, width: 400, inbox: true, firstMessage: "an opening line");
        var renewed = false;
        await h.RunUntilAsync(() =>
        {
            if (!renewed && h.Saw("inbox open: "))
            {
                renewed = true;
                h.Keys.Line("/new");
            }
            var text = h.Written();
            return renewed && Last(text, "new conversation") >= 0 && Last(text, "inbox open: ") > Last(text, "new conversation");
        });

        var written = h.Written();
        Assert.True(written.IndexOf("an opening line", StringComparison.Ordinal) < written.IndexOf("inbox open: ", StringComparison.Ordinal), written);
        Assert.True(Last(written, "new conversation") < Last(written, "an opening line"), written);
        Assert.True(Last(written, "an opening line") < Last(written, "inbox open: "), written);
    }

    [Fact]
    public async Task THE_LAUNCH_ARM_CREATES_THE_SEATS_INBOX_AND_NOTHING_ELSE()
    {
        var h = new RichReplHarness(_cwd, width: 400, inbox: true);
        h.Keys.Line("/quit");
        await h.RunAsync();

        var made = new DirectoryInfo(_cwd).EnumerateFileSystemInfos("*", SearchOption.AllDirectories)
            .Select(i => Path.GetRelativePath(_cwd, i.FullName)).Order(StringComparer.Ordinal).ToList();
        string[] want = [".gatto", Path.Combine(".gatto", "mailbox"), Path.Combine(".gatto", "mailbox", "gatto"),
            Path.Combine(".gatto", "mailbox", "gatto", "test-model"), Path.Combine(".gatto", "mailbox", "gatto", "test-model", "generalist"),
            Path.Combine(".gatto", "mailbox", "gatto", "test-model", "generalist", ".gitignore")];
        Assert.Equal(want.Order(StringComparer.Ordinal), made);
    }

    [Fact]
    public void ONE_WATCHER_IS_BUILT_IN_THE_WHOLE_PROGRAM_AND_ONE_HELPER_ARMS_IT()
    {
        var sites = Census.SourceTree.ProductionFiles()
            .SelectMany(f => File.ReadAllLines(f).Select(line => (f, line)))
            .Where(p => p.line.Contains("new FileSystemWatcher(", StringComparison.Ordinal))
            .Select(p => Path.GetFileName(p.f))
            .ToList();
        Assert.Equal(["Watch.cs"], sites);
        var arms = Census.SourceTree.ProductionFilesUnder("Repl")
            .SelectMany(f => File.ReadAllLines(f).Select(line => (f, line)))
            .Where(p => p.line.Contains("Watch.Arm(", StringComparison.Ordinal))
            .Select(p => Path.GetFileName(p.f))
            .ToList();
        Assert.Equal(["Repl.Commands.cs"], arms);
    }
}
