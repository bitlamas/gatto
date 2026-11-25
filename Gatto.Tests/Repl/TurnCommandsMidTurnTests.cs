//the commands the composer answers while a turn runs: their rows and state move before the turn may end, and the waiting forms are answered only after it
using System.Text.Json;
using Gatto.Core;
using Gatto.Core.Client;
using Gatto.Core.Loop.Permissions;
using Gatto.Core.Tools;
using Gatto.Repl;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Repl;

public sealed class TurnCommandsMidTurnTests : IDisposable
{
    private readonly string _cwd = Directory.CreateTempSubdirectory("gatto-turn-cmd-").FullName;

    public void Dispose() { try { Directory.Delete(_cwd, recursive: true); } catch { } }

    //types each line once the turn runs, waits for the settle, records what the session reads then, and hands it the quit line
    private sealed class TypeTool(RichReplHarness h, string[] lines, Func<bool>? settle, Func<string> seen, List<string> record) : ITool
    {
        public string Name => "hold";
        public string Description => "holds the turn open";
        public JsonElement ParametersSchema => JsonDocument.Parse("""{"type":"object"}""").RootElement;

        public async Task<ToolResult> ExecuteAsync(JsonElement args, IToolContext ctx, CancellationToken ct)
        {
            var done = true;
            foreach (var line in lines)
            {
                h.Keys.Line(line);
                //the pump releases a key when it samples next, so the keys being taken only says the composer read them, not that any arm ran
                done &= await h.UntilAsync(() => h.Keys.Handled, Waits.Bound);
            }
            //the settle is the synchronous effect of one answered line, the turn cannot end while this tool still waits
            if (settle is not null) done &= await h.UntilAsync(settle, Waits.Bound);
            lock (record) record.Add(done ? seen() : "a line or its settle never came");
            h.Keys.Line("/quit");
            return new ToolResult("held");
        }
    }

    //runs one turn the tool holds open, typing the lines mid-turn, and hands back what was true when they settled plus everything written
    private async Task<(List<string> Record, string Written)> RunMidTurn(
        string[] lines, Func<string> seen, Func<bool>? settle = null,
        WildState? wild = null, Action<bool>? persistWild = null,
        Func<string, RoleSwitchResult>? switchRole = null)
    {
        var record = new List<string>();
        var reg = new ToolRegistry();
        var h = new RichReplHarness(_cwd, tools: reg, wild: wild, persistWild: persistWild, switchRole: switchRole);
        reg.Register(new TypeTool(h, lines, settle, seen, record));
        h.Client.EnqueueTurn(new StreamEvent.ToolCallReady(new ToolCall("c1", "hold", "{}")), new StreamEvent.Finished("tool_calls", null));
        h.Client.EnqueueTurn(new StreamEvent.TextDelta("second round"), new StreamEvent.Finished("stop", null));
        h.Keys.Line("go");
        await h.RunAsync();
        return (record, h.Written());
    }

    //a row said during the turn lands in what was written before the turn's last words, and one that waited appears only after them
    private static void AssertSaidMidTurn(string written, string row)
    {
        var turnEnd = written.IndexOf("second round", StringComparison.Ordinal);
        Assert.True(turnEnd >= 0, "the held turn never reached its last words");
        var at = written.IndexOf(row, StringComparison.Ordinal);
        Assert.True(at >= 0 && at < turnEnd, $"the row {row} is at {at}, the turn's last words at {turnEnd}");
    }

    private static void AssertSaidAfterTurn(string written, string row)
    {
        var turnEnd = written.IndexOf("second round", StringComparison.Ordinal);
        Assert.True(turnEnd >= 0, "the held turn never reached its last words");
        Assert.True(written.IndexOf(row, StringComparison.Ordinal) > turnEnd, $"the row {row} was said before the turn ended");
    }

    //the bare toggle is true and its row is committed while the turn still runs
    [Fact]
    public async Task WILD_MID_TURN_FLIPS_THE_STATE_AND_SAYS_ITS_ROW_BEFORE_THE_TURN_ENDS()
    {
        var wild = new WildState();
        var (record, written) = await RunMidTurn(["/wild"], () => "taken", () => wild.On, wild: wild);

        Assert.Equal(["taken"], record);
        AssertSaidMidTurn(written, "wild mode on");
    }

    //the persisting form calls the persist callback with true while the turn still runs
    [Fact]
    public async Task WILD_ALWAYS_MID_TURN_PERSISTS_THE_GRANT_BEFORE_THE_TURN_ENDS()
    {
        var wild = new WildState();
        var persisted = new List<bool>();
        var (record, written) = await RunMidTurn(["/wild always"], () => "taken",
            () => { lock (persisted) return persisted.Contains(true) && wild.On; },
            wild: wild, persistWild: on => { lock (persisted) persisted.Add(on); });

        Assert.Equal(["taken"], record);
        AssertSaidMidTurn(written, "persisted for this project");
    }

    //the inverse of the toggle takes wild off while the turn still runs
    [Fact]
    public async Task SAFE_MID_TURN_TURNS_WILD_OFF_AND_SAYS_ITS_ROW_BEFORE_THE_TURN_ENDS()
    {
        var wild = new WildState { On = true };
        var (record, written) = await RunMidTurn(["/safe"], () => "taken", () => !wild.On, wild: wild);

        Assert.Equal(["taken"], record);
        AssertSaidMidTurn(written, "wild mode off");
    }

    //the three read-only rows land while the turn still runs, and the bare toggle typed last settles every earlier arm past
    [Fact]
    public async Task ROLE_SANDBOX_AND_MODEL_QUANT_SAY_THEIR_ROWS_BEFORE_THE_TURN_ENDS()
    {
        var wild = new WildState();
        var (record, written) = await RunMidTurn(["/role", "/sandbox", "/model quant", "/wild"], () => "taken", () => wild.On, wild: wild);

        Assert.Equal(["taken"], record);
        AssertSaidMidTurn(written, "role: default");
        AssertSaidMidTurn(written, "sandbox off");
        AssertSaidMidTurn(written, "this session has no model profile to read files from");
    }

    //the reader-less allowance sentence lands while the turn still runs, and the bare toggle typed last settles the earlier arm
    [Fact]
    public async Task USAGE_WITH_NO_READER_SAYS_ITS_ROW_BEFORE_THE_TURN_ENDS()
    {
        var wild = new WildState();
        var (record, written) = await RunMidTurn(["/usage", "/wild"], () => "taken", () => wild.On, wild: wild);

        Assert.Equal(["taken"], record);
        AssertSaidMidTurn(written, Gatto.Repl.Repl.LocalAllowance);
    }

    //the forms with an argument wait in the queue and are answered only once the turn has ended
    [Fact]
    public async Task THE_WAITING_FORMS_ARE_ANSWERED_ONLY_AFTER_THE_TURN()
    {
        Func<string, RoleSwitchResult> switchRole = name => name == "coder"
            ? new RoleSwitchResult(true, "coder", "test-model", "coder system", null)
            : new RoleSwitchResult(false, "", null, null, "unknown role in the test");

        var (record, written) = await RunMidTurn(["/role coder", "/sandbox on", "/model other"], () => "taken", switchRole: switchRole);

        Assert.Equal(["taken"], record);
        AssertSaidAfterTurn(written, "role: coder");
        AssertSaidAfterTurn(written, "sandbox off");
        AssertSaidAfterTurn(written, Gatto.Repl.Repl.ModelSwitchUnavailable);
    }
}
