using Gatto.Cli.Setup.Shelves;

namespace Gatto.Cli.Setup;

//the engine step: found, fetch, consent, fallback, steer, verify, failed, and the connect road
internal sealed partial class SetupFlow
{
    //the engine step sets the llama road, a user who never opens connect is not undecided. one handler serves the connect row on both first screens
    private SetupScreen EngineStep()
    {
        Path = SetupPath.Llama;
        _narrate.Add(new SetupScreen.Info("step.model", [new SetupRow("Model")]));
        return NextLlamaSegment();
    }


    //what an address at a Hub door says on gatto model, which adds models and talks to the server it already has
    private const string AddressNotice = "gatto setup connects gatto to a server. This screen adds models.";

    //the address the user typed on the connect road, null after a port scan
    private string? _typedServer;

    private static bool IsAddress(string answer) =>
        ShelfControls.Untyped(answer) is { } t && Gatto.Cli.Setup.Tui.TypedDoor.Classify(t) == Gatto.Cli.Setup.Tui.DoorInput.Address;

    //every typed-id ask says the same: a name is needed, and on gatto model an address is answered here while on setup it passes on to the connect road
    private string? TypedIdProblem(string t) =>
        t.Trim().Length == 0 ? "Type a name like org/model"
        : _addRoad && Gatto.Cli.Setup.Tui.TypedDoor.Classify(t) == Gatto.Cli.Setup.Tui.DoorInput.Address ? AddressNotice
        : null;

    //one answer for an address at any Hub door: in place on gatto model, the connect road on gatto setup. an ask comes back as it was, its own check says the notice
    private SetupScreen AnswerAddress(string typed)
    {
        if (_addRoad)
            return LastScreen switch
            {
                SetupScreen.Choice here => Emit(here with { Notice = AddressNotice }, _awaiting),
                SetupScreen.Ask ask => Emit(ask, _awaiting),
                _ => throw new InvalidOperationException("an address was answered with nothing on screen to keep"),
            };

        //the model road's own writes go with it, the engine stays, since a step back returns to the llama road and the engine is on disk
        if (_awaiting == ShelfLoadingKey) CancelLoad();
        Writes = Writes with { CreateModel = null, ModelLlamaServer = null, MoveTo = null, Projector = null, ActivateFile = null };
        Picked = null;
        Selected = null;
        _modelId = null;
        Path = SetupPath.Connect;
        return TypedAddress(typed);
    }

    //leave the llama road for the user's own server in one home (three screens open it, three copies could disagree)
    private SetupScreen TakeConnectRoad()
    {
        Path = SetupPath.Connect;
        return ProbeForServer();
    }

    //the one sentence for a server that is already gatto's own, shown on the found or retry screen. the value is at most one, one serve.json names one port
    private string? _ownServerSaid;

    //use a comma rather than an em dash (em dashes are forbidden on any wizard screen). the wording follows the drawn mock
    private static string OwnServerRow(string baseUrl, string model) =>
        $"The server at {baseUrl} is gatto's own, it is serving {model}, which is the road option 1 "
        + "on the engine screen takes.";

    //the typed door for a server not on a port gatto reaches. it is also why the retry state offers one option instead of two. a property rather than a const, a const can't read the chosen glyph set
    private string ServerDoorPlaceholder =>
        $"type the server's address{Glyphs.Ellipsis} e.g. http://127.0.0.1:8080";

    //a screen on the road rather than a failure page, the user is still asked something. ports are named by owner, so the user can recognise their own setup
    private SetupScreen NoServerAnswered() => Emit(new SetupScreen.Choice(
        RetryKey,
        "Which server should gatto talk to?",
        [new ChoiceOption(Retry, "Look again")],
        BodyRows: _typedServer is { } typedServer
            ? [
                new SetupRow($"No server answered at {typedServer}."),
                new SetupRow(""),
                new SetupRow("Start your server, then look again, or type its address if it lives "
                    + "somewhere else."),
              ]
            : _ownServerSaid is { } said
            ? [
                new SetupRow($"{said} No other server answered."),
                new SetupRow(""),
                new SetupRow("Start the server you run yourself, then look again, or type its "
                    + "address if it lives somewhere else."),
              ]
            : [
                new SetupRow("No server answered on the ports llama.cpp and LM Studio usually use "
                    + "on this machine."),
                new SetupRow(""),
                new SetupRow("Start your server, then look again, or type its address if it lives "
                    + "somewhere else."),
              ],
        Door: ServerDoorPlaceholder), RetryKey);

    //accept only the retry option or a typed address. falling into the default arm would leave setup, the worst reading of a typed address
    private SetupScreen AnswerRetry(string optionKey) =>
        optionKey == Retry ? _typedServer is { } typed ? TypedAddress(typed) : ProbeForServer()
            : TypedAddress(ShelfControls.Untyped(optionKey)!);

    //probe exactly the address the user typed, on silence re-show that screen (they told gatto where to look)
    private SetupScreen TypedAddress(string typed)
    {
        //the address is kept, so the silence names it and Look again asks it again
        _typedServer = typed.Trim();
        _found = probes.ProbeAt(_typedServer);
        _contextWasReported = _found?.NCtx is > 0;
        return _found is null ? NoServerAnswered() : Complete(_found.BaseUrl, _found.NCtx ?? 0);
    }

