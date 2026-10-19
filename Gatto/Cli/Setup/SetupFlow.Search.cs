using Gatto.Cli.Setup.Shelves;
using Gatto.Core.Acquire;
using Gatto.Terminal;

namespace Gatto.Cli.Setup;

//the search: the shelf load, the local shelf, chips and empty rows, the lookup and the hub pick
internal sealed partial class SetupFlow
{
    //the fit check decides the shelf's rows. an empty result offers the ways that still work
    private SetupScreen Search(IReadOnlyList<ModelRow>? keepIfEmpty = null, Func<SetupScreen>? then = null)
    {
        //any search replaces a looked-up row
        _lookedUp = null;
        //drop _scanTyped here, every path from the local shelf to the Hub passes through this method. a latch outliving its shelf would filter the next
        _scanTyped = null;
        //every Hub search runs behind the loading shelf where the face draws one, so a chip or a typed word never freezes the frame
        if (LoadsShelfInBackground)
            return StartLoad(scan: false, null, search: true, keepIfEmpty, then ?? SearchScreen);
        RunSearch(keepIfEmpty);
        return (then ?? SearchScreen)();
    }

    //the query as the controls set it now, read on the flow's thread so a load carries the question that was asked
    private ModelSearchRequest CurrentRequest() => new(_lit, _lift, _search);

    //run the search without emitting a screen, the caller reads the outcome first and a screen no face painted would still sit in _emitted
    private void RunSearch(IReadOnlyList<ModelRow>? keepIfEmpty) =>
        ApplySearch(probes.SearchModels(CurrentRequest(), null, CancellationToken.None), keepIfEmpty);

    //the search's fields set from its outcome, on the flow's thread whether the search ran here or behind the loading shelf
    private void ApplySearch(ShelfOutcome outcome, IReadOnlyList<ModelRow>? keepIfEmpty)
    {
        //keep the rows in hand when a search that only re-arranges returns nothing, so it never empties the list it arranges
        if (outcome.Rows.Count == 0 && keepIfEmpty is { Count: > 0 } kept)
        {
            //set _carriedOver where the fallback happened rather than from the result's shape, so the sentence can say the order changed but the selection did not
            _carriedOver = true;
            _rows = kept;
        }
        else
        {
            _carriedOver = false;
            _rows = Sorted(outcome.Rows);
            _searchCause = outcome.Cause;
            _hiddenByKind = outcome.HiddenByKind;
            _moreBehind = outcome.MoreBehindA;
            _counted = outcome;
        }
        //set _stoodOnAShelf from the rows that came back, so it reports the shelf the user actually has
        if (_rows.Count > 0) _stoodOnAShelf = true;
    }

    //one load of the shelf: the scan and the search it waits for, both returning facts and writing no field, and the screen it lands on
    private sealed record ShelfLoad(
        ShelfSource Source, Task<ScanResult>? Scan, string? ScanRoot,
        Task<ShelfOutcome>? Search, CancellationTokenSource? Stop,
        IReadOnlyList<ModelRow>? KeepIfEmpty, Func<SetupScreen> Then,
        Task? Other = null, string? Step = null,   //another wait the landing reads through Then, a typed repo id's lookup, with the step line that names it
        bool Opening = false,   //drawn on the start-up screen rather than the loading shelf
        Serving? Serving = null);   //the serving probe the landed shelf marks its loaded model from

    private ShelfLoad? _load;

    //the two reads of the model the local server holds, which a server that does not answer makes cost their whole deadline
    private sealed record Serving(Task<string?> Id, Task<string?> Path)
    {
        public bool IsCompleted => Id.IsCompleted && Path.IsCompleted;
    }

    //the serving probe in flight or last answered. a request to the local server is never cancelled, so a restarted load takes over one still running
    private Serving? _serving;

    //the serving probe's answers while a landed load composes its screen, null otherwise so every other screen reads the server itself
    private (string? Id, string? Path)? _servingNow;

    //the last answers a shelf drew from, which a key that only re-arranges its rows reuses. a closed port costs each ask its 2 s deadline
    private (bool Known, string? Value) _keptId, _keptPath;

    private bool _rearranging;

    private string? LoadedId()
    {
        if (_servingNow is { } now) return (_keptId = (true, now.Id)).Value;
        if (_rearranging && _keptId.Known) return _keptId.Value;
        return (_keptId = (true, probes.LoadedModelId())).Value;
    }

    private string? LoadedPath()
    {
        if (_servingNow is { } now) return (_keptPath = (true, now.Path)).Value;
        if (_rearranging && _keptPath.Known) return _keptPath.Value;
        return (_keptPath = (true, probes.LoadedModelPath())).Value;
    }

    //a sort, a local chip or a local a draws the same rows in another order or subset, so it asks the server nothing
    private SetupScreen Rearranged(Func<SetupScreen> screen)
    {
        _rearranging = true;
        try { return screen(); }
        finally { _rearranging = false; }
    }

    //the search reports from the pool, so its latest moment and the generation that may write it sit under one lock
    private readonly object _loadGate = new();
    private int _loadGeneration;
    private SearchProgress? _loadProgress;

    //the line about this machine once the scan answered, composed once since it reads the models folder per file
    private string? _loadLocal;

    //whether a control's back step waits for the load to say if its shelf holds a list
    private bool _stepAwaitsLoad;

