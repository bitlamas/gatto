namespace Gatto.Cli.Setup;

//everything a wizard run intends to write, held as data until one place applies it: model, then llama_server, then the audition
internal sealed record WriteSet
{
    //an endpoint intent: the name, its base URL and its context. the context can't be null, so an unknown one is resolved by probe or by asking first
    internal sealed record Endpoint(string Name, string BaseUrl, int Context);

    //a model to scaffold, with the context computed for this machine rather than the GGUF's trained ceiling
    internal sealed record Model(string GgufPath, int Port, int Context, string? Id = null,
        bool Replace = false, string? AddToModelId = null,   //only the collision screen's confirmation sets Replace, and AddToModelId adds the file to a model gatto already has
        Gatto.Roles.ModelSource? Source = null,   //the hub repo and file a fetched model came from, null for a model found on disk
        string? PinGpu = null);   //the product name of the integrated GPU the fit counted, which the write turns into a -dev pin

    public Endpoint? UpsertEndpoint { get; init; }
    public Model? CreateModel { get; init; }

    //makes a model's newly joined file the one it uses, set only when the done step's "from now on?" question was answered yes
    public (string ModelId, string Quant)? ActivateFile { get; init; }

    //the vision projector to write into the model's mmproj, or null. it sits outside Model because the screens after the ask rebuild Model wholesale
    public string? Projector { get; init; }

    //a llama-server binary for this model only, written into its profile.json from the unsupported-arch branch, where the pinned build can't run it
    public string? ModelLlamaServer { get; init; }

    //where to move the model before it is written, or null to keep it where it is. null is the default, a multi-GB copy is a choice
    public string? MoveTo { get; init; }

    //the weights root the user typed on the consent screen, the root every fetch and scan uses. an adopted file goes to MoveTo instead, a different folder
    public string? WeightsRoot { get; init; }

    public string? LlamaServer { get; init; }
    public string? DefaultModel { get; init; }

    //the default model written only when the config has none: absent writes, present leaves it alone. the choice DefaultModel makes always writes
    public string? DefaultModelIfAbsent { get; init; }

    //the endpoint the next launch resolves through. only the connect path sets it, the llama path derives its own and must not re-point the config
    public string? DefaultEndpoint { get; init; }

    //where to install a copy of the running gatto and put it on PATH. null on a run that doesn't install, so a leave installs nothing
    public string? InstallTo { get; init; }

    //repair only the findability, without copying. set instead of InstallTo, so the two can't both apply and install the older image this flag refuses
    public bool PathOnly { get; init; }

    //the update-check answer when this run asked it. null means it wasn't asked, so nothing is written and a re-run can't re-record it
    public bool? UpdateCheck { get; init; }

    //whether this run intends to write anything at all. a run that ends with nothing accumulated is a user who left, which is a legal outcome
    public bool IsEmpty =>
        UpsertEndpoint is null && CreateModel is null && LlamaServer is null && DefaultModel is null
        && DefaultModelIfAbsent is null;
}
