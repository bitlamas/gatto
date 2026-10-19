namespace Gatto.Core;

//the files that share a session's folder and stem, so every session glob leaves out the same set
public static class SessionFiles
{
    public const string Ledger = "ledger";
    public const string Trace = "trace";

    //true for a ledger or trace sidecar, which also ends in .jsonl and would otherwise be read as a session
    public static bool IsSidecar(string path) =>
        path.EndsWith("." + Ledger + ".jsonl", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith("." + Trace + ".jsonl", StringComparison.OrdinalIgnoreCase);

    //the sidecar of the given kind beside a session file
    public static string Sidecar(string sessionPath, string kind) =>
        Path.Combine(Path.GetDirectoryName(sessionPath)!, Path.GetFileNameWithoutExtension(sessionPath) + "." + kind + ".jsonl");
}
