namespace Gatto.Roles;

//what a serve run reports, one method per event, in facts rather than sentences. two events that read differently are two methods
public interface IServeListener
{
    //a start refused while a server this home owns is still alive
    void Refused(string model, int pid);

    //a detached start answered its health probe
    void Ready(string model, int port, int pid);

    //one detached poll went unanswered, with the time the poll measured
    void Loading(TimeSpan elapsed);

    //the detached poll ran out while the server was still coming up, and the child is left running with its record kept
    void StillLoading(string model, int pid);

    //a foreground start, where the child holds the terminal until it exits or the user stops it
    void ServingForeground(string model, int pid);

    //the foreground child went down on its own
    void Exited(string model);

    //the user stopped a foreground child, reported before the kill is attempted
    void Stopping(string model, int pid);

    //a kill that failed, from either path, so the same failure is never worded two ways
    void KillFailed(int pid, string message);

    //the child died before it became ready, and an absent log is a different fact from an empty one (a foreground start writes none)
    void DiedDuringLoad(IReadOnlyList<string> tail, bool logAbsent);

    //stop found no record at all
    void NotServingOnStop();

    //the server this home owns is down and its record is gone, the same event as a foreground stop
    void Stopped(string model, int pid);

    //the pid is alive and belongs to something else now, so it is never killed and only the record goes
    void NotOurs(int pid, string processName);

    //stop found a record whose pid is gone
    void StaleOnStop(int pid, string model);

    //status found no record at all
    void NotServingOnStatus();

    //status found a record whose pid is not a running llama-server
    void StaleOnStatus(int pid, string model);

    //status found the recorded server gone: the code when a foreground start saw it end, and the log tail only for a detached run, which owns serve.log
    void DiedOnStatus(string model, int pid, int? exitCode, string? exitedAt, IReadOnlyList<string>? tail) =>
        StaleOnStatus(pid, model);

    //status found the server this home owns, with the answer to the health probe
    void Status(string model, int port, int pid, bool healthy);

    //the rolling log could not be kept, and which of the two happened is all the words need (the server is unaffected either way)
    void PumpFault(ServeLogFault fault);
}

//what went wrong with the rolling log, a fact that the layer which paints it turns into a sentence
public enum ServeLogFault
{
    NotOpened,
    NotWritten,
}

//a listener for a caller that reads an outcome and prints nothing, kept in this layer because callers on both sides need it
public sealed class NullServeListener : IServeListener
{
    public static readonly IServeListener Instance = new NullServeListener();

    public void Refused(string model, int pid) { }
    public void Ready(string model, int port, int pid) { }
    public void Loading(TimeSpan elapsed) { }
    public void StillLoading(string model, int pid) { }
    public void ServingForeground(string model, int pid) { }
    public void Exited(string model) { }
    public void Stopping(string model, int pid) { }
    public void KillFailed(int pid, string message) { }
    public void DiedDuringLoad(IReadOnlyList<string> tail, bool logAbsent) { }
    public void NotServingOnStop() { }
    public void Stopped(string model, int pid) { }
    public void NotOurs(int pid, string processName) { }
    public void StaleOnStop(int pid, string model) { }
    public void NotServingOnStatus() { }
    public void StaleOnStatus(int pid, string model) { }
    public void Status(string model, int port, int pid, bool healthy) { }
    public void PumpFault(ServeLogFault fault) { }
}
