using System.Text;
using Gatto.Core;
using Gatto.Core.Loop;
using Gatto.Core.Session;
using Gatto.Core.Tools;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Repl;

//save writes the last reply to the typed path, refuses the home and an existing file, and reaches no model
[Collection("e2e")]
public class SaveCommandTests : IDisposable
{
    private const string Reply = "première ligne\nsecond line";

    private readonly string _root = Directory.CreateTempSubdirectory("gatto-save-").FullName;
    private string Home => Path.Combine(_root, "home");
    private string Cwd => Path.Combine(_root, "project");

    public SaveCommandTests()
    {
        Directory.CreateDirectory(Home);
        Directory.CreateDirectory(Cwd);
        Environment.SetEnvironmentVariable("GATTO_HOME", Home);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("GATTO_HOME", null);
        Directory.Delete(_root, recursive: true);
    }

    private static Conversation Answered()
    {
        var convo = new Conversation("sys");
        convo.AddUser("what is it");
        convo.AddAssistant(Reply);
        return convo;
    }

    private Gatto.Repl.Repl ReplOn(Conversation convo, FakeChatClient? client = null)
    {
        client ??= new FakeChatClient();
        var loop = new AgentLoop(client, new ToolRegistry(), new HookBus(), new TestToolContext(Cwd), "m");
        return new Gatto.Repl.Repl(loop, convo, "generalist", "m", null, client, null, null, Cwd,
            () => Composed.Text("sys"), _ => new Gatto.Repl.RoleSwitchResult(false, "", null, null, "unused"));
    }

    private static string Saved(string fullPath, int bytes) => $"saved {Plural.Of(bytes, "byte")} to {fullPath}";

    [Fact]
    public void THE_FILE_HOLDS_THE_LAST_REPLY_AS_UTF8_WITH_NO_BYTE_ORDER_MARK()
    {
        var convo = Answered();
        var line = ReplOn(convo).HandleSave(@"notes\answer.md");

        var full = Path.Combine(Cwd, "notes", "answer.md");
        var expected = Encoding.UTF8.GetBytes(Gatto.Repl.Repl.LastTextExchange(convo)!.Value.Assistant);
        Assert.Equal(expected, File.ReadAllBytes(full));
        Assert.NotEqual(0xEF, File.ReadAllBytes(full)[0]);
        Assert.Equal(Saved(full, expected.Length), line);
    }

    [Fact]
    public void AN_EXISTING_FILE_IS_NAMED_AS_TYPED_AND_LEFT_AS_IT_WAS()
    {
        var full = Path.Combine(Cwd, "answer.md");
        File.WriteAllBytes(full, [1, 2, 3]);

        Assert.Equal("answer.md exists", ReplOn(Answered()).HandleSave("answer.md"));
        Assert.Equal([1, 2, 3], File.ReadAllBytes(full));
    }

