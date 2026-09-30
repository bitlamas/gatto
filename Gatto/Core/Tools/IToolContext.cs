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
}
