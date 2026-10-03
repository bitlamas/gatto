using Gatto.Core.Home;

namespace Gatto.Cli.Setup;

//the only place in the wizard that writes. a failure part-way leaves the earlier writes valid and names the failed step
internal static class WriteSetApply
{
    //applies the run's intents, null on success or the sentence naming the step that failed. this overload drops the move note
    public static string? Apply(string homePath, WriteSet writes, out string? modelId) =>
        Apply(homePath, writes, out modelId, out _);

    //a failed move is reported in moveNote and never returned, so the model still scaffolds at its original path
    public static string? Apply(string homePath, WriteSet writes, out string? modelId, out string? moveNote,
        Func<string, string?>? addToPath = null, Func<string, string, string?>? copyExe = null)   //test seams, so a suite never writes the real PATH, and production passes null for both
    {
        var pathDeed = addToPath ?? Gatto.Cli.PathInstaller.Add;
        var copyDeed = copyExe ?? Gatto.Cli.SelfInstall.Apply;
        modelId = null;
        moveNote = null;

        //the move runs first, before the model: the model records an absolute path. a move afterwards would leave the profile naming a file that is gone
        if (writes.CreateModel is { } wantsModel && writes.MoveTo is { Length: > 0 } dest)
        {
            //derive the id here and pass it on Id, so the scaffold doesn't derive a second id for the same model
            var modelIdForFolder = wantsModel.Id
                ?? Gatto.Core.Models.ModelId.Derive(
                    Gatto.Core.Models.GgufReader.Read(wantsModel.GgufPath), wantsModel.GgufPath);

            var moved = MoveModel(wantsModel.GgufPath,
                Gatto.Core.Acquire.ModelLocation.ForModelIn(dest, modelIdForFolder), writes.Projector);
            moveNote = moved.Note;
            writes = writes with
            {
                CreateModel = wantsModel with { GgufPath = moved.ModelPath, Id = modelIdForFolder },
                Projector = moved.ProjectorPath,
            };
        }

        try
        {
            //a second quant joins the model it belongs to: the file list gains an entry and nothing else moves. it is added inactive
            if (writes.CreateModel is { AddToModelId: { Length: > 0 } joins } add)
            {
                Gatto.Roles.ModelFiles.Add(
                    System.IO.Path.Combine(homePath, "models"), joins, add.GgufPath);
                //the completion point is the model the file joined, so the run ends on the model the user asked for
                modelId = joins;
            }
            else if (writes.CreateModel is { } p)
            {
                //llama-server's own name for the GPU the fit counted, so the profile runs where the shelf said the model fits
                string? device = null;
                if (p.PinGpu is { Length: > 0 } gpu)
                {
                    device = DeviceFor(homePath, writes, gpu);
                    if (device is null)
                        moveNote = string.Join(" ", new[] { moveNote, $"llama-server did not list {gpu}, so the new model is not pinned to it "
                            + "and may run on another GPU. Its profile takes \"-dev\" and the device name in extra_args." }.OfType<string>());
                }
                modelId = Gatto.Roles.ModelScaffold.Create(
                    System.IO.Path.Combine(homePath, "models"), p.GgufPath, p.Port, p.Id, p.Context,
                    //true only when the collision screen answered "replace"
                    p.Replace,
                    //null unless the projector ask was answered yes. it is read from WriteSet's own member, because the ask outlives the rebuild of CreateModel
                    writes.Projector,
                    //the per-model server override, read from its own member for the same reason. null on every ordinary run
                    writes.ModelLlamaServer,
                    p.Source, device);
            }
        }
        catch (Exception ex) { return $"couldn't create the model: {ex.Message}"; }

        //the done step's question, answered yes: the file joined inactive and this is the deed that makes it the one in use
        try
        {
            if (writes.ActivateFile is { } pick)
                Gatto.Roles.ModelFiles.SetActive(
                    System.IO.Path.Combine(homePath, "models"), pick.ModelId, pick.Quant);
        }
        catch (Exception ex) { return $"couldn't switch the file: {ex.Message}"; }

        try
        {
            if (writes.LlamaServer is { } exe) GattoConfigWriter.SetLlamaServer(homePath, exe);
            //the weights root, written here rather than when it was typed, because gatto asks before it writes
            if (writes.WeightsRoot is { } root) GattoConfigWriter.SetWeightsRoot(homePath, root);
        }
        catch (Exception ex) { return $"couldn't save the llama-server path: {ex.Message}"; }

        try
        {
            if (writes.UpsertEndpoint is { } e)
                GattoConfigWriter.UpsertEndpoint(homePath, e.Name, e.BaseUrl, e.Context);
        }
        catch (Exception ex) { return $"couldn't save the server settings: {ex.Message}"; }

        //the default endpoint is written after the endpoint itself, or the reader's next launch fails with a message that can't say what was attempted
        try
        {
            if (writes.DefaultEndpoint is { } name) GattoConfigWriter.SetDefaultEndpoint(homePath, name);
        }
        catch (Exception ex) { return $"couldn't set the default endpoint: {ex.Message}"; }

        //the default is written with the model, so ending the flow at the check still leaves the config pointed at something
        try
        {
            //only the connect road names an endpoint, every other road scaffolds or picks a local model
            string Owner() => writes.DefaultEndpoint ?? "local";
            if (writes.DefaultModel is { Length: > 0 } chosen)   //an id the user confirmed always writes
                GattoConfigWriter.SetEndpointDefaultModel(homePath, Owner(), chosen);
            else if ((writes.DefaultModelIfAbsent ?? modelId) is { Length: > 0 } ifAbsent
                     && GattoConfig.Load(homePath).ModelDefaultFor(Owner()) is null)   //a scaffolded id writes only into a home with no default, or adding a model would repoint a bare gatto
                GattoConfigWriter.SetEndpointDefaultModel(homePath, Owner(), ifAbsent);
        }
        catch (Exception ex) { return $"couldn't set the default model: {ex.Message}"; }

        //the update-check consent, written through SetConsentKey, the writer built for this
        try
        {
            if (writes.UpdateCheck is { } allowed)
                GattoConfigWriter.SetConsentKey(homePath, "update_check", allowed);
        }
        catch (Exception ex) { return $"couldn't save the update-check choice: {ex.Message}"; }

        //the install is last on purpose: whatever runs last is what a failure cannot prevent, and the config matters more than the copy
        try
        {
            if (writes.InstallTo is { } dir)
            {
                if (copyDeed(Environment.ProcessPath ?? "", dir) is { } copyProblem)
                    return copyProblem;
                if (pathDeed(dir) is { } pathProblem)
                    return $"gatto was installed to {dir}, but {pathProblem}";
            }
            //the repair-only case: findability is fixed and nothing is copied, so it stays an else if on the install branch
            else if (writes.PathOnly)
            {
                if (pathDeed(Gatto.Cli.SelfInstall.DefaultDir()) is { } repairProblem)
                    return repairProblem;
            }
        }
        catch (Exception ex) { return $"couldn't install gatto: {ex.Message}"; }

        return null;
    }