    [Fact]
    public void NO_REPLY_SAYS_NOTHING_TO_SAVE_AND_CREATES_NOTHING()
    {
        var convo = new Conversation("sys");
        convo.AddAssistant("an opening line the role file wrote");

        Assert.Equal("nothing to save", ReplOn(convo).HandleSave(@"sub\answer.md"));
        Assert.False(Directory.Exists(Path.Combine(Cwd, "sub")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_PATH_UNDER_THE_HOME_IS_REFUSED_AND_CREATES_NOTHING(bool throughDotDot)
    {
        var typed = throughDotDot ? @"..\home\sub\answer.md" : Path.Combine(Home, "sub", "answer.md");

        Assert.Equal(Gatto.Repl.Repl.SaveIntoHome, ReplOn(Answered()).HandleSave(typed));
        Assert.False(Directory.Exists(Path.Combine(Home, "sub")));
    }

    //the test is a folder prefix, so a sibling whose name starts with the home's is outside it
    [Fact]
    public void A_SIBLING_WHOSE_NAME_STARTS_WITH_THE_HOMES_IS_WRITTEN()
    {
        var full = Path.Combine(_root, "home2", "answer.md");

        Assert.Equal(Saved(full, Encoding.UTF8.GetByteCount(Reply)), ReplOn(Answered()).HandleSave(full));
    }

    [Fact]
    public void A_TILDE_PATH_RESOLVES_UNDER_THE_USERS_FOLDER()
    {
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "x.md"), ToolArgs.Resolve(Cwd, @"~\x.md"));
    }

    [Fact]
    public void A_PARENT_THAT_DOES_NOT_EXIST_IS_CREATED()
    {
        ReplOn(Answered()).HandleSave(@"deep\er\answer.md");

        Assert.Equal(Reply, File.ReadAllText(Path.Combine(Cwd, "deep", "er", "answer.md")));
    }

    [Theory]
    [InlineData("bad<name.md")]
    [InlineData("adir")]
    [InlineData(@"afile\answer.md")]
    public void A_PATH_THAT_CANNOT_BE_WRITTEN_SAYS_NOT_SAVED_AND_NOT_EXISTS(string typed)
    {
        Directory.CreateDirectory(Path.Combine(Cwd, "adir"));
        File.WriteAllText(Path.Combine(Cwd, "afile"), "x");

        var line = ReplOn(Answered()).HandleSave(typed);

        Assert.StartsWith("not saved: ", line, StringComparison.Ordinal);
        Assert.NotEqual($"{typed} exists", line);
    }

    [Theory]
    [InlineData(@"C:\work\project", "project-20261119-202141.md")]
    [InlineData(@"C:\work\project\", "project-20261119-202141.md")]
    [InlineData(@"C:\", "20261119-202141.md")]
    public void A_BARE_SAVE_NAMES_THE_FILE_AFTER_THE_FOLDER_AND_THE_TIME(string cwd, string name)
    {
        Assert.Equal(name, Gatto.Repl.Repl.BareSaveName(cwd, new DateTime(2026, 11, 19, 20, 21, 41)));
    }

    [Fact]
    public void A_BARE_SAVE_WRITES_THAT_NAME_IN_THE_WORKING_FOLDER()
    {
        var line = ReplOn(Answered()).HandleSave("");

        var written = Assert.Single(Directory.GetFiles(Cwd));
        Assert.Matches(@"^project-\d{8}-\d{6}\.md$", Path.GetFileName(written));
        Assert.Equal(Saved(written, Encoding.UTF8.GetByteCount(Reply)), line);
        Assert.Equal(Reply, File.ReadAllText(written));
    }

    [Fact]
    public void A_BARE_SAVE_WITH_NO_REPLY_SAYS_NOTHING_TO_SAVE()
    {
        Assert.Equal("nothing to save", ReplOn(new Conversation("sys")).HandleSave(""));
        Assert.Empty(Directory.GetFiles(Cwd));
    }

    [Fact]
    public async Task THE_RICH_FACE_SAVES_WITH_NO_REQUEST_AND_NO_NEW_RECORD()
    {
        var h = new RichReplHarness(Cwd, width: 240);
        h.Convo.AddUser("what is it");
        h.Convo.AddAssistant(Reply);
        var before = h.Convo.Messages.Count;
        h.Keys.Line("/save answer.md").Line("/quit");
        await h.RunAsync();

        Assert.True(h.Saw(Saved(Path.Combine(Cwd, "answer.md"), Encoding.UTF8.GetByteCount(Reply))), h.Written());
        Assert.Equal(before, h.Convo.Messages.Count);
        Assert.Empty(h.Client.Requests);
    }

    [Fact]
    public void THE_PLAIN_FACE_PRINTS_THE_SAME_LINE()
    {
        var client = new FakeChatClient();
        var convo = Answered();
        var repl = ReplOn(convo, client);
        var output = new StringWriter();
        var oldOut = Console.Out;
        var oldIn = Console.In;
        Console.SetOut(output);
        Console.SetIn(new StringReader("/save answer.md\n/save\n/quit\n"));
        try { repl.RunAsync(CancellationToken.None).GetAwaiter().GetResult(); }
        finally
        {
            Console.SetOut(oldOut);
            Console.SetIn(oldIn);
        }

        //the plain prompt is written before each answer on the same line
        var lines = output.ToString().Split('\n').Select(l => l.TrimEnd('\r')).Select(l => l.StartsWith("> ", StringComparison.Ordinal) ? l[2..] : l).ToList();
        Assert.True(lines.Contains(Saved(Path.Combine(Cwd, "answer.md"), Encoding.UTF8.GetByteCount(Reply))), output.ToString());
        Assert.Contains(lines, l => l.StartsWith($"saved {Plural.Of(Encoding.UTF8.GetByteCount(Reply), "byte")} to {Path.Combine(Cwd, "project-")}", StringComparison.Ordinal));
        Assert.Equal(3, convo.Messages.Count);
        Assert.Empty(client.Requests);
    }
}
