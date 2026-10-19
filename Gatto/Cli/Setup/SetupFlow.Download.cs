using Gatto.Cli.Setup.Shelves;
using Gatto.Core.Acquire;

namespace Gatto.Cli.Setup;

//the watch and the download, the landed path, a wrong file and a partial set
internal sealed partial class SetupFlow
{
    //after the pick: the download, and the watch

    //every pick ends on a screen that waits, an Info with nothing after it ends the run
    private SetupScreen PickedFromHub(ModelRow row)
    {
        Picked = row;
        _pickedFromLocal = false;
        //a fresh pick starts a new watch, nothing explained yet
        _watchMark = "";
        //an offer exists only when every file has a published fingerprint, the same rule the fetch refuses on. the screen cannot offer what the fetch would decline
        if (probes.ModelOffer(row) is { } offer) return ModelConsent(offer);
        _watchCause = WatchCause.NoFingerprint;
        return DownloadScreen(row);
    }

    //the one road onto the browser watch, the cause is set on entry so no call site can forget it
    private SetupScreen Watch(WatchCause why)
    {
        _watchCause = why;
        return DownloadScreen(Picked!);
    }

    //the carried cause decides which sentence renders, each arm says only what it can claim
    private SetupScreen DownloadScreen(ModelRow row)
    {
        var files = WatchFiles(row);
        //use the larger of the two counts, a shard set may report one member against thirteen shards, undercounting leaves a partial model
        var count = Math.Max(files.Count, row.RowQuant!.ShardCount);
        var rows = new List<SetupRow>
        {
            new(WatchCauseSentence(count, files.Count, row.RowQuant!.Bytes), RowTone.Aside),
            "",
            ModelFacts.Row("download", FileWord(files[0]), lift: files[0].Name),
        };

        //say and rather than with, every file here is the user's to fetch
        foreach (var extra in files.Skip(1))
            rows.Add(ModelFacts.Row("and", FileWord(extra), lift: extra.Name));

        //many files get the repo's file list, a single file the direct link. branch on the count, don't re-derive from names
        var url = count > 1
            ? Gatto.Core.Acquire.HubUrl.Tree(row.RowFile!.RepoId)
            : Gatto.Core.Acquire.HubUrl.Download(row.RowFile!.RepoId, row.RowQuant!.RepoPath);
        rows.Add(ModelFacts.Row("from", Trimmed(url))); //the link is the instruction, a bare file name sends people to a search engine
        rows.Add("");

        //the partial sentence replaces the standing one. a screen that just said one of two files is here owes an explanation first
        var here = files.Count(f => Arrived(f.Name));
        rows.Add(here > 0 && here < count
            ? new SetupRow(PartialSentence(files, here, count), RowTone.Aside)
            : "gatto is watching for it, you don't need to press anything.");

        //say this only where the fingerprint cannot be checked, elsewhere the arrival line already tells it
        if (_watchCause == WatchCause.NoFingerprint)
            rows.Add(new SetupRow("It will check the name and the size when a file lands, but "
                + "without a published fingerprint it can't check the bytes.", RowTone.Aside));

        rows.Add("");

        //name where it is looking from the scan's own answer, say nothing yet only while nothing has arrived
        rows.Add(new SetupRow("It is watching:",
            Tail: here > 0 ? "checking every few seconds" : "checking every few seconds, nothing yet"));
        rows.AddRange(RootRows());

        //the blank belongs to the sentence below it, the frame adds its own before the option list. two would open a gap nobody drew
        if (here == 0) rows.Add("");

        //say gatto will find the file, don't say resume, it re-walks from the start. once a file is here the sentence describes a wait that is gone
        if (here == 0)
            rows.Add(new("This can take hours on a big model. You can leave with Esc and run gatto "
                + "setup again later, gatto will find the file.", Highlight: ["gatto setup"]));

        return Emit(new SetupScreen.Choice(
            DownloadKey, ModelTitle, WatchOptions(row), BodyRows: rows, Watching: true,
            Door: $"type the path to the downloaded file or folder{Glyphs.Ellipsis}",
            ArrivedLabel: $"{Glyphs.Ok} {row.RowQuant!.FileName} arrived"), DownloadKey);
    }