    //start the scan, the search or both on the pool and draw the loading shelf at once. a scan already running can be carried over
    private SetupScreen StartLoad(bool scan, string? scanRoot, bool search,
        IReadOnlyList<ModelRow>? keepIfEmpty, Func<SetupScreen> then,
        ShelfSource source = ShelfSource.Hub, Task<ScanResult>? scanning = null,
        Task? other = null, string? step = null, bool opening = false, bool serving = true)
    {
        CancelLoad();
        int generation;
        lock (_loadGate)
        {
            generation = ++_loadGeneration;
            _loadProgress = null;
        }
        _loadLocal = null;

        var stop = search ? new CancellationTokenSource() : null;
        var token = stop?.Token ?? CancellationToken.None;
        var request = CurrentRequest();
        //a moment from an earlier load is dropped by its generation, so a restarted count never shows the old one
        var sink = new SearchSink(p => { lock (_loadGate) if (generation == _loadGeneration) _loadProgress = p; });

        if (serving && _serving is not { IsCompleted: false })
            _serving = new Serving(Task.Run(probes.LoadedModelId), Task.Run(probes.LoadedModelPath));

        _load = new ShelfLoad(source,
            scanning ?? (scan ? Task.Run(() => probes.Scan(scanRoot)) : null), scanRoot,
            search ? Task.Run(() => probes.SearchModels(request, sink, token)) : null, stop,
            keepIfEmpty, then, other, step, opening, serving ? _serving : null);
        return LoadingScreen();
    }

    //stop the Hub requests a load no longer wants and forget it. the scan reads only the disk and finishes into nothing
    private void CancelLoad()
    {
        if (_load is not { } old) return;
        _load = null;
        //cancel without disposing, since the search may still register on the token while it unwinds
        old.Stop?.Cancel();
    }

    //the same load asked again with the controls as they are now, keeping the scan it already started
    private SetupScreen RestartSearch(ShelfLoad load) =>
        StartLoad(scan: false, load.ScanRoot, search: true, load.KeepIfEmpty, load.Then, load.Source,
            scanning: load.Scan);

    //the shelf with every control in place and no rows yet. the face draws the purr and the step line from PollShelfLoad
    private SetupScreen LoadingScreen()
    {
        if (_load is { Opening: true }) return StartingScreen();
        var hub = _load?.Source != ShelfSource.Local;
        var screen = (SetupScreen.Choice)Emit(new SetupScreen.Choice(
            ShelfLoadingKey,
            ModelTitle,
            [],
            //the face reads no shape from a loading view, so the machine is not read before the first frame
            Shelf: new ShelfView([], Gatto.Core.Hardware.MachineShape.CpuOnly,
                Families: Families.Load().Ladder, Family: hub ? LitChip() : _localFamily ?? "all",
                Lit: hub ? _lit : null,
                Source: hub ? ShelfSource.Hub : ShelfSource.Local,
                Folder: hub ? null : _scanTyped,
                Searched: hub && _search is { Length: > 0 },
                Loading: true),
            Watching: true,
            Door: hub ? ShelfDoorPlaceholderOf(Glyphs) : LocalShelfDoorPlaceholderOf(Glyphs)),
            ShelfLoadingKey);
        return _stepAwaitsLoad ? HonestBack(screen) : screen;
    }

    //the start-up screen: the cat, one sentence, and the purr and the step line the face draws beside them from PollShelfLoad
    private SetupScreen StartingScreen() => Emit(new SetupScreen.Choice(
            ShelfLoadingKey, null, [], BodyRows: [new SetupRow(StartingWait)], Watching: true)
        { Hero = CatOf(Glyphs), Starting = true }, ShelfLoadingKey);

    //the start-up screen's sentence, whole in one place so a change of wording is one line
    private string StartingWait => $"please wait while gatto fetches the models from Hugging Face{Glyphs.Ellipsis}";

    //the loading shelf's two lines, read by the face once per frame on the flow's thread. only the search's moment crosses threads
    public ShelfLoadTick? PollShelfLoad()
    {
        if (_load is not { } load) return null;
        SearchProgress? moment;
        lock (_loadGate) moment = _loadProgress;
        var looking = $"looking for models on this machine{Glyphs.Ellipsis}";
        var scanning = load.Scan is { IsCompleted: false };

        //the server's answer is the last wait once the rest are in, so the line names it
        if (load.Serving is { IsCompleted: false } && !scanning && load.Search is not { IsCompleted: false }
            && load.Other is not { IsCompleted: false })
            return new ShelfLoadTick(ServingStep, load.Source == ShelfSource.Local ? null : LocalLine(load));

        //a typed folder is named, since it is the one place this scan looks that the user chose. the line stays until the shelf is drawn
        if (load.Source == ShelfSource.Local)
            return new ShelfLoadTick(
                load.ScanRoot is { Length: > 0 } root ? $"looking for models in {root}{Glyphs.Ellipsis}" : looking,
                null);

        return new ShelfLoadTick(
            load.Step ?? (load.Search is null ? "" : SearchStep(moment)),
            scanning ? looking : LocalLine(load));
    }

    //the line about this machine once the scan answered, null while it runs or when it failed
    private string? LocalLine(ShelfLoad load) =>
        load.Scan is { IsCompletedSuccessfully: true } done ? _loadLocal ??= LocalLineOf(done.Result) : null;

    //the step line while only the serving probe is out
    private const string ServingStep = "checking the model gatto is serving";

    //the step line from the search's latest moment, the publishers named from the list it was given so no copy names one
    private string SearchStep(SearchProgress? moment) => moment switch
    {
        null => "asking Hugging Face for models",
        { Stage: SearchStage.Listing, Publishers: [var one] } => $"asking Hugging Face for {one}'s models",
        { Stage: SearchStage.Listing } listing =>
            $"asking Hugging Face for {listing.Total} publishers' models {Glyphs.Dot} {listing.Done} of {listing.Total}",
        { } reading => $"reading the files of {reading.Total} {(reading.Total == 1 ? "model" : "models")} "
            + $"{Glyphs.Dot} {reading.Done} of {reading.Total}",
    };

    //the discovery line the landed shelf will show, from the scan in hand, or null when it would say nothing
    private string? LocalLineOf(ScanResult scan)
    {
        var found = scan.Found.Count(NotOnTheList);
        return found > 0 ? DiscoveryRow(found, _addRoad).Text : null;
    }

