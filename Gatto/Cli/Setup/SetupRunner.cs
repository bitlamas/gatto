namespace Gatto.Cli.Setup;

//run a SetupFlow in front of a SetupFace until it ends. the flow decides what to ask and the face how it looks, and nothing here may judge
internal static class SetupRunner
{
    //the /model add entry, which starts the same flow at the model segment rather than at the fork
    public static int Run(SetupFlow flow, IWizardSurface face, string? homePath = null,
        bool modelSegmentOnly = false)
        => Run(flow, face, out _, homePath, modelSegmentOnly);

    //the model this run armed, or null. the id comes from Apply's own out-parameter rather than a config re-read
    public static int Run(SetupFlow flow, IWizardSurface face, out string? addedModelId,
        string? homePath = null, bool modelSegmentOnly = false)
    {
        addedModelId = null;

        //the runner is the seam, since the flow spends the budget and the face knows the screen. set before the first screen, so the first search is already sized
        flow.RowBudget = face.RowBudget;

        //the same seam for the glyph vocabulary, since the flow writes copy that must match the face rather than reading the environment itself
        flow.Glyphs = face.Glyphs;

        //the same seam for the source switch and the choice rows, facts about the face's keyboard. a literal here once passed the whole suite, so tie it to the face
        flow.CanSwitchSource = face.CanSwitchSource;
        flow.FaceShowsChoiceBodyRows = face.ShowsChoiceBodyRows;
        flow.LoadsShelfInBackground = face.DrawsLoadingShelf;
        //both roads, gatto model and gatto setup, open the model step on the start-up screen
        flow.OpensOnLoadingScreen = face.DrawsLoadingShelf;

        var screen = modelSegmentOnly ? flow.StartAtModelSegment() : flow.Start();

        while (true)
        {
            //the writes happen here, mid-run, as soon as the flow says it has what it needs, since everything after reads them
            if (flow.NeedsWritesApplied)
            {
                if (homePath is not { } h) return 0;   //no home to write into (tests drive the flow directly)

                //the home is created at the first real write, so leaving the wizard before one leaves no home behind. it is idempotent, so the later apply sites inherit it
                Gatto.Core.Home.GattoHome.EnsureInitialized(h);

                if (WriteSetApply.Apply(h, flow.Writes, out var written, out var moveNote) is { } problem)
                {
                    face.Show(new WizardScreen.Info("write.failed", [problem]));
                    return 1;
                }

                //show the move note. a move that did not happen must not leave the user believing their weights left Downloads, and it is a line rather than an exit
                if (moveNote is { Length: > 0 } note)
                    face.Show(new WizardScreen.Info("move.incomplete", [note]));

                //written is null on the connect path, which is what the flow routes on. an empty string would make the two runs indistinguishable
                screen = flow.ResumeAfterWrites(written);

                //one owner for the armed id, read after the resume so the flow holds every path. only a non-null overwrites, so a later write cannot erase an earlier one
                if (flow.ArmedModelId is { Length: > 0 } armed) addedModelId = armed;
                continue;
            }

            //step markers reach the face here, drained before each screen so each renders where the run reached it. an Info the flow only stores is never shown
            foreach (var note in flow.TakeNarration()) face.Show(note);

            switch (screen)
            {
                case WizardScreen.Choice c:
                    //the watch predicate, its tick and the check's moments are wired here, from one hand and one clock. null on a watch that is not a download
                    if (face.Choose(c, c.Watching ? flow.PollWatch : null,
                            c.Watching ? flow.PollTick : null,
                            c.Watching ? flow.PollCheck : null,
                            c.Watching ? flow.PollShelfLoad : null) is not { } key)
                        return Left(face, flow);
                    screen = flow.Answer(key);
                    break;

                case WizardScreen.Info i:
                    face.Show(i);
                    //an Info that nothing follows ends the run, since the flow only returns one as passing narration
                    return 0;

                case WizardScreen.Ask a:
                    if (face.Ask(a) is not { } typed)
                    {
                        //the Esc key on a typed screen goes back when AllowBack is set, which is what the footer already promises
                        if (!a.AllowBack) return Left(face, flow);
                        screen = flow.Answer(SetupFlow.BackKey);
                        break;
                    }
                    screen = flow.Answer(typed);
                    break;

                case WizardScreen.Progress:
                    return 0;   //a Progress screen belongs to the tick line, so this case ends the run rather than drawing it

                case WizardScreen.Terminal t:
                    //both paths apply above, since prove-it reads the config from disk and must read one this run wrote

                    //default_model is written by the apply that creates the model, so a run that leaves at the check still points at something

                    face.End(t);
                    return t.Success ? 0 : 1;

                default:
                    throw new InvalidOperationException("unreachable, WizardScreen is closed");
            }
        }
    }


    //backing out with Esc, with the words composed by the flow. only the flow knows whether this run's writes already happened, so a fixed sentence could be false

    //one home for the leave screen, reached by the cancel path. it is a Terminal rather than an Info, since End is what composes the closing line
    private static int Left(IWizardSurface face, SetupFlow flow)
    {
        //the flow is told before its rows and sentence are read, since Esc never reaches the flow
        flow.MarkLeaving();
        face.End(new WizardScreen.Terminal("left", flow.LeavingRows, flow.LeaveStep, Success: true));
        return 0;
    }
}
