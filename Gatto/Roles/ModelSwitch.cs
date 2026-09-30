using System.Text.Json;
using Gatto.Core.Client;
using Gatto.Core.Home;
using Gatto.Core.Memory;

namespace Gatto.Roles;

//the decision core's answer, mapped by the caller into the record the Repl sees, with the model and composition null on failure
internal sealed record ModelSwitchOutcome(
    bool Success, string ModelId, string? SystemText, int? ContextBudget,
    string? Message, Model? Model, Composition? Composition, int MemoryTruncatedLines = 0);

//the decision core for /model, which persists the choice and returns the composition. it mutates no session state, so the caller applies the side effects
internal static class ModelSwitch
{
    //load, port guard, recompose and persist, without the probe, which the caller runs only after these succeed and nothing changed on a failure
    internal static ModelSwitchOutcome Decide(
        string modelsDir,
        string homePath,
        string requestedModelId,
        RoleFile role,
        IReadOnlyList<(string Path, string Content)> contextFiles,
        IReadOnlyDictionary<string, JsonElement?>? endpointThinking,
        string? currentBaseUrl,
        string? cwd = null,
        //a delegate, since the index budget belongs to the model being switched to. the caller owns rooting, and null means memory is off
        Func<Model, MemoryIndex.LoadResult>? loadMemory = null,
        bool memoryNudge = false)
    {
        //load the model, and on failure nothing must change
        Model model;
        try { model = Model.Load(modelsDir, requestedModelId); }
        catch (GattoConfigException ex)
        {
            return new ModelSwitchOutcome(false, requestedModelId, null, null, ex.Message, null, null);
        }

        //refuse a model on another port, since the client's base url is fixed at construction and a live swap would talk to the wrong server
        var sessionPort = Uri.TryCreate(currentBaseUrl, UriKind.Absolute, out var bu) ? bu.Port : -1;
        if (model.Profile.Port != sessionPort)
            return new ModelSwitchOutcome(false, requestedModelId, null, null,
                $"model '{requestedModelId}' serves on port {model.Profile.Port}, but this session is " +
                $"connected to {currentBaseUrl} — restart gatto to switch to a model on a different address",
                null, null);

        //recompose against the new model, with the role, the gates and the context files unchanged, since gates come from the role file
        var memory = loadMemory?.Invoke(model) ?? new MemoryIndex.LoadResult(null, 0);
        var comp = RoleComposition.Compose(role, model, contextFiles, endpointThinking, cwd: cwd, date: DateTime.Now,
            memoryIndex: memory.Text, memoryTruncatedLines: memory.TruncatedLines, memoryNudge: memoryNudge);

        //the caller persists and recomposes after the server work, so a failed start never leaves default_model written for weights that never loaded
        return new ModelSwitchOutcome(true, model.Id, comp.SystemText, model.Profile.Context, null,
            model, comp, memory.TruncatedLines);
    }

    //commit a decision that was already made and proven, separate from Decide so the caller can put the serving change between them
    internal static string? Persist(string homePath, string modelId)
    {
        try { GattoConfigWriter.SetDefaultModel(homePath, modelId); return null; }
        catch (GattoConfigException ex) { return $"could not persist the model choice: {ex.Message}"; }
    }

    //the loaded model when it genuinely disagrees with the armed one, shared with the launch probe so both call the same mismatch
    internal static ServingName? DescribeProbe(Model armed, LoadedModel? loaded, string modelsDir)
    {
        if (Model.MatchesLoaded(armed, loaded) is not false) return null;   //anything that isn't a clear disagreement returns null

        //the server holding another file of this same model is no mismatch, since switching quant leaves the old one running. another model's weights still are
        if (loaded?.ModelPath is { Length: > 0 } held
            && armed.Profile.Files.Any(f => Same(f.Path, held)))
            return null;

        //name the loaded weights by the model id that claims them, since a 90-character path is illegible, and fall back to the file name
        var claimed = Model.ListIds(modelsDir)
            .Select(id => { try { return Model.Load(modelsDir, id); } catch (Exception) { return null; } })
            .FirstOrDefault(p => p is not null && Model.MatchesLoaded(p, loaded) == true);

        //the name and whether it is on the shelf come out of this one listing, so no caller asks the shelf again
        var servingId = claimed
            //a false answer from MatchesLoaded implies a path, but this stays null-safe since the invariant lives in another method
            ?.Id ?? (loaded?.ModelPath is { Length: > 0 } p ? Path.GetFileNameWithoutExtension(p) : null);

        return servingId is { Length: > 0 } name ? new ServingName(name, claimed is not null) : null;
    }

    //the loaded name and whether a user can type it, since a file stem names no model and offering it would be false
    internal readonly record struct ServingName(string Name, bool OnTheShelf);

    //two paths naming the same file, compared normalized and case-insensitively, with a malformed path answering no instead of throwing
    private static bool Same(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch (Exception) { return false; }
    }
}
