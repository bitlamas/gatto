using Gatto.Roles;

namespace Gatto.Tests.Roles;

//what the manager reported, kept as facts. a test here pins the event, the sentences are the corpus's oracle
internal sealed class RecordingServeListener : IServeListener
{
    private readonly List<(string Name, object?[] Args)> _calls = [];

    public IReadOnlyList<string> Names => [.. _calls.Select(c => c.Name)];

    public bool Heard(string name) => _calls.Any(c => c.Name == name);

    //the arguments of the first call to name. it throws when the event never fired, so a test cannot read a fact out of an event that did not happen
    public object?[] Facts(string name) =>
        _calls.FirstOrDefault(c => c.Name == name).Args
            ?? throw new Xunit.Sdk.XunitException(
                $"no '{name}' was reported. what arrived: " + (Names.Count == 0 ? "nothing" : string.Join(", ", Names)));

    private void Add(string name, params object?[] args) => _calls.Add((name, args));

    public void Refused(string model, int pid) => Add(nameof(Refused), model, pid);
    public void Ready(string model, int port, int pid) => Add(nameof(Ready), model, port, pid);
    public void Loading(TimeSpan elapsed) => Add(nameof(Loading), elapsed);
    public void StillLoading(string model, int pid) => Add(nameof(StillLoading), model, pid);
    public void ServingForeground(string model, int pid) => Add(nameof(ServingForeground), model, pid);
    public void Exited(string model) => Add(nameof(Exited), model);
    public void Stopping(string model, int pid) => Add(nameof(Stopping), model, pid);
    public void KillFailed(int pid, string message) => Add(nameof(KillFailed), pid, message);

    public void DiedDuringLoad(IReadOnlyList<string> tail, bool logAbsent) =>
        Add(nameof(DiedDuringLoad), tail, logAbsent);

    public void NotServingOnStop() => Add(nameof(NotServingOnStop));
    public void Stopped(string model, int pid) => Add(nameof(Stopped), model, pid);
    public void NotOurs(int pid, string processName) => Add(nameof(NotOurs), pid, processName);
    public void StaleOnStop(int pid, string model) => Add(nameof(StaleOnStop), pid, model);
    public void NotServingOnStatus() => Add(nameof(NotServingOnStatus));
    public void StaleOnStatus(int pid, string model) => Add(nameof(StaleOnStatus), pid, model);

    public void Status(string model, int port, int pid, bool healthy) =>
        Add(nameof(Status), model, port, pid, healthy);

    public void PumpFault(ServeLogFault fault) => Add(nameof(PumpFault), fault);
}
