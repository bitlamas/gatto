using System.Text.Json;
using Gatto.Core.Acquire;
using Gatto.Core.Home;
using Gatto.Core.Models;

namespace Gatto.Roles;

//each member is the decision the screen must take: a matching repo adds, a proven different repo collides, anything less than proof asks
public enum IdClash
{
    //nothing holds this id, so this is an ordinary adoption
    Free,

    //both sides name the same repo, so this is a second quant of one model, added with no collision screen
    SameModel,

    //both sides name a repo and they differ, so two models share a general.name and the collision screen offers no add
    DifferentModel,

    //the holder will not load, so the collision screen offers no add, since adding a file to a model that will not load always fails
    HolderUnreadable,

    //one side names no repo, so the screen asks, which covers a local pick against anything and a hub pick against a local model
    CannotTell,
}

//builds a model skeleton from a .gguf, deriving what it can and leaving sampling and thinking out, since a guessed block reads as authoritative
public static class ModelScaffold
{
    //what every scaffolded model passes to llama-server. the last two flags turn the server's own context management off, since gatto is the context manager
    private static IReadOnlyList<string> StandardExtraArgs(bool streams) =>
        ["-np", "1", "-fa", "on", .. LoadModeFlags(streams), "--jinja", "--no-context-shift", "--cache-reuse", "0"];   //the load-mode spelling comes from the table the engine update rewrites, so no profile is written in a dialect the pinned build refuses

    //a streamed table is mapped and read lazily. the engine never puts it on the GPU, and a copy into memory would cost its whole size
    private static IReadOnlyList<string> LoadModeFlags(bool streams)
    {
        var dialect = LoadModeDialect.Load();
        return streams ? [.. dialect.LoadMode("mmap"), .. dialect.LazyMode("on")] : dialect.LoadMode("none");
    }

    //writes profile.json under modelsDir\<id> and returns the id. throws when the model exists, the GGUF can't be read or the id isn't a usable folder name
    public static string Create(string modelsDir, string ggufPath, int port, string? id = null,
        int? context = null, bool replace = false, string? projector = null,
        string? llamaServer = null, ModelSource? source = null, string? device = null)
    {
        var resolved = ResolveShardOne(Path.GetFullPath(ggufPath));

        //a typed id is used as given once it validates. a null id gets DeriveId
        if (id is not null) ValidateId(id);

        var md = GgufReader.Read(resolved);
        id ??= DeriveId(md, resolved, source?.RepoId);

        var dir = Path.Combine(modelsDir, id);
        if (Directory.Exists(dir))
        {
            //only the collision screen passes replace, after the user answered it
            if (!replace)
                throw new GattoConfigException($"model '{id}' already exists at {dir} — remove it first, or rename the model file");

            //the replace deletes the whole directory, since per-model material such as system-append.md sits beside the profile and would be wrong beside other weights
            Directory.Delete(dir, recursive: true);
        }

        Directory.CreateDirectory(dir);

        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            //a fresh model names only the file it was scaffolded from and marks it active, since the loader refuses a profile with nothing active
            w.WriteStartArray("files");
            w.WriteStartObject();
            w.WriteString("path", resolved);
            //quant is a label read off the file name, so a name with no token gets no key at all
            if (Gatto.Core.Acquire.QuantToken.Of(resolved) is { Length: > 0 } quant)
                w.WriteString("quant", quant);
            w.WriteBoolean("active", true);
            w.WriteEndObject();
            w.WriteEndArray();
            //the repo and file it was fetched from, when there is one, so a match can compare repos instead of file names
            if (source is { } src)
            {
                w.WriteStartObject("source");
                w.WriteString("repo_id", src.RepoId);
                w.WriteString("file", src.File);
                w.WriteEndObject();
            }
            w.WriteNumber("port", port);
            //a missing context key is left out so the load fails loudly, since Model.Load requires it
            if (context is { } chosen) w.WriteNumber("context", chosen);   //the wizard's budget. a null context keeps the GGUF's trained maximum as written
            else if (md.ContextLength is int ctx) w.WriteNumber("context", ctx);
            //no mmproj key without a projector, so the loader never sees an empty path
            if (!string.IsNullOrWhiteSpace(projector)) w.WriteString("mmproj", projector);
            //no llama_server key without a binary, so gatto never asks the server manager to spawn nothing
            if (!string.IsNullOrWhiteSpace(llamaServer)) w.WriteString("llama_server", llamaServer);
            w.WriteNumber("gpu_layers", 99);
            w.WriteString("cache_type_k", "q8_0");
            w.WriteString("cache_type_v", "q8_0");
            w.WriteStartArray("extra_args");
            foreach (var a in StandardExtraArgs(ModelDiscovery.StreamedBytesOrNull(resolved) is > 0)) w.WriteStringValue(a);
            //the device the fit counted, since llama-server left alone takes a discrete card over a larger unified pool
            if (!string.IsNullOrWhiteSpace(device)) { w.WriteStringValue("-dev"); w.WriteStringValue(device); }
            w.WriteEndArray();
            w.WriteEndObject();
        }