    //a file already here drops its size. mark nothing until one has arrived, a list of misses reads as failure
    private string FileWord(ModelFetchFile file) =>
        !AnyArrived() ? file.Name + $" {Glyphs.Dot} " + file.Size
        : Arrived(file.Name) ? $"{Glyphs.Ok} {file.Name}, here"
        : $"{Glyphs.Bad} {file.Name} {Glyphs.Dot} {file.Size} {Glyphs.Dot} still missing";

    //read off _discovered, the poll refreshes it, so the marks cannot disagree with the disk the watch just looked at
    private bool Arrived(string fileName) =>
        _discovered.Any(m => string.Equals(
            System.IO.Path.GetFileName(m.Path), fileName, StringComparison.OrdinalIgnoreCase));

    private bool AnyArrived() =>
        Picked is { } row && WatchFiles(row).Any(f => Arrived(f.Name));

    //a missing encoder names its consequence, the model can't see. a shard set gets the general sentence, it has no such consequence to name
    private string PartialSentence(IReadOnlyList<ModelFetchFile> files, int here, int total)
    {
        var missingEncoder = files.Any(f => f.Encoder && !Arrived(f.Name));
        var count = total == 2 && here == 1
            ? "One of the two files is here."
            : $"{here} of the {total} files are here.";
        return missingEncoder
            ? count + " The model can't see images without its encoder, the link above has both; "
                + "gatto keeps watching."
            : count + " The link above has all of them; gatto keeps watching.";
    }


    //the watch waits for every file, not just the name that matched. a declined encoder is not missing
    private bool UnitHere(ModelRow row) =>
        WatchFiles(row).Where(f => f.Encoder).All(f => _declinedEncoder || Arrived(f.Name));

    //offer it only where the missing files are encoders and something has already arrived. a set without shards cannot load
    private bool CanGoTextOnly(ModelRow row)
    {
        var files = WatchFiles(row);
        if (!files.Any(Arrived2)) return false;
        //every file still missing must be an encoder, without its shards the model will not load
        return files.Where(f => !Arrived(f.Name)).All(f => f.Encoder)
            && files.Any(f => f.Encoder && !Arrived(f.Name));

        bool Arrived2(ModelFetchFile f) => Arrived(f.Name);
    }

    //the files the user must fetch, worded with the same composer as consent, the two screens cannot describe one unit differently
    private static IReadOnlyList<ModelFetchFile> WatchFiles(ModelRow row)
    {
        var files = new List<ModelFetchFile>();
        foreach (var m in row.RowQuant!.Members)
            files.Add(new(m.FileName, SizeWords.Auto(m.Bytes)));
        //the encoder comes from the chosen file's own repo, since another publisher's encoder belongs to another download
        if (Gatto.Core.Acquire.ProjectorPick.Best(row.RowProjectors) is { } encoder)
            foreach (var m in encoder.Members)
                files.Add(new(m.FileName, SizeWords.Auto(m.Bytes), Encoder: true));
        return files;
    }

    //why the download is theirs, in the state that is actually true. the tail is shared, only the reason differs
    private string WatchCauseSentence(int fileCount, int listed, long totalBytes)
    {
        var why = _watchCause == WatchCause.Unreachable
            ? "gatto couldn't reach Hugging Face, so this time the download is yours, everything "
              + "below is exact."
            : "Hugging Face publishes no fingerprint for these files, so the download is yours, "
              + "everything below is exact.";
        if (fileCount <= 1) return why;
        var how = fileCount == 2 ? "download both" : "download them all";
        //state the total only where the rows cannot say it, a set listed as one row has nowhere else for the size
        var total = fileCount > listed
            ? $" (total {SizeWords.Auto(totalBytes)})"
            : "";
        return why + $" It is {InWords(fileCount)} files{total}: {how} into one folder.";
    }

    //the drawn copy says both only of two, above that say the number
    private static string InWords(int n) => n == 2 ? "two" : n.ToString(
        System.Globalization.CultureInfo.InvariantCulture);

