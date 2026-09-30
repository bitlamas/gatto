using Gatto.Core;
using Gatto.Repl.Input;

namespace Gatto.Repl;

//the three answers to the attach question, the middle one stops the nag in this project
public enum ConfirmAttachAnswer
{
    //send this one
    Yes,
    //send this one and stop asking in this project
    YesAlways,
    //send nothing, the composer comes back with everything attached, and Esc means this too
    No,
}

//builds the question, kept out of the Repl so the wording is testable without a terminal and the count is computed once
public static class ConfirmAttach
{
    //the prompt's rows, the token figure is approximate and comes from the same constant the ♯ notices use, so the two can't disagree
    public static SelectSpec Spec(int imageCount)
    {
        var tokens = imageCount * ImageAttach.EstimatedTokensPerImage;
        return new SelectSpec(
            TitleRows: new TitleRow[] { new("attach images") },
            Question: new PromptQuestion(
                $"this message attaches {Plural.Of(imageCount, "image")} (~{Approx(tokens)} tokens) — send it?"),
            Options: new SelectOption[]
            {
                new("Yes"),
                new("Yes, and don't ask again in this project"),
                new("No — keep editing"),
            },
            FooterHint: "Esc to keep editing");
    }

    //the Esc key and any unexpected outcome mean No, the answer that sends nothing cannot cost anything
    public static ConfirmAttachAnswer Interpret(SelectOutcome outcome) => outcome switch
    {
        SelectOutcome.Chosen { Index: 0 } => ConfirmAttachAnswer.Yes,
        SelectOutcome.Chosen { Index: 1 } => ConfirmAttachAnswer.YesAlways,
        _ => ConfirmAttachAnswer.No,
    };

    private static string Approx(int n) =>
        n >= 1000
            ? (n / 1000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "k"
            : n.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
