using Gatto.Repl;
using Gatto.Roles;

namespace Gatto.Cli;

//the serving side of /model, split out of GattoApp so a test can drive it. the caller arms on a null return
internal static class ModelSwitchDeed
{
    //one Swap delegate covers both starting the incoming model and restoring the one the server held. its first argument is the id the server held
    internal sealed record Seams(
        Func<string, Model, bool> Swap,
        Func<Model, bool> Start);

    //a null return means the caller arms. confirmed comes from the wizard that already asked, so only the swap confirm is skipped
    internal static ModelSwitchResult? Run(
        ModelSwitchOutcome outcome,
        Model? outgoing,
        ServeManager manager,
        Func<WizardAsk, string?>? ask,
        bool confirmed,

        Action<string> warn,
        Seams seams)
    {
        //the confirm is where the re-prefill cost becomes the user's choice, and one screen draws it even when the target was never consented
        ModelSwitchResult? ConfirmAndSwap(RunningInfo serving)
        {
            //check confirmed first, so the wizard never composes the confirm screen, a discarded compose still reads the outgoing model
            if (!confirmed && outgoing is not null && ask is not null
                && !SwapConfirm.Confirmed(ask(SwapConfirm.Compose(
                    outgoing, outcome.Model!, serving.Model))))
                //declining the confirm changes nothing, the session keeps the model it has
                return new ModelSwitchResult(false, outcome.ModelId, null, null,
                    serving.Model == outgoing.Id
                        ? $"staying on {outgoing.Id}"
                        : $"keeping {serving.Model} running",
                    null, null, null);

            if (!seams.Swap(serving.Model, outcome.Model!))
                //the failed swap leaves the session untouched, and the message names the target because a bool can't say whether the restore worked
                return new ModelSwitchResult(false, outcome.ModelId, null, null,
                    $"stayed on {outgoing?.Id ?? "the current model"}, and tried to put {serving.Model} back on the server, see above",
                    null, null, null);

            return null;
        }

        ModelSwitchResult? ArmWithNothingServing()
        {
            //nothing is serving after a model is added, so the launch's own decision table decides whether to start one
            var startHere = AutoServe.Decide(
                interactive: true, gattoServesThisEndpoint: true, serverAnswered: false,
                consent: outcome.Model!.Profile.AutoServe,
                canAsk: ask is not null);

            //the decision table is the only thing deciding, so this site and the launch can't drift into two policies about one key
            if (startHere == AutoServeAction.Serve)
                seams.Start(outcome.Model!);
            else
                //declining a start must not un-arm the model, the user only said not now
                warn($"{outcome.Model!.Id} is armed but nothing is serving it. "
                    + $"gatto serve start {outcome.Model!.Id} when you want it.");

            return null;
        }

        //when the server already holds the requested model nothing changes, no confirm and no swap, only the recompose
        if (manager.Serving(outcome.Model!.Id).Match<ModelSwitchResult?>(
                idle: ArmWithNothingServing,
                servingThis: _ => null,
                servingOther: ConfirmAndSwap) is { } decided)
            return decided;

        //a switch writes nothing, /model moves the session and the picker's d key sets the default
        return null;   //the caller arms
    }
}