    //every answer the loading shelf can give: its own landing, a chip or a term that restarts it, m, and the rest after a stop
    private SetupScreen AnswerShelfLoading(string key)
    {
        if (_load is not { } load) return Search();

        if (key == Landed) return Land(load);

        //a local chip only filters what the scan finds, so the scan goes on and the frame lights the chip
        if (load.Source == ShelfSource.Local && key.StartsWith(CtlFamily, StringComparison.Ordinal))
        {
            var local = key[CtlFamily.Length..];
            _localFamily = local is "all" or "" ? null : local;
            return LoadingScreen();
        }

        if (load.Search is not null && key.StartsWith(CtlFamily, StringComparison.Ordinal))
        {
            Light(key[CtlFamily.Length..]);
            _search = null;
            return RestartSearch(load);
        }

        //an address is no term, it is answered as at the shelf's own door
        if (ShelfControls.Untyped(key) is { } address
            && Gatto.Cli.Setup.Tui.TypedDoor.Classify(address) == Gatto.Cli.Setup.Tui.DoorInput.Address)
            return AnswerAddress(address);

        //a term typed in the door restarts the load with that term, the chip rule applied to the door. a path or a repo id goes its own way
        if (load.Search is not null && ShelfControls.Untyped(key) is { } typed
            && Gatto.Cli.Setup.Tui.TypedDoor.Classify(typed) != Gatto.Cli.Setup.Tui.DoorInput.Path
            && !HubUrl.TryParse(typed, out _))
        {
            _search = typed.Trim().Length == 0 ? null : typed.Trim();
            return RestartSearch(load);
        }

        //m drops the Hub search and waits for the scan alone, which is already running
        if (load.Source == ShelfSource.Hub && key == CtlSource && load.Scan is not null)
        {
            load.Stop?.Cancel();
            _load = load with
            {
                Source = ShelfSource.Local, Search = null, Stop = null,
                Then = () => DiscoveredScreen(heading: null, invitation: true),
            };
            return LoadingScreen();
        }

        CancelLoad();
        return load.Source == ShelfSource.Hub ? AnswerSearch(key) : AnswerDiscovered(key);
    }

    //apply what the load found on the flow's thread, then draw the screen it was started for
    private SetupScreen Land(ShelfLoad load)
    {
        _load = null;
        if (load.Scan is { } scan) ApplyScan(Joined(scan, new ScanResult([], [])), load.ScanRoot);
        if (load.Search is { } search)
            ApplySearch(Joined(search, new ShelfOutcome([], 0, 0, 0, false, 0, [], null)), load.KeepIfEmpty);
        SetupScreen shown;
        _servingNow = load.Serving is { } serving ? (Joined(serving.Id, null), Joined(serving.Path, null)) : null;
        try { shown = load.Then(); }
        finally { _servingNow = null; }
        if (!_stepAwaitsLoad) return shown;
        _stepAwaitsLoad = false;
        return KeepStepIfNoList(shown);
    }

    //a probe that threw is an answer with nothing in it, so the loading shelf still lands
    private static T Joined<T>(Task<T> task, T empty)
    {
        try { return task.GetAwaiter().GetResult(); }
        catch (Exception) { return empty; }
    }

    //report moments synchronously, Progress<T> posts to a captured context and would deliver a moment late
    private sealed class SearchSink(Action<SearchProgress> onReport) : IProgress<SearchProgress>
    {
        public void Report(SearchProgress value) => onReport(value);
    }