    //runs before the model so the profile records where the file ended up. the projector moves in its own transaction too, or the mmproj is stranded
    private static (string ModelPath, string? ProjectorPath, string? Note) MoveModel(
        string ggufPath, string destDir, string? projector)
    {
        var model = Gatto.Core.Acquire.ModelAdoption.MoveAsync(
            new Gatto.Core.Acquire.AdoptionRequest(ggufPath, destDir, _ => { }),
            CancellationToken.None).GetAwaiter().GetResult();

        if (!model.Ok)
            return (ggufPath, projector,
                $"couldn't move the model to {destDir} ({model.Detail}), it stays where it is, "
                + "and everything else is set up");

        //the encoder follows only when it sits beside the model. one the user pointed at from elsewhere is not part of this move
        var movedProjector = projector;
        string? note = null;
        if (projector is { Length: > 0 } enc
            && string.Equals(Path.GetDirectoryName(Path.GetFullPath(enc)),
                Path.GetDirectoryName(Path.GetFullPath(ggufPath)), StringComparison.OrdinalIgnoreCase))
        {
            var moved = Gatto.Core.Acquire.ModelAdoption.MoveAsync(
                new Gatto.Core.Acquire.AdoptionRequest(enc, destDir, _ => { }),
                CancellationToken.None).GetAwaiter().GetResult();

            if (moved.Ok) movedProjector = moved.FinalPath;
            else note = $"the model moved, but its vision file stayed behind ({moved.Detail})";
        }

        return (model.FinalPath, movedProjector, note);
    }

    //the engine this run writes or the one configured, asked for its device names, and null when there is no engine or the listing has no such GPU
    private static string? DeviceFor(string homePath, WriteSet writes, string gpu)
    {
        var exe = writes.ModelLlamaServer ?? writes.LlamaServer;
        if (exe is null)
            try { exe = GattoConfig.Load(homePath).LlamaServer; }
            catch (Exception) { }   //a config that won't load leaves the model unpinned and noted, never unwritten
        return exe is { Length: > 0 }
            ? Gatto.Core.Tools.LlamaServerProbe.DeviceNamed(exe, gpu, TimeSpan.FromSeconds(15))
            : null;
    }
}
