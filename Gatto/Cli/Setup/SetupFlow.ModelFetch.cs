using Gatto.Cli.Setup.Shelves;
using Gatto.Core.Acquire;

namespace Gatto.Cli.Setup;

//a model's fetch from the hub: consent, fetching, paused, arrived, dropped, mismatch and the typed root
internal sealed partial class SetupFlow
{

    //strip the -GGUF suffix, the sentence is about the model rather than the quantized files. this stays a single-purpose helper, other screens name a file
    internal static string ModelDisplayName(string repoId)
    {
        var name = repoId.Split('/').Last();
        return name.EndsWith("-GGUF", StringComparison.OrdinalIgnoreCase) ? name[..^5] : name;
    }

    //the repo as a person reads it, without a scheme, this row is a fact rather than a link
    private static string HubLabel(string repoId) => "huggingface.co/" + repoId;

    //put the weights first and the encoder last, that is the fetch order and the next-file reader reads position
    private ModelView ViewOf(ModelFetchOffer offer)
    {
        var files = new List<ModelFetchFile>();
        foreach (var m in offer.Weights.Members)
            files.Add(new(m.FileName, SizeWords.Auto(m.Bytes)));
        if (offer.Projector is { } encoder)
            foreach (var m in encoder.Members)
                files.Add(new(m.FileName, SizeWords.Auto(m.Bytes), Encoder: true));
        return new(HubLabel(offer.RepoId), offer.IntoDir, files, FreeWords(offer.IntoDir));
    }

    //return nothing when there is only one file, the sentence exists to explain two files and absence is the honest copy
    private IReadOnlyList<SetupRow> ConsentProse(ModelFetchOffer offer)
    {
        //show the root note first, it is about the deed the user just did
        if (_rootNote is { Length: > 0 } note)
            return [new SetupRow(note, RowTone.Aside)];
        var shards = offer.Weights.Members.Count;
        if (shards > 1)
            return [new SetupRow($"This model is {shards} files, gatto fetches them one by one "
                + "into one folder, and checks each file, byte for byte, as it lands.", RowTone.Aside)];
        if (offer.Projector is not null)
            return [new SetupRow($"{ModelDisplayName(offer.RepoId)} can see images, so the fetch "
                + "is two files, the model and the small encoder that does the seeing.", RowTone.Aside)];
        return [];
    }

    //keep a margin above the download size, a disk that fills mid-fetch loses the whole transfer
    internal const long WeightsMarginBytes = 1_000_000_000L;

    //use the engine door's shape and words, a user who typed a path once should not learn a second way
    internal static string WeightsDoorPlaceholderOf(Gatto.Terminal.GlyphSet g) =>
        $"type a folder to keep model files in{g.Ellipsis}";

    //word the free space on the drive at this path, or null when the probe cannot read it, and null renders nothing.
    private string? FreeWords(string path) =>
        probes.FreeSpaceOn(path) is { } free ? SizeWords.Auto(free) + " free" : null;

    //count the weights and the encoder together, a refusal computed from the weights alone would pass a target that fills mid-fetch
    private static long DownloadBytes(ModelFetchOffer offer) =>
        offer.Weights.Members.Sum(m => m.Bytes)
        + (offer.Projector is { } p ? p.Members.Sum(m => m.Bytes) : 0L);

    //name all three figures in the refusal, so the user can see which number to change. refuse only when the drive can be read
    private string? TooSmall(ModelFetchOffer offer, string root)
    {
        if (probes.FreeSpaceOn(root) is not { } free) return null;
        var wanted = DownloadBytes(offer);
        if (free >= wanted + WeightsMarginBytes) return null;
        return $"not enough room in {root}: {SizeWords.Auto(wanted)} to fetch plus a "
            + $"{SizeWords.Auto(WeightsMarginBytes)} margin, and {SizeWords.Auto(free)} free.";
    }