    //draw the Hub shelf over whatever RunSearch last found
    private SetupScreen SearchScreen()
    {
        //keep the screen's own row budget, the engine's request cap is another component's number. the escape rows come out of the same budget
        var shown = Math.Min(_rows.Count, RowBudget);

        //read the loaded model once for the whole shelf, probing the running server per row costs a round trip per row
        var loadedId = LoadedId();

        //read the machine once, pricing six rows against six reads gives six chances to disagree about the card
        var hardware = probes.Hardware() is { } probe
            ? Gatto.Core.Hardware.HardwareClassifier.Classify(probe) : null;

        var options = new List<ChoiceOption>();

        //a paused fetch takes the shelf's first row, in session only, read from disk so it survives a restart
        var paused = _addRoad ? probes.PausedFetches() : [];
        var resume = paused.Count > 0 ? paused[0] : null;
        if (resume is not null)
            options.Add(new ChoiceOption(ResumePausedFetch, ResumeRow(resume)));

        for (var i = 0; i < shown; i++)
            options.Add(new ChoiceOption(
                i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                //make the option's label the identity only, the transcript echoes it and a whole table row there is unreadable
                SearchRow.Label(_rows[i])));
                //add no description to the option, the facts that change the pick are table columns and the rest is on the model's page

        //keep the point-at-a-folder option, a user whose models sit on an unswept drive has no way out but re-downloading them
        options.Add(new ChoiceOption(Elsewhere, "I already have a model, let me point at the folder"));
        //on an empty shelf with no frame offer the escape rows again, the frame's keys are not shown there. a function that is a key must not also be a row
        if (_rows.Count == 0 && !SearchFoundNothing)
        {
            //the allowlist shapes browsing, a user can still type any model by name
            options.Add(new ChoiceOption(TypeAnId, "Type a model's name from Hugging Face"));
        }
        //keep the neutral empty branch, it claims nothing and covers an emptiness the engine cannot evidence


        //say what these rows are first and the order second, keep a verdict word like best off the heading
        return Emit(new SetupScreen.Choice(
            SearchKey,
            //the heading follows rows and the frame, so the typed-word sentence shows only where no shelf speaks
            Question: _rows.Count != 0 || HasLadder ? ModelTitle
                : SearchFoundNothing ? $"Nothing on Hugging Face answers \"{_search}\""
                : EmptyHeading(_searchCause),
            options,
            //a populated shelf keeps one discovery aside, the header strip says the rest. an empty shelf keeps its prose unless a frame speaks for it
            BodyRows: _rows.Count > 0 ? DiscoveryRows()
                : HasLadder ? [] : EmptyRows(_searchCause),
            //door and shelf appear together only when a family ladder is in force, whatever the row count. pass the shown slice, the cap above decided it
            Door: HasLadder ? ShelfDoorPlaceholderOf(Glyphs) : null,
            Shelf: !HasLadder ? null : new ShelfView(
                [.. _rows.Take(shown)], Shape,
                Lift: _lift,
                //take the ladder from the families table, the chip order is recorded in families.json and a second copy could drift
                Families: Families.Load().Ladder, Family: LitChip(), Lit: _lit, SmallestFirst: _smallestFirst,
                CarriedOver: _carriedOver, HiddenByKind: _hiddenByKind, MoreBehind: _moreBehind,
                OnCard: _counted?.OnCard ?? 0, InMemory: _counted?.InMemory ?? 0, TooBig: _counted?.TooBig ?? 0,
                Cut: _counted?.Cut ?? false, RateLimitedFor: _counted?.RateLimitedFor, RateLimited: _counted?.RateLimited ?? false,
                //count what the budget left off from the rows this search produced, a budget-sized count could report rows that never existed
                Total: _rows.Count,
                MoreBelow: _rows.Count - shown,
                //build the facts over the same enumeration as the rows, the index alignment ShelfView guards then holds by construction
                Facts: [.. _rows.Take(shown).Select(r => FactsFor(r, loadedId, hardware))],
                //the option and the table read the same resume string from here, one home read twice
                Resume: resume is null ? null : ResumeRow(resume),
                //the chips row's own searched bit, separate from the fit toggle above. a looked-up repo is a search too
                Searched: _search is { Length: > 0 } || _lookedUp is not null,
                //leave Empty null whenever rows are on the shelf. the chip-empty line may not name a or all, neither would lift anything
                Empty: ChipFoundNothing ? ChipEmptyRows()
                    : SearchFoundNothing ? SearchEmptyRows()
                    : shown == 0 ? NothingToShowRows() : null),
            //keep the typed word in the door where the search found nothing, a populated shelf shows no draft
            Draft: SearchFoundNothing ? _search : null,
            //the outage screen says it offers the switch, the body's m row reads the same predicate
            SwitchesSource: _rows.Count == 0 && !HasLadder && _searchCause is HubSearchCause.HubFailed
                && OffersTheLocalShelf),
            SearchKey);
    }

    //rescan on arrival, a download may have finished elsewhere since the first sweep. an old scan would deny the user their own file
    private SetupScreen ToLocalShelf()
    {
        //the sweep runs behind the local loading shelf where the face draws one, since a disk scan takes seconds
        if (LoadsShelfInBackground)
            return StartLoad(scan: true, null, search: false, keepIfEmpty: null,
                then: () => DiscoveredScreen(heading: null, invitation: true), source: ShelfSource.Local);
        //no source field here, the shelf view has it and a second field could disagree with the screen
        Sweep(null); //the Hub has no typed folder
        //pass invitation because pressing m asks about this machine, an empty answer gets its own screen
        return DiscoveredScreen(heading: null, invitation: true);
    }

    //a publisher is uploader text, cut like a local file name so a long one cannot push the pane
    private const int PublisherCells = 24;

    //a projector beside a file is this model's only when every model file in that folder is this model's, a shared folder such as Downloads proves nothing
    private bool OnlyModelIn(string ggufPath, string groupName)
    {
        var folder = System.IO.Path.GetDirectoryName(ggufPath);
        return _discovered
            .Where(f => string.Equals(System.IO.Path.GetDirectoryName(f.Path), folder, StringComparison.OrdinalIgnoreCase))
            .All(f => string.Equals(LocalShelf.ModelNameOf(System.IO.Path.GetFileName(f.Path)), groupName, StringComparison.OrdinalIgnoreCase));
    }

    //the block that replaces the table when a chip hides every row, keep it in the view because body rows render above the shelf
    private ShelfView LocalView(IReadOnlyList<(LocalShelf.LocalGroup Group, FoundModel File, bool Ruled)> shown,
        Gatto.Core.Hardware.HardwareClass? hw, int total, string? loadedPath, Func<string, string?> existing,
        IReadOnlyList<string>? empty = null)
    {
        //read once here, the answer comes from probing the running server
        var loadedId = LoadedId();

        //resolve a local row through ExistingModelFor once, and read its profile and its folder once, so the row and its facts share one reading
        var read = shown.Select(s =>
        {
            var id = existing(s.File.Path);
            var added = id is { } known ? probes.Added(known) : null;
            var vision = added?.HasProjector == true
                || probes.ProjectorBeside(s.File.Path) && OnlyModelIn(s.File.Path, s.Group.Name);
            var publisher = added?.RepoId is { Length: > 0 } repo ? repo.Split('/')[0] : s.File.Header?.QuantizedBy;
            return (Id: id, Vision: vision, Publisher: publisher is { Length: > 0 } named
                ? Gatto.Terminal.TermText.TruncateCells(Gatto.Terminal.TermText.Sanitize(named), PublisherCells, Glyphs) : null);
        }).ToList();

        return new ShelfView(
            //the badge register is keyed by file name and the badge stays on the row of the badged file, so a row asks by its file's name on disk
            [.. shown.Select((s, i) => LocalShelf.Row(s.Group, s.File, hw, badge: probes.BadgeForFile(System.IO.Path.GetFileName(s.File.Path)),
                vision: read[i].Vision))],
            Shape,
            Families: Families.Load().Ladder, Family: _localFamily ?? "all",
            Total: total,
            Facts: [.. shown.Select((s, i) => LocalShelf.FactsFor(s.Group, s.File, s.Ruled, hw,
                have: HaveFor(read[i].Id, loadedId, s.File.Path, loadedPath), haveId: read[i].Id, publisher: read[i].Publisher))],
            Source: ShelfSource.Local,
            SmallestFirst: _smallestFirst,
            Folder: _scanTyped,
            HiddenByFamily: total - shown.Count,
            Empty: empty);
    }