        File.WriteAllBytes(Path.Combine(dir, "profile.json"), ms.ToArray());

        //the notes are a sibling file, since the profile can't reference it under the loader's key allowlist. a failed write must not cost the user the model
        try
        {
            File.WriteAllText(Path.Combine(dir, TuningNotes.FileName),
                TuningNotes.Compose(Model.Load(modelsDir, id).Profile));
        }
        catch (Exception) { }

        return id;
    }

    //the model whose files hold this path, or null, and the one place that resolves a path so no second copy can disagree
    public static string? ModelFor(string modelsDir, string ggufPath)
    {
        string? full;
        try { full = ResolveShardOne(Path.GetFullPath(ggufPath)); }
        catch (Exception) { return null; }

        foreach (var (path, id) in ConfiguredByPath(modelsDir))
            if (string.Equals(path, full, StringComparison.OrdinalIgnoreCase)) return id;
        return null;
    }

    //a model with a source is matched by repo alone. only a sourceless model is matched by file name, and the precedence stays here
    public static string? ModelForHubRow(string modelsDir, string repoId, string ggufFileName)
    {
        var sourceless = new List<(string Id, ModelProfile Profile)>();

        foreach (var id in Model.ListIds(modelsDir))
        {
            ModelProfile profile;
            //a profile that will not load is skipped, and doctor is where the user hears about it
            try { profile = Model.Load(modelsDir, id).Profile; }
            catch (Exception) { continue; }

            if (profile.Source is { } source)
            {
                //no empty-repoId guard, since a recorded repo_id is never empty and the comparison already fails
                if (string.Equals(source.RepoId, repoId, StringComparison.OrdinalIgnoreCase))
                    return id;
                //sourced and not this repo, so the name match must not overturn that answer
                continue;
            }

            sourceless.Add((id, profile));
        }

        if (string.IsNullOrWhiteSpace(ggufFileName)) return null;

        foreach (var (id, profile) in sourceless)
            //every file of the model is asked, so a quant switch doesn't blank the mark on the Hub row
            foreach (var file in profile.Files)
            {
                string path;
                try { path = Path.GetFullPath(file.Path); }
                catch (Exception) { continue; }

                if (string.Equals(Path.GetFileName(path), ggufFileName, StringComparison.OrdinalIgnoreCase))
                    return id;
            }
        return null;
    }

    //the id this file would take when another file already holds it, null when the id is free or the same file has it
    public static string? CollidingModel(string modelsDir, string ggufPath, string? repoId = null)
    {
        string resolved;
        try { resolved = ResolveShardOne(Path.GetFullPath(ggufPath)); }
        catch (Exception) { return null; }

        string wanted;
        try { wanted = DeriveId(GgufReader.Read(resolved), resolved, repoId); }
        catch (Exception) { return null; }     //an unreadable file is the scaffold's error to raise, so this returns null

        if (!Directory.Exists(Path.Combine(modelsDir, wanted))) return null;

        //a model that already holds this exact file is no collision. every one of its files is checked, so the choice between a model and itself is never offered
        var held = ConfiguredByPath(modelsDir)
            .Where(e => string.Equals(e.Id, wanted, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (held.Count > 0)
            return held.Any(e => string.Equals(e.Path, resolved, StringComparison.OrdinalIgnoreCase))
                ? null : held[0].Id;

        //the directory exists but won't load, and Create would still throw on it
        return wanted;
    }

    //a matching repo adds, a proven different repo collides, and anything less than proof asks, so no header guess decides for the user
    public static (IdClash Kind, string? Id) ClashFor(
        string modelsDir, string ggufPath, string? incomingRepoId)
    {
        if (CollidingModel(modelsDir, ggufPath, incomingRepoId) is not { } id) return (IdClash.Free, null);

        string? existingRepo;
        //an unloadable holder is its own answer, since a null repo would fold it into cannot-tell and offer an option that always fails
        try { existingRepo = Model.Load(modelsDir, id).Profile.Source?.RepoId; }
        catch (Exception) { return (IdClash.HolderUnreadable, id); }

        if (incomingRepoId is not { Length: > 0 } incoming
            || existingRepo is not { Length: > 0 } existing)
            return (IdClash.CannotTell, id);

        return (string.Equals(incoming, existing, StringComparison.OrdinalIgnoreCase)
            ? IdClash.SameModel
            : IdClash.DifferentModel, id);
    }

    //every model file to the id that wraps it, the one home for that question. one id can appear more than once, so callers cannot assume one entry each
    private static IReadOnlyList<(string Path, string Id)> ConfiguredByPath(string modelsDir)
    {
        var modeled = new List<(string, string)>();
        foreach (var id in Model.ListIds(modelsDir))
        {
            ModelProfile profile;
            try { profile = Model.Load(modelsDir, id).Profile; }
            catch (Exception) { continue; }

            foreach (var file in profile.Files)
            {
                string p;
                try { p = Path.GetFullPath(file.Path); }
                catch (Exception) { continue; }
                modeled.Add((p, id));
            }
        }
        return modeled;
    }

    //the .gguf files no model points at that declare a context_length, and a file or folder that won't read is skipped rather than fatal
    public static IReadOnlyList<string> FindUnconfigured(string weightsDir, string modelsDir)
    {
        if (!Directory.Exists(weightsDir)) return Array.Empty<string>();

        //the same map ModelFor reads, one rule with two callers
        var modeled = ConfiguredByPath(modelsDir)
            .Select(e => e.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var ggufFiles = EnumerateGgufFilesRecursive(weightsDir);

        return ggufFiles
            .Select(Path.GetFullPath)
            .Select(ResolveShardOne)                      //shard 2 and 3 collapse onto shard 1
            .Distinct(StringComparer.OrdinalIgnoreCase)    //the collapsed paths are deduped, so a sharded set counts once
            .Where(p => !modeled.Contains(p))
            .Where(IsServable)                             //a projector or an embedding GGUF fails IsServable, so it never reaches the picker
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    //every .gguf under the folder, skipping a subfolder that cannot be read and returning nothing for a missing folder
    private static IEnumerable<string> EnumerateGgufFilesRecursive(string rootDir)
    {
        var queue = new Queue<string>();
        queue.Enqueue(rootDir);

        while (queue.Count > 0)
        {
            var currentDir = queue.Dequeue();

            IEnumerable<string> files = [];
            try
            {
                files = Directory.EnumerateFiles(currentDir, "*.gguf");
            }
            catch (UnauthorizedAccessException) { }
            catch (DirectoryNotFoundException) { }
            //any other failure is left to bubble, so a real error is not swallowed

            foreach (var file in files)
                yield return file;

            IEnumerable<string> subdirs = [];
            try
            {
                subdirs = Directory.EnumerateDirectories(currentDir);
            }
            catch (UnauthorizedAccessException) { }
            catch (DirectoryNotFoundException) { }
            //any other failure is left to bubble, so a real error is not swallowed

            foreach (var subdir in subdirs)
                queue.Enqueue(subdir);
        }
    }

    //true when the header has a context_length and no pooling_type, since pooling marks an embedding model that would sail through a context-only check
    private static bool IsServable(string path)
    {
        try
        {
            var md = GgufReader.Read(path);
            return md.ContextLength is not null && md.PoolingType is null;
        }
        catch (Exception) { return false; }
    }

    //a sharded model is loaded by pointing llama.cpp at shard 1, the first path ShardSiblings returns
    private static string ResolveShardOne(string path) => ModelDiscovery.ShardSiblings(path)[0];

    //the id becomes a directory name verbatim, so a path-hostile one is a hard error, since the user will type the same id at /model
    private static void ValidateId(string id) => Gatto.Core.Models.ModelId.Validate(id);

    //the one derivation, shared with the move site in Cli, since two derivations would be two ids for one model
    private static string DeriveId(GgufMetadata md, string path, string? repoId) =>
        Gatto.Core.Models.ModelId.Derive(md, path, repoId);
}
