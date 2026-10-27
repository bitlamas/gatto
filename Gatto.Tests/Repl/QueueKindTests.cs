using Gatto.Repl;
using Gatto.Repl.Commands;

namespace Gatto.Tests.Repl;

//the mark a queued text gets at the composer decides whether the loop may deliver it mid-turn, so a command or an attachment never goes as plain text
public class QueueKindTests : IDisposable
{
    private readonly string _cwd = Directory.CreateTempSubdirectory("gatto-queue-kind-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_cwd, recursive: true); } catch { }
    }

    private static readonly byte[] Png = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0x07];

    private static IReadOnlyList<SlashCommands.Cmd> Known() =>
        [.. SlashCommands.All, new SlashCommands.Cmd("/undo", "[n|path]", "list snapshots", Source: "undo.csx")];

    [Fact]
    public void A_BUILT_IN_COMMAND_IS_A_COMMAND()
    {
        Assert.Equal(EntryKind.Command, Gatto.Repl.Repl.QueueKind("/compact", Known(), pasted: false, _cwd));
        Assert.Equal(EntryKind.Command, Gatto.Repl.Repl.QueueKind("  /role coder ", Known(), pasted: false, _cwd));
    }

    [Fact]
    public void A_REGISTERED_COMMAND_IS_A_COMMAND()
    {
        Assert.Equal(EntryKind.Command, Gatto.Repl.Repl.QueueKind("/undo 2", Known(), pasted: false, _cwd));
    }

    [Fact]
    public void A_MESSAGE_CARRYING_A_PENDING_PASTE_IS_AN_ATTACHMENT()
    {
        Assert.Equal(EntryKind.Attachment, Gatto.Repl.Repl.QueueKind("what is in [Image #1]", Known(), pasted: true, _cwd));
    }

    [Fact]
    public void A_MESSAGE_NAMING_AN_IMAGE_FILE_IS_AN_ATTACHMENT()
    {
        File.WriteAllBytes(Path.Combine(_cwd, "shot.png"), Png);
        Assert.Equal(EntryKind.Attachment, Gatto.Repl.Repl.QueueKind("look at shot.png please", Known(), pasted: false, _cwd));
    }

    [Fact]
    public void A_MESSAGE_NAMING_AN_AMBIGUOUS_IMAGE_IS_AN_ATTACHMENT_SO_THE_DISPATCHER_REFUSES_IT()
    {
        File.WriteAllBytes(Path.Combine(_cwd, "shot.png"), Png);
        File.WriteAllBytes(Path.Combine(_cwd, "shot.jpg"), Png);
        Assert.Equal(EntryKind.Attachment, Gatto.Repl.Repl.QueueKind("look at shot.gif", Known(), pasted: false, _cwd));
    }

    [Fact]
    public void PLAIN_TEXT_IS_PLAIN()
    {
        Assert.Equal(EntryKind.Plain, Gatto.Repl.Repl.QueueKind("also check the tests folder", Known(), pasted: false, _cwd));
        Assert.Equal(EntryKind.Plain, Gatto.Repl.Repl.QueueKind("a missing.png nobody has", Known(), pasted: false, _cwd));
    }
}
