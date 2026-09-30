using System.Text;

namespace Gatto.Core.Client;

//the seam the client tees its outbound body through, null means off and the flag is read once at composition
public interface IWireLog
{
    //the body exactly as it will be sent, implementations must not modify it and the caller guards a throw
    void Record(string body);
}

//writes each outbound chat body verbatim to its own file, one per request so N can be diffed against N-1 (off unless GATTO_DEBUG_WIRE=1)
public sealed class WireLog : IWireLog
{
    //no BOM, the file's only claim is that it holds what went on the wire
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string directory;
    private int sequence;

    public WireLog(string directory)
    {
        this.directory = directory;
        Directory.CreateDirectory(directory);
    }

    public void Record(string body)
    {
        var n = Interlocked.Increment(ref sequence);
        //pad to five digits, so the names sort in send order (the counter is shared across subagents and a long run reaches four digits)
        File.WriteAllText(Path.Combine(directory, $"{n:D5}.json"), body, Utf8NoBom);
    }

    //takes the flag's value so the decision stays a pure function a test can drive without mutating the environment
    public static WireLog? FromFlag(string? flag, string directory) =>
        flag == "1" ? new WireLog(directory) : null;

    //one folder per run (the sequence restarts at 00001, so a shared folder would overwrite the earlier dumps)
    public static string RunDirectory(string home, DateTimeOffset now, int pid) =>
        Path.Combine(home, "wire",
            $"{now.UtcDateTime:yyyyMMdd-HHmmss}-{pid.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
}
