using Gatto.Core.Client;
using Gatto.Core.Loop;
using Gatto.Core.Loop.Permissions;

namespace Gatto.Tests;

//the gate's step 0: no tool writes gatto's own folder in any mode, and with --yes a tool stays in the working folder or a grant
public sealed class WorkspaceGateTests : IDisposable
{
    private readonly string _base = Directory.CreateTempSubdirectory("gatto-wsgate-").FullName;
    private string Project => Directory.CreateDirectory(Path.Combine(_base, "proj")).FullName;
    private string Home => Directory.CreateDirectory(Path.Combine(_base, "home", ".gatto")).FullName;
    private string Elsewhere => Directory.CreateDirectory(Path.Combine(_base, "elsewhere")).FullName;
    public void Dispose() { try { Directory.Delete(_base, recursive: true); } catch (IOException) { } }

    //answers yes to everything, so a refusal can only come from the boundary, and counts the asks
    private sealed class YesPrompter : IPermissionPrompter
    {
        public int Asked { get; private set; }
        public PermissionDecision AskWithReason(PermissionRequest request) { Asked++; return new(PermissionAnswer.Once); }
    }

    private PermissionGate Gate(bool autoYes, PermissionStore? store = null, WildState? wild = null, string? root = null, YesPrompter? prompter = null) =>
        new(store ?? PermissionStore.InMemory(root ?? Project), prompter ?? new YesPrompter(), autoYes, wild: wild, home: Home);

    private static Task Call(PermissionGate gate, string tool, object args) =>
        gate.CheckAsync(new HookPayload(Call: new ToolCall("c1", tool, System.Text.Json.JsonSerializer.Serialize(args))));

    private const string HomeWords = "that path is in gatto's own folder, which a tool writes only when the user says yes, and nobody is asked in this run";
    private const string ShellHomeWords = "that command names gatto's own folder, which a command touches only when the user says yes, and nobody is asked in this run; read_file can read a file there";
    private const string OutsideWords = "that path is outside the working folder, and with --yes a tool works only inside it";
    private const string LeavesWords = "that command reaches outside the working folder, and with --yes a command works only inside it";

    [Fact]
    public async Task With_yes_a_read_outside_the_working_folder_is_refused_and_one_inside_or_granted_passes()
    {
        var gate = Gate(autoYes: true);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Call(gate, "read_file", new { path = Path.Combine(Elsewhere, "x.txt") }));
        Assert.Equal(OutsideWords, ex.Message);
        await Call(gate, "read_file", new { path = "notes.txt" });
        await Call(gate, "glob", new { pattern = "*.cs" });

        var store = PermissionStore.InMemory(Project);
        store.GrantWriteDir(Elsewhere, persist: false);
        await Call(Gate(autoYes: true, store), "read_file", new { path = Path.Combine(Elsewhere, "x.txt") });
    }

    [Fact]
    public async Task With_yes_a_grep_rooted_elsewhere_and_a_write_elsewhere_are_refused()
    {
        var gate = Gate(autoYes: true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Call(gate, "grep", new { pattern = "x", root = Elsewhere }));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Call(gate, "write_file", new { path = Path.Combine(Elsewhere, "x.txt"), content = "x" }));
    }

    [Fact]
    public async Task With_yes_a_command_reaching_outside_is_refused_and_one_inside_passes()
    {
        var gate = Gate(autoYes: true);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Call(gate, "shell", new { command = "Get-ChildItem C:\\ -Recurse" }));
        Assert.Equal(LeavesWords, ex.Message);
        await Call(gate, "shell", new { command = "Get-ChildItem . -Recurse" });
    }

    //with --yes the environment is outside by every spelling, the provider drive and the braced variable as much as $env:
    [Theory]
    [InlineData("Write-Output $env:USERPROFILE")]
    [InlineData("Get-Item Env:USERPROFILE")]
    [InlineData("gci env:")]
    [InlineData("Write-Output $Env:USERPROFILE")]
    [InlineData("Write-Output ${env:USERPROFILE}")]
    [InlineData("[System.Environment]::GetEnvironmentVariable('USERPROFILE')")]
    public async Task With_yes_a_command_reading_the_environment_is_refused(string command)
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Call(Gate(autoYes: true), "shell", new { command }));
        Assert.Equal(LeavesWords, ex.Message);
    }

    [Fact]
    public async Task Without_yes_a_read_outside_the_working_folder_passes()
    {
        await Call(Gate(autoYes: false), "read_file", new { path = Path.Combine(Elsewhere, "x.txt") });
        await Call(Gate(autoYes: false), "shell", new { command = "Get-ChildItem C:\\ -Recurse" });
    }

    [Fact]
    public async Task A_write_into_gattos_folder_is_refused_with_yes_and_asked_in_wild_mode_and_past_a_grant()
    {
        var target = new { path = Path.Combine(Home, "gatto.json"), content = "{}" };
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Call(Gate(autoYes: true), "write_file", target));
        Assert.Equal(HomeWords, ex.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Call(Gate(autoYes: true), "edit_file", new { path = Path.Combine(Home, "gatto.json"), old_string = "a", new_string = "b" }));

        var wild = new YesPrompter();
        await Call(Gate(autoYes: false, wild: new WildState { On = true }, prompter: wild), "write_file", target);
        Assert.Equal(1, wild.Asked);

        var granted = new YesPrompter();
        var store = PermissionStore.InMemory(Project);
        store.GrantWriteDir(Home, persist: false);
        await Call(Gate(autoYes: false, store, prompter: granted), "write_file", target);
        Assert.Equal(1, granted.Asked);

        //reading gatto's folder is no write, so outside --yes it passes unasked
        var reader = new YesPrompter();
        await Call(Gate(autoYes: false, prompter: reader), "read_file", new { path = Path.Combine(Home, "gatto.json") });
        Assert.Equal(0, reader.Asked);
    }

    [Fact]
    public async Task A_command_naming_gattos_folder_is_refused_with_yes_and_asked_without()
    {
        var command = $"Set-Content '{Path.Combine(Home, "gatto.json")}' '{{}}'";
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Call(Gate(autoYes: true), "shell", new { command }));
        Assert.Equal(ShellHomeWords, ex.Message);

        var prompter = new YesPrompter();
        await Call(Gate(autoYes: false, wild: new WildState { On = true }, prompter: prompter), "shell", new { command });
        Assert.Equal(1, prompter.Asked);
    }

    [Fact]
    public async Task The_memory_tool_passes_with_yes()
    {
        await Call(Gate(autoYes: true), "memory_write", new { slug = "x", content = "y" });
    }

    //gatto launched inside its own folder: the working folder does not open the home to writes
    [Fact]
    public async Task A_working_folder_inside_gattos_folder_does_not_open_it_to_writes()
    {
        var inside = Directory.CreateDirectory(Path.Combine(Home, "extensions")).FullName;
        var gate = Gate(autoYes: true, root: inside);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Call(gate, "write_file", new { path = "x.csx", content = "x" }));
        Assert.Equal(HomeWords, ex.Message);
    }
}