    //say the model's name and how far the download got. one home, so the option and the table cannot spell the row differently
    private static string ResumeRow(ISetupProbes.PausedFetch p) =>
        $"resume {ModelDisplayName(p.RepoId)}, {SizeWords.Auto(p.Done)} of {SizeWords.Auto(p.Total)}";

    //gatto asks before it fetches. one option, the way out is Esc and the footer says so. a second numbered row saying no is the same choice offered twice
    private SetupScreen ModelConsent(ModelFetchOffer offer)
    {
        _modelOffer = offer;
        _modelView = ViewOf(offer);
        //reset the clock for a new consent, resuming skips this screen and the elapsed must accumulate across a drop
        _modelElapsedMs = 0;
        return Emit(new SetupScreen.Choice(
            ModelConsentKey,
            ModelTitle,
            //add the second option, one option plus an escape is a trap with no way back to the shelf. reuses the watch screen's words, no new copy
            [new ChoiceOption(ModelFetchNow, "Fetch it now"),
             new ChoiceOption(PickAnother, "Pick a different model", Advances: false)],
            BodyRows: ConsentProse(offer),
            //state in the placeholder what typing into the folder door does, a refusal with no words looks like a folder that vanished
            Door: WeightsDoorPlaceholderOf(Glyphs),
            Model: _modelView), ModelConsentKey);
    }

    //the fetch runs and the user waits, the face draws from the tick the flow stores
    private SetupScreen ModelFetching()
    {
        var shards = _modelOffer!.Weights.Members.Count;
        return Emit(new SetupScreen.Choice(
            ModelFetchingKey,
            ModelTitle,
            //pause keeps the bytes and stop deletes the partial. the escape option goes last, as the face requires on a watching screen
            [
                new ChoiceOption(ModelFetchPause, "pause", Advances: false, Press: "Space"),
                new ChoiceOption(ModelFetchStop, "stop, deletes the partial", Advances: false),
            ],
            BodyRows:
            [
                new SetupRow(shards > 1
                    ? $"This model is {shards} files, gatto fetches them one by one into one folder, "
                        + "and checks each file, byte for byte, as it lands."
                    : "Nothing to do here, gatto fetches, checks each file is byte for byte the one "
                        + "Hugging Face publishes, and moves on by itself.", RowTone.Aside),
            ],
            Model: _modelView,
            Watching: true,
            KeysOnly: true), ModelFetchingKey);
    }

    //nothing is running here, so stop deletes the partial through the probe and goes back to consent, as the live stop does
    private SetupScreen AnswerModelFetchingPaused(string answer)
    {
        if (answer == ModelFetchResume)
        {
            //start the fetch the same way as everywhere else, HubFetch reads the partial file to decide what resume means
            _modelPaused = false;
            return StartModelFetch(_modelOffer!);
        }

        if (answer != ModelFetchStop)
            throw new InvalidOperationException(
                $"the paused fetch screen has no handler for the answer '{answer}'");

        probes.DeletePartial(_modelOffer!);
        _modelPaused = false;
        EndFetch();
        return ModelConsent(_modelOffer!);
    }

    //keep Watching true while paused, the escape chord depends on it and Esc would otherwise leave the session with the bytes still on disk
    private SetupScreen ModelFetchingPaused()
    {
        var shards = _modelOffer!.Weights.Members.Count;
        return Emit(new SetupScreen.Choice(
            ModelFetchingPausedKey,
            ModelTitle,
            //put resume on the key that paused, a screen whose only ways out delete the bytes is a trap. the go-back option stays out, an option leading nowhere is worse
            [
                new ChoiceOption(ModelFetchResume, "carry on", Advances: false, Press: "Space"),
                new ChoiceOption(ModelFetchStop, "delete the partial", Advances: false),
            ],
            BodyRows:
            [
                new SetupRow(shards > 1
                    ? $"This model is {shards} files, gatto fetches them one by one into one folder, "
                        + "and checks each file, byte for byte, as it lands."
                    : "Nothing to do here, gatto fetches, checks each file is byte for byte the one "
                        + "Hugging Face publishes, and moves on by itself.", RowTone.Aside),
            ],
            Model: _modelView,
            Watching: true,
            KeysOnly: true,
            Paused: true), ModelFetchingPausedKey);
    }

