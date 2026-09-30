namespace Gatto.Roles.Audition;

//the kind of thing an audition is reporting
internal enum AuditionStage
{
    //something the user has to keep, like starting or reusing a server, so it belongs in the scrollback
    Note,

    //a battery task is about to run, the live surface a task needs because it takes tens of seconds
    Started,

    //a battery task has been graded
    Done,

    //the server has answered nothing and the wait passed the note interval, only the wizard's own server start emits this
    Loading,

    //a server was already serving this model, so the run reused it, and only the CLI caller wants to hear about it
    Reusing,
}

//one fact while an audition runs, typed rather than a string so each caller decides how it looks
internal sealed record AuditionProgress(
    AuditionStage Stage,
    string Text = "",
    string TaskId = "",
    string Label = "",
    int Index = 0,
    int Total = 0,
    bool Pass = false)
{
    public static AuditionProgress Note(string text) => new(AuditionStage.Note, Text: text);

    public static AuditionProgress Reusing(string text) => new(AuditionStage.Reusing, Text: text);

    //no text, the stage itself is the whole fact
    public static AuditionProgress Loading() => new(AuditionStage.Loading);

    public static AuditionProgress Started(BatteryTask task, int index, int total) =>
        new(AuditionStage.Started, TaskId: task.Id, Label: task.Label, Index: index, Total: total);

    public static AuditionProgress Done(BatteryTask task, int index, int total, bool pass) =>
        new(AuditionStage.Done, TaskId: task.Id, Label: task.Label, Index: index, Total: total, Pass: pass);
}
