using Gatto.Repl;
using Gatto.Roles;

namespace Gatto.Cli;

//the confirm screen for a model swap that restarts the server, so the user chooses the re-prefill before it starts. the consent facts are on this screen too
internal static class SwapConfirm
{
    public const string Go = "go";

    //the incoming model's auto_serve. null means this screen shows the concrete ask too, and answering it is the consent for this swap

    //this screen composes and does not decide whether it is shown: whether the server must change is ServeManager.Serving's answer

    //the model the running server actually holds, from serve.json. this is what gets unloaded, so it is what the screen names

    //asked only when this model is the one the server holds. the switch restarts the server and the conversation is read again, so the screen owes that cost

    //the one home for the question. two surfaces ask it, the in-session confirm and the wizard's done step, and two spellings would drift by a word
    public static string QuantQuestion(string modelId, string quant) =>
        $"Run {modelId} on {quant} from now on?";

    public static WizardAsk QuantSwitch(string modelId, string quant, string servingQuant) =>
        new(
            QuantQuestion(modelId, quant),
            [
                new SelectOption($"Yes, switch this model to {quant}"),
                new SelectOption($"No, keep {servingQuant}; {quant} stays on disk"),
            ],
            FreeTextLabel: null,
            //one row, because the restart and its cost are a single consequence and splitting them invites a reader to accept the first half
            BodyRows:
            [
                new BodyRow($"{modelId} is the model you're on, so its server restarts with {quant}. "
                    + "Everything said so far has to be read again before it can answer, the first "
                    + "message after the switch will take longer."),
            ]);

    //asked while this model is serving, since gatto cannot tell which file is loaded. neither answer deletes the file, it only changes the listing
    public static WizardAsk QuantRemove(string modelId, string quant) =>
        new(
            $"Forget {quant} from {modelId}?",
            [
                new SelectOption($"Yes, {modelId} stops listing {quant}"),
                new SelectOption($"No, keep {quant} on the list"),
            ],
            FreeTextLabel: null,
            BodyRows:
            [
                //the row names the running server and not the file it loaded, because that would be a claim about a fact nobody checked
                new BodyRow($"{modelId}'s server is running, and whatever it loaded goes on "
                    + "answering until it restarts."),
                new BodyRow($"The file itself stays on disk either way. This only changes what "
                    + $"{modelId} lists."),
            ]);

    //the one home for this question. the prompter confirm and the wizard's in-session done step both ask it, so a second spelling would drift
    public static string SwitchQuestion(string incomingId) => $"Switch to {incomingId}?";

    //the two answers, in one home. the Confirmed predicate reads the first word of whichever label comes back, so the labels must not drift from it
    public static IReadOnlyList<SelectOption> SwitchOptions(
        string outgoingId, string incomingId, string servingModelId) =>
        [
            new SelectOption($"Yes, unload {servingModelId} and load {incomingId}"),
            //the decline says what stays: the session's model when the server holds it, otherwise the model the server holds
            new SelectOption(outgoingId == servingModelId
                ? $"No, stay on {outgoingId}"
                : $"No, keep {servingModelId} running"),
        ];

    //the two consequence rows in one home: what happens to the machine and what the conversation pays. the wizard draws these same rows
    public static IReadOnlyList<BodyRow> Consequences(string incomingId, string servingModelId) =>
        [
            new BodyRow($"{servingModelId}'s server stops and {incomingId}'s starts in its place."),
            //no duration is promised, since how long a re-read takes depends on the conversation and the machine
            new BodyRow("Everything said so far has to be read again by the new model "
                + "before it can answer, the first message after the switch will take longer."),
        ];

    //the prompter form and the wizard's done step are one composition, so this delegates. the concrete block is gone, since absent now means yes and nothing asks
    public static WizardAsk Compose(Model outgoing, Model incoming, string servingModelId) =>
        InSessionDone(outgoing.Id, incoming.Id, servingModelId);

    //the ask only, because the done step already drew the model's file, size and config path. the swap itself stays with GattoApp
    public static WizardAsk InSessionDone(string outgoingId, string incomingId, string servingModelId) =>
        new(
            SwitchQuestion(incomingId),
            SwitchOptions(outgoingId, incomingId, servingModelId),
            FreeTextLabel: null,
            BodyRows: [.. Consequences(incomingId, servingModelId)]);


    //the line spoken after a wizard switch. the session has to have moved, so confirmed alone is not enough. a switch that ends on the same model says nothing
    public static string? SwitchNotice(bool confirmed, string? outgoingId, string incomingId) =>
        confirmed && !string.Equals(outgoingId, incomingId, StringComparison.OrdinalIgnoreCase)
            ? $"now on {incomingId}, everything said so far is read again on your next message"
            : null;

    //maps the prompter's answer back to a decision. null is an Esc, a stay, because a swap nobody confirmed must not happen
    public static bool Confirmed(string? answer) =>
        answer is not null && answer.StartsWith("Yes", StringComparison.Ordinal);
}
