using System.Text;
using System.Text.Json;
using Gatto.Core.Client;
using Gatto.Core.Commands;
using Gatto.Core.Loop;
using Gatto.Core.Tools;
using Gatto.Extensions;

namespace Gatto.Tests;

//the shipped undo script through the real host and bus, with the home and the working folder in temp. roslyn compiles once per class
[Collection("e2e")]
public sealed class UndoExtensionTests : IClassFixture<RoslynWarmupFixture>, IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-undo-home-").FullName;
    private readonly string _cwd = Directory.CreateTempSubdirectory("gatto-undo-cwd-").FullName;
    private readonly List<string> _log = new();

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch { }
        try { Directory.Delete(_cwd, recursive: true); } catch { }
    }

    //the shipped bytes, written into a temp extensions folder and loaded as a user's home would load them
    internal (HookBus Hooks, CommandRegistry Commands) Load()
    {
        var ext = Path.Combine(_home, "extensions");
        Directory.CreateDirectory(ext);
        File.WriteAllText(Path.Combine(ext, "undo.csx"), ShippedExtensions.Files["extensions/undo.csx"]);
        var (api, _, _) = ExtensionHost.BuildApi(_home, _cwd, () => null, () => null, _log.Add);
        var loaded = ExtensionHost.LoadAll(ext, api, _log.Add);
        Assert.Single(loaded);
        var hooks = new HookBus();
        var commands = new CommandRegistry();
        ExtensionHost.Commit(loaded, new ToolRegistry(), hooks, commands, ["/help"], _log.Add);
        Assert.Empty(_log);
        return (hooks, commands);
    }

    private static ToolCall Call(string id, string tool, string argsJson) => new(id, tool, argsJson);

    private static string Args(params (string Key, string Value)[] kv)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            foreach (var (k, v) in kv) w.WriteString(k, v);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private string Folder() => Directory.GetDirectories(Path.Combine(_home, "undo")).Single();

    private IReadOnlyList<JsonElement> Index() =>
        File.ReadAllLines(Path.Combine(Folder(), "index.jsonl")).Select(l => JsonDocument.Parse(l).RootElement).ToList();

    [Fact]
    public async Task WRITE_FILE_ON_AN_EXISTING_FILE_COPIES_ITS_BYTES_AND_INDEXES_THE_CALL()
    {
        var (hooks, _) = Load();
        var target = Path.Combine(_cwd, "x.cs");
        File.WriteAllBytes(target, [1, 2, 3]);
        await hooks.EmitToolCallAsync(new HookPayload(Call: Call("c1", "write_file", Args(("path", "x.cs"), ("content", "new")))));
        var rows = Index();
        var row = Assert.Single(rows);
        Assert.Equal(1, row.GetProperty("n").GetInt32());
        Assert.False(row.TryGetProperty("call", out _));   //the call id is the server's and may repeat, so no row holds it
        Assert.Equal("write_file", row.GetProperty("tool").GetString());
        Assert.Equal("x.cs", row.GetProperty("path").GetString());
        Assert.Equal(target, row.GetProperty("full").GetString());
        Assert.Equal(3, row.GetProperty("bytes").GetInt32());
        Assert.Equal([1, 2, 3], File.ReadAllBytes(Path.Combine(Folder(), row.GetProperty("file").GetString()!)));
        Assert.Matches(@"^\d{8}-\d{6}-\d+$", Path.GetFileName(Folder()));
    }

    [Fact]
    public async Task WRITE_FILE_ON_AN_ABSENT_FILE_RECORDS_THE_ABSENT_MARKER()
    {
        var (hooks, _) = Load();
        await hooks.EmitToolCallAsync(new HookPayload(Call: Call("c1", "write_file", Args(("path", "new.txt"), ("content", "x")))));
        var row = Assert.Single(Index());
        Assert.True(row.GetProperty("absent").GetBoolean());
        Assert.False(row.TryGetProperty("file", out _));
    }

    [Fact]
    public async Task EDIT_FILE_COPIES_AND_READ_FILE_COPIES_NOTHING()
    {
        var (hooks, _) = Load();
        File.WriteAllText(Path.Combine(_cwd, "a.txt"), "aaa");
        await hooks.EmitToolCallAsync(new HookPayload(Call: Call("c1", "edit_file", Args(("path", "a.txt"), ("old_string", "a"), ("new_string", "b")))));
        await hooks.EmitToolCallAsync(new HookPayload(Call: Call("c2", "read_file", Args(("path", "a.txt")))));
        Assert.Single(Index());
    }

    [Fact]
    public async Task THE_RESULT_HOOK_MARKS_BLOCKED_FAILED_AND_RAN()
    {
        var (hooks, _) = Load();
        File.WriteAllText(Path.Combine(_cwd, "a.txt"), "a");
        foreach (var id in new[] { "c1", "c2", "c3" })
            await hooks.EmitToolCallAsync(new HookPayload(Call: Call(id, "write_file", Args(("path", "a.txt"), ("content", "x")))));
        await hooks.EmitToolResultAsync(new HookPayload(Call: Call("c1", "write_file", "{}"), Result: new ToolResult("blocked: no", IsError: true)));
        await hooks.EmitToolResultAsync(new HookPayload(Call: Call("c2", "write_file", "{}"), Result: new ToolResult("cannot write", IsError: true)));
        await hooks.EmitToolResultAsync(new HookPayload(Call: Call("c3", "write_file", "{}"), Result: new ToolResult("ok")));
        var marks = Index().Where(e => e.TryGetProperty("mark", out _)).ToDictionary(e => Assert.Single(e.GetProperty("mark").EnumerateArray()).GetInt32(), e => e.GetProperty("state").GetString());
        Assert.Equal("blocked", marks[1]);
        Assert.Equal("failed", marks[2]);
        Assert.Equal("ran", marks[3]);
    }

    [Fact]
    public async Task A_RESULT_FOR_A_CALL_WITH_NO_SNAPSHOT_IS_IGNORED()
    {
        var (hooks, _) = Load();
        await hooks.EmitToolResultAsync(new HookPayload(Call: Call("gate", "write_file", "{}"), Result: new ToolResult("blocked: gate", IsError: true)));
        Assert.False(Directory.Exists(Path.Combine(_home, "undo")));   //nothing was recorded, so the folder was never made
    }

    [Fact]
    public async Task A_LOCKED_FILE_LOGS_ONE_LINE_AND_THE_CALL_PROCEEDS()
    {
        var (hooks, _) = Load();
        var target = Path.Combine(_cwd, "locked.bin");
        using var held = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
        await hooks.EmitToolCallAsync(new HookPayload(Call: Call("c1", "write_file", Args(("path", "locked.bin"), ("content", "x")))));
        Assert.Single(_log, l => l.Contains("locked.bin", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_FILE_ABOVE_THE_CAP_IS_SKIPPED_AND_LOGGED()
    {
        var (hooks, _) = Load();
        var target = Path.Combine(_cwd, "big.bin");
        using (var fs = new FileStream(target, FileMode.Create)) fs.SetLength(50L * 1024 * 1024 + 1);
        await hooks.EmitToolCallAsync(new HookPayload(Call: Call("c1", "write_file", Args(("path", "big.bin"), ("content", "x")))));
        Assert.Single(_log, l => l.Contains("big.bin", StringComparison.Ordinal));
        Assert.False(Directory.Exists(Path.Combine(_home, "undo")));
    }

    [Fact]
    public void RETENTION_TRIMS_A_DEAD_FOLDER_OLDEST_FIRST_AND_LEAVES_A_LIVE_REUSED_PID_ALONE()
    {
        var undo = Path.Combine(_home, "undo");
        //a dead process: a pid no process has, with 3 rows and 3 copies
        var dead = Path.Combine(undo, "20200101-000000-999999");
        Directory.CreateDirectory(dead);
        for (var n = 1; n <= 3; n++) File.WriteAllBytes(Path.Combine(dead, $"{n}-f.bin"), new byte[70 * 1024 * 1024]);
        File.WriteAllLines(Path.Combine(dead, "index.jsonl"), Enumerable.Range(1, 3).Select(n =>
            $"{{\"n\":{n},\"ts\":\"2020-01-01T00:00:0{n}+00:00\",\"tool\":\"write_file\",\"path\":\"f.bin\",\"full\":\"C:\\\\f.bin\",\"bytes\":{70 * 1024 * 1024},\"file\":\"{n}-f.bin\"}}"));
        //this process's own pid under a wrong start stamp reads as a reused pid, which is dead
        var me = Environment.ProcessId;
        var reused = Path.Combine(undo, $"20200101-000000-{me}");
        Directory.CreateDirectory(reused);
        File.WriteAllText(Path.Combine(reused, "index.jsonl"), "{\"n\":1,\"ts\":\"2020-01-01T00:00:00+00:00\",\"tool\":\"write_file\",\"path\":\"r\",\"full\":\"C:\\\\r\",\"absent\":true}\n{\"mark\":[1],\"state\":\"ran\"}\n");
        //this process under its real stamp is live and is never trimmed
        var start = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToString("yyyyMMdd-HHmmss");
        var live = Path.Combine(undo, $"{start}-{me}");
        Directory.CreateDirectory(live);
        File.WriteAllText(Path.Combine(live, "index.jsonl"), "{\"n\":1,\"ts\":\"2020-01-01T00:00:00+00:00\",\"tool\":\"write_file\",\"path\":\"l\",\"full\":\"C:\\\\l\",\"absent\":true}\n");

        Load();   //retention runs at load: 210 MB of dead copies pass 200 MB, so the oldest dead row goes first

        Assert.False(File.Exists(Path.Combine(dead, "1-f.bin")));
        Assert.True(File.Exists(Path.Combine(dead, "2-f.bin")));
        Assert.True(File.Exists(Path.Combine(dead, "3-f.bin")));
        Assert.True(Directory.Exists(live));
        Assert.False(Directory.Exists(reused));   //its one row is the oldest of the dead ones, so it goes first and its folder with it, its mark included
        var kept = File.ReadAllLines(Path.Combine(dead, "index.jsonl"));
        Assert.DoesNotContain(kept, l => l.Contains("\"n\":1,", StringComparison.Ordinal));
    }

    [Fact]
    public async Task THE_APPEND_THAT_CROSSES_THE_CAP_TRIMS_THIS_FOLDER_BUT_NEVER_BELOW_ITS_NEWEST_TEN()
    {
        var (hooks, _) = Load();
        //eleven 20 MB files: the eleventh append crosses 200 MB, and the oldest row goes, leaving ten
        for (var i = 1; i <= 11; i++)
        {
            var target = Path.Combine(_cwd, $"f{i}.bin");
            using (var fs = new FileStream(target, FileMode.Create)) fs.SetLength(20L * 1024 * 1024);
            await hooks.EmitToolCallAsync(new HookPayload(Call: Call($"c{i}", "write_file", Args(("path", $"f{i}.bin"), ("content", "x")))));
            await hooks.EmitToolResultAsync(new HookPayload(Call: Call($"c{i}", "write_file", "{}"), Result: new ToolResult("ok")));
        }
        var rows = Index().Where(e => !e.TryGetProperty("mark", out _)).ToList();
        Assert.Equal(10, rows.Count);
        Assert.Equal(2, rows[0].GetProperty("n").GetInt32());
        Assert.False(File.Exists(Path.Combine(Folder(), "1-f1.bin")));
        Assert.True(File.Exists(Path.Combine(Folder(), "11-f11.bin")));
        Assert.DoesNotContain(Index(), e => e.TryGetProperty("mark", out var m) && m.EnumerateArray().Any(x => x.GetInt32() == 1));   //the dropped row's mark goes with it
    }

    private async Task<IReadOnlyList<string>> ShellPaths(HookBus hooks, string command)
    {
        await hooks.EmitToolCallAsync(new HookPayload(Call: Call("s", "shell", Args(("command", command)))));
        var index = Path.Combine(_home, "undo");
        if (!Directory.Exists(index) || !File.Exists(Path.Combine(Folder(), "index.jsonl"))) return [];
        return Index().Where(e => !e.TryGetProperty("mark", out _)).Select(e => e.GetProperty("path").GetString()!).ToList();
    }

    [Theory]
    [InlineData("Set-Content a.txt 'x'", "a.txt")]
    [InlineData("sc -Path a.txt -Value x", "a.txt")]
    [InlineData("Add-Content -LiteralPath a.txt x", "a.txt")]
    [InlineData("'x' | Out-File -FilePath a.txt", "a.txt")]
    [InlineData("'x' | tee a.txt", "a.txt")]
    [InlineData("New-Item a.txt -ItemType File", "a.txt")]
    [InlineData("Clear-Content a.txt", "a.txt")]
    [InlineData("Remove-Item a.txt", "a.txt")]
    [InlineData("rm a.txt", "a.txt")]
    [InlineData("del a.txt -Force", "a.txt")]
    [InlineData("Rename-Item a.txt b.txt", "a.txt")]
    [InlineData("[IO.File]::WriteAllText('a.txt', 'x')", "a.txt")]
    [InlineData("[System.IO.File]::AppendAllText(\"a.txt\", 'x')", "a.txt")]
    [InlineData("echo x > a.txt", "a.txt")]
    [InlineData("echo x >> a.txt", "a.txt")]
    [InlineData("dotnet build; Set-Content a.txt x", "a.txt")]
    [InlineData("Set-Content 'sub dir\\a.txt' x", "sub dir\\a.txt")]
    [InlineData("Remove-Item -Confirm:$false a.txt", "a.txt")]
    [InlineData("git diff > a.txt", "a.txt")]                //a git command that writes nothing itself still has its redirect read
    [InlineData("dotnet build 2> a.txt", "a.txt")]
    [InlineData("dotnet build 2>&1 > a.txt", "a.txt")]
    [InlineData("Remove-Item -Verbose a.txt", "a.txt")]
    [InlineData("Remove-Item -r a.txt", "a.txt")]
    public async Task A_WRITE_FORM_SNAPSHOTS_ITS_TARGET(string command, string expected)
    {
        var (hooks, _) = Load();
        Directory.CreateDirectory(Path.Combine(_cwd, "sub dir"));
        File.WriteAllText(Path.Combine(_cwd, "a.txt"), "a");
        File.WriteAllText(Path.Combine(_cwd, "sub dir", "a.txt"), "a");
        Assert.Equal([expected], await ShellPaths(hooks, command));
    }

    [Fact]
    public async Task A_MOVE_FROM_A_MISSING_SOURCE_RECORDS_IT_ABSENT_AND_COPIES_THE_DESTINATION()
    {
        var (hooks, _) = Load();
        File.WriteAllText(Path.Combine(_cwd, "a.txt"), "a");
        Assert.Equal(["gone.txt", "a.txt"], await ShellPaths(hooks, "Move-Item gone.txt a.txt"));
        var rows = Index().Where(e => !e.TryGetProperty("mark", out _)).ToList();
        Assert.True(rows[0].GetProperty("absent").GetBoolean());
        Assert.Equal(1, rows[1].GetProperty("bytes").GetInt32());
    }

    [Fact]
    public async Task MOVE_SNAPSHOTS_SOURCE_AND_DESTINATION_AND_COPY_THE_DESTINATION_ONLY()
    {
        var (hooks, _) = Load();
        File.WriteAllText(Path.Combine(_cwd, "a.txt"), "a");
        File.WriteAllText(Path.Combine(_cwd, "b.txt"), "b");
        Assert.Equal(["a.txt", "b.txt"], await ShellPaths(hooks, "Move-Item a.txt b.txt"));
    }

    [Fact]
    public async Task COPY_SNAPSHOTS_THE_DESTINATION_ONLY()
    {
        var (hooks, _) = Load();
        File.WriteAllText(Path.Combine(_cwd, "a.txt"), "a");
        File.WriteAllText(Path.Combine(_cwd, "b.txt"), "b");
        Assert.Equal(["b.txt"], await ShellPaths(hooks, "Copy-Item a.txt -Destination b.txt"));
    }

    [Theory]
    [InlineData("Remove-Item a.txt,b.txt")]
    [InlineData("Remove-Item a.txt, b.txt")]
    [InlineData("Remove-Item 'a.txt','b.txt'")]
    [InlineData("Remove-Item \"a.txt\", 'b.txt'")]
    public async Task A_COMMA_LIST_SNAPSHOTS_EACH_FILE(string command)
    {
        var (hooks, _) = Load();
        File.WriteAllText(Path.Combine(_cwd, "a.txt"), "a");
        File.WriteAllText(Path.Combine(_cwd, "b.txt"), "b");
        Assert.Equal(["a.txt", "b.txt"], await ShellPaths(hooks, command));
        Assert.All(Index().Where(e => !e.TryGetProperty("mark", out _)), e => Assert.Equal(1, e.GetProperty("bytes").GetInt32()));
    }

    [Theory]
    [InlineData("Select-String -Pattern x a.txt")]
    [InlineData("Set-Content $target x")]
    [InlineData("Set-Content (Join-Path a b) x")]
    [InlineData("dotnet test 2>&1")]
    [InlineData("Get-Content a.txt > $null")]
    [InlineData("$x = 1 -gt 0")]
    [InlineData("Remove-Item sub -Recurse")]
    [InlineData("Copy-Item src -Destination sub -Recurse")]
    [InlineData("New-Item sub2 -ItemType Directory")]
    [InlineData("git checkout -- a.txt")]
    [InlineData("git restore a.txt")]
    [InlineData("git reset --hard")]
    [InlineData("Remove-Item *.tmp")]
    [InlineData("Remove-Item a?.txt")]
    public async Task THESE_SNAPSHOT_NOTHING(string command)
    {
        var (hooks, _) = Load();
        File.WriteAllText(Path.Combine(_cwd, "a.txt"), "a");
        Directory.CreateDirectory(Path.Combine(_cwd, "sub"));
        Assert.Empty(await ShellPaths(hooks, command));
    }

    [Theory]
    [InlineData("Remove-Item sub -Recurse", "directory")]
    [InlineData("git checkout -- a.txt", "git")]
    [InlineData("Remove-Item *.tmp", "wildcard")]
    public async Task A_DIRECTORY_OR_GIT_TARGET_IS_LOGGED_BY_KIND(string command, string why)
    {
        var (hooks, _) = Load();
        Directory.CreateDirectory(Path.Combine(_cwd, "sub"));
        await ShellPaths(hooks, command);
        Assert.Contains(_log, l => l.Contains($"unsnapshotted: {why}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_SHELL_RESULT_WITH_AN_ERROR_OR_A_TIMEOUT_MARKS_RAN()
    {
        var (hooks, _) = Load();
        File.WriteAllText(Path.Combine(_cwd, "a.txt"), "a");
        await hooks.EmitToolCallAsync(new HookPayload(Call: Call("c1", "shell", Args(("command", "Set-Content a.txt x; dotnet build")))));
        await hooks.EmitToolResultAsync(new HookPayload(Call: Call("c1", "shell", "{}"), Result: new ToolResult("exit 1", IsError: true)));
        await hooks.EmitToolCallAsync(new HookPayload(Call: Call("c2", "shell", Args(("command", "Set-Content a.txt y")))));
        await hooks.EmitToolResultAsync(new HookPayload(Call: Call("c2", "shell", "{}"), Result: new ToolResult("command timed out after 1ms", IsError: true)));
        var marks = Index().Where(e => e.TryGetProperty("mark", out _)).ToDictionary(e => Assert.Single(e.GetProperty("mark").EnumerateArray()).GetInt32(), e => e.GetProperty("state").GetString());
        Assert.Equal("ran", marks[1]);
        Assert.Equal("ran", marks[2]);
    }

    [Fact]
    public async Task A_TARGET_OUTSIDE_THE_FOLDER_IS_LOGGED_AND_NOT_COPIED()
    {
        var (hooks, _) = Load();
        var outside = Path.Combine(_home, "o.txt");
        File.WriteAllText(outside, "o");
        Assert.Empty(await ShellPaths(hooks, $"Set-Content '{outside}' x"));
        Assert.Contains(_log, l => l.Contains("unsnapshotted: outside folder", StringComparison.Ordinal));
    }

    private static async Task<string> Run(CommandRegistry commands, string arg)
    {
        Assert.True(commands.TryGet("/undo", out var row));
        return await row.Handler(arg, CancellationToken.None);
    }

    [Fact]
    public async Task AN_EMPTY_LIST_SAYS_SO()
    {
        var (_, commands) = Load();
        Assert.Equal("no snapshots for this folder", await Run(commands, ""));
    }

    [Fact]
    public async Task THE_LIST_SHOWS_RAN_ROWS_NEWEST_FIRST_AND_HIDES_FAILED_AND_BLOCKED()
    {
        var (hooks, commands) = Load();
        File.WriteAllText(Path.Combine(_cwd, "a.txt"), "a");
        foreach (var (id, state) in new[] { ("c1", "ran"), ("c2", "failed"), ("c3", "blocked"), ("c4", "ran") })
        {
            await hooks.EmitToolCallAsync(new HookPayload(Call: Call(id, "write_file", Args(("path", "a.txt"), ("content", "x")))));
            var result = state switch { "failed" => new ToolResult("no", IsError: true), "blocked" => new ToolResult("blocked: no", IsError: true), _ => new ToolResult("ok") };
            await hooks.EmitToolResultAsync(new HookPayload(Call: Call(id, "write_file", "{}"), Result: result));
        }
        var lines = (await Run(commands, "")).Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("1 ", lines[0]);
        Assert.Contains("write_file", lines[0], StringComparison.Ordinal);
        Assert.Contains("a.txt", lines[0], StringComparison.Ordinal);
        Assert.Contains("1 B", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("2 ", lines[1]);
    }

    [Fact]
    public async Task A_CANCELLED_CALL_IN_THIS_PROCESS_LISTS_AS_UNKNOWN()
    {
        var (hooks, commands) = Load();
        File.WriteAllText(Path.Combine(_cwd, "a.txt"), "a");
        await hooks.EmitToolCallAsync(new HookPayload(Call: Call("c1", "shell", Args(("command", "Set-Content a.txt x")))));
        var line = Assert.Single((await Run(commands, "")).Split('\n'));
        Assert.Contains("shell?", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_DEAD_PROCESS_LISTS_AS_UNKNOWN_WITH_ITS_START_AND_A_LIVE_SIBLING_IS_HIDDEN()
    {
        var undo = Path.Combine(_home, "undo");
        var target = Path.Combine(_cwd, "d.txt").Replace("\\", "\\\\");
        var dead = Path.Combine(undo, "20200101-000000-999999");
        Directory.CreateDirectory(dead);
        File.WriteAllText(Path.Combine(dead, "1-d.txt"), "old");
        File.WriteAllText(Path.Combine(dead, "index.jsonl"),
            $"{{\"n\":1,\"ts\":\"2020-01-01T00:00:01+00:00\",\"tool\":\"write_file\",\"path\":\"d.txt\",\"full\":\"{target}\",\"bytes\":3,\"file\":\"1-d.txt\"}}\n");
        //a live sibling: this process under a stamp 1 second off, which the 2 second window reads as the same process
        var me = System.Diagnostics.Process.GetCurrentProcess();
        var sibling = Path.Combine(undo, $"{me.StartTime.AddSeconds(1):yyyyMMdd-HHmmss}-{me.Id}");
        Directory.CreateDirectory(sibling);
        File.WriteAllText(Path.Combine(sibling, "index.jsonl"),
            $"{{\"n\":1,\"ts\":\"2020-01-01T00:00:02+00:00\",\"tool\":\"write_file\",\"path\":\"s.txt\",\"full\":\"{target}\",\"absent\":true}}\n");
        var (_, commands) = Load();
        var line = Assert.Single((await Run(commands, "")).Split('\n'));
        Assert.Contains("write_file?", line, StringComparison.Ordinal);
        Assert.Contains("d.txt", line, StringComparison.Ordinal);
        Assert.Contains("2020-01-01 00:00:01", line, StringComparison.Ordinal);   //the row's time with its day, since it is not today
        Assert.EndsWith("  2020-01-01 00:00:00", line);   //the other process's start, from its folder name, as a time
    }

    [Fact]
    public async Task A_HALF_WRITTEN_LAST_LINE_IN_ANOTHER_INDEX_IS_SKIPPED()
    {
        var undo = Path.Combine(_home, "undo");
        var target = Path.Combine(_cwd, "d.txt").Replace("\\", "\\\\");
        var dead = Path.Combine(undo, "20200101-000000-999998");
        Directory.CreateDirectory(dead);
        File.WriteAllText(Path.Combine(dead, "index.jsonl"),
            $"{{\"n\":1,\"ts\":\"2020-01-01T00:00:01+00:00\",\"tool\":\"write_file\",\"path\":\"d.txt\",\"full\":\"{target}\",\"absent\":true}}\n{{\"n\":2,\"ts\":\"2020-01");
        var (_, commands) = Load();
        Assert.Single((await Run(commands, "")).Split('\n'));
    }

    [Fact]
    public async Task RESTORE_BY_NUMBER_WRITES_THE_BYTES_BACK_AND_SNAPSHOTS_WHAT_IT_REPLACED()
    {
        var (hooks, commands) = Load();
        var target = Path.Combine(_cwd, "a.txt");
        File.WriteAllText(target, "old");
        await hooks.EmitToolCallAsync(new HookPayload(Call: Call("c1", "write_file", Args(("path", "a.txt"), ("content", "new")))));
        await hooks.EmitToolResultAsync(new HookPayload(Call: Call("c1", "write_file", "{}"), Result: new ToolResult("ok")));
        File.WriteAllText(target, "new");
        var line = await Run(commands, "1");
        Assert.StartsWith("restored a.txt to ", line);
        Assert.EndsWith("(3 B)", line);
        Assert.Equal("old", File.ReadAllText(target));
        var rows = Index().Where(e => !e.TryGetProperty("mark", out _)).ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal("undo", rows[1].GetProperty("tool").GetString());
        Assert.Equal("new", File.ReadAllText(Path.Combine(Folder(), rows[1].GetProperty("file").GetString()!)));
        var list = (await Run(commands, "")).Split('\n');
        Assert.Equal(2, list.Length);   //the restore's own snapshot is listed, so the restore is undoable
    }

    [Fact]
    public async Task RESTORE_OF_AN_ABSENT_MARKER_DELETES_THE_FILE_AND_KEEPS_ITS_FOLDER()
    {
        var (hooks, commands) = Load();
        await hooks.EmitToolCallAsync(new HookPayload(Call: Call("c1", "write_file", Args(("path", "sub\\n.txt"), ("content", "x")))));
        await hooks.EmitToolResultAsync(new HookPayload(Call: Call("c1", "write_file", "{}"), Result: new ToolResult("ok")));
        Directory.CreateDirectory(Path.Combine(_cwd, "sub"));
        File.WriteAllText(Path.Combine(_cwd, "sub", "n.txt"), "x");
        var line = await Run(commands, "1");
        Assert.StartsWith("deleted sub\\n.txt, it did not exist before ", line);
        Assert.False(File.Exists(Path.Combine(_cwd, "sub", "n.txt")));
        Assert.True(Directory.Exists(Path.Combine(_cwd, "sub")));
    }

    [Fact]
    public async Task RESTORE_BY_PATH_TAKES_THE_NEWEST_RAN_ROW_OF_THAT_PATH()
    {
        var (hooks, commands) = Load();
        var target = Path.Combine(_cwd, "a.txt");
        File.WriteAllText(target, "v1");
        await hooks.EmitToolCallAsync(new HookPayload(Call: Call("c1", "write_file", Args(("path", "a.txt"), ("content", "v2")))));
        await hooks.EmitToolResultAsync(new HookPayload(Call: Call("c1", "write_file", "{}"), Result: new ToolResult("ok")));
        File.WriteAllText(target, "v2");
        await hooks.EmitToolCallAsync(new HookPayload(Call: Call("c2", "write_file", Args(("path", "a.txt"), ("content", "v3")))));
        await hooks.EmitToolResultAsync(new HookPayload(Call: Call("c2", "write_file", "{}"), Result: new ToolResult("ok")));
        File.WriteAllText(target, "v3");
        await Run(commands, "a.txt");
        Assert.Equal("v2", File.ReadAllText(target));
    }

    [Fact]
    public async Task A_BARE_NUMBER_IS_ALWAYS_N_EVEN_WHEN_A_FILE_IS_NAMED_SO()
    {
        var (hooks, commands) = Load();
        File.WriteAllText(Path.Combine(_cwd, "7"), "seven");
        File.WriteAllText(Path.Combine(_cwd, "a.txt"), "a");
        //a snapshot of the file named 7 exists, so a path-first build would restore seven. two rows, so 7 is out of range as n
        await hooks.EmitToolCallAsync(new HookPayload(Call: Call("c1", "write_file", Args(("path", "7"), ("content", "eight")))));
        await hooks.EmitToolResultAsync(new HookPayload(Call: Call("c1", "write_file", "{}"), Result: new ToolResult("ok")));
        File.WriteAllText(Path.Combine(_cwd, "7"), "eight");
        await hooks.EmitToolCallAsync(new HookPayload(Call: Call("c2", "write_file", Args(("path", "a.txt"), ("content", "x")))));
        await hooks.EmitToolResultAsync(new HookPayload(Call: Call("c2", "write_file", "{}"), Result: new ToolResult("ok")));
        var line = await Run(commands, "7");
        Assert.DoesNotContain("restored", line, StringComparison.Ordinal);
        Assert.Equal("eight", File.ReadAllText(Path.Combine(_cwd, "7")));
    }

    [Fact]
    public async Task A_REUSED_CALL_ID_MARKS_ONLY_THE_ROWS_OF_ITS_OWN_CALL()
    {
        var (hooks, commands) = Load();
        File.WriteAllText(Path.Combine(_cwd, "a.txt"), "a");
        File.WriteAllText(Path.Combine(_cwd, "b.txt"), "b");
        //a server that streams no id gives every call the same empty one
        await hooks.EmitToolCallAsync(new HookPayload(Call: Call("", "write_file", Args(("path", "a.txt"), ("content", "x")))));
        await hooks.EmitToolResultAsync(new HookPayload(Call: Call("", "write_file", "{}"), Result: new ToolResult("ok")));
        await hooks.EmitToolCallAsync(new HookPayload(Call: Call("", "write_file", Args(("path", "b.txt"), ("content", "x")))));
        await hooks.EmitToolResultAsync(new HookPayload(Call: Call("", "write_file", "{}"), Result: new ToolResult("cannot write", IsError: true)));
        var line = Assert.Single((await Run(commands, "")).Split('\n'));
        Assert.Contains("a.txt", line, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("11")]
    [InlineData("nothere.txt")]
    public async Task AN_UNKNOWN_N_OR_PATH_PRINTS_ONE_LINE_AND_CHANGES_NOTHING(string arg)
    {
        var (hooks, commands) = Load();
        File.WriteAllText(Path.Combine(_cwd, "a.txt"), "a");
        await hooks.EmitToolCallAsync(new HookPayload(Call: Call("c1", "write_file", Args(("path", "a.txt"), ("content", "x")))));
        await hooks.EmitToolResultAsync(new HookPayload(Call: Call("c1", "write_file", "{}"), Result: new ToolResult("ok")));
        var before = File.ReadAllLines(Path.Combine(Folder(), "index.jsonl")).Length;
        var line = await Run(commands, arg);
        Assert.Single(line.Split('\n'));
        Assert.DoesNotContain("restored", line, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllLines(Path.Combine(Folder(), "index.jsonl")).Length);
    }

    [Theory]
    [InlineData("{\"mark\":[1]}")]
    [InlineData("{\"mark\":[\"x\"],\"state\":\"ran\"}")]
    public async Task A_MALFORMED_MARK_LINE_IS_SKIPPED_AND_THE_FOLDER_STILL_LISTS(string badMark)
    {
        var undo = Path.Combine(_home, "undo");
        var target = Path.Combine(_cwd, "d.txt").Replace("\\", "\\\\");
        var dead = Path.Combine(undo, "20200101-000000-999997");
        Directory.CreateDirectory(dead);
        File.WriteAllText(Path.Combine(dead, "index.jsonl"),
            $"{{\"n\":1,\"ts\":\"2020-01-01T00:00:01+00:00\",\"tool\":\"write_file\",\"path\":\"d.txt\",\"full\":\"{target}\",\"absent\":true}}\n{badMark}\n");
        var (_, commands) = Load();
        var line = Assert.Single((await Run(commands, "")).Split('\n'));
        Assert.Contains("d.txt", line, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"sub dir\\s t.txt\"")]
    [InlineData("'sub dir\\s t.txt'")]
    public async Task A_QUOTED_PATH_RESTORES_AS_THE_BARE_ONE_DOES(string arg)
    {
        var (hooks, commands) = Load();
        Directory.CreateDirectory(Path.Combine(_cwd, "sub dir"));
        var target = Path.Combine(_cwd, "sub dir", "s t.txt");
        File.WriteAllText(target, "v1");
        await hooks.EmitToolCallAsync(new HookPayload(Call: Call("c1", "write_file", Args(("path", "sub dir\\s t.txt"), ("content", "v2")))));
        await hooks.EmitToolResultAsync(new HookPayload(Call: Call("c1", "write_file", "{}"), Result: new ToolResult("ok")));
        File.WriteAllText(target, "v2");
        Assert.StartsWith("restored ", await Run(commands, arg));
        Assert.Equal("v1", File.ReadAllText(target));
    }

    [Theory]
    [InlineData("{\"n\":\"x\",\"ts\":\"2020-01-01T00:00:02+00:00\",\"tool\":\"write_file\",\"path\":\"e.txt\",\"full\":\"E\",\"absent\":true}")]
    [InlineData("{\"n\":2,\"tool\":\"write_file\",\"path\":\"e.txt\",\"full\":\"E\",\"absent\":true}")]
    [InlineData("{\"n\":2,\"ts\":\"not a time\",\"tool\":\"write_file\",\"path\":\"e.txt\",\"full\":\"E\",\"absent\":true}")]
    [InlineData("{\"n\":2,\"ts\":\"2020-01-01T00:00:02+00:00\",\"path\":\"e.txt\",\"full\":\"E\",\"absent\":true}")]
    [InlineData("{\"n\":2,\"ts\":\"2020-01-01T00:00:02+00:00\",\"tool\":\"write_file\",\"path\":\"e.txt\",\"full\":7,\"absent\":true}")]
    [InlineData("{\"n\":2,\"ts\":\"2020-01-01T00:00:02+00:00\",\"tool\":\"write_file\",\"path\":\"e.txt\",\"full\":\"E\",\"bytes\":\"x\",\"file\":\"2-e.txt\"}")]
    [InlineData("{\"n\":2,\"ts\":\"2020-01-01T00:00:02+00:00\",\"tool\":\"write_file\",\"path\":\"e.txt\",\"full\":\"E\",\"bytes\":3,\"file\":7}")]
    [InlineData("{\"n\":2,\"ts\":\"2020-01-01T00:00:02+00:00\",\"tool\":\"write_file\",\"path\":\"e.txt\",\"full\":\"E\",\"bytes\":3}")]
    [InlineData("{\"n\":2,\"ts\":\"2020-01-01T00:00:02+00:00\",\"tool\":\"write_file\",\"path\":\"e.txt\",\"full\":\"E\",\"file\":\"2-e.txt\"}")]
    [InlineData("{\"n\":2,\"ts\":\"2020-01-01T00:00:02+00:00\",\"tool\":\"write_file\",\"path\":\"e.txt\",\"full\":\"E\",\"bytes\":3,\"file\":\"..\\\\e.txt\"}")]
    [InlineData("{\"n\":2,\"ts\":\"2020-01-01T00:00:02+00:00\",\"tool\":\"write_file\",\"path\":\"e.txt\",\"full\":\"F\",\"absent\":true}")]
    public async Task A_MALFORMED_ROW_LINE_IS_SKIPPED_AND_THE_FOLDER_STILL_LISTS(string badRow)
    {
        var undo = Path.Combine(_home, "undo");
        var target = Path.Combine(_cwd, "d.txt").Replace("\\", "\\\\");
        var dead = Path.Combine(undo, "20200101-000000-999996");
        Directory.CreateDirectory(dead);
        File.WriteAllText(Path.Combine(dead, "index.jsonl"),
            $"{{\"n\":1,\"ts\":\"2020-01-01T00:00:01+00:00\",\"tool\":\"write_file\",\"path\":\"d.txt\",\"full\":\"{target}\",\"absent\":true}}\n{badRow.Replace("\"E\"", "\"" + Path.Combine(_cwd, "e.txt").Replace("\\", "\\\\") + "\"").Replace("\"F\"", "\"" + Path.Combine(_cwd, "..", "e.txt").Replace("\\", "\\\\") + "\"")}\n");
        var (_, commands) = Load();
        var line = Assert.Single((await Run(commands, "")).Split('\n'));
        Assert.Contains("d.txt", line, StringComparison.Ordinal);
    }

    [Fact]
    public void A_ROW_WHOSE_FILE_IS_A_PATH_NEVER_DELETES_WHAT_IT_NAMES()
    {
        var elsewhere = Directory.CreateTempSubdirectory("gatto-undo-victim-").FullName;
        try
        {
            var victim = Path.Combine(elsewhere, "victim.txt");
            File.WriteAllText(victim, "keep");
            var dead = Path.Combine(_home, "undo", "20200101-000000-999995");
            Directory.CreateDirectory(dead);
            var full = Path.Combine(_cwd, "e.txt").Replace("\\", "\\\\");
            var file = victim.Replace("\\", "\\\\");
            //300 MB on paper passes the 200 MB cap, so the trim at load takes this row first
            File.WriteAllText(Path.Combine(dead, "index.jsonl"),
                $"{{\"n\":1,\"ts\":\"2020-01-01T00:00:01+00:00\",\"tool\":\"write_file\",\"path\":\"e.txt\",\"full\":\"{full}\",\"bytes\":300000000,\"file\":\"{file}\"}}\n");
            Load();
            Assert.True(File.Exists(victim));
        }
        finally { try { Directory.Delete(elsewhere, recursive: true); } catch { } }
    }
}