    //look for a server, treat nothing found as a retry state rather than a dead exit. the user probably hasn't started their server yet, quitting means starting over
    private SetupScreen ProbeForServer()
    {
        //gatto's own serving server is narrated and passed rather than offered. the match keys on the answered port, a typed read stops a dead process claiming it
        List<int>? spent = null;
        _ownServerSaid = null;
        _typedServer = null;
        while (true)
        {
            _found = probes.ProbeServer(spent);
            if (_found is null) break;
            if (!Uri.TryCreate(_found.BaseUrl, UriKind.Absolute, out var uri)) break;
            if (probes.GattoServingOn(uri.Port) is not { Length: > 0 } serving) break;

            _ownServerSaid = OwnServerRow(_found.BaseUrl, serving);
            (spent ??= []).Add(uri.Port);
            _found = null;
        }

        if (_found is null) return NoServerAnswered();

        //never adopt a found server silently, the user confirms it. its url and model list are the server's own strings, shown through the sanitizing widget
        return Emit(new SetupScreen.Choice(
            ConfirmServerKey,
            //ask the road's question the same way on every screen of it, the title doesn't change under the user
            "Which server should gatto talk to?",
            [new ChoiceOption(Yes, "Use this server")],
            //compute the row from the model list, several entries are a catalogue rather than all up. the cost sentence stays here too
            BodyRows: [
                .. _ownServerSaid is { } skipped ? (IReadOnlyList<SetupRow>)[skipped, ""] : [],
                ConnectCost, "",
                ServerFacts.Row(ServerFacts.Server, _found.BaseUrl),
                //compute the row from the list itself: one entry is a model serving, several is a catalogue of what it could load on demand
                .. (IReadOnlyList<SetupRow>)(_found.Models switch
                {
                    { Count: 0 } => [ServerFacts.Row(ServerFacts.Serving, "it did not say")],
                    { Count: 1 } m => [ServerFacts.Row(ServerFacts.Serving, Gatto.Core.Models.ServedModelName.Display(m[0]))],
                    var many =>
                        [ServerFacts.Row(ServerFacts.Lists, $"{many.Count} models it can load")],
                }),
                //say the context wasn't reported rather than guessing at it, and name the ask that the next screen keeps
                ServerFacts.Row(ServerFacts.Context,
                    _found.NCtx is { } n && n > 0
                        ? $"{n:N0} tokens {Glyphs.Dot} reported by the server"
                        : "not reported, gatto will ask"),
            ],
            Door: OtherServerDoorPlaceholder), ConfirmServerKey);
    }


    //its own fact family, different steps use different words and a longer label must not collapse another's alignment. the labels come from the drawn mock
    internal static class SummaryFacts
    {
        public static readonly string[] Labels =
            ["machine", "engine", "model", "check", "config", "gatto", "server", "context"];

        public static readonly int Column = Labels.Max(l => l.Length) + 3;

        public static SetupRow Row(string label, string value, string? lift = null) =>
            new(label.PadRight(Column) + value, RowTone.Aside, Indent: 0, Hang: Column,
                Lift: lift is null ? null : [lift]);
    }

    //the check step's own label column, kept separate even when widths match today. different steps use different words, one shared width fits none
    internal static class ProveFacts
    {
        public static readonly string[] Labels =
            ["server", "model", "asking", "context", "said", "speed"];

        public static readonly int Column = Labels.Max(l => l.Length) + 3;

        public static SetupRow Row(string label, string value, string? lift = null) =>
            new(label.PadRight(Column) + value, RowTone.Aside, Indent: 0, Hang: Column,
                Lift: lift is null ? null : [lift]);

        //round the figure first and read the band from the rounded value, so the sentence can't disagree with the number shown. the band only chooses words
        public static SetupRow Speed(double rate)
        {
            var shown = Math.Round(rate, 1);
            var figure = $"~{shown:0.#} tok/s";
            return new SetupRow(
                "speed".PadRight(Column)
                    + $"{figure}, {Gatto.Roles.Audition.AuditionReport.SpeedNote(shown)}",
                RowTone.Aside, Hang: Column, Highlight: [figure]);
        }
    }

    //its own title rather than CheckTitle: that asks whether a model can run commands, this asks whether a server answers
    private const string ConnectCheckTitle = "Does it answer?";

    //the connect road's own label column, one shared width fits neither step's labels. the lists label stays listed so a wider label later can't collapse the rows
    internal static class ServerFacts
    {
        public const string Server = "server";
        public const string Serving = "serving";
        public const string Lists = "lists";
        public const string Context = "context";

        public static readonly string[] Labels = [Server, Serving, Lists, Context];

        public static readonly int Column = Labels.Max(l => l.Length) + 3;

        public static SetupRow Row(string label, string value) =>
            new(label.PadRight(Column) + value, RowTone.Aside, Indent: 0, Hang: Column);
    }

    //the door placeholder says another, a server is already found and named above it
    private string OtherServerDoorPlaceholder =>
        $"type another server's address{Glyphs.Ellipsis} e.g. http://127.0.0.1:8080";

    //state what this option is, don't phrase it as a warning, so a user of another server isn't turned away
    private static readonly SetupRow ConnectCost = new(
        "gatto will talk to this server as it is, it can't switch models or change settings on a "
        + "server it didn't start.");