    //drop the scheme, the row is a fact to read rather than a link to click
    private static string Trimmed(string url) =>
        url.StartsWith("https://", StringComparison.Ordinal) ? url["https://".Length..] : url;

    //the answer that loses nothing comes first, above the one that abandons work already on disk
    private IReadOnlyList<ChoiceOption> WatchOptions(ModelRow row) =>
        CanGoTextOnly(row)
            ? [new ChoiceOption(UseTextOnly, "Use it text-only"),
               new ChoiceOption(PickAnother, "Pick a different model", Advances: false)]
            : DownloadOptions;

    //watching never traps anyone, the door takes a path saved elsewhere and this option picks another model
    private static IReadOnlyList<ChoiceOption> DownloadOptions =>
    [
        //the standing typed door takes a path in place, don't bring back a row that opens a second screen for it
        new ChoiceOption(PickAnother, "Pick a different model", Advances: false),
    ];

    //the one entry point for the watch, the flow knows the walk and the runner must not branch on a screen key
    public bool PollWatch() =>
        //a loading shelf resolves when its scan and its search have both answered, and no disk question is asked while it runs
        _load is { } load ? (load.Scan?.IsCompleted ?? true) && (load.Search?.IsCompleted ?? true)
            && (load.Other?.IsCompleted ?? true) && (load.Serving?.IsCompleted ?? true)
        //a paused fetch resolves nothing, so this arm leads the rest. without it the flow falls to asking the disk about a browser download
        : _modelPaused ? false
        : _fetching is { } fetch ? fetch.IsCompleted
        //task fields answer before disk questions, a task is running or it is not, the fields below are told apart by which are set
        : _modelFetch is { } model ? model.IsCompleted
        //the check's watch resolves also when the load ask is due, the next screen decides which happened
        : _audition is { } check ? check.IsCompleted || LoadAskIsDue()
        //these tasks sit above the disk questions, a task is running or it is not
        : _proving is { } proving ? proving.IsCompleted
        : _loading is { } one ? one.IsCompleted
        : Picked is not null ? PollForDownload()
        : PollForPartialSet();

    //called by the face on its own clock, it refreshes _discovered and the answer reads that, one scan cannot disagree with a second
    public bool PollForDownload()
    {
        if (Picked is not { } row) return false;

        var rescan = probes.Scan(null);
        _discovered = rescan.Found;
        _scanRoots = rescan.Roots;

        //the poll answers has anything changed. a partial set matches while Adopt refuses it, an is-it-there poll would resolve the screen the instant it appeared
        var mark = MarkOf(_discovered.FirstOrDefault(m => Answers(m, row.RowQuant!)));
        if (mark == _watchMark) return false;

        _watchMark = mark;
        return mark.Length > 0;
    }

    //the state the screens render, absent, k of n, or complete, one home because three sites read it
    private static string MarkOf(FoundModel? m) =>
        m is null ? "" : $"{m.Path}|{m.ShardsPresent}|{m.IsComplete}";

    //reset by a new pick, said once is per watch. a carried mark would swallow the first shard of the next one
    private string _watchMark = "";

    //the same watch for a set nobody picked, anchored on the set's own file name since there is no pick to match
    public bool PollForPartialSet()
    {
        if (_watchingSet is not { } waitingFor) return false;

        var rescan = probes.Scan(null);
        _discovered = rescan.Found;
        _scanRoots = rescan.Roots;

        var mark = MarkOf(_discovered.FirstOrDefault(m => Answers(m, waitingFor)));
        if (mark == _watchMark) return false;

        _watchMark = mark;
        return mark.Length > 0;
    }

    //the set a partial screen is waiting to fill, shaped as the quant it would have been picked as, null elsewhere
    private HubQuant? _watchingSet;