    //every file was checked against its published digest, that's why the screen says byte for byte. the elapsed comes from the caller, a second clock could disagree
    private SetupScreen ModelArrived(ModelView view, long elapsedMs)
    {
        //set the verified flag here, the fetch checked every file to reach it, other routes to this screen don't
        _modelVerified = true;
        return ModelArrivedScreen(view, elapsedMs);
    }

    private SetupScreen ModelArrivedScreen(ModelView view, long elapsedMs) => Emit(new SetupScreen.Choice(
        ModelArrivedKey,
        ModelTitle,
        [new ChoiceOption(ModelArrivedNext, "next"), new ChoiceOption(ModelArrivedLeave, "leave")],
        BodyRows:
        [
            new SetupRow($"{view.First.Name} arrived, byte for byte the "
                + (view.Files.Count > 1 ? "files" : "file") + " Hugging Face publishes.",
                RowTone.Aside, Glyph: RowGlyph.Good, Lift: [view.First.Name]),
            "",
            ModelFacts.Row("fetched", view.First.Name + $" {Glyphs.Dot} " + view.First.Size + $" {Glyphs.Dot} verified",
                lift: view.First.Name),
            .. view.Files.Skip(1).Select(f => ModelFacts.Row(
                f.Encoder ? "with" : "and", f.Name + $" {Glyphs.Dot} " + f.Size + $" {Glyphs.Dot} verified", lift: f.Name)),
            ModelFacts.Row("into", view.Into),
            //keep the closing blank, a keys-only screen has no option list
            "",
        ],
        KeysOnly: true,
        PurredMs: elapsedMs), ModelArrivedKey);


    //keep the partial after a drop, an option that deleted it would break the promise on the screen. the two rows read one figure off the kept tick
    private SetupScreen ModelDropped(FetchTick kept)
    {
        var (done, total) = SizeWords.Pair(kept.Done, kept.Total);
        //the delete option appears only on the setup screen. a session leaves on one Esc, a delete no line shows must not destroy bytes
        _droppedPartial = _addRoad ? null : _modelOffer;
        return Emit(new SetupScreen.Choice(
            ModelDroppedKey,
            ModelTitle,
            [new ChoiceOption(ModelResume, "Resume the fetch"),
             new ChoiceOption(PickAnother, "Pick a different model", Advances: false)],
            BodyRows:
            [
                new SetupRow("The connection dropped mid-fetch. What already arrived is safe, "
                    + "resuming picks up where it stopped, nothing is fetched twice.",
                    RowTone.Aside, Glyph: RowGlyph.Bad),
                "",
                ModelFacts.Row("kept", kept.FileName, lift: kept.FileName),
                ModelFacts.Row("", $"{done} of {total}"),
            ],
            Kept: kept), ModelDroppedKey);
    }

    //show both fingerprints, a screen saying only there was no match leaves nothing to check. the bad file is deleted, nothing is kept and no delete is promised
    private SetupScreen ModelMismatch(Gatto.Core.Acquire.HubFetchResult got) => Emit(new SetupScreen.Choice(
        ModelMismatchKey,
        ModelTitle,
        [new ChoiceOption(ModelRefetch, "Fetch it again"),
         new ChoiceOption(PickAnother, "Pick a different model", Advances: false)],
        BodyRows:
        [
            new SetupRow("What arrived is not the file Hugging Face publishes, the copy broke "
                + "somewhere on the way, so gatto deleted it. Fetching again usually fixes it.",
                RowTone.Aside, Glyph: RowGlyph.Bad),
            "",
            ModelFacts.Row("expected", Fingerprint.Short(Glyphs, got.Expected)
                + $" {Glyphs.Dot} the file's sha256 fingerprint on Hugging Face"),
            ModelFacts.Row("got", Fingerprint.Short(Glyphs, got.Actual) + $" {Glyphs.Dot} what actually arrived"),
        ]), ModelMismatchKey);


