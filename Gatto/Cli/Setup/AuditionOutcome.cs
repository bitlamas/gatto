namespace Gatto.Cli.Setup;

//what came of the audition, and the three answers are separate: could not run is not a pass or a fail
internal enum AuditionOutcome
{
    //measured, and it drove the tools
    Passed,
    //measured, and it did not, so warn and keep going if the user insists
    Failed,
    //not measured, our side broke, so this is neither a pass nor a fail
    CouldNotRun,
}

//what the check measured, with each field nullable and dropped when absent. the task counts come from the verdict, since a pass can be four of five
internal sealed record AuditionFacts(
    string? Quant, string? Sampling, string? Thinking, double? TokensPerSecond,
    int? TasksPassed = null, int? TasksTotal = null);

//why a check could not run, typed, while the verdict stays at three. every arm still reports CouldNotRun and the sentence is composed at the face
internal abstract record CheckStumble
{
    //the file is shorter than its own header says, so the row says incomplete. folder keeps its trailing separator, and the byte count is a floor
    internal sealed record Incomplete(
        string File, string Folder, long FoundBytes, long ExpectedBytes) : CheckStumble;

    //the server died before the model loaded, so the screen quotes its last lines and claims no cause
    internal sealed record Exited(IReadOnlyList<string> LastLines, string LogPath) : CheckStumble;

    //the server died mid-battery, so the screen keeps the tasks that ran and names no cause. an empty log still gets a row, since the absence is the fact
    internal sealed record Stopped(IReadOnlyList<string> LastLines) : CheckStumble;

    //the server could not be made this model's, and the reason is the probe's own words. keep them as given, this seam must not compose the sentence
    internal sealed record Unavailable(string Reason) : CheckStumble;
}

//the verdict and its evidence travel as one. the Recorded field is set only when the badge file was written, and Tasks holds the runner's records
internal sealed record AuditionCheck(
    AuditionOutcome Outcome, AuditionFacts? Facts = null, DateOnly? Recorded = null,
    IReadOnlyList<Gatto.Roles.Audition.AuditionTaskResult>? Tasks = null,
    CheckStumble? Stumble = null);