    //the watch driven by the user rather than a clock, an arrival adopts exactly as a discovered model so the two cannot diverge
    private SetupScreen AnswerDownload(string key)
    {
        if (Picked is not { } row) return Search();          //nothing to watch for, not reachable
        //read the _discovered the poll refreshed, a second scan could disagree with the first. a vanished file just returns to the watch
        if (key == Landed)
        {
            if (_discovered.FirstOrDefault(m => Answers(m, row.RowQuant!)) is not { } arrived)
                return DownloadScreen(row);

            var check = Accept(arrived, row.RowQuant!);
            return check.Verdict switch
            {
                //still being written or unreadable both mean not yet, neither is an accusation
                WatchVerdict.StillArriving or WatchVerdict.Unreadable => DownloadScreen(row),
                WatchVerdict.NotThePublishedFile => WrongFile(row, arrived, check),
                //the whole unit must be here, a vision model missing its encoder stays on the watch
                _ when !UnitHere(row) => DownloadScreen(row),
                _ => AdoptFromWatch(arrived),
            };
        }
        if (key == PickAnother) return Search();

        //the user said the smaller unit is the unit, remember it so nothing asks again
        if (key == UseTextOnly)
        {
            _declinedEncoder = true;
            return _discovered.FirstOrDefault(m => Answers(m, row.RowQuant!)) is { } weights
                ? Adopt(weights)
                : DownloadScreen(row);
        }

        //a wrapped answer from the door is the path, normalize it with the shared rule. an empty answer falls through to the rescan and does nothing
        if (ShelfControls.Untyped(key) is { } door
            && Gatto.Roles.Serve.LlamaAssetSteering.NormalizePath(door) is { Length: > 0 } typed)
            return AnswerLandedPath(typed);

        var rescan = probes.Scan(null);
        _discovered = rescan.Found;
        _scanRoots = rescan.Roots;
        var landed = _discovered.FirstOrDefault(m => Answers(m, row.RowQuant!));

        return landed is null ? DownloadScreen(row) : Adopt(landed);
    }

    //the sibling of the common-folders watch, differing only in where it looks. a miss returns to the watch, which lists the folders it searched
    private SetupScreen AnswerLandedPath(string root)
    {
        var scan = probes.Scan(root);
        _discovered = scan.Found;
        _scanRoots = scan.Roots;

        //only reached from the download watch, which returns to search when nothing is picked
        if (Picked is not { } row) return DiscoveredScreen(heading: null);

        if (_discovered.FirstOrDefault(m => Answers(m, row.RowQuant!)) is { } landed) return Adopt(landed);

        return _discovered.Count == 0
            ? DownloadScreen(row)
            : DiscoveredScreen("That file isn't there yet, these are the models gatto can see");
    }

    //the order lives in the pure flow where tests drive it. unreadable answers null, calling a locked file the wrong file would lie
    private WatchCheck Accept(FoundModel found, HubQuant picked)
    {
        //a .part beside it means the writer is not done, keep watching rather than judge a growing file
        if (probes.StillArriving(found.Path)) return new(WatchVerdict.StillArriving);

        //where a fingerprint was published it decides, a file whose bytes differ cannot have the published hash anyway
        if (picked.Sha256 is { Length: > 0 } expected)
        {
            var actual = probes.Sha256Of(found.Path);
            //a file gatto cannot hash is usually one still written without a .part, wait rather than accuse
            if (actual is not { Length: > 0 }) return new(WatchVerdict.Unreadable);

            return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)
                ? new(WatchVerdict.Verified, actual, expected)
                : new(WatchVerdict.NotThePublishedFile, actual, expected);
        }

        //the size check is for single files only. a set's Bytes is a summed total, comparing it against one shard calls every correct set wrong
        if (picked.ShardCount <= 1 && found.Set is null
            && picked.Bytes > 0 && found.FileBytes > 0 && found.FileBytes != picked.Bytes)
            return new(WatchVerdict.NotThePublishedFile);

