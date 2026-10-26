using System.Linq;

namespace Gatto.Core.Tools;

//one ask_user choice, and Recommended is advisory: the renderer marks at most the first one flagged and never reorders
public sealed record AskOption(string Label, string? Description = null, bool Recommended = false)
{
    public static implicit operator AskOption(string label) => new(label);
}

public sealed record AskQuestion(
    string Question, string Header, IReadOnlyList<AskOption> Options, bool MultiSelect)
{
    //lets a caller pass plain labels, the implicit string conversion does not lift over lists, and its caller is the bundled ask_user host
    public AskQuestion(string question, string header, IReadOnlyList<string> options, bool multiSelect)
        : this(question, header, options.Select(o => (AskOption)o).ToList(), multiSelect) { }
}

public sealed record AskAnswer(string Header, IReadOnlyList<string> Selected);

public interface IUserPrompter
{
    Task<IReadOnlyList<AskAnswer>> AskAsync(IReadOnlyList<AskQuestion> questions, CancellationToken ct);
}

public interface IToolContext
{
    string Cwd { get; }
    string HomePath { get; }
    IUserPrompter? Prompter { get; }

    //the live session a shell child is told about, read at each call since /new and a resume change it. null where no session exists
    Gatto.Core.SessionIdentity? Session => null;

    //the transcript files whose turns this session already holds, the one being written and the one resumed from, so a tool can leave the caller's own turns out. empty where no session exists
    IReadOnlyList<string> SessionFiles => [];

    //the process's sandbox, read at each call so a run_agent child's shell is confined through its parent's context. null where none exists
    Gatto.Core.Sandbox.SandboxState? Sandbox => null;

    //the background tasks a shell call may start, armed by the rich face alone. null everywhere else, where the flag runs in the foreground
    BackgroundTasks? Tasks => null;

    //the trace and the open tool.exec span, which the loop hands a tool per call only when tracing. no ambient span exists
    Trace Trace => Trace.Off;
    long TraceParent => 0;
}
