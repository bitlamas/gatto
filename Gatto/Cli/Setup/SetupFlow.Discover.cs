using Gatto.Cli.Setup.Shelves;
using Gatto.Core.Acquire;

namespace Gatto.Cli.Setup;

//models found on disk: the typed folder, adopt, move, projector, custom build, collision and quant
internal sealed partial class SetupFlow
{
    //offer what is already on this machine before proposing a download, an empty scan is normal rather than a failure
    private SetupScreen Discover(string? extraRoot)
    {
        //keep the discovered list first on the plain face, it cannot bind m, hiding the list behind the key would delete it
        if (!CanSwitchSource)
        {
            Sweep(extraRoot);
            return DiscoveredScreen(heading: null);
        }

        //the scan and the search start together behind the loading shelf, and the landing is chosen when both have answered
        if (LoadsShelfInBackground)
        {
            var opening = OpensOnLoadingScreen && !_opened;
            _opened = true;
            return StartLoad(scan: true, extraRoot, search: true, keepIfEmpty: null, then: AfterDiscover,
                opening: opening);
        }

        Sweep(extraRoot);
        //run the search before choosing a landing, the arm below depends on its outcome and a landing chosen first would guess at it
        RunSearch(keepIfEmpty: null);
        return AfterDiscover();
    }

    //open the local shelf on an outage only when this machine has models, an empty machine gets the Hub's failure screen. scope it to the arrival only
    private SetupScreen AfterDiscover() =>
        _searchCause is HubSearchCause.HubFailed && _discovered.Count > 0
            ? DiscoveredScreen(heading: null, notice: OutOfReach)
            : SearchScreen();

    //treat a typed .gguf as the model itself, sweep its folder and adopt the scan's row (a second FoundModel here could disagree)
    private SetupScreen DiscoverTyped(string root)
    {
        _pickedFromLocal = true;
        //sweep a non-.gguf path as itself, and a .gguf's folder, a file path as a folder hides every model
        var scanRoot = root.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)
            ? System.IO.Path.GetDirectoryName(root) is { Length: > 0 } folder ? folder : null
            : root;
        //for a bare .gguf name don't sweep a folder, the name is not a folder either, and leave _scanTyped unset
        if (scanRoot is null) _scanTyped = null;

        if (LoadsShelfInBackground)
        {
            //latched now rather than at the landing, so the loading frame's folder slot names the folder being swept
            if (scanRoot is not null) _scanTyped = scanRoot;
            return StartLoad(scan: true, scanRoot, search: false, keepIfEmpty: null,
                then: () => AfterTyped(root), source: ShelfSource.Local);
        }

