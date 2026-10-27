using Gatto.Core.Client;
using Gatto.Repl;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Repl;

//the rich face with a real watch: /watch acts at once on the composer, and a letter at rest starts a turn of its own with nothing attached. /new takes the queued letters with it
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

    //the folder as the line shows it, the profile read as ~
    private static string Shown(string folder) =>
        Gatto.Repl.Input.HomePath.AbbreviateProfile(folder, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    [Fact]
    public async Task THE_WATCHING_LINE_READS_THE_PROFILE_AS_A_TILDE_AND_THE_WATCH_KEEPS_THE_ABSOLUTE_PATH()
    {
        var h = new RichReplHarness(_cwd, width: 400);
        string? folder = null;
        h.Keys.Line("/watch inbox");
        await h.RunUntilAsync(() =>
        {
            if (!h.Saw("♯ watching ")) return false;
            folder = h.Watch?.Folder;
            return true;
        });

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(Inbox, folder);
        Assert.StartsWith(profile, Inbox, StringComparison.OrdinalIgnoreCase);   //the temp folder sits under the profile, so the line must abbreviate it
        Assert.True(h.Saw("♯ watching ~\\"), h.Written());
        Assert.False(h.Saw($"watching {profile}"), h.Written());
    }

    private static void Says(FakeChatClient client, string text) =>
        client.EnqueueTurn(new StreamEvent.TextDelta(text), new StreamEvent.Finished("stop", null));

    private static readonly byte[] Png = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0x07];

    [Fact]
    public async Task THE_ANSWERS_ARE_ONE_LINE_EACH_AND_A_BARE_WATCH_PRINTS_THE_FOLDER_IT_ARMED()
    {
        var h = new RichReplHarness(_cwd, width: 400);
        h.Keys.Line("/watch off").Line("/watch inbox").Line("/watch").Line("/watch off").Line("/watch bad|name").Line("/quit");
        await h.RunAsync();

        var text = h.Written();
        var at = new[] { "not watching", $"watching {Shown(Inbox)}", $"watching {Shown(Inbox)}", "watch off", "watch failed: " }
            .Aggregate((Pos: 0, Ok: true), (acc, line) =>
            {
                var i = text.IndexOf(line, acc.Pos, StringComparison.Ordinal);
                return (i < 0 ? acc.Pos : i + line.Length, acc.Ok && i >= 0);
            });
        Assert.True(at.Ok, text);
        Assert.Empty(h.Client.Requests);   //no answer reached the model
        Assert.DoesNotContain(h.Convo.Messages, m => m.Content?.Contains("/watch", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task A_BARE_WATCH_ARMS_THE_MODELS_MAILBOX_AND_WRITES_ITS_GITIGNORE()
    {
        var h = new RichReplHarness(_cwd, width: 400);
        h.Keys.Line("/watch").Line("/quit");
        await h.RunAsync();

        var folder = Path.Combine(_cwd, ".gatto", "mailbox", Gatto.Repl.Repl.MailboxName(h.ModelName));
        Assert.True(h.Saw($"watching {Shown(folder)}"), h.Written());
        Assert.Equal("*\n", File.ReadAllText(Path.Combine(folder, ".gitignore")));
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
        var h = new RichReplHarness(_cwd, width: 400);
        Says(h.Client, "read your letter");
        var path = Path.Combine(Inbox, "2026-10-27-a.md");
        var written = false;
        int? budget = null;
        h.Keys.Line("/watch inbox");

        await h.RunUntilAsync(() =>
        {
            if (!written && h.Saw($"watching {Shown(Inbox)}"))
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
        var h = new RichReplHarness(_cwd, width: 400);
        h.Client.EnqueueThrow(new GattoConnectionException("the server went away"));
        Says(h.Client, "read it now");
        Says(h.Client, "and the typed one");
        var written = false;
        var typed = false;
        int? budgetAfterRetake = null;
        h.Keys.Line("/watch inbox");

        await h.RunUntilAsync(() =>
        {
            if (!written && h.Saw($"watching {Shown(Inbox)}"))
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
        h.Keys.Line("/watch inbox").Line("go");
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

    [Fact]
    public void ONE_WATCHER_IS_BUILT_IN_THE_WHOLE_PROGRAM_AND_ONLY_THE_WATCH_COMMAND_ARMS_IT()
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