    //keep the trailing separator on the folder that holds a file, so a row reads as a place rather than as a truncated path.
    private static string Folder(string path) =>
        System.IO.Path.GetDirectoryName(path) + System.IO.Path.DirectorySeparatorChar;

    //set _arrivedByWatch where the watch's find is adopted, so OfferMove gives the arrival's words rather than a bare question
    private SetupScreen AdoptFromWatch(FoundModel model)
    {
        _arrivedByWatch = true;
        return Adopt(model);
    }

    private SetupScreen AnswerModelEnding(string key)
    {
        //clear _droppedPartial before the switch, every option here removes the delete promise. a path added later must not leave it standing
        _droppedPartial = null;

        return key switch
        {
            //same deed, the fetch resumes when a .part is on disk
            ModelResume or ModelRefetch => StartModelFetch(_modelOffer!),
            PickAnother => Search(),
            _ => throw new InvalidOperationException(
                $"the screen '{_awaiting ?? "(none)"}' has no handler for the answer '{key}'"),
        };
    }

    //nothing is written here, the root is only a Writes intent. a folder typed and entered accepts the consent, a refusal keeps the screen
    private SetupScreen TypedWeightsRoot(string typed)
    {
        //treat an empty answer as no folder, accepting it would set an empty root as a Writes intent and the scaffold writes it
        if (string.IsNullOrWhiteSpace(typed)) return ModelConsent(_modelOffer!);

        var root = Gatto.Roles.Serve.LlamaAssetSteering.NormalizePath(typed);
        var offer = _modelOffer!;

        if (TooSmall(offer, root) is { } refusal)
        {
            _rootNote = refusal;
            return ModelConsent(offer);
        }

        Writes = Writes with { WeightsRoot = root };
        _rootNote = null;

        var accepted = offer with
        {
            //spell out System.IO.Path, this class has its own Path property and a bare name would resolve to that
            IntoDir = Gatto.Core.Acquire.ModelLocation.ForModelIn(root, offer.ModelId)
                + System.IO.Path.DirectorySeparatorChar,
        };

        //move _modelOffer and _modelView together, or the fetching screen names the old folder while the transfer goes to the new one
        _modelOffer = accepted;
        _modelView = ViewOf(accepted);
        return StartModelFetch(accepted);
    }

    //start a fetch only on the fetch key, everything else goes to TypedWeightsRoot and back to this screen. never throw here, the screen offers the typed row itself
    private SetupScreen AnswerModelConsent(string key)
    {
        //send PickAnother to Search, the one place that shows the shelf again, so this screen cannot drift from the watch screen's copy
        if (key == PickAnother) return Search();

        //read the typed mark to learn the answer is a folder. the dispatch throws on anything that is neither an option nor a folder
        if (ShelfControls.Untyped(key) is { } typed) return TypedWeightsRoot(typed);

        //clear _rootNote here rather than on emit, it describes the last answer and dies on the next real deed
        _rootNote = null;
        return StartModelFetch(_modelOffer!);
    }

    //run the fetch on a task the screen watches, a synchronous fetch leaves no clock turning and the progress bar cannot move
    private SetupScreen StartModelFetch(ModelFetchOffer offer)
    {
        _modelFetchCancel = new CancellationTokenSource();
        var ct = _modelFetchCancel.Token;
        //mint a fresh _modelFetchPause per sitting, a resume comes through here and EndFetch already dropped the previous one
        _modelFetchPause = new CancellationTokenSource();
        var pause = _modelFetchPause.Token;
        //clear _modelPaused here, every path that starts or resumes a fetch passes through this method
        _modelPaused = false;
        //measure the wait here for the arrival screen, the tick's elapsed is not the whole wait
        _modelClock = System.Diagnostics.Stopwatch.StartNew();
        //pass ct to the delegate, on Task.Run it would mean do not start if already cancelled. the delegate owns the partial
        _modelFetch = Task.Run(() => probes.FetchModel(offer, new TickSink(OnTick), ct, pause), CancellationToken.None);
        return ModelFetching();
    }

