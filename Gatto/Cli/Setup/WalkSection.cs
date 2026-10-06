using Gatto.Terminal;

namespace Gatto.Cli.Setup;

//the walk's named steps and the screen each one holds, a table so a new screen cannot ship sectionless
internal static class WalkSection
{
    public const string Machine = "machine";
    public const string Engine = "engine";
    public const string Model = "model";
    public const string Server = "server";
    public const string Check = "check";
    public const string Done = "done";

    //the llama road in order, since everything before the current section reads done and everything after reads pending
    public static readonly string[] LlamaRoad = [Machine, Engine, Model, Check, Done];

    //the connect road, where engine and model collapse into one server section instead of being ticked off unvisited
    public static readonly string[] ConnectRoad = [Machine, Server, Check, Done];

    //the road of /model add, three sections since a live session already answered the machine and the engine. the entry picks it
    public static readonly string[] InSessionRoad = [Model, Check, Done];

    //which section a screen sits in, keyed by the whole screen key since model.audition.offer is the check
    public static readonly IReadOnlyDictionary<string, string> Of = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        //machine
        ["machine"] = Machine,

        //engine, and the fork counts as engine even though its narration marker says model
        ["step.model"] = Engine,
        ["llama.found"] = Engine,
        ["llama.failed"] = Engine,
        ["engine.skipped"] = Engine,
        ["llama.consent"] = Engine,
        ["llama.fetching"] = Engine,
        ["llama.fallback"] = Engine,
        ["llama.fetched"] = Engine,
        ["llama.steer"] = Engine,
        ["llama.path"] = Engine,
        ["llama.ok"] = Engine,

        //server
        ["connect.retry"] = Server,
        ["connect.confirm"] = Server,
        ["connect.context"] = Server,
        ["connect.model"] = Server,
        ["connect.modelname"] = Server,

        //model
        ["model.none"] = Model,
        ["model.found"] = Model,
        ["model.search"] = Model,
        ["model.loading"] = Model,
        //the picker is part of choosing a model, since it is the shelf's own control opened full-screen
        ["model.publisher"] = Model,
        ["model.typedid"] = Model,
        ["model.scanpath"] = Model,
        ["model.download"] = Model,
        //the in-app fetch is the model step, since the subject decides the section and the subject is which model gatto will run
        ["model.consent"] = Model,
        ["model.fetching"] = Model,
        //a paused fetch is the same section, since the walk has stopped rather than moved
        ["model.fetching.paused"] = Model,
        ["model.arrived"] = Model,
        ["model.dropped"] = Model,
        ["model.mismatch"] = Model,
        ["model.partial"] = Model,
        ["model.move"] = Model,
        ["model.move.path"] = Model,
        ["model.collision"] = Model,
        //the done step's quant question is still the model section, since a file joining a model is a fact about that model
        ["model.quantfromnow"] = Model,
        ["model.reused"] = Model,
        //the join's narration, said before its question, in the same section as the question
        ["model.quantadded"] = Model,
        //both rows name llama.cpp and still sit in the model step, since they are settings on the model just chosen
        ["projector"] = Model,
        ["custom.build"] = Model,
        ["custom.build.path"] = Model,

        //check
        ["step.first"] = Check,
        ["model.audition.offer"] = Check,
        ["model.audition.insession"] = Check,
        ["model.audition.held"] = Check,
        ["model.audition.incomplete"] = Check,
        ["model.audition.stillloading"] = Check,
        ["model.audition.exited"] = Check,
        ["model.audition.stopped"] = Check,
        ["model.audition.running"] = Check,
        ["model.audition.waiting"] = Check,
        ["model.audition.answers"] = Check,
        ["model.audition.failed"] = Check,
        ["model.audition.passed"] = Check,
        ["prove"] = Check,

        //done, and the consent question belongs here since it is asked after the check and before the terminal
        ["update"] = Done,
        ["done"] = Done,
        //the summary is the done step's last screen
        ["done.summary"] = Done,
        //these belong to the done step, since the install question is its first question and the section already has a heading
        ["install"] = Done,
        ["install.update"] = Done,
        ["segment.install"] = Done,
    };

    //screens deliberately left off the strip, each with its reason, since an empty strip draws no cursor
    public static readonly IReadOnlyDictionary<string, string> NotOnTheStrip = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["welcome"] = "the map itself: it SHOWS the strip's road before the walk starts, so no "
            + "section is live and no cursor is drawn",

        ["left"] = "leaving is not a step of the walk. The strip describes where you ARE, and this "
            + "screen is where you are not",
        ["back.refused"] = "unreachable by construction (the flow stamps AllowBack), and a screen no "
            + "walk arrives at has no position to report",

        //returned, then consumed before any face sees them
        ["writes.pending"] = "never shown BY CONSTRUCTION: the runner applies the write pause before "
            + "the switch can render it",
        ["default.ready"] = "the Done-pause, consumed by the same branch",
        ["consent.ready"] = "the consent flush, consumed by the same branch",

        //constructed outside the flow, by SetupRunner, listed here so the census has no blind spot
        ["write.failed"] = "constructed by SetupRunner, outside the flow's stamping sites",
        ["move.incomplete"] = "constructed by SetupRunner, outside the flow's stamping sites",
    };

    //the strip for a screen, road and position, with no cursor when the section is on another road
    public static IReadOnlyList<StripSection> For(string? screenKey, SetupPath path, bool inSession = false)
    {
        //the in-session flag wins over the path, since /model add enters the llama path at its model segment
        var road = inSession ? InSessionRoad : path == SetupPath.Connect ? ConnectRoad : LlamaRoad;
        var current = screenKey is not null && Of.TryGetValue(screenKey, out var section)
            ? Array.IndexOf(road, section)
            : -1;

        return
        [
            .. road.Select((name, i) => new StripSection(name,
                current < 0 || i > current ? StripState.Pending
                : i < current ? StripState.Done
                : StripState.Current))
        ];
    }
}