        return new(WatchVerdict.NameAndSizeOnly);
    }

    //the watch keeps watching, nothing adopted and nothing deleted. the file is the user's, unlike the fetch's mismatch which deletes what it pulled
    private SetupScreen WrongFile(ModelRow row, FoundModel found, WatchCheck check)
    {
        //a collection expression because a spread inside an object initializer does not compile
        List<SetupRow> rows =
        [
            new("A file with the right name is here, but it isn't the published one.",
                RowTone.Aside, Glyph: RowGlyph.Bad),
            "",
            //the rows name what was actually compared, the fingerprint where published, otherwise the size. draw no row in a state where its claim cannot be true
            .. check.Expected is { Length: > 0 }
                ? (IReadOnlyList<SetupRow>)
                [
                    ModelFacts.Row("expected", Fingerprint.Short(Glyphs, check.Expected)
                        + $" {Glyphs.Dot} the file's fingerprint on Hugging Face"),
                    ModelFacts.Row("got", Fingerprint.Short(Glyphs, check.Actual)
                        + $" {Glyphs.Dot} the file in " + Folder(found.Path)),
                ]
                :
                [
                    ModelFacts.Row("expected", SizeWords.Auto(row.RowQuant!.Bytes)
                        + $" {Glyphs.Dot} the size Hugging Face lists"),
                    ModelFacts.Row("got", SizeWords.Auto(found.FileBytes)
                        + $" {Glyphs.Dot} the file in " + Folder(found.Path)),
                ],
            "",
            new SetupRow("A broken copy looks like this, downloading it again usually fixes it. "
                + "gatto keeps watching.", RowTone.Aside),
            "",
            new SetupRow("It is watching:", Tail: "checking every few seconds"),
            .. RootRows(),
        ];

        return Emit(new SetupScreen.Choice(
            DownloadKey, ModelTitle, WatchOptions(row), BodyRows: rows, Watching: true,
            Door: $"type the path to the downloaded file or folder{Glyphs.Ellipsis}",
            ArrivedLabel: $"{Glyphs.Ok} {row.RowQuant!.FileName} arrived"), DownloadKey);
    }

    //a set answers the pick by stem and shard count (shards arrive in any order), Adopt still decides it's complete
    private static bool Answers(FoundModel found, HubQuant picked)
    {
        var name = System.IO.Path.GetFileName(found.Path);
        if (string.Equals(name, picked.FileName, StringComparison.OrdinalIgnoreCase)) return true;

        return ShardName.Parse(name) is { IsSet: true } a
            && ShardName.Parse(picked.FileName) is { IsSet: true } b
            && a.Count == b.Count
            && string.Equals(a.Stem, b.Stem, StringComparison.OrdinalIgnoreCase);
    }

    //a partial set never adopts, a model missing shards will not load. the k of n count says the download is progressing
    private SetupScreen PartialSet(FoundModel model)
    {
        var set = model.Set!;         //the bang is safe because IsComplete is false only when Set is non-null
        var folder = System.IO.Path.GetDirectoryName(model.Path) ?? "";
        //arm the watch on the set on screen and arm the mark to what the screen shows. otherwise the first poll resolves the screen the instant it appears
        _watchMark = MarkOf(model);
        _watchingSet = Picked is null
            //the match reads names only, the size stays the file's own byte count rather than zeroed
            ? new HubQuant(System.IO.Path.GetFileName(model.Path), model.FileBytes, null, set.Count)
            : null;
        return Emit(new SetupScreen.Choice(
            PartialSetKey,
            $"Still arriving, {model.ShardsPresent} of {set.Count} files so far",
            [new ChoiceOption(PickAnother, "Pick a different model", Advances: false)],
            BodyRows:
            [
                new SetupRow($"This model comes as {set.Count} files, and gatto needs all of them "
                    + "in the same folder before it can use any of them."),
                "",
                //leave it raw, the face sanitizes every row it renders, sanitizing here would be the rule's second home
                new SetupRow($"  {folder}"),
                "",
                new SetupRow("gatto is watching for the rest, you don't need to press anything."),
            ], Watching: true), PartialSetKey);
    }

    //a completed set reuses the download answer, so the same matcher that found it half-arrived adopts it. a discovery-only set re-enters Discover
    private SetupScreen AnswerPartialSet(string key)
    {
        if (key == PickAnother) return Search();
        return Picked is not null ? AnswerDownload(Landed) : Discover(extraRoot: null);
    }
}