    //the machine classified once per screen, null when the hardware could not be read, so no row is priced against an invented machine
    private Gatto.Core.Hardware.HardwareClass? HardwareClass() =>
        probes.Hardware() is { } probe ? Gatto.Core.Hardware.HardwareClassifier.Classify(probe) : null;

    //a group's row file by the rules of the local shelf, and whether a rule chose it. with none, the first complete file stands and carries no pick mark
    private (FoundModel File, bool Ruled) RowFileFor(LocalShelf.LocalGroup g, Gatto.Core.Hardware.HardwareClass? hw,
        string? loadedPath, Func<string, string?> existing)
    {        //the active file is a full path, so two folders' files of one name stay apart
        var active = g.Files.Select(f => existing(f.Path)).OfType<string>()
            .Select(id => probes.ActiveModelFile(id)?.Path).FirstOrDefault(p => p is not null);
        var ruled = LocalShelf.RowFileOf(g, hw,
            served: f => loadedPath is not null && string.Equals(f.Path, loadedPath, StringComparison.OrdinalIgnoreCase),
            activePath: active, listed: f => existing(f.Path) is not null,
            //the pick rule's floor reads the total from the size label, so a Q3 file never wins by rule without one
            totalParams: (long?)g.Files.Select(f => f.Header?.SizeLabel).Select(ModelStructure.SizeMagnitude).FirstOrDefault(m => m is not null));
        return ruled is { } r ? (r, true) : (g.Files.FirstOrDefault(f => f.IsComplete) ?? g.Files[0], false);
    }

    //one answer per path for the life of one screen, since the live lookup reads the models folder on every call
    private static Func<string, string?> Memo(Func<string, string?> ask)
    {
        var seen = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        return path => seen.TryGetValue(path, out var id) ? id : seen[path] = ask(path);
    }

    //a pane's local file choice, as a full path, set by the unpick step and read by the row's answer that follows it
    private string? _localChoice;

    //reuse the shared family clause. no fit branch here, the local shelf never hides rows for price
    private IReadOnlyList<string> LocalChipEmptyRows(int hiddenByFamily) =>
    [
        Gatto.Repl.Render.Chrome.Cats.EmptyOf(Glyphs),
        "",
        FamilyEmptySentence(_localFamily, heldClause: null, pricedOut: false, hiddenByFamily),
    ];

    //read every field off the row, hardware passed in once per screen, a null structure renders an empty cell and never a guess
    private Shelves.ModelFacts FactsFor(ModelRow r, string? loadedId,
        Gatto.Core.Hardware.HardwareClass? hw)
    {
        //ask the repo first, the file name only when there is no repo. carry the id too, the pane names it and a second ask could drift
        var id = r.RowFile is { } file ? probes.ModelForHubRow(file.RepoId, SearchRow.RowFileName(r)) : null;
        var fileCount = r.RowFile is { } f && r.RepoOf(f) is { FileCount: > 0 } repo ? repo.FileCount : (int?)null;
        return new(Structure: r.Structure, Experts: r.Experts,
            //a Hub row names a repo rather than a file here, so the facts carry no path
            Have: HaveFor(id, loadedId), HaveId: id,
            //quants come from the tree call the search already made, fit uses FitOf like Pick, so pane and row cannot disagree
            FileCount: fileCount,
            Files: QuantsOf(r, hw),
            Publishers: PublishersOf(r, hw));
    }

    //every publisher priced the way QuantsOf prices the row's, the row's own publisher taking the row's file as its pick
    internal static IReadOnlyList<Shelves.PanePublisher>? PublishersOf(ModelRow r,
        Gatto.Core.Hardware.HardwareClass? hw)
    {
        if (hw is not { } machine || r.Publishers.Count == 0) return null;
        return [.. r.Publishers.Select((offer, i) => new Shelves.PanePublisher(offer.Org, FilesOf(r, offer, machine),
            i == r.RowPublisher ? r.RowFile : offer.Pick))];
    }

    //price every file the row's publisher holds across its repos, a file with no token included, null when the trees gave none
    internal static IReadOnlyList<Shelves.PaneFile>? QuantsOf(ModelRow r,
        Gatto.Core.Hardware.HardwareClass? hw)
    {
        if (hw is not { } machine || (r.RowOffer ?? r.Publishers.FirstOrDefault()) is not { } offer) return null;

        var files = FilesOf(r, offer, machine);
        return files.Count > 0 ? files : null;
    }

    //keep the repos' own order, the renderer sorts. a file is named by reference, since two repos of one publisher can both hold a Q4_K_M
    private static IReadOnlyList<Shelves.PaneFile> FilesOf(ModelRow r, PublisherOffer offer,
        Gatto.Core.Hardware.HardwareClass machine) =>
        Shelves.PaneFile.Tagged([.. offer.Repos.SelectMany(repo => HubSearch.Candidates(repo.Quants, r.Params)
            .Select(q => new Shelves.PaneFile(
                QuantToken.Of(q.FileName),
                q.Bytes,
                HubSearch.FitOf(q.Bytes, r.NativeCtx, machine, Shelves.Pane.KvContext, r.Arch, q.StreamedBytes),
                new FileRef(offer.Org, repo.RepoId, q.RepoPath))))]);

