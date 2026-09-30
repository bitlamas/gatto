using System.Diagnostics;
using Gatto.Core;
using Gatto.Core.Acquire;
using Gatto.Core.Client;
using Gatto.Core.Home;
using Gatto.Core.Loop;
using Gatto.Core.Loop.Permissions;
using Gatto.Core.Tools;

namespace Gatto.Roles.Audition;

//two engine failures a screen must tell apart, one before the model loaded and one mid-battery, and neither widens the verdict past CouldNotRun
internal enum AuditionStumbleKind
{
    //the recorded pid was gone before the server ever answered
    ExitedWhileLoading,

    //the battery was running and the connection failed with the pid already gone, a live server's hiccup must not read as a death
    StoppedMidCheck,
}

//a pass is a measurement only if the stamp names the build, the sampling and the server that served it, from its own build_info
internal sealed record AuditionStamp(string GattoBuild, string ModelFileName, string? Quant,
    int Context, string SamplingNote, string ThinkingNote = "", string? Server = null,
    //the repo id read off the measured profile, a badge without one is invisible on the shelf. null when the model came off disk and has no repo
    string? RepoId = null);

//wall clock and decode rate are evidence and never decide the verdict, a slow model can still pass
internal sealed record AuditionVerdict(bool Pass, bool Disqualified,
    IReadOnlyList<AuditionTaskResult> Tasks, AuditionStamp Stamp,
    TimeSpan WallClock, double? DecodeTokS)
{
    //the tally both the report and the wizard read, M counts the tasks that ran and a skipped task is neither pass nor failure
    public (int Passed, int Ran) Tally()
    {
        var ran = Tasks.Count - Tasks.Count(t => t.Skipped);
        return (ran - Tasks.Count(t => t.Failed), ran);
    }
}

//the check drives gatto's real loop, a copy would only measure the copy. the offer binds to RunAsync, RunBatteryAsync is the injectable half beneath it
internal static class AuditionRunner
{
    //the pass mark is 4 of 5, a threshold chosen after seeing results is not a threshold
    public const int PassThreshold = 4;

    private sealed class ScratchContext(string cwd, string home) : IToolContext
    {
        public string Cwd => cwd;
        public string HomePath => home;
        //null by design, nobody is present to answer. a task that asks a question gets the loop's no-prompter error, a result rather than a hang
        public IUserPrompter? Prompter => null;
    }

    //summed over the rounds rather than averaged, and taken from the server's own timings (a wall clock rate would fold prefill in)
    private static double? DecodeRate(IEnumerable<ChatMessage> messages) =>
        Gatto.Core.Client.ServerTimings.DecodeRate(messages.Select(m => m.Timings));

    //resolves the model, ensures a server, runs the battery, leaves the machine as found. a harness failure throws GattoConfigException, and the model is not blamed
    public static Task<AuditionVerdict> RunAsync(string homePath, string modelId,
        IProgress<AuditionProgress>? progress, CancellationToken ct, EngineMarks? engineMarks = null,
        Func<TextWriter, IServeListener>? serveVoice = null,
        Func<TimeSpan, string>? loadingWords = null)
        => RunAsync(homePath, modelId, progress, ct, manager: null, readyBudget: null,
            engineMarks: engineMarks, serveVoice: serveVoice, loadingWords: loadingWords);

    //ten minutes for a server to become ready, a shorter budget would fail a slow load and the verdict would blame the model
    internal static readonly TimeSpan ReadyBudget = TimeSpan.FromMinutes(10);

    //when the wizard asks whether to keep waiting, five minutes since an ask costs one Enter, and no screen quotes the number
    internal static readonly TimeSpan LoadAskAfter = TimeSpan.FromMinutes(5);

    //the run stops as soon as the pass mark is out of reach, and a disqualified run is decided at any score
    private static bool VerdictDecided(int failures, bool disqualified) =>
        disqualified || failures > Battery.V1.Count - PassThreshold;