        Sweep(scanRoot);
        return AfterTyped(root);
    }

    //a typed .gguf the sweep found is adopted, and anything else answers with the local shelf and its invitation, so an empty folder says so
    private SetupScreen AfterTyped(string root) =>
        root.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)
        && _discovered.FirstOrDefault(m => string.Equals(m.Path, root, StringComparison.OrdinalIgnoreCase)) is { } named
            ? Adopt(named)
            : DiscoveredScreen(heading: null, invitation: true);

    //keep the sweep and the rows it leaves together, so two screens cannot scan the same folder differently.
    private void Sweep(string? extraRoot) => ApplyScan(probes.Scan(extraRoot), extraRoot);

    //the sweep's fields set from a scan's answer, on the flow's thread whether the scan ran here or behind the loading shelf
    private void ApplyScan(ScanResult scan, string? extraRoot)
    {
        _discovered = scan.Found;
        _scanRoots = scan.Roots;
        //keep _scanTyped latched through later ambient sweeps, the user typed it and these rows still come from that folder
        if (extraRoot is { Length: > 0 }) _scanTyped = extraRoot;
        //filter the scan's rows to the typed folder and name only that folder in the roots, the scan merges the ambient roots in
        if (_scanTyped is { } typed)
        {
            _discovered = [.. scan.Found.Where(m => Gatto.Core.Acquire.ModelLocation.IsInside(typed, m.Path))];
            _scanRoots = [typed];
        }
    }

    //build the list over the last scan, the folder answer matches filenames against those results. set invitation only where the user asked for this shelf
    private SetupScreen DiscoveredScreen(string? heading, string? notice = null, bool invitation = false)
    {
        //the empty shelf names where gatto looked and invites a folder, the user's models may live on a drive that was never scanned
        if (invitation && _discovered.Count == 0 && _scanRoots.Count > 0)
            return Emit(new SetupScreen.Choice(
                DiscoveredKey,
                ModelTitle,
                //the door types a folder and Esc reaches the Hub, so the shelf has no option rows
                [],
                Door: $"type a folder path{Glyphs.Ellipsis}",
                //pass no families for an empty shelf, the block takes the table's place and the chips row draws nothing
                Shelf: new ShelfView([], Shape, Source: ShelfSource.Local, Empty: EmptyLocalRows())),
                DiscoveredKey);

        if (_discovered.Count == 0)
        {
            //add the scan line only when rows follow it, the next screen already reports nothing and two messages read as a fault
            return Search(then: () =>
            {
                if (_rows.Count > 0)
                    //put this on the narration channel rather than _emitted, a row queued behind the returned screen is never read
                    _narrate.Add(new SetupScreen.Info("model.none",
                        [new SetupRow("No models found on this machine yet"), .. LookedInRows()]));
                return SearchScreen();
            });
        }

        //adopted models stay (with their have-mark), this is also the reuse picker. one row per model, its files in the pane
        var groups = LocalShelf.Group(_discovered);
        var hw = HardwareClass();
        //read once per shelf, the answer comes from probing the running server, and the model of each file comes from reading the models folder
        var loadedPath = LoadedPath();
        var existing = Memo(probes.ExistingModelFor);
        var shown = new List<(int At, LocalShelf.LocalGroup Group, FoundModel File, bool Ruled)>();
        //no chip lit shows every family, models with no family included
        for (var i = 0; i < groups.Count; i++)
        {
            var (file, ruled) = RowFileFor(groups[i], hw, loadedPath, existing);
            if (_localFamily is not { } fam || Families.Load().FamilyOf(file.Header?.Architecture) == fam)
                shown.Add((i, groups[i], file, ruled));
        }
        //a params click sorts the local shelf as it sorts the Hub's, each option moving with its row
        shown = [.. SortedBy(shown, s => ModelStructure.SizeMagnitude(s.File.Header?.SizeLabel), s => s.Group.Name)];

        var options = new List<ChoiceOption>();
        foreach (var (at, g, m, _) in shown)
            //keep a vendor off the row, a vendor label editorialises about someone else's weights
            options.Add(new ChoiceOption(
                at.ToString(System.Globalization.CultureInfo.InvariantCulture), //index into the scan's groups even when a chip filters, AnswerDiscovered regroups the same scan and reads the row's file by path
                g.Name,
                $"{Gb((ulong)Math.Max(0, m.FileBytes))} {Glyphs.Dot} {System.IO.Path.GetDirectoryName(m.Path)}"));

        return Emit(new SetupScreen.Choice(
            DiscoveredKey,
            heading ?? (groups.Count == 1 ? "Found a model already on this machine" : $"Found {groups.Count} models already on this machine"),
            options,
            //draw the notice in the body above the chips, where the Hub shelf puts its discovery line, so a shelf's reason always appears there
            BodyRows: notice is { Length: > 0 } why ? [new SetupRow(why, Tone: RowTone.Aside)] : [],
            //keep the door even when a chip empties the shelf, only the filter can empty it here. a null door and shelf make the face draw a numbered list
            Door: LocalShelfDoorPlaceholderOf(Glyphs),
            //draw the local list through the same shelf view as the Hub, only the source differs
            Shelf: LocalView([.. shown.Select(s => (s.Group, s.File, s.Ruled))], hw, groups.Count, loadedPath, existing,
                empty: shown.Count == 0 ? LocalChipEmptyRows(groups.Count) : null)), DiscoveredKey);
    }

    private SetupScreen AnswerDiscovered(string key)
    {
        //re-search the Hub rather than restoring a snapshot, a stale shelf prices against state nobody re-read
        if (key == CtlSource) return Search();

        //same chip keys for both shelves, this one writes the local field (a Hub press says nothing about this disk)
        if (key.StartsWith(CtlFamily, StringComparison.Ordinal))
        {
            var chip = key[CtlFamily.Length..];
            _localFamily = chip is "all" or "" ? null : chip;
            return Rearranged(() => DiscoveredScreen(heading: null));
        }

        //the params header sorts here as on the Hub, one field for both shelves, so m keeps the order
        if (key == CtlParams)
        {
            _smallestFirst = _smallestFirst is not true;
            return Rearranged(() => DiscoveredScreen(heading: null));
        }

        //the footer draws a here, so answer it (unhandled it reaches Numbered and throws, and the toggle is shared with the Hub shelf)
        if (key == CtlLift)
        {
            _lift = !_lift;
            return Rearranged(() => DiscoveredScreen(heading: null));
        }

        //an address is answered as at the Hub's doors, everything else typed here is a path
        if (ShelfControls.Untyped(key) is { } address
            && Gatto.Cli.Setup.Tui.TypedDoor.Classify(address) == Gatto.Cli.Setup.Tui.DoorInput.Address)
            return AnswerAddress(address);

        //the typed answer is a folder, read it through Untyped (a numeral typed in the door and a row index are the same string)
        if (ShelfControls.Untyped(key) is { } typedAnswer
            && Gatto.Roles.Serve.LlamaAssetSteering.NormalizePath(typedAnswer) is { Length: > 0 } typedRoot)
            return DiscoverTyped(typedRoot);

        //the key names a group of the same scan, and the file is resolved by path: the pane's choice, else the row's file
        var group = Numbered(LocalShelf.Group(_discovered), key, DiscoveredKey);
        _pickedFromLocal = true;
        var path = _localChoice ?? RowFileFor(group, HardwareClass(), LoadedPath(), Memo(probes.ExistingModelFor)).File.Path;
        _localChoice = null;
        return Adopt(group.Files.FirstOrDefault(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"{DiscoveredKey} chose {path}, which is not one of the row's files"));
    }

    //the one completion point for a chosen model. a found file stays put, a wizard pick must not move 30 GB
    private SetupScreen Adopt(FoundModel model, Func<SetupScreen>? stopped = null)
    {
        //check completeness here and nowhere else, every route inherits the refusal. do it before the choice is announced
        if (!model.IsComplete) return PartialSet(model);

        Selected = model;

        //a file that already belongs to a model reuses its id and skips the scaffold, that's what makes a re-run idempotent
        if (probes.ExistingModelFor(model.Path) is { Length: > 0 } existing)
        {
            _modelId = existing;
            //tell them an existing model was found, it's why the next screen is not the one they expected
            _narrate.Add(new SetupScreen.Info("model.reused",
                //in /model add the user is already inside gatto, the row says added there
                [new SetupRow(_addRoad
                    ? $"{Glyphs.Ok} Already added as {existing}, using that model"
                    : $"{Glyphs.Ok} Already set up as {existing}, using that model")]));
            return Done();
        }

        //a file about to get a profile entry is read first unless the run already holds its hash, so the entry records the bytes
        if (KnownHashFor(model) is null)
            return Read(model, hash => { Know(model, hash); return AdoptNew(model); },
                stopped ?? (() => DiscoveredScreen(heading: null)));

        return AdoptNew(model);
    }

    //the adoption past the reuse and the read, the clash question or the move offer
    private SetupScreen AdoptNew(FoundModel model)
    {
        //a different file holding the same id is a question only the user can answer, don't merge it with the reuse above
        var (clash, clashId) = probes.ClashFor(model.Path, SourceOf(model)?.RepoId);
        if (clashId is { Length: > 0 } held)
        {
            _clashingModelId = held;
            //a second quant joins its model in silence, no screen for a question with one real answer
            if (clash == Gatto.Roles.Models.IdClash.SameModel) return AddFileIntent(model, held);

            //the add option shows only when gatto can't tell, you can't add to a model that won't load
            return ModelCollision(held, canAdd: clash == Gatto.Roles.Models.IdClash.CannotTell);
        }

        return OfferMove(model, replace: false);
    }

    //the arrival version of the move offer, same key and handler. move is first here, the screen just argued for it
    private SetupScreen WatchArrived(FoundModel model, MoveOffer offer)
    {
        _moveOffer = offer;
        var name = System.IO.Path.GetFileName(model.Path);
        var verified = _watchCause == WatchCause.Unreachable
            ? $"{name} arrived, byte for byte the file Hugging Face publishes."
            //when there's no hash, name what was checked instead (one headline row in two states)
            : $"{name} arrived, name and size matching what Hugging Face lists.";

        List<SetupRow> rows =
        [
            new(verified, RowTone.Aside, Glyph: RowGlyph.Good, Lift: [name]),
            "",
            ModelFacts.Row("found", model.Path),
        ];

        //name the encoder that arrived too, two files fetched and one reported would under-claim
        if (Picked is { } row)
            foreach (var extra in WatchFiles(row).Where(f => f.Encoder && Arrived(f.Name)))
                rows.Add(ModelFacts.Row("with", extra.Name + (_watchCause == WatchCause.Unreachable
                    ? $" {Glyphs.Dot} verified"
                    //reuse the headline's words here, the row and the sentence above must not drift
                    : $" {Glyphs.Dot} name and size match"), lift: extra.Name));

        rows.Add("");
        rows.Add(new SetupRow("Downloads is where files land, not where models live, a cleared "
            + "Downloads folder would take the model with it.", RowTone.Aside));

        return Emit(new SetupScreen.Choice(
            MoveKey,
            ModelTitle,
            [new ChoiceOption(Yes, $"Move files into {offer.SuggestedDir}"),
             new ChoiceOption(No, "Leave them in Downloads")],
            BodyRows: rows,
            PurredSinceWatch: true), MoveKey);
    }

    //leaving it is first and the default (a multi-GB copy is a choice), another drive says copy with the size up front
    private SetupScreen OfferMove(FoundModel model, bool replace)
    {
        _moveReplace = replace;
        //a file the watch waited for gets the arrival screen, it says what arrived before asking where it lives
        if (_arrivedByWatch && probes.MoveOfferFor(model.Path) is { } watched)
        {
            Selected = model;
            return WatchArrived(model, watched);
        }
        if (probes.MoveOfferFor(model.Path) is not { } offer) return OfferProjector(model, replace);

        _moveOffer = offer;
        var rows = new List<SetupRow>
        {
            //state the fact and why it matters, don't tell the user they should move
            new("This model is in your Downloads folder, which Windows can be set to clean out "
                + "automatically."),
        };

        if (offer.StorageSense == "armed") //storage sense informs, it never gates
            rows.Add(new SetupRow("That cleanup is currently armed on this machine.", RowTone.Aside));

        //an unmeasured size gets no size clause, 0 GB on a screen that prices the copy reads as free
        if (offer.CrossVolume)
            rows.Add(new SetupRow(
                offer.Bytes > 0
                    ? $"That folder is on another drive, so moving would COPY {SearchRow.Gb(offer.Bytes)}."
                    : "That folder is on another drive, so moving would COPY the file rather than rename it.",
                RowTone.Aside));

        return Emit(new SetupScreen.Choice(
            MoveKey,
            "Where should this model live?",
            [
                new ChoiceOption(No, "Leave it where it is"),
                new ChoiceOption(Yes, $"Move it to {offer.SuggestedDir}"),
                new ChoiceOption(Elsewhere, "Move it somewhere I choose"),
            ],
            BodyRows: rows), MoveKey);
    }

    //keep writes nothing, null would read as a stored intent, silence means leave it alone
    private SetupScreen AnswerMove(string key)
    {
        _awaiting = null;

        if (key == Yes && _moveOffer is { } offer)
        {
            Writes = Writes with { MoveTo = offer.SuggestedDir };
            return OfferProjector(Selected!, _moveReplace);
        }

        if (key == Elsewhere)
            return Emit(new SetupScreen.Ask(
                MovePathKey,
                "Which folder?",
                //the folder doesn't need to exist, adoption creates it. reject only an empty answer
                Validate: t => Gatto.Roles.Serve.LlamaAssetSteering.NormalizePath(t).Length == 0
                    ? "Type or paste a folder path"
                    : null,
                Placeholder: @"Type or paste the folder, e.g. D:\models"), MovePathKey);

        return OfferProjector(Selected!, _moveReplace);
    }

    private SetupScreen AnswerMovePath(string typed)
    {
        _awaiting = null;
        Writes = Writes with { MoveTo = typed };
        return OfferProjector(Selected!, _moveReplace);
    }

    //a projector sits beside the model, offer to add it. show what the pair costs to run, and only when adding it changes that
    private SetupScreen OfferProjector(FoundModel model, bool replace)
    {
        _projectorReplace = replace;
        //the watch already offered text-only while the encoder was on screen, don't ask the same question twice
        if (_declinedEncoder) return OfferCustomBuild(model, replace);
        //no projector goes on to the build offer, going straight to the scaffold would leave that offer unreachable
        if (probes.ProjectorFor(model.Path) is not { } found)
            return OfferCustomBuild(model, replace);

        _projector = found.Path;
        var rows = new List<SetupRow>
        {
            new($"Found {System.IO.Path.GetFileName(found.Path)} beside your model, "
                + "that is what lets a model read images."),
        };

        //warn only when the projector changes the fit, a screen that warns about everything trains users to skip warnings
        if (probes.PairFit(model.Path, found.Bytes) is { } fit && fit.Alone != fit.WithProjector)
            rows.Add(new SetupRow(
                $"Loading both together {SearchRow.FitWords(fit.WithProjector, Shape, Glyphs)}, "
                + $"the model on its own {SearchRow.FitWords(fit.Alone, Shape, Glyphs)}.", RowTone.Aside));

        return Emit(new SetupScreen.Choice(
            ProjectorKey,
            "Add vision to this model?",
            [
                //yes leads the cursor, but the screen still asks, a projector beside the weights is never armed on its own
                new ChoiceOption(Yes, "Yes, add the projector"),
                new ChoiceOption(No, "No, set it up for text"),
            ],
            BodyRows: rows), ProjectorKey);
    }

    //yes writes the projector, no writes nothing (Back's Writes restore is what stops an abandoned choice)
    private SetupScreen AnswerProjector(string key)
    {
        _awaiting = null;
        if (key == Yes && _projector is { Length: > 0 } path)
            Writes = Writes with { Projector = path };

        return OfferCustomBuild(Selected!, _projectorReplace);
    }

    //when the pinned build can't load this architecture, offer one that can, only this branch writes it. no is first and scaffolds anyway
    private SetupScreen OfferCustomBuild(FoundModel model, bool replace)
    {
        _customBuildReplace = replace;
        var arch = probes.ArchitectureOf(model.Path);
        if (!ArchNote.NeedsAnotherBuild(arch)) return ScaffoldIntent(model, replace);

        return Emit(new SetupScreen.Choice(
            CustomBuildKey,
            "Point this model at a different llama.cpp build?",
            [
                //no is first, most users have no second build and the model is written either way. on /model add the row says added
                new ChoiceOption(No, _addRoad ? "No, add it anyway" : "No, set the model up anyway"),
                new ChoiceOption(Yes, "Yes, I have one"),
            ],
            BodyRows: [.. ArchNote.AskRows(arch).Select(r => (SetupRow)r)]), CustomBuildKey);
    }

    //yes opens the path ask, no writes nothing, Back's Writes restore keeps an abandoned answer off disk
    private SetupScreen AnswerCustomBuild(string key)
    {
        _awaiting = null;
        if (key != Yes) return ScaffoldIntent(Selected!, _customBuildReplace);

        return Emit(new SetupScreen.Ask(
            CustomBuildPathKey,
            "Where is that llama-server.exe?",
            Validate: LlamaPathComplaint,
            Placeholder: LlamaPathPlaceholder), CustomBuildPathKey);
    }

    //verify a per-model binary with the same probe and sentences as the global one (a wrong shape is wrong under any key)
    private SetupScreen AnswerCustomBuildPath(string typed)
    {
        _awaiting = null;
        var result = probes.VerifyLlamaServer(typed);
        if (LlamaProbeComplaint(result.Shape, probes.ChooseLlamaAsset()?.CudartZipName is not null,
                steeredDoor: false, result.VcRuntimeAbsent, result.SiblingName)
            is { } complaint)
            return Emit(new SetupScreen.Ask(
                CustomBuildPathKey, complaint,
                Validate: LlamaPathComplaint,
                Placeholder: LlamaPathPlaceholder), CustomBuildPathKey);

        //write what actually ran, a folder answer stores the exe the probe resolved to
        Writes = Writes with { ModelLlamaServer = result.Ran ?? typed };
        //don't announce the pick again here, the answer is in the transcript right above
        return ScaffoldIntent(Selected!, _customBuildReplace);
    }

    //when the repo can't tell a second quant from a same-named model, ask the user. when adding is impossible the option is absent
    private SetupScreen ModelCollision(string existingId, bool canAdd) => Emit(new SetupScreen.Choice(
        ModelCollisionKey,
        //call the weights a file here, the word model already means the setup
        $"You already have a model called {existingId}, set up for a different file",
        [
            new ChoiceOption(UseExistingModel, "Use the model I already have"),
            //add-as-a-file is second because it loses nothing, replace is last as the only option that throws work away
            .. canAdd
                ? new[] { new ChoiceOption(AddAsFile, $"Add it as another file of {existingId}") }
                : [],
            new ChoiceOption(ReplaceModel, "Replace it with this model"),
        ],
        BodyRows:
        [
            //say what replacing costs, the model's own settings and tuning are gone
            new SetupRow($"Replacing rewrites {existingId} for the file you just chose. Anything "
                + "you tuned in that model, its settings, its system-append, is gone."),
        ]), ModelCollisionKey);

    private SetupScreen AnswerModelCollision(string optionKey)
    {
        _awaiting = null;
        if (optionKey == UseExistingModel)
        {
            //keep the existing id and skip the scaffold, the chosen file simply isn't adopted
            _modelId = _clashingModelId;
            _narrate.Add(new SetupScreen.Info("model.reused",
                [new SetupRow($"{Glyphs.Ok} Keeping {_clashingModelId} as it is")]));
            return Done();
        }

        //the user says it's the same model, so join it, don't replace it
        if (optionKey == AddAsFile) return AddFileIntent(Selected!, _clashingModelId!);

        return OfferMove(Selected!, replace: true);
    }

    //skip the create screens, this model already has its projector and server. the id is passed so the file lands in that folder
    private SetupScreen AddFileIntent(FoundModel model, string existingId)
    {
        Writes = Writes with
        {
            CreateModel = new WriteSet.Model(model.Path, Port: DefaultPort,
                Context: probes.ContextFor(model.Path), Id: existingId, AddToModelId: existingId,
                Sha256: KnownHashFor(model)),
        };
        //save the join separately, the WriteSet is cleared before its question is asked
        _joining = (existingId, Gatto.Core.Acquire.QuantToken.Of(model.Path) ?? "");
        //the closing record names the joined model, or its /model and gatto audition lines print with no id
        _modelId = existingId;
        NeedsWritesApplied = true;
        return Emit(WritesPending, null);
    }

    //the joined file is inactive until yes, so ask either way. if the model is serving, say the running server keeps its file
    private SetupScreen QuantFromNow(string modelId, string quant)
    {
        //say what happened before asking about it (the file is already there, the question is about next)
        _narrate.Add(new SetupScreen.Info("model.quantadded",
            [new SetupRow($"{Glyphs.Ok} {quant} is added to {modelId}.")]));

        var serving = string.Equals(probes.LoadedModelId(), modelId, StringComparison.OrdinalIgnoreCase);
        return Emit(new SetupScreen.Choice(
            QuantFromNowKey,
            SwapConfirm.QuantQuestion(modelId, quant),
            [
                //each option names its own outcome, the reader shouldn't fetch the consequence from the body
                new ChoiceOption(Yes, $"Yes, {modelId} will use {quant}"),
                new ChoiceOption(No, $"No, keep {quant} on the list unused for now"),
            ],
            BodyRows: serving
                ? [new SetupRow($"{modelId} is serving right now. This changes the setting: the "
                    + "running server keeps the file it started with until it restarts.")]
                : [new SetupRow($"{quant} is on {modelId}'s list either way. This chooses which "
                    + "file it uses.")]),
            QuantFromNowKey);
    }

    //yes records an intent for the Done pause, no keeps the file and undoes nothing
    private SetupScreen AnswerQuantFromNow(string optionKey)
    {
        _awaiting = null;
        if (optionKey == Yes && _joining is { } j)
            Writes = Writes with { ActivateFile = (j.Id, j.Quant) };
        return Done();
    }

    //the hash the run holds for this file, matched by full path so it never lands on another file, and dropped when a stat shows the bytes changed since
    private string? KnownHashFor(FoundModel model)
    {
        if (_knownHash is not { } known || !string.Equals(System.IO.Path.GetFullPath(known.Path),
                System.IO.Path.GetFullPath(model.Path), StringComparison.OrdinalIgnoreCase))
            return null;
        var now = StatOf(model.Path);
        return now is null || known.Size is null || (now.Value.Size == known.Size && now.Value.Written == known.Written)
            ? known.Sha256 : null;
    }

    //what the run learned about a file's bytes and the stat it was taken at, and a failed stat only loses the guard since the writer stats the file again
    private void Know(FoundModel model, string? sha256)
    {
        if (sha256 is not { Length: > 0 } hash) return;
        var stat = StatOf(model.Path);
        _knownHash = (model.Path, hash, stat?.Size, stat?.Written);
    }

    private static (long Size, DateTime Written)? StatOf(string path)
    {
        try
        {
            var info = new System.IO.FileInfo(path);
            return info.Exists ? (info.Length, info.LastWriteTimeUtc) : null;
        }
        catch (Exception) { return null; }   //a path the stat cannot read keeps the hash without its guard
    }

    //the repo a model came from, only for the file the user picked. the id and the clash check both read this one answer
    private Gatto.Roles.Models.ModelSource? SourceOf(FoundModel model) =>
        Picked is { RowFile: { } file, RowQuant: { } quant } && Answers(model, quant) ? new Gatto.Roles.Models.ModelSource(file.RepoId, quant.FileName) : null;

    //the model intent in one place, replacing or not, so the collision branch can't drift on context, port or the pause

    //this records an intent and pauses for the writer. if a screen ever sits between an intent and its pause, add the back-walk test in the same commit
    private SetupScreen ScaffoldIntent(FoundModel model, bool replace)
    {
        //use the computed context, a ceiling this machine can't hold is a model that fails to load
        var context = probes.ContextFor(model.Path, Writes.Projector);
        Writes = Writes with
        {
            CreateModel = new WriteSet.Model(model.Path, Port: DefaultPort,
                Context: context, Replace: replace,
                Source: SourceOf(model),
                PinGpu: probes.ServeOnlyGpu(),
                GpuLayers: probes.SplitLayersFor(model.Path, context, Writes.Projector),
                Sha256: KnownHashFor(model)),
        };
        //pause for the writer before offering anything, no screen may audition a model that isn't on disk yet
        NeedsWritesApplied = true;
        return Emit(WritesPending, null);
    }
}