    //apply the choice once, by replacing the row's file and its regime. an unresolvable reference leaves the old file standing
    private void ApplyPickedFile(string key, FileRef file)
    {
        if (!int.TryParse(key, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var at)
            || at < 0 || at >= _rows.Count) return;

        var row = _rows[at];
        if (row.QuantOf(file) is not { } chosen) return;
        var publisher = row.Publishers.ToList().FindIndex(p => string.Equals(p.Org, file.Publisher, StringComparison.OrdinalIgnoreCase));
        var fit = probes.Hardware() is { } snapshot
            ? HubSearch.FitOf(chosen.Bytes, row.NativeCtx, Gatto.Core.Hardware.HardwareClassifier.Classify(snapshot),
                Shelves.Pane.KvContext, row.Arch, chosen.StreamedBytes)
            : row.Fit;
        _rows = [.. _rows.Select((r, i) => i == at
            ? r with { RowFile = file, Fit = fit, RowPublisher = publisher >= 0 ? publisher : r.RowPublisher }
            : r)];
    }

    //loaded is a claim about a file, so compare paths when both are known. with no path to compare, a matching id still reads loaded
    private static HaveMark HaveFor(string? modelId, string? loadedId,
        string? path = null, string? loadedPath = null)
    {
        if (modelId is not { Length: > 0 }) return HaveMark.None;
        if (!string.Equals(modelId, loadedId, StringComparison.OrdinalIgnoreCase)) return HaveMark.Added; //a loaded model is also added, loaded wins (it changes what Enter does)
        if (loadedPath is not { Length: > 0 } || path is not { Length: > 0 }) return HaveMark.Loaded;
        return SamePath(path, loadedPath) ? HaveMark.Loaded : HaveMark.OtherFile;
    }