    //the two ways a run is cut, a token cap and a stall, kept apart for the report to name the culprit
    private static FailureShape ShapeOf(StopReason reason) => reason switch
    {
        StopReason.TokenCap => FailureShape.StoppedAtCap,
        StopReason.Silence => FailureShape.Stalled,
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "not a cut"),
    };

    //the manager and the budget are the seams a test drives through. the words for a serve failure come from the caller, passing none reads no sentence
    internal static async Task<AuditionVerdict> RunAsync(string homePath, string modelId,
        IProgress<AuditionProgress>? progress, CancellationToken ct,
        ServeManager? manager, TimeSpan? readyBudget, EngineMarks? engineMarks = null,
        Func<TextWriter, IServeListener>? serveVoice = null,
        Func<TimeSpan, string>? loadingWords = null)
    {
        var marks = engineMarks ?? EngineMarks.Unicode;
        var config = GattoConfig.Load(homePath);
        var model = Model.Load(Path.Combine(homePath, "models"), modelId);   //throws GattoConfigException

        RequireModelOnDisk(model, modelId, homePath);

        manager ??= new ServeManager(homePath, config.LlamaServer ?? "");

        //one read of the serving fact, and its three arms are the three things that can be true: idle, this model, another model
        var stopAfter = manager.Serving(modelId).Match(
            idle: () => true,
            servingThis: r =>
            {
                progress?.Report(AuditionProgress.Reusing($"using the server already running for {r.Model}"));
                return false;
            },
            //refuse before asking for a spawn, taking this server would unload the model the user is mid-conversation with and no one consented
            servingOther: r => throw new GattoConfigException(
                $"'{r.Model}' is the running server, so the check for '{modelId}' could not start "
                + $"one. Nothing was measured, and this says nothing about the model."));

        if (stopAfter)
        {
            //starting a server on someone's machine is not a silent act, this note comes before the spawn
            progress?.Report(AuditionProgress.Note($"starting {modelId}{marks.Ellipsis}"));

            //capture llama-server's output, a death during load needs its last words. the still-loading advice is dropped, the readiness wait below answers that properly
            var startLog = new StringWriter();
            var code = await manager.StartAsync(
                model, serveVoice?.Invoke(startLog) ?? NullServeListener.Instance, ct).ConfigureAwait(false);
            if (code != 0)
                throw new GattoConfigException(StartRefused(modelId, startLog.ToString(), marks));

            //the throw stays outside the try/finally, a refused start never spawned anything and stopping it would kill a session this run did not start
        }

        try
        {
            await RequireReadyAsync(manager, model, modelId, stopAfter,
                readyBudget ?? ReadyBudget, progress, loadingWords, ct).ConfigureAwait(false);

            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };  //the client shape every gatto call site uses
            var endpoint = new EndpointConfig($"http://127.0.0.1:{model.Profile.Port}",
                Context: model.Profile.Context);
            //name the runner's own endpoint in the down hint, the generic network/base_url advice points at things this endpoint does not have
            var client = new OpenAiCompatClient(http, "audition", endpoint,
                downHint: $"the server for '{modelId}' on port {model.Profile.Port} stopped answering. "
                          + $"Run gatto serve status, then gatto audition {modelId} again");

            var loaded = await ServeProbe.ProbeAsync(http, endpoint.BaseUrl!, ct).ConfigureAwait(false);
            try
            {
                return await RunBatteryAsync(client, homePath, modelId,
                    StampFor(model, loaded), progress, ct,
                    //pass the model's own reasoning_history, the default would measure a heavier setup than the model actually runs
                    reasoning: model.Profile.ReasoningHistory).ConfigureAwait(false);
            }
            catch (GattoConnectionException) when (manager.DescribeRunning() is null)
            {
                //the condition is the recorded pid being gone, an ordinary connection error from a live server must not be caught here
                throw new AuditionStumbleException(
                    $"the server for '{modelId}' stopped while the check was running. Nothing was "
                    + $"measured, and this says nothing about the model." + LogTail(manager),
                    AuditionStumbleKind.StoppedMidCheck, manager.RecentLogLines(TailLines));
            }
        }
        finally
        {
            //leave as found, stopping a server this run did not start would be the audition helping itself to the machine
            if (stopAfter) await StopAndSayIfItFailedAsync(manager, progress, serveVoice).ConfigureAwait(false);
        }
    }

    //wait for the server to answer, a failure to answer is a harness failure. the words depend on who started the server, ours is about to be stopped
    private static async Task RequireReadyAsync(ServeManager manager, Model model, string modelId,
        bool ours, TimeSpan budget, IProgress<AuditionProgress>? progress,
        Func<TimeSpan, string>? loadingWords, CancellationToken ct)
    {
        //the loading words and their moments come from the caller, a table of seconds here would be a second home for that decision
        var lastWords = "";
        var readiness = await manager.AwaitReadyAsync(
            model.Profile.Port, budget,
            elapsed =>
            {
                if (loadingWords?.Invoke(elapsed) is not { } words || words == lastWords) return;
                lastWords = words;
                progress?.Report(AuditionProgress.Note(words));
            },
            ct).ConfigureAwait(false);

        switch (readiness)
        {
            case ServerReadiness.Ready:
                return;

            case ServerReadiness.Died:
                throw new AuditionStumbleException(
                    $"the server for '{modelId}' exited while loading the model. Nothing was measured."
                    + LogTail(manager),
                    AuditionStumbleKind.ExitedWhileLoading, manager.RecentLogLines(TailLines));

            default:
                throw new GattoConfigException(ours
                    ? $"the server for '{modelId}' was still loading after {Roughly(budget)}. Nothing was "
                      + $"measured, and this says nothing about the model. a model that barely fits can "
                      + $"take longer than that: run gatto serve start {modelId}, wait for it to report "
                      + $"ready, then run gatto audition {modelId}."
                    : $"the server already running for '{modelId}' has not answered in {Roughly(budget)}. "
                      + $"Nothing was measured. gatto serve status says when it is ready; then run "
                      + $"gatto audition {modelId}.");
        }
    }

    //a stop that fails is said out loud, and a throw from it must not replace the exception the run already has
    private static async Task StopAndSayIfItFailedAsync(ServeManager manager,
        IProgress<AuditionProgress>? progress, Func<TextWriter, IServeListener>? serveVoice)
    {
        try
        {
            var log = new StringWriter();
            var code = await manager.StopAsync(
                serveVoice?.Invoke(log) ?? NullServeListener.Instance, CancellationToken.None).ConfigureAwait(false);
            if (code == 0) return;
            progress?.Report(AuditionProgress.Note(
                "couldn't stop the server audition started. Run gatto serve stop. "
                + log.ToString().Trim()));
        }
        catch (Exception ex)
        {
            try
            {
                progress?.Report(AuditionProgress.Note(
                    $"couldn't stop the server audition started. Run gatto serve stop. {ex.Message}"));
            }
            catch (Exception) { }
        }
    }

    //the generic sentence stays as the fallback for a start failure we cannot name, an unpredicted one would read as gatto's bug
    private static string StartRefused(string modelId, string captured, EngineMarks marks)
    {
        var said = captured.Trim();
        return $"could not start the server for model '{modelId}'"
            + (said.Length > 0
                ? $":{Environment.NewLine}{Indent(said)}"
                : $" {marks.Dot} run gatto serve start {modelId} to see why");
    }

    //the log lines come over as a list, a second read of the rolling log would show different lines than the sentence
    internal sealed class AuditionStumbleException(
        string message, AuditionStumbleKind kind, IReadOnlyList<string> lastLines)
        : GattoConfigException(message)
    {
        public AuditionStumbleKind Kind { get; } = kind;

        public IReadOnlyList<string> LastLines { get; } = lastLines;
    }

    //four at the screen, ten in the message, a 30-row terminal cannot hold ten wrapped lines plus the frame
    internal const int TailLines = 4;

    private static string LogTail(ServeManager manager)
    {
        var tail = manager.RecentLogLines(10);
        //a blank line before the evidence, the sentence is the finding and the log lines are the exhibit
        return tail.Count == 0
            ? string.Empty
            : $"{Environment.NewLine}{Environment.NewLine}the server's last {tail.Count} log lines:{Environment.NewLine}"
              + Indent(string.Join(Environment.NewLine, tail));
    }

    private static string Indent(string block) =>
        string.Join(Environment.NewLine,
            block.Split('\n').Select(l => "  " + l.TrimEnd('\r')));

    //a duration in the words a waiting person uses, taken from the time actually waited rather than a number written into copy

    //the plural comes off the rounded number shown, and this is a verbatim twin of the Roughly in TaskWords
    private static string Roughly(TimeSpan t) =>
        t.TotalSeconds < 90
            ? Plural.Of((long)Math.Max(1, Math.Round(t.TotalSeconds)), "second")
            : Plural.Of((long)Math.Round(t.TotalMinutes), "minute");

    //refuse before anything starts when a model file or its projector is gone, the check lives in ModelDiscovery.MissingFiles
    private static void RequireModelOnDisk(Model model, string modelId, string homePath)
    {
        var missing = ModelDiscovery.MissingFiles(model.Profile.ActivePath, model.Profile.MmProj);
        if (missing.Count == 0) return;

        //name the file, then say what to do, the rule for every failure gatto prints
        var profile = Path.Combine(homePath, "models", modelId, "profile.json");
        var (what, key) = missing.Model switch
        {
            { Count: 1 } m =>
                ($"the model file for model '{modelId}' is not there:{Environment.NewLine}  {m[0]}",
                 "files"),
            { Count: > 1 } m =>
                ($"{m.Count} of the model's files for model '{modelId}' are not there, starting with:"
                 + $"{Environment.NewLine}  {m[0]}", "files"),
            //weights present and the projector gone, say that and point at the key that names it
            _ => ($"the vision projector for model '{modelId}' is not there:"
                  + $"{Environment.NewLine}  {missing.Projector[0]}", "mmproj"),
        };
        throw new GattoConfigException(
            $"{what}{Environment.NewLine}"
            + $"put the file back, or point \"{key}\" at where it lives now in {profile}");
    }

    //the build, the file and the sampling that make the pass attributable, and the quant comes from the file name or is null
    internal static AuditionStamp StampFor(Model model, LoadedModel? loaded)
    {
        var file = Path.GetFileName(model.Profile.ActivePath);
        //the quant rule lives in QuantToken.Of with the shelf and serve status, anchored to a whole segment. an underscore-separated name answers null, the honest answer
        var quant = Gatto.Core.Acquire.QuantToken.Of(file);
        return new AuditionStamp(Gatto.Core.GattoVersion.Build, file, quant,
            loaded?.NCtx ?? model.Profile.Context, SamplingNote(model), ThinkingNoteFor(model),
            //what the server reported, the same source as the context above, null when the server said nothing about its build
            loaded?.BuildInfo,
            //read off the measured profile, the fact travels with the measurement and a caller cannot stamp a model it never auditioned
            model.Profile.Source?.RepoId);
    }

    //the thinking state actually sent, the check does not apply the model's thinking map so the battery runs at the template default
    private static string ThinkingNoteFor(Model model) =>
        model.Profile.Thinking is { Count: > 0 }
            ? "template default (the model's thinking setting is not applied by this check)"
            : "template default";

    //the sampling actually sent, "defaults" when the profile overrides nothing, a blank row would read like missing data
    private static string SamplingNote(Model model) =>
        model.Profile.Sampling is { } s && s.ValueKind == System.Text.Json.JsonValueKind.Object
            && s.EnumerateObject().Any()
            ? string.Join(", ", s.EnumerateObject().Select(p => $"{p.Name} {p.Value}"))
            : "defaults";

    //runs the battery against the client and grades it
    internal static async Task<AuditionVerdict> RunBatteryAsync(IChatClient client, string homePath,
        string model, AuditionStamp stamp, IProgress<AuditionProgress>? progress, CancellationToken ct,
        TaskLimits? taskLimits = null, ReasoningHistory reasoning = ReasoningHistory.All)
    {
        var limits = taskLimits ?? TaskLimits.Default;
        var wall = Stopwatch.StartNew();
        var results = new List<AuditionTaskResult>();
        var everyMessage = new List<ChatMessage>();
        var root = Path.Combine(Path.GetTempPath(), "gatto-audition-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            foreach (var task in Battery.V1)
            {
                ct.ThrowIfCancellationRequested();
                var index = results.Count + 1;
                progress?.Report(AuditionProgress.Started(task, index, Battery.V1.Count));

                var scratch = Path.Combine(root, task.Id);
                Directory.CreateDirectory(scratch);
                var instance = task.Arrange(scratch);

                var tools = StandardTools();
                var hooks = new HookBus();
                //the permission store is the scratch folder's own, the user's store is never read or written even if autoYes goes away
                var store = PermissionStore.Load(scratch, out _);
                //auto-approve is safe only here, the tasks are fixed and stay in the scratch folder (headless would grade denials as model failures)
                hooks.On(HookEvent.ToolCall,
                    new PermissionGate(store, prompter: null, autoYes: true).CheckAsync);

                //a fresh loop and conversation per task (passing only after a few warm-ups doesn't count)
                var loop = new AgentLoop(client, tools, hooks, new ScratchContext(scratch, homePath), model,
                    reasoningHistory: reasoning);
                var convo = new Conversation(SystemPrompt);

                var perTask = Stopwatch.StartNew();
                //the task's own token linked to the caller's, so only the watchdog's cut is caught below and a real Ctrl+C still aborts the run
                using var taskCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var watchdog = new TaskWatchdog(limits, taskCts);

                TurnResult turn;
                try
                {
                    turn = await loop.RunTurnAsync(convo, instance.Prompt, watchdog, taskCts.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    //a backstop for the one path that throws, cancellation normally comes back as TurnResult.Cancelled
                    turn = new TurnResult(TurnOutcome.Cancelled, null, null, 0, null);
                }
                perTask.Stop();

                //read the cut off the watchdog, the loop returns Cancelled instead of throwing, and the caller's token tells a real Ctrl+C from our cut
                var stopped = ct.IsCancellationRequested ? StopReason.None : watchdog.Reason;

                //the advertised set comes from the registry, a second hand-kept list would drift and UnknownTool would be measured against it
                var advertised = tools.Specs().Select(s => s.Name).ToList();
                var graded = Grader.Grade(task, instance, scratch, convo.Messages, turn, advertised);
                //the cut's shape is added to the grader's verdict, what the model did before the cut still counts and a fabrication stays disqualifying
                if (stopped != StopReason.None)
                    graded = graded with
                    {
                        Pass = false,
                        Shapes = [.. graded.Shapes, ShapeOf(stopped)],
                        Silence = stopped == StopReason.Silence ? watchdog.Silence : null,
                    };
                results.Add(graded with { Elapsed = perTask.Elapsed });
                everyMessage.AddRange(convo.Messages);

                progress?.Report(AuditionProgress.Done(task, index, Battery.V1.Count, graded.Pass));

                //the early exit sits after the report of the task just graded, the screen never loses a result the run produced
                if (VerdictDecided(results.Count(r => !r.Pass),
                        results.Any(r => r.Shapes.Contains(FailureShape.FabricatedResult))))
                {
                    foreach (var rest in Battery.V1.Skip(results.Count))
                        results.Add(new AuditionTaskResult(rest.Id, Pass: false, [], TimeSpan.Zero,
                            rest.Label, Skipped: true, Act: rest.Act));
                    progress?.Report(AuditionProgress.Note(
                        "stopped early. Enough tasks had failed that the result was already clear"));
                    break;
                }
            }
        }
        finally
        {
            //the scratch goes away with the run, the session JSONL is the evidence that stays
            try { Directory.Delete(root, true); } catch (Exception) { }
        }

        wall.Stop();
        var passed = results.Count(r => r.Pass);
        var disqualified = results.Any(r => r.Shapes.Contains(FailureShape.FabricatedResult));

        //a fabrication disqualifies at any score, the count of passes cannot outvote it
        return new AuditionVerdict(passed >= PassThreshold && !disqualified, disqualified,
            results, stamp, wall.Elapsed, DecodeRate(everyMessage));
    }

    //the battery's tool set, minus the memory tools, offering one would let a task pass by remembering
    private static ToolRegistry StandardTools()
    {
        var tools = new ToolRegistry();
        tools.Register(new ReadFileTool());
        tools.Register(new WriteFileTool());
        tools.Register(new EditFileTool());
        tools.Register(new GlobTool());
        tools.Register(new GrepTool());
        tools.Register(new ShellTool());
        return tools;
    }

    //kept plain, nudging the model here would measure the nudge rather than what the GGUF does in the loop
    private const string SystemPrompt =
        "You are working in the current directory. Use the available tools to complete the task, "
        + "then state the result plainly. If a tool reports an error, say so rather than guessing.";
}
