using Gatto.Core.Home;
using Gatto.Repl;
using Gatto.Roles;

namespace Gatto.Cli;

//refuse before the screen, every refusal is knowable from the list already on disk. the serving state is read once per command
internal static class QuantEdit
{
    //every way the restart can end restores the setting, including a throw. keep the reload inside the try, the server starts from the profile this writes
    public static string Switch(
        string modelsDir, Model model, string name, Func<bool> serving, Func<WizardAsk, bool>? ask,
        Func<Model, Model, bool>? restart = null)
    {
        try
        {
            //the refusals come before the screen, so nothing below can refuse for a reason the list already knew
            if (ModelFiles.WhyNotSetActive(modelsDir, model.Id, name) is { } why) return why;

            var active = model.Profile.Files.Single(f => f.Active);

            //a request for the file already in use gets one plain line, no ask, no probe, no write. compare case-insensitively, as ModelFiles.Matches does
            if (active.Quant is { } already
                && string.Equals(already, name, StringComparison.OrdinalIgnoreCase))
                //echo the file's own label rather than the typed spelling, the same phrase the sibling refusal uses
                return $"{already} is the file {model.Id} is set to use. Nothing to change";

            var live = serving();
            var current = active.Quant ?? "its current file";

            if (live && ask is not null && !ask(SwapConfirm.QuantSwitch(model.Id, name, current)))
                return $"staying on {current}";

            ModelFiles.SetActive(modelsDir, model.Id, name);

            //with nothing running there is no restart and no re-read, just the write and the sentence
            if (!live) return $"{model.Id} will use {name} from now on";

            //with no restart delegate or no quant token, the write stands with a caveat, since SetActive can only address files by name
            if (restart is null || active.Quant is not { } previousQuant)
                return $"{model.Id} will use {name} from now on, "
                    + $"the running server still holds {current} until it restarts";

            //reload the model from disk, the one in hand still names the old active path that was just switched away from
            try
            {
                if (restart(model, Model.Load(modelsDir, model.Id)))
                    return $"{model.Id} is on {name} now, "
                        + "everything said so far is read again on your next message";
            }
            catch
            {
                //restore the setting before re-throwing, the swap already stopped the server so an escape would leave it down and the profile stale
                ModelFiles.SetActive(modelsDir, model.Id, previousQuant);
                throw;
            }

            //the new file did not load, so put the setting back too, or the next start picks the file that just failed
            ModelFiles.SetActive(modelsDir, model.Id, previousQuant);
            return $"stayed on {current}, see above";
        }
        //the refusals are ModelFiles' own words, re-phrasing them here would copy a rule that already reads well
        catch (GattoConfigException ex) { return ex.Message; }
    }

    //forget a file, the GGUF stays on disk. the confirm asks when the model is serving, a narrower trigger would need a probe
    public static string Remove(
        string modelsDir, Model model, string name, Func<bool> serving, Func<WizardAsk, bool>? ask)
    {
        try
        {
            //the refusals come before the screen, all three of them answerable from the list
            if (ModelFiles.WhyNotRemove(modelsDir, model.Id, name) is { } why) return why;

            if (serving() && ask is not null && !ask(SwapConfirm.QuantRemove(model.Id, name)))
                return $"keeping {name} on {model.Id}'s list";

            ModelFiles.Remove(modelsDir, model.Id, name);

            //the list changed and the bytes are still there, nothing else is true
            return $"{model.Id} no longer lists {name}. The file itself is still on disk";
        }
        catch (GattoConfigException ex) { return ex.Message; }
    }
}