    private SetupScreen AnswerConfirm(string optionKey)
    {
        //a typed answer here is another address rather than a refusal, the door invites one. read the typed mark directly, the wrapper sets it
        if (ShelfControls.Untyped(optionKey) is { } address) return TypedAddress(address);

        if (optionKey != Yes || _found is null) return Leave();

        //the context is required, so a server that reports its window settles it and one that doesn't gets asked. never infer the answer from a model ceiling
        _contextWasReported = _found.NCtx is > 0;
        return _found.NCtx is { } n && n > 0
            ? Complete(_found.BaseUrl, n)
            : Emit(new SetupScreen.Ask(
                ContextKey,
                //say why the number is asked, gatto asks only when the server would not say
                "How much context should gatto use?",
                BodyRows: [new SetupRow(
                    "This server didn't report its context window, so gatto has to ask. If the "
                    + "server reports its own context later, that number overrides this answer.")],
                Validate: ContextComplaint,
                //offer the default as a row the user can pick. a keystroke the render never showed the widget can't honour
                Placeholder: $"or type a number of tokens{Glyphs.Ellipsis}",
                //compute the label and the value from one constant, so the sentence and the number can't part. the value stays plain because it gets parsed
                Offer: new AskOffer(
                    $"Use {OfferedServerContext:N0}",
                    OfferedServerContext.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                //use next as the enter verb, answering this question is the step. one offered row with a door gives the same answer
                EnterVerb: "next"),
                ContextKey);
    }

    private SetupScreen AnswerContext(string typed)
    {
        if (_found is null) return Leave();

        //an empty answer accepts the offer, the placeholder is the offer and enter must mean what the screen says
        var text = typed.Trim();
        if (text.Length == 0) return Complete(_found.BaseUrl, OfferedServerContext);

        //re-check the answer here, the face validates but a test drives the flow directly
        if (ContextComplaint(text) is not null) return Emit(_emitted[^1], ContextKey);
        return Complete(_found.BaseUrl,
            int.Parse(text, System.Globalization.CultureInfo.InvariantCulture));
    }

    //null when the typed context is usable, otherwise the sentence to show. empty is usable, it takes the offer
    private static string? ContextComplaint(string typed)
    {
        var t = typed.Trim();
        if (t.Length == 0) return null;
        return int.TryParse(t, System.Globalization.NumberStyles.None,
                   System.Globalization.CultureInfo.InvariantCulture, out var n) && n > 0
            ? null
            : "That needs to be a whole number of tokens, like 4096";
    }

    //the connect flow's completion, where the path ends with one endpoint whose context is known. the endpoint and a model must both be set here, or the next launch throws
    private SetupScreen Complete(string baseUrl, int context)
    {
        _connectBaseUrl = baseUrl;
        _connectContext = context;

        //reuse an endpoint that already points here rather than adding a second one, server names only urls nothing is set for
        var existing = probes.ExistingEndpointFor(baseUrl);

        //the name local decides the launch branch, so an unmanaged server goes beside it rather than replacing it. this covers the config-only case
        _besideLocal = string.Equals(existing, "local", StringComparison.Ordinal);
        var name = _besideLocal ? null : existing;

        return ContinueConnect(name ?? "server");
    }

    //the served model that requests will name, gatto cannot invent it (only the server knows what it answers to)
    private SetupScreen ContinueConnect(string endpointName)
    {
        _connectEndpointName = endpointName;
        var models = _found?.Models ?? [];

        //ask only when several models are listed, one is not a choice and picking for the user would be gatto choosing their model
        if (models.Count == 1) return FinishConnect(endpointName, models[0]);

        if (models.Count > 1)
            return Emit(new SetupScreen.Choice(
                ModelPickKey,
                "Which model should gatto ask for?",
                //the options are the server's own strings, rendered through the widget that sanitizes them.
                [.. models.Select(m => new ChoiceOption(m, m))],
                BodyRows: [new SetupRow("This server lists several. gatto names one on every "
                    + "request; you can change it later in gatto.json.")],
                //leave the rows unnumbered, the server's own strings can be any length or count and the footer names the arrows
                Unnumbered: true),
                ModelPickKey);

        //keep the question in the title and the reason in the body (a title carrying both became the longest line)
        return Emit(new SetupScreen.Ask(
            ModelNameKey,
            "What model should gatto ask for?",
            BodyRows: [new SetupRow("This server didn't list its models. gatto names one on every "
                + "request, so it needs the name this server answers to.")],
            Validate: t => string.IsNullOrWhiteSpace(t) ? "Type the model name the server expects" : null,
            Placeholder: $"the name of the model this server answers to{Glyphs.Ellipsis}",
            //use next as the enter verb, it names where the answer goes while the default continue reads as resuming something
            EnterVerb: "next"), ModelNameKey);
    }

    //write the intent, then pause, the prove reads gatto.json from disk and would measure the config that stood before this flow
    private SetupScreen FinishConnect(string endpointName, string modelId)
    {
        _connectModel = modelId;
        Writes = Writes with
        {
            UpsertEndpoint = new WriteSet.Endpoint(endpointName, _connectBaseUrl!, _connectContext),
            //the connect endpoint resolves through the cloud branch, where default_model is a verbatim model string. do not turn this into a model lookup
            DefaultEndpoint = endpointName,
            DefaultModel = modelId,
        };
        NeedsWritesApplied = true;
        return Emit(WritesPending, null);
    }

    //the engine step's own label column, one width over two steps' label sets fits neither. list every label the screens use so a later one cannot move the column
    internal static class EngineFacts
    {
        public const string Found = "found";
        public const string Build = "build";
        public const string Checked = "checked";
        public const string Tested = "tested";

        //list the labels of screens that have not shipped yet, they are not dead and the widest sets the column the goldens pin
        public static readonly string[] Labels =
            [Found, Build, Checked, Tested, "fetch", "fetched", "from", "into", "download", "extract to"];

        public static readonly int Column = Labels.Max(l => l.Length) + 3;

        //one fact row, whose hang keeps a wrapped value under its own column rather than at the margin. the lift marks a file name the reader must recognise later.
        public static SetupRow Row(string label, string value, string? lift = null) =>
            new(label.PadRight(Column) + value, RowTone.Aside, Indent: 0, Hang: Column,
                Lift: lift is null ? null : [lift]);

    }


    //the step's label column, the widest label anywhere in the step sets it so the value column does not jump between frames
    internal static class CheckFacts
    {
        //list every label the check step can draw, the ones not yet on a shipped screen still set the column
        public static readonly string[] Labels =
        [
            "model", "tasks", "server", "running", "done", "quant", "sampling", "thinking",
            "speed", "recorded", "file", "found", "expected", "last lines", "first reply",
        ];

        public static readonly int Column = Labels.Max(l => l.Length) + 3;

        //let this row take the two cells the frame keeps for prose, a fact block folds where prose wraps
        public static SetupRow Row(string label, string value, string? lift = null, bool wide = false) =>
            new(label.PadRight(Column) + value, RowTone.Aside, Indent: 0, Hang: Column,
                Lift: lift is null ? null : [lift], Wide: wide);

        //a second line under a fact, at the fact's own column, so a continuation never sits off the value it continues.
        public static SetupRow Cont(string value, bool wide = false) =>
            new(new string(' ', Column) + value, RowTone.Aside, Indent: 0, Hang: Column, Wide: wide);

        //a fact whose value is a list that may break only between its items.
        public static SetupRow Row(string label, IReadOnlyList<string> items) =>
            new(label.PadRight(Column), RowTone.Aside, Indent: 0, Hang: Column, Items: items);
    }

    //the model block's label column, computed from its own widest label (a shared constant would use one block's word for the other)
    internal static class ModelFacts
    {
        public static readonly string[] Labels =
            ["fetch", "fetching", "fetched", "with", "and", "then", "from", "into", "download",
             "kept", "expected", "got", "found"];

        public static readonly int Column = Labels.Max(l => l.Length) + 3;

        //one fact row at this block's column, the same shape as the engine block but deliberately not shared, the column must differ
        public static SetupRow Row(string label, string value, string? lift = null) =>
            new(label.PadRight(Column) + value, RowTone.Aside, Indent: 0, Hang: Column,
                Lift: lift is null ? null : [lift]);
    }

    //compose the fetch block in one place, so the consent screen and the offline arm cannot describe one fetch two ways.
    private static EngineView EngineBlock(Offer offer, bool gattoFetches) => new(
        gattoFetches,
        offer.Pick.ZipName,
        offer.Assets is { } a ? SizeWords.Auto(a.Server.Size) : null,
        Gatto.Roles.Serve.LlamaAssetSteering.PinnedRelease,
        offer.Into,
        //take the companion from the pick rather than the release read, a runtime is a fact about the machine. the offline arm has no release read to consult
        offer.Pick.CudartZipName,
        offer.Assets?.Companion is { } c ? SizeWords.Auto(c.Size) : null);

    //the one typed-row placeholder for every engine screen that takes a path, so the door reads the same everywhere. take the glyph set as an argument rather than a const, tests must name the vocabulary they assert
    internal static string LlamaDoorPlaceholderOf(Gatto.Terminal.GlyphSet g) =>
        $"type the path to llama-server.exe{g.Ellipsis}";

    //the hub shelf's typed row, taken from the drawn corpus rather than written anew
    internal static string ShelfDoorPlaceholderOf(Gatto.Terminal.GlyphSet g) =>
        $"search models{g.Ellipsis}";

    //the local shelf's longer placeholder, from the local shelf's own goldens, its door both searches the machine and takes a path
    internal static string LocalShelfDoorPlaceholderOf(Gatto.Terminal.GlyphSet g) =>
        $"type a .gguf to add, a folder to look in{g.Ellipsis}";

    //the one title for every screen of the engine step, a title that varied would read as a different step. it asks which build gatto should use
    internal const string EngineTitle = "Which engine build fits this machine?";

    //the model step's question, one function rather than two constants (a caller that picks could drift)
    internal static string ModelTitleFor(bool inSession) =>
        inSession ? "Which model should gatto add?" : "Which model should gatto start with?";

    //this flow's own question, for the road it is on.
    private string ModelTitle => ModelTitleFor(_addRoad);

    //the one door label for every screen that offers the connect road, so the spelling cannot drift. it names two servers so the reader recognises their own case
    internal const string ConnectDoorLabel =
        "Use a server you start yourself, LM Studio, Ollama, or another";

    //the one home for the opening fact of why this machine gets this build, both variables highlighted
    private static SetupRow SteeringRow(Gatto.Roles.Serve.LlamaAsset asset) =>
        new($"According to your hardware, {asset.HardwareFact}, you need the {asset.BuildName} build.",
            Highlight: [asset.HardwareFact, asset.BuildName]);

    private SetupScreen NextLlamaSegment()
    {
        //sweep every engine rather than reading one configured path, a machine with two builds is the case this screen exists for
        foreach (var engine in probes.EngineSweep())
        {
            //read the build from the verify that already ran, so the flow does not probe twice. the complaint sentences stay LlamaProbeComplaint's
            var result = probes.VerifyLlamaServer(engine.Path);

            //the screen names the exe that actually ran, and a use-it writes that one.
            if (result.Shape is Gatto.Core.Acquire.ProbeShape.ClassicServer && result.Build is { } build)
                return Found(result.Ran ?? engine.Path, build,
                    Gatto.Roles.Serve.LlamaAssetSteering.Classify(build, engine.GattoFolder));

            //an exe that fails verification is not a found engine, the sweep is gatto's guess. a skipped engine goes into a note instead of a screen
            _narrate.Add(new SetupScreen.Info(EngineSkippedKey,
                [new SetupRow(
                    $"skipped {System.IO.Path.GetFileName(engine.Path)}, {SweepSkipReason(result)}",
                    RowTone.Aside)]));
        }

        return NoEngine();
    }


    //the short note for why the sweep dropped a candidate, written for the record rather than for the user
    private static string SweepSkipReason(Gatto.Core.Acquire.ProbeResult result) => result.Shape switch
    {
        Gatto.Core.Acquire.ProbeShape.KnownSibling when result.SiblingName is { Length: > 0 } =>
            "not the server",
        Gatto.Core.Acquire.ProbeShape.NotClassic => "it did not answer as the server",
        Gatto.Core.Acquire.ProbeShape.TimedOut => "it did not answer in time",
        Gatto.Core.Acquire.ProbeShape.DllNotFound => "this Windows could not load it",
        _ => "gatto could not run it",
    };

    //the found screen states three facts and offers the two roads out. it asks nothing about whether the engine exists, the title already asks which build to run
    private SetupScreen Found(string path, string build, Gatto.Roles.Serve.LlamaAssetSteering.FoundState state)
    {
        //hold the exe the screen named, a second sweep at answer time could disagree with the row the user saw
        _foundEngine = path;
        //hold the build for the same reason as the path, the summary must report the build the user saw
        _foundBuild = build;

        var pin = Gatto.Roles.Serve.LlamaAssetSteering.PinnedRelease;
        var name = Gatto.Roles.Serve.LlamaAssetSteering.ReleaseName(build);

        //build the door row once for all four arms, so one ruled label cannot drift into four spellings.
        ChoiceOption door = new(ForkConnect, ConnectDoorLabel);
        ChoiceOption use = new(FoundUse, "Use it");

        //keep the first option as the default, so the order carries the choice and no flag can disagree with the rows
        (string Tail, IReadOnlyList<ChoiceOption> Options, string? Note) arm = state switch
        {
            Gatto.Roles.Serve.LlamaAssetSteering.FoundState.Exact =>
                ($" {Glyphs.Dot} the release gatto is tested with",
                 (IReadOnlyList<ChoiceOption>)[use, door], null),

            Gatto.Roles.Serve.LlamaAssetSteering.FoundState.Newer =>
                ($", newer than {pin}, the release gatto is tested with",
                 [use, new ChoiceOption(FoundFetch, $"Fetch {pin}, the tested release, instead"), door], null),

            //state the two builds and leave the reader to draw the relation. a sentence that calls one older reads as a verdict on a build the user chose.
            Gatto.Roles.Serve.LlamaAssetSteering.FoundState.Older =>
                ($" {Glyphs.Dot} {pin}, the release gatto is tested with",
                 [new ChoiceOption(FoundFetch, $"Fetch {pin}, the tested release"),
                  new ChoiceOption(FoundUse, "Keep the one that's here"), door],
                 "Newer models sometimes need a newer engine, the shelf's \"runs on this machine\" "
                 + "answers assume the tested release."),

            Gatto.Roles.Serve.LlamaAssetSteering.FoundState.Fork =>
                ($", not a release gatto recognises; the tested one is {pin}",
                 [use, new ChoiceOption(FoundFetch, $"Fetch {pin}, the tested release, instead"), door], null),

            _ => throw new ArgumentOutOfRangeException(nameof(state), state, "no found arm for this state"),
        };

        return Emit(new SetupScreen.Choice(
        FoundKey,
        EngineTitle,
        arm.Options,
        BodyRows:
        [
            new SetupRow("llama-server.exe is already on this machine.", Glyph: RowGlyph.Good),
            "",
            EngineFacts.Row(EngineFacts.Found, path),
            //spell the build through ReleaseName, the probe captures what the banner said while gatto writes the b form elsewhere
            EngineFacts.Row(EngineFacts.Build, name + arm.Tail),
            //use the past tense, this reports a run that already happened and the build above came from that same run
            EngineFacts.Row(EngineFacts.Checked, "it ran, and it answered as the server gatto drives"),
            //show the one dim line on the arm where the default flips, it explains why the shelf's answers depend on the engine
            .. arm.Note is null ? (IReadOnlyList<SetupRow>)[] : ["", new SetupRow(arm.Note, RowTone.Aside)],
        ],
        //keep the door idle rather than focused, a user with one engine is not being asked to type
        Door: LlamaDoorPlaceholderOf(Glyphs)), FoundKey);
    }

    //fail closed on a key this screen does not know, treating any other answer as use-it would commit a config write by omission
    private SetupScreen AnswerFound(string optionKey)
    {
        if (optionKey == ForkConnect) return TakeConnectRoad();

        //go straight to the fetch rather than through the consent screen, the pressed option already names it
        if (optionKey == FoundFetch)
            return FetchableOffer() is { Assets: not null } offer ? StartFetch(offer) : Steer();

        if (optionKey != FoundUse)
            throw new InvalidOperationException(
                $"the found screen has no handler for the answer '{optionKey}'");

        //non-null by construction, FoundKey reaches here only from Found
        _llamaPath = _foundEngine!;
        Writes = Writes with { LlamaServer = _foundEngine! };
        return Discover(extraRoot: null);
    }

    //what a fetch of the pinned release would be here, or null when there is none. a null asset means GitHub did not answer
    private sealed record Offer(Gatto.Roles.Serve.LlamaAsset Pick, string Into, EnginePair? Assets);

    //ask whether this machine can be fetched for, the only machine without a fetch is one gatto cannot read, it can name no build
    private Offer? FetchableOffer() =>
        probes.ChooseLlamaAsset() is { } pick && probes.EngineOffer() is { } o
            ? new Offer(pick, o.IntoDir, o.Assets)
            : null;

    //the engine step with nothing found: fetch the engine, fall back to a manual download, or steer when no fetch is possible.
    private SetupScreen NoEngine() =>
        FetchableOffer() is not { } offer ? Steer()
        : offer.Assets is not null ? Consent(offer)
        : Fallback(offer, "gatto couldn't reach GitHub to fetch the engine, so this time the "
            + "download is yours, everything below is exact.");

    //the one question before the engine download, stating what arrives, where from and where it goes. the size is read from GitHub rather than estimated
    private SetupScreen Consent(Offer offer)
    {
        _offer = offer;
        return Emit(new SetupScreen.Choice(
            ConsentKey,
            EngineTitle,
            [
                new ChoiceOption(ConsentFetch, "Fetch it now"),
                new ChoiceOption(ForkConnect, ConnectDoorLabel),
            ],
            BodyRows:
            [
                //lift llama.cpp rather than accent it, the engine is the subject of the sentence
                new SetupRow(
                    "Models don't run by themselves, they need an engine: llama.cpp, the open-source "
                    + "server gatto drives. gatto looked in the usual places and hasn't found it.",
                    RowTone.Aside, Lift: ["llama.cpp"]),
                "",
                SteeringRow(offer.Pick),
            ],
            Engine: EngineBlock(offer, gattoFetches: true),
            //keep the door idle rather than focused, a user who runs their own build points setup at it without leaving the screen
            Door: LlamaDoorPlaceholderOf(Glyphs)), ConsentKey);
    }

    //the offline arm, shown when the release read or the fetch failed, using the same destination the consent screen names
    private SetupScreen Fallback(Offer offer, string why)
    {
        _offer = offer;
        return Emit(new SetupScreen.Choice(
            FallbackKey,
            EngineTitle,
            [new ChoiceOption(FallbackDone, "Done, check that folder")],
            BodyRows: [new SetupRow(why, RowTone.Aside), "", SteeringRow(offer.Pick)],
            Engine: EngineBlock(offer, gattoFetches: false),
            Door: LlamaDoorPlaceholderOf(Glyphs)), FallbackKey);
    }

    //verify what arrived through the same probe every other door runs, so a wrong exe is refused before it runs
    private SetupScreen StartFetch(Offer offer)
    {
        //non-null by construction, both call sites match Assets first
        var assets = offer.Assets!;
        _fetchCancel = new CancellationTokenSource();
        var ct = _fetchCancel.Token;
        //fetch once, a second attempt to read the error could succeed and report a failure gatto no longer has
        _fetching = Task.Run(() => probes.FetchEngine(assets, new TickSink(OnTick), ct), CancellationToken.None);
        return Fetching(offer);
    }

    //the fetch has stopped, either it finished or Esc stopped it
    private SetupScreen AfterFetch(Offer offer, string answer)
    {
        var fetch = _fetching!;
        _fetching = null;

        if (answer == FetchStop)
        {
            _fetchCancel!.Cancel();
            //wait for the task to unwind before reporting, the partial is deleted inside the download's own failing path
            try { fetch.GetAwaiter().GetResult(); } catch (Exception) { } //stopping the fetch is not a failure, so the catch is empty.
            EndFetch();
            //return to the consent screen rather than leaving setup, declining the download is not leaving
            return Consent(offer);
        }

        Gatto.Cli.EngineFetch got;
        //turn a throw from the fetch into a screen, the task has already completed. an unhandled throw would leave the wizard watching a finished subject
        try { got = fetch.GetAwaiter().GetResult(); }
        catch (Exception ex)
        {
            got = new(null, $"gatto couldn't finish the download: {ex.Message}");
        }
        EndFetch();

        if (got.ServerPath is not { } exe)
            return Fallback(offer, got.Error
                ?? "gatto couldn't finish the download, so this time it is yours, everything below "
                + "is exact.");

        var result = probes.VerifyLlamaServer(exe);
        if (LlamaProbeComplaint(result.Shape, offer.Pick.CudartZipName is not null,
                steeredDoor: true, result.VcRuntimeAbsent, result.SiblingName) is { } complaint)
            return Failed(result.Shape, complaint, exe);

        var verified = result.Ran ?? exe;
        _llamaPath = verified;
        Writes = Writes with { LlamaServer = verified };
        //take the zip name from the offer the screen was built from, the arrival sentence names what the user agreed to fetch
        return Fetched(offer, offer.Assets!.Server.ZipName);
    }

    //the engine arrived, so this line states a verification result rather than repeating the answer the user just gave.
    private SetupScreen Fetched(Offer offer, string zipName) => Emit(new SetupScreen.Choice(
        FetchedKey,
        EngineTitle,
        [new ChoiceOption(FetchedNext, "next"), new ChoiceOption(FetchedLeave, "leave")],
        BodyRows:
        [
            new SetupRow("That's the one, fetched, verified, and it answers as the server gatto "
                + "drives.", RowTone.Aside, Glyph: RowGlyph.Good, Lift: ["That's the one"]),
            "",
            EngineFacts.Row("fetched", zipName + $" {Glyphs.Dot} digest matched", lift: zipName),
            EngineFacts.Row("into", offer.Into),
            EngineFacts.Row(EngineFacts.Checked,
                $"{Gatto.Roles.Serve.EngineSearch.ExeName} ran, the path gatto writes to its config is "
                + "the .exe that ran"),
            //keep the closing blank, without it the last fact sits directly on the footer rule
            "",
        ],
        KeysOnly: true,
        //let the face supply the elapsed time from the watch it just ran, a figure composed here would be a second clock
        PurredSinceWatch: true), FetchedKey);

    private SetupScreen AnswerConsent(string optionKey)
    {
        if (optionKey == ForkConnect) return TakeConnectRoad();

        //fail closed on an unknown key, an unrecognised answer must not start a large download by omission
        if (optionKey != ConsentFetch)
            throw new InvalidOperationException(
                $"the consent screen has no handler for the answer '{optionKey}'");

        return StartFetch(_offer!);
    }

    //the screen where gatto does the work, one option only (nothing to choose). reuses the consent's EngineView, so nothing changes while the user watches
    private SetupScreen Fetching(Offer offer)
    {
        _offer = offer;
        return Emit(new SetupScreen.Choice(
            FetchingKey,
            EngineTitle,
            //declare the key even though its face cannot reach this screen. a fact stated only where it matters now turns a later reachability change into a defect
            [new ChoiceOption(FetchStop, "stop, deletes the partial", Advances: false)],
            BodyRows:
            [
                //state the screen's whole job in one aside, it is context for the rows below. a companion download gets its own sentence, both files must reach one folder
                new SetupRow(offer.Assets?.Companion is null
                    ? "Nothing to do here, gatto fetches the build, checks it against GitHub's own "
                        + "digest, and moves on by itself."
                    : "Nothing to do here, gatto fetches both files into one folder, checks each "
                        + "against GitHub's own digest, and moves on by itself.", RowTone.Aside),
            ],
            Engine: EngineBlock(offer, gattoFetches: true),
            Watching: true,
            KeysOnly: true), FetchingKey);
    }

    //one entry for both endings of the watch, each means the fetch has stopped and which one it was is the parameter
    private SetupScreen AnswerFetching(string answer) => AfterFetch(_offer!, answer);

    //answer with the folder rather than a composed exe path, one resolution rule finds the server inside it
    private SetupScreen AnswerFallback(string optionKey) =>
        optionKey == FallbackDone
            ? VerifyLlama(_offer!.Into)
            : throw new InvalidOperationException(
                $"the fallback screen has no handler for the answer '{optionKey}'");

    private SetupScreen AnswerFetched(string optionKey) =>
        optionKey == FetchedNext ? Discover(extraRoot: null) : Leave();

    //the screen for a machine gatto cannot read, it states no build and offers no fetch (gatto must not guess a build it cannot price)
    private SetupScreen Steer() =>
        Emit(new SetupScreen.Choice(
            SteerKey,
            EngineTitle,
            [new ChoiceOption(ForkConnect, ConnectDoorLabel)],
            Door: LlamaDoorPlaceholderOf(Glyphs),
            BodyRows:
            [
                "Couldn't read this machine well enough to pick a llama.cpp build.",
                "",
                "You can download any llama.cpp Windows build and point gatto at llama-server.exe.",
                "",
                //name where to get a build, this branch has the least to go on. the url comes straight from LlamaAssetSteering
                new("All builds:", RowTone.Aside),
                new(Gatto.Roles.Serve.LlamaAssetSteering.ReleasePage, RowTone.Aside),
                "",
                "Once you're done, type the path below.",
            ]),
            SteerKey);

    //hint pasting rather than typing, people paste paths and a pasted folder works too. name a folder, the screen above tells the user to extract into one
    internal const string LlamaPathPlaceholder = @"Type or paste the path, e.g. C:\llama";

    //the one home for the path rule, shared by the three screens that ask for a path.
    private static string? LlamaPathComplaint(string typed) =>
        Gatto.Roles.Serve.LlamaAssetSteering.NormalizePath(typed).Length == 0
            ? "Paste the path to llama-server.exe"
            : null;

    //drive every branch off ProbeShape rather than parsing the output, and leave the cuda-no-device shape out until a real run captures it
    private SetupScreen VerifyLlama(string path)
    {
        var result = probes.VerifyLlamaServer(path);
        if (LlamaProbeComplaint(result.Shape, probes.ChooseLlamaAsset()?.CudartZipName is not null,
                steeredDoor: true, result.VcRuntimeAbsent, result.SiblingName)
            is { } complaint) return Failed(result.Shape, complaint, path);

        //write the exe the probe ran, writing back the folder would store a path that cannot start a server while claiming verification
        var verified = result.Ran ?? path;
        _llamaPath = verified;
        Writes = Writes with { LlamaServer = verified };
        //keep this narration line, it reports a verification result rather than repeating the answer, and the channel hands it to a face
        _narrate.Add(new SetupScreen.Info("llama.ok", [new SetupRow($"{Glyphs.Ok} That's the one")]));
        return Discover(extraRoot: null);
    }

    //the one home for what each probe shape means, shared by both verification paths. the cudart cause is certain only on the steered door
    private static string? LlamaProbeComplaint(
        Gatto.Core.Acquire.ProbeShape shape, bool cudartExpected, bool steeredDoor,
        bool vcRuntimeAbsent, string? sibling) => shape switch
    {
        Gatto.Core.Acquire.ProbeShape.ClassicServer => null,

        //name the right file beside the wrong one, a release ships many programs that answer alike and the user is one edit away
        Gatto.Core.Acquire.ProbeShape.KnownSibling when sibling is { Length: > 0 } =>
            $"That is {sibling}, the server is {Gatto.Core.Acquire.LlamaSiblings.ServerName} "
            + "in the same folder.",

        //check the runtime first, a missing Visual C++ runtime cannot be fixed by anything beside the exe
        Gatto.Core.Acquire.ProbeShape.DllNotFound when vcRuntimeAbsent =>
            "That build needs the Microsoft Visual C++ runtime, and this Windows doesn't have it. "
            + "Install the 'Visual C++ Redistributable (x64)' from Microsoft.",

        //the measured exit code is the signal, with empty output streams.
        Gatto.Core.Acquire.ProbeShape.DllNotFound when cudartExpected && steeredDoor =>
            "That build starts and immediately stops, its CUDA runtime is missing. "
            + "Extract the cudart zip into the SAME folder.",

        //name no cause without a discriminator, so the sentence states what was observed and offers a conditional hint.
        Gatto.Core.Acquire.ProbeShape.DllNotFound =>
            "That build starts and immediately stops, Windows says a file it needs is missing. "
            + "Check that everything from the download was extracted into the same folder."
            + (cudartExpected
                ? " If this is a CUDA build, it also needs the cudart zip extracted beside it."
                : ""),

        //three programs are called llama.cpp and only one shape works, so the sentence names the file to look for.
        Gatto.Core.Acquire.ProbeShape.NotClassic =>
            "That runs, but it isn't the llama-server gatto drives, some builds ship "
            + "a single combined program instead. Look for llama-server.exe specifically.",

        Gatto.Core.Acquire.ProbeShape.TimedOut =>
            "That didn't answer in time. If it's on a network drive, try a local copy.",

        _ => "Couldn't run that, check the path points at llama-server.exe itself.",
    };

    //two failure arms, re-checking the same path helps only when the file is right, otherwise the typed door is the only move
    private SetupScreen Failed(Gatto.Core.Acquire.ProbeShape shape, string sentence, string tested)
    {
        _foundEngine = tested;

        IReadOnlyList<SetupRow> body =
        [
            //accent the exe name inside the sentence, it is the word to look for, and the highlight skips a span that is absent
            new SetupRow(sentence, Highlight: [Gatto.Roles.Serve.EngineSearch.ExeName], Glyph: RowGlyph.Bad),
            "",
            EngineFacts.Row(EngineFacts.Tested, tested),
        ];

        //name the runtime shape explicitly, only there does re-checking the same path help. every other shape falls to the typed door
        if (shape is Gatto.Core.Acquire.ProbeShape.DllNotFound)
            return Emit(new SetupScreen.Choice(
                FailedKey,
                EngineTitle,
                [new ChoiceOption(FailedRetry, "Check it again")],
                BodyRows: body,
                Door: LlamaDoorPlaceholderOf(Glyphs)), FailedKey);

        return Emit(new SetupScreen.Ask(
            LlamaPathKey,
            //use the step's title as the label, the bad row says what happened and this says where in the flow the user is
            Label: EngineTitle,
            Validate: LlamaPathComplaint,
            Placeholder: LlamaDoorPlaceholderOf(Glyphs),
            BodyRows: body,
            EnterVerb: "check the path"), LlamaPathKey);
    }
}