    //the fetch stopped (finished, dropped, paused or stopped by a key), its outcome picks the next screen
    private SetupScreen AfterModelFetch(string answer)
    {
        var fetch = _modelFetch!;
        _modelFetch = null;
        //accumulate rather than read, a resumed fetch is one wait in two sittings and the arrival reports the whole wait
        _modelElapsedMs += _modelClock?.ElapsedMilliseconds ?? 0;

        if (answer == ModelFetchStop)
        {
            _modelFetchCancel!.Cancel();
            //wait for the fetch to unwind, the partial is deleted inside the cancelled path, returning early reports it gone while still on disk
            try { fetch.GetAwaiter().GetResult(); } catch (Exception) { } //a stopped fetch is not a failure
            EndFetch();
            //the user declined this download, which is not leaving setup, so go back to consent. the partial is gone and the offer is honest again
            return ModelConsent(_modelOffer!);
        }

        //cancel the pause source before the join, or the join waits for a transfer nobody asked to stop
        if (answer == ModelFetchPause) _modelFetchPause!.Cancel();

        Gatto.Core.Acquire.HubFetchResult got;
        //turn a throw into a Dropped result, an unhandled throw leaves the wizard watching a finished fetch. keep this the one join and catch for every path
        try { got = fetch.GetAwaiter().GetResult(); }
        catch (Exception) { got = new(Gatto.Core.Acquire.HubFetchOutcome.Dropped); }

        //draw the paused screen only when the fetch reports Paused, the face waits for a key and a fetch can finish inside that window
        if (got.Outcome == Gatto.Core.Acquire.HubFetchOutcome.Paused)
        {
            //read the last tick after the join, ticks arrive from the fetch's own thread. keep it for the paused screen's bar, clear the rest after
            EndFetch(keepTick: true);
            _modelPaused = true;
            return ModelFetchingPaused();
        }
        //read the tick after the join, a cross-thread read of an unarrived value would hide the kept partial from the drop screen. read it before EndFetch clears it
        var last = PollTick();
        EndFetch();

        return got.Outcome switch
        {
            Gatto.Core.Acquire.HubFetchOutcome.Arrived =>
                ModelArrived(_modelView!.Value, _modelElapsedMs),
            //a drop that kept a tick can be resumed, the drop screen shows the figure
            Gatto.Core.Acquire.HubFetchOutcome.Dropped when last is { } kept => ModelDropped(kept),
            //a drop that kept nothing goes to the browser watch, the Hub was out of reach and nothing arrived. the watch can verify with the offer's fingerprints
            Gatto.Core.Acquire.HubFetchOutcome.Dropped => Watch(WatchCause.Unreachable),
            Gatto.Core.Acquire.HubFetchOutcome.Mismatch => ModelMismatch(got),
            //route this arm to the watch, the tree changed between the offer and the fetch, gatto can't prove what it pulled
            _ => Watch(WatchCause.NoFingerprint),
        };
    }

    //rescan the disk instead of composing a FoundModel from the offer, adopt refuses an incomplete set and only the disk can say it's complete
    private SetupScreen AnswerModelArrived(string key)
    {
        if (key != ModelArrivedNext) return Leave();

        var rescan = probes.Scan(_modelOffer!.IntoDir);
        _discovered = rescan.Found;
        _scanRoots = rescan.Roots;
        //return to consent when the rescan cannot find the arrival it announced, the watch would render a stale cause after a verified fetch
        return _discovered.FirstOrDefault(m => Answers(m, Picked!.RowQuant!)) is { } landed
            ? Adopt(landed)
            : ModelConsent(_modelOffer!);
    }
}