    //compare full paths case-insensitively, a malformed path answers not the same rather than throwing into a render
    private static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(System.IO.Path.GetFullPath(a), System.IO.Path.GetFullPath(b),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    //true when a sort's own search found nothing and the rows on screen are the old ones
    private bool _carriedOver;

    //leave the root rows raw, the face is the sanitize seam, sanitizing here would be a second home for that rule
    private IReadOnlyList<SetupRow> LookedInRows()
    {
        if (_scanRoots.Count == 0) return [];
        return [.. new SetupRow[] { "", "gatto looked in:" }, .. RootRows()];
    }

    //put the indent on the row rather than in the text, a wrapped path reads as two paths
    private IReadOnlyList<SetupRow> RootRows() =>
        [.. _scanRoots.Select(root => new SetupRow(root, Indent: 2))];

    //the words are the user's, quoted and not paraphrased, quoting is what makes the sentence checkable by the person who typed it
    private string SearchEmptySentence() => $"nothing on Hugging Face answers \"{_search}\"";

    //a chip emptied the shelf, which is not the search finding nothing. only true when a count backs the emptiness
    private bool ChipFoundNothing =>
        _rows.Count == 0 && _search is null && LitChip() is { } chip && chip != "all"
        && _searchCause is HubSearchCause.NothingFits;

    //latched once the walk has stood on a shelf, not derived from the search and family, because the all chip clears both
    private bool HasLadder => _stoodOnAShelf && _searchCause is not HubSearchCause.HubFailed;

    //never cleared by a later empty search, having stood on a shelf is not undone by narrowing it to nothing
    private bool _stoodOnAShelf;

    //an unreachable Hub is excluded, the search never ran so there is no honest sentence about the word
    private bool SearchFoundNothing =>
        _rows.Count == 0 && _search is { Length: > 0 }
        && _searchCause is not HubSearchCause.HubFailed;

    //this block renders inside the shelf so the chips row stays above the cat. reuse Shelf.HiddenClause, a second spelling would drift
    private IReadOnlyList<string> ChipEmptyRows() =>
    [
        Gatto.Repl.Render.Chrome.Cats.EmptyOf(Glyphs),
        "",
        ChipSentence(),
    ];

    //the fit case wins when both numbers are set, it means this family has rows and every one was too big
    private string ChipSentence() =>
        FamilyEmptySentence(LitChip(), HeldClause, PricedOut, 0);

    //the fit clause wins, it is the one sentence the engine actually measured. the local shelf passes no fit clause, nothing there is held for price
    private string FamilyEmptySentence(string? family, string? heldClause, bool pricedOut, int hiddenByFamily)
    {
        if (heldClause is { } held)
            return pricedOut
                ? $"No {family} model fits this machine's memory {Glyphs.Dot} {held}"
                : $"No {family} model on this shelf {Glyphs.Dot} {held}";
        if (Gatto.Cli.Setup.Shelves.Shelf.FamilyClause(hiddenByFamily) is { } elsewhere)
            return $"No {family} model on this shelf {Glyphs.Dot} {elsewhere}";
        return $"No {family} model on this shelf";
    }

    //what the a key brings back, or null when it brings back nothing. named once so sentences cannot ask the question differently
    private string? HeldClause => _moreBehind ? "a shows all" : null;

    //the engine listed models and none had a file that fits, which is the only evidence for a sentence about memory
    private bool PricedOut => _searchCause is HubSearchCause.NothingFits;

    //this emptiness has no count behind it, so the sentence must not name a or all, neither lifts anything here
    private IReadOnlyList<string> NothingToShowRows() =>
    [
        "",
        Gatto.Repl.Render.Chrome.Cats.EmptyOf(Glyphs),
        "",
        "nothing to show on this shelf right now",
        $"another family might {Glyphs.Dot} d to search by name",
        "",
        "",
    ];

    //its blank rows lead and end the block so the door stays put when a search comes back empty
    private IReadOnlyList<string> SearchEmptyRows() =>
    [
        "",
        Gatto.Repl.Render.Chrome.Cats.EmptyOf(Glyphs),
        "",
        SearchSentence(),
        //the ways forward are things the door does, which is why it keeps the focus and the typed word
        $"another word might {Glyphs.Dot} or paste a full repo id, gatto takes those too",
        "",
        "",
    ];

    //a memory claim needs PricedOut, rows held for being newer were never priced, so name what is held and a
    private string SearchSentence() =>
        HeldClause is { } held
            ? PricedOut
                ? $"nothing answering \"{_search}\" fits this machine's memory {Glyphs.Dot} {held}"
                : $"{SearchEmptySentence()} {Glyphs.Dot} {held}"
            : SearchEmptySentence();

    //one heading per cause the search evidenced, don't say small machine when the Hub is down
    private static string EmptyHeading(HubSearchCause? cause) => cause switch
    {
        //keep shelf out of these sentences, it is our word, a register a user never saw
        HubSearchCause.NothingFits => "None of these models fit this machine",
        HubSearchCause.HubFailed => "Couldn't reach the model list",
        _ => "No models to show right now",
    };

    //all branches end with the ways forward, an empty screen with no next step is where a wizard loses someone
    private IReadOnlyList<SetupRow> EmptyRows(HubSearchCause? cause) => cause switch
    {
        //hardware line first, a user who reads the numbers sees arithmetic about their machine rather than an opinion about the models
        HubSearchCause.NothingFits =>
        [
            HardwareLine(probes.Hardware()),
            "Every model gatto looked at needs more than that.",
            NextWays,
        ],
        //say the machine is fine, otherwise people go check their wifi for nothing
        HubSearchCause.HubFailed =>
        [
            "gatto couldn't reach Hugging Face, so there is no list to show. Nothing is wrong with this machine.",
            //offer the m row only when a local shelf stands behind it and only on faces that bind m
            .. OffersTheLocalShelf
                ? new[] { DiscoveryRow(Announceable, _addRoad) with
                    { Text = $"The {Announceable} {(Announceable == 1 ? "model on this machine is" : "models on this machine are")} one m away" } }
                : [],
            NextWays,
        ],
        //the unevidenced case, wording true whichever thing happened
        _ =>
        [
            HardwareLine(probes.Hardware()),
            "That can mean nothing here fits this machine, or that the model list is out of reach.",
            NextWays,
        ],
    };

    //list the sweep's own roots, a re-derived list would describe a sweep nobody made
    private IReadOnlyList<string> EmptyLocalRows() =>
    [
        Gatto.Repl.Render.Chrome.Cats.EmptyOf(Glyphs),
        "",
        //a typed folder can be empty on a machine that has models
        _scanTyped is null
            ? "no models on this machine yet, gatto looked in:"
            : "no models in that folder, gatto looked in:",
        .. _scanRoots,
        "",
        "type a folder path below, gatto looks there and in its subfolders",
        //the one key besides the door, with the footer's verb, back only where there is a step to go back to
        CanGoBack ? "press Esc to go back" : "press Esc to leave",
    ];

    //the count is the sweep's own, so the number and the shelf behind m cannot disagree. the two roads' wording differs on purpose
    private static SetupRow DiscoveryRow(int found, bool inSession) =>
        new(inSession
                ? $"gatto found {found} model{(found == 1 ? "" : "s")} on this machine not yet on its list, m shows them"
                : $"gatto also found {found} model{(found == 1 ? "" : "s")} already on this machine, m shows them",
            Tone: RowTone.Aside, Keys: ["m"]);

    //no sweep means nothing to announce, no source switch means naming a key this face never binds
    private IReadOnlyList<SetupRow> DiscoveryRows() =>
        OffersTheLocalShelf ? [DiscoveryRow(Announceable, _addRoad)] : [];

    //whether m has a local shelf with something on it, every m row reads this one predicate
    private bool OffersTheLocalShelf => CanSwitchSource && Announceable > 0;

    //in a session the sentence counts models not yet on its list. on the setup road all count, there is no list yet
    private int Announceable => _discovered.Count(NotOnTheList);

    //reuse ExistingModelFor rather than asking the same question a second way
    private bool NotOnTheList(FoundModel m) =>
        !_addRoad || probes.ExistingModelFor(m.Path) is not { Length: > 0 };

    //state what happened and blame nothing. it names no m, the footer already advertises the way back
    internal const string OutOfReach = "Hugging Face is out of reach, showing the models on this machine.";

    //one home for the ways out of an empty shelf, three branches offering them separately would drift
    private const string NextWays = "You can point gatto at a model you already have, or name one directly.";

    private SetupScreen AnswerSearch(string key)
    {
        //resuming goes to consent rather than straight to the transfer, the user agrees to the rest of the download. the fetch reads its own .part file
        if (key == ResumePausedFetch && probes.PausedFetches() is [{ } paused, ..]
            && probes.ResumeOffer(paused) is { } resumed)
            return ModelConsent(resumed);

        //m is a toggle, so the flow reads its own state rather than the answer
        if (key == CtlSource) return ToLocalShelf();

        //the first click sorts smallest first, each later click flips it, and the rows in hand are re-arranged without a request
        if (key == CtlParams)
        {
            _smallestFirst = _smallestFirst is not true;
            _rows = Sorted(_rows);
            return Rearranged(SearchScreen);
        }

        //a chip lights its family, all lights every family
        if (key.StartsWith(CtlFamily, StringComparison.Ordinal))
        {
            Light(key[CtlFamily.Length..]);
            //the chip clears the typed search, the engine ignores family while a search is in force, without the clear the key does nothing
            _search = null;
            return Search();
        }

        if (key == CtlLift)
        {
            //a toggle of exactly two states, calling it a cycle would promise a third
            _lift = !_lift;
            return Search();
        }

        if (key == TypeAnId || key == CtlSearch)
            return Emit(new SetupScreen.Ask(
                TypedIdKey,
                "What is it called on Hugging Face?",
                Validate: TypedIdProblem,
                Placeholder: "Type or paste the id, e.g. org/model"), TypedIdKey);

        if (key == Elsewhere)
            return Emit(new SetupScreen.Ask(
                ScanPathKey,
                "Which folder should gatto look in?",
                Validate: t => Gatto.Roles.Serve.LlamaAssetSteering.NormalizePath(t).Length == 0 ? "Type a folder path" : null,
                Placeholder: @"Type or paste the folder, e.g. C:\models"),
                ScanPathKey);

        //read the typed text before the Numbered tail, Numbered throws on anything that is not a row index
        if (ShelfControls.Untyped(key) is { } typed) return AnswerTypedText(typed);

        return PickedFromHub(Numbered(_rows, key, SearchKey));
    }

    //remember the term before the walk, the empty screen quotes it and the shelf's controls rerun the search
    private SetupScreen SearchFor(string text)
    {
        _search = text.Length == 0 ? null : text;
        return Search();
    }

    //one classifier for both doors, so the two typing screens share one grammar
    private SetupScreen AnswerTypedText(string typed)
    {
        if (Gatto.Cli.Setup.Tui.TypedDoor.Classify(typed) == Gatto.Cli.Setup.Tui.DoorInput.Address) return AnswerAddress(typed);
        if (Gatto.Cli.Setup.Tui.TypedDoor.Classify(typed) == Gatto.Cli.Setup.Tui.DoorInput.Path)
            return DiscoverTyped(Gatto.Roles.Serve.LlamaAssetSteering.NormalizePath(typed));

        return AnswerTypedId(typed);
    }

    //three outcomes, three sentences, a typo is not an outage
    private SetupScreen AnswerTypedId(string typed)
    {
        //classify what was typed before anything acts, search is the default, most of what gets typed is a search
        if (!HubUrl.TryParse(typed, out var reference)) return SearchFor(typed.Trim());

        if (reference is HubRef.Collection)
            //a collection is many models and gatto can't pick one, the sentence says what the link is
            return Emit(new SetupScreen.Ask(
                TypedIdKey,
                "That's a collection, which is many models, open one of them and paste that instead",
                Validate: TypedIdProblem,
                Placeholder: "Type or paste the id, e.g. org/model"), TypedIdKey);

        //use the parsed id, a pasted link resolves the repo it names
        var typedId = ((HubRef.Model)reference).RepoId;

        //the lookup reads the repo, its tree and its tables, so it runs behind the loading shelf with a line that names the id
        if (LoadsShelfInBackground)
        {
            var lookup = Task.Run(() => probes.EvaluateTypedId(typedId));
            return StartLoad(scan: false, null, search: false, keepIfEmpty: null,
                then: () => AnswerLookup(Joined(lookup, (TypedIdOutcome)new TypedIdOutcome.Unreachable(false))),
                other: lookup, step: $"looking up {typedId} on Hugging Face", serving: false);
        }

        return AnswerLookup(probes.EvaluateTypedId(typedId));
    }

    //three outcomes of a typed repo id, three sentences, a typo is not an outage
    private SetupScreen AnswerLookup(TypedIdOutcome outcome)
    {
        switch (outcome)
        {
            case TypedIdOutcome.Ok ok:
                return LookedUp(ok.Row);

            case TypedIdOutcome.Malformed:
                //the refusal names the shape of a valid name
                return Emit(new SetupScreen.Ask(
                    TypedIdKey,
                    "That doesn't look like a Hugging Face name, they look like org/model, e.g. bartowski/Qwen3-8B-GGUF",
                    Validate: TypedIdProblem,
                    Placeholder: "Type or paste the id, e.g. org/model"), TypedIdKey);

            //say it as a fact about the files, a verdict on the model is not allowed here
            case TypedIdOutcome.NoUsableQuant:
                return Emit(new SetupScreen.Ask(
                    TypedIdKey,
                    "Found it, but none of its files fit this machine, try a smaller model",
                    Validate: TypedIdProblem,
                    Placeholder: "Type or paste the id, e.g. org/model"), TypedIdKey);

            //say what the repo is, don't blame what the user typed
            case TypedIdOutcome.NoWeights:
                return Emit(new SetupScreen.Ask(
                    TypedIdKey,
                    "Found it, but that repo publishes no weights, it holds no model to run",
                    Validate: TypedIdProblem,
                    Placeholder: "Type or paste the id, e.g. org/model"), TypedIdKey);

            case TypedIdOutcome.Unreachable u:
                return Emit(new SetupScreen.Ask(
                    TypedIdKey,
                    u.Gated
                        ? "That one needs you to accept its licence on Hugging Face first"
                        //say "or private", the Hub answers 401 whether a repo is absent or it is private
                        : "Couldn't find that one, check the name, or it may be private",
                    Validate: TypedIdProblem,
                    Placeholder: "Type or paste the id, e.g. org/model"), TypedIdKey);

            default:
                throw new InvalidOperationException("unreachable, TypedIdOutcome is closed");
        }
    }

    //a typed repo id lands on the shelf as its one row, so the pane can choose another file before Enter goes forward with the row's pick
    private SetupScreen LookedUp(ModelRow row)
    {
        _lookedUp = row.RowFile?.RepoId ?? row.Model;
        _carriedOver = false;
        _rows = [row];
        _searchCause = null;
        _hiddenByKind = 0;
        _moreBehind = false;
        int Is(Gatto.Core.Models.FitRegime fit) => row.Fit == fit ? 1 : 0;
        _counted = new ShelfOutcome(_rows,
            Is(Gatto.Core.Models.FitRegime.FitsGpu), Is(Gatto.Core.Models.FitRegime.FitsRamOnly), Is(Gatto.Core.Models.FitRegime.DoesNotFit),
            MoreBehindA: false, HiddenByKind: 0, FullPageOrgs: [], Cause: null);
        _stoodOnAShelf = true;
        return SearchScreen();
    }
}
