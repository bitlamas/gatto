using Gatto.Cli.Setup;
using Gatto.Repl;
using Gatto.Terminal;

namespace Gatto.Tests.Setup;

//a wizard surface must read keys through the session's prompter, a second reader on stdin would fight the input pump of a live session
public class PrompterWizardSurfaceTests
{
    //records every ask and replays the scripted answers.
    private sealed class FakePrompter(params string?[] answers) : IWizardPrompter
    {
        private int _i;
        public List<WizardAsk> Asked { get; } = [];

        public WizardAnswer? AskOne(WizardAsk ask)
        {
            Asked.Add(ask);
            if (_i >= answers.Length) return null;
            //the index advances on every call, null entries included. a scripted null means the user left, and a stuck index would spin the re-ask tests
            var a = answers[_i++];
            return a is null ? null : new WizardAnswer(a);
        }
    }

    private static WizardScreen.Choice Choice() => new(
        "k", "pick one",
        [new ChoiceOption("alpha", "The Alpha"), new ChoiceOption("beta", "The Beta")]);

    //the leave sentence must fit an in-session exit, and no blank row may reach this sink (a blank line renders as a bare gutter mark)
    [Fact]
    public void LEAVING_MODEL_ADD_USES_THE_IN_SESSION_SENTENCE_AND_COMMITS_NO_BARE_ROW()
    {
        var home = Directory.CreateTempSubdirectory("gatto-sb5-").FullName;
        try
        {
            var sink = new StringWriter();
            //with no scripted answers the prompter returns null at the first question, as the Esc key does
            var surface = new PrompterWizardSurface(new FakePrompter(), sink);

            SetupRunner.Run(new SetupFlow(new Gatto.Tests.Fakes.WizardProbes()), surface, out var added,
                home, modelSegmentOnly: true);

            var text = sink.ToString();
            Assert.Null(added);                                    //leaving with nothing written must report no model as added
            Assert.Contains(SetupFlow.NothingAddedText, text, StringComparison.Ordinal);
            //check the leave sentence by its words, the in-session run must never show them
            Assert.DoesNotContain("run gatto setup again", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Nothing has been written", text, StringComparison.Ordinal);

            //no committed row may be blank, a whitespace-only line renders as a bare ♯
            var blank = text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n')
                .Where(string.IsNullOrWhiteSpace).ToList();
            Assert.Empty(blank);
        }
        finally { try { Directory.Delete(home, true); } catch (Exception) { } }
    }

    //adding a model must not overwrite an existing default, the writer alone cannot see which field it receives
    [Fact]
    public void A_MODEL_ADDED_ON_A_CONFIGURED_MACHINE_DOES_NOT_REPOINT_THE_LAUNCH()
    {
        var home = Directory.CreateTempSubdirectory("gatto-default-kept-walk-").FullName;
        try
        {
            Gatto.Core.Home.GattoHome.EnsureInitialized(home);
            Gatto.Core.Home.GattoConfigWriter.SetDefaultModel(home, "qwen-before");

            var sink = new StringWriter();
            SetupRunner.Run(new SetupFlow(InSessionProbes(home, heldBy: "lfm2.5-1.2b-thinking")),
                new PrompterWizardSurface(new FakePrompter(FirstRowLabel, SetupFlow.AddUncheckedLabel, "next"), sink),
                out _, home, modelSegmentOnly: true);

            Assert.Equal("qwen-before", Gatto.Core.Home.GattoConfig.Load(home).DefaultModel);
        }
        finally { try { Directory.Delete(home, true); } catch (Exception) { } }
    }

    //a first default must still be written when none exists, only the overwrite was banned. this test would also pass on an always-write tree
    [Fact]
    public void A_MODEL_ADDED_WHERE_THERE_IS_NO_DEFAULT_STILL_BECOMES_ONE()
    {
        var home = Directory.CreateTempSubdirectory("gatto-first-default-walk-").FullName;
        try
        {
            Gatto.Core.Home.GattoHome.EnsureInitialized(home);
            Assert.Null(Gatto.Core.Home.GattoConfig.Load(home).DefaultModel);

            var sink = new StringWriter();
            SetupRunner.Run(new SetupFlow(InSessionProbes(home, heldBy: "lfm2.5-1.2b-thinking")),
                new PrompterWizardSurface(new FakePrompter(FirstRowLabel, SetupFlow.AddUncheckedLabel, "next"), sink),
                out var added, home, modelSegmentOnly: true);

            Assert.NotNull(added);
            Assert.Equal(added, Gatto.Core.Home.GattoConfig.Load(home).DefaultModel);
        }
        finally { try { Directory.Delete(home, true); } catch (Exception) { } }
    }

    //the in-session leaving copy must not say setup, and both _defaultLanded branches are tested because the unset one is rare
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OPEN3_THE_IN_SESSION_LEAVING_COPY_NEVER_SAYS_SETUP(bool defaultLanded)
    {
        var home = Directory.CreateTempSubdirectory("gatto-open3-").FullName;
        try
        {
            var probes = InSessionProbes(home);
            var prompter = defaultLanded
                //the scripted answers pick a row, run the check, and leave after the default is set
                ? new FakePrompter(FirstRowLabel, "Yes, check it")
                //leaving at the offer to run the check keeps the default unset, which is the state this test needs
                : new FakePrompter(FirstRowLabel);
            var sink = new StringWriter();

            SetupRunner.Run(new SetupFlow(probes), new PrompterWizardSurface(prompter, sink),
                out _, home, modelSegmentOnly: true);

            //one pattern must catch both setup and set up, searching for one spelling alone is blind to the other
            Assert.DoesNotMatch(SetUpVoice, sink.ToString());
        }
        finally { try { Directory.Delete(home, true); } catch (Exception) { } }
    }

    //the failed-check leaving line is a separate copy on another branch. it needs its own flow test, and the screen must not call the model fine
    [Fact]
    public void F6_THE_IN_SESSION_STRUGGLED_LINE_ALSO_DROPS_THE_SETUP_VOICE()
    {
        var home = Directory.CreateTempSubdirectory("gatto-f6-").FullName;
        try
        {
            var probes = InSessionProbes(home, new AuditionCheck(
                AuditionOutcome.Failed, new AuditionFacts("Q4_K_M", "greedy", "off", 42.5, 2, 5)));
            //the scripted answers pick a row, run the failing check, take the model anyway, and give consent
            var prompter = new FakePrompter(
                FirstRowLabel, "Yes, check it", "Use it anyway", "Yes, check weekly");
            var sink = new StringWriter();

            SetupRunner.Run(new SetupFlow(probes), new PrompterWizardSurface(prompter, sink),
                out var added, home, modelSegmentOnly: true);

            var text = sink.ToString();
            Assert.NotNull(added);
            Assert.DoesNotMatch(SetUpVoice, text);
            //assert the failure is stated and no claim of fineness is made, the phrase sits in the row the outcome branches into
            Assert.Contains("struggled", text, StringComparison.Ordinal);
            Assert.DoesNotContain("answering properly", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Congratulations", text, StringComparison.Ordinal);
        }
        finally { try { Directory.Delete(home, true); } catch (Exception) { } }
    }

    //builds the fixture for a full in-session /model add run, no recorded update consent so the consent question is really asked
    private static Gatto.Tests.Fakes.WizardProbes InSessionProbes(string home, AuditionCheck? check = null,
        string? existingModel = null, Gatto.Core.Acquire.ProveOutcome? prove = null,
        string? heldBy = null)
    {
        //write a real synthetic GGUF header, the flow validates the magic and a text stub aborts the run
        var gguf = Path.Combine(home, FirstRowLabel);
        File.WriteAllBytes(gguf, GgufTestBytes.SyntheticHeader(kv =>
        {
            kv.Str("general.architecture", "qwen3");
            kv.U32("qwen3.context_length", 4096);
        }));
        return new Gatto.Tests.Fakes.WizardProbes
        {
            Llama = @"C:\llama\llama-server.exe",
            Found = [new Gatto.Core.Acquire.FoundModel(gguf, 4_000_000_000, null)],
            Audition = check ?? new AuditionCheck(
                AuditionOutcome.Passed,
                new AuditionFacts("Q4_K_M", "greedy", "off", 42.5, 5, 5)),
            UpdateAnswered = null,
            ExistingModel = existingModel,
            HeldBy = heldBy,
            Prove = prove ?? new Gatto.Core.Acquire.ProveOutcome(true, "hi", TimeSpan.FromSeconds(2)),
        };
    }

    //the discovery screen labels a row with its file name, and FakePrompter answers by label
    private const string FirstRowLabel = "tiny-Q4_K_M.gguf";

    //one pattern must match the setup voice in every spelling, with a bounded gap so it cannot match across a sentence
    private static readonly System.Text.RegularExpressions.Regex SetUpVoice =
        new(@"\bsetup\b|\bset\b(\s+\w+){0,3}\s+up\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    //show the pattern a known match before believing any absence result from it, a silent pattern would pass every guard
    [Theory]
    [InlineData("setup")]
    [InlineData("Run gatto setup again")]
    [InlineData("gatto is now set up!")]
    [InlineData("No, set the model up anyway")]   //a known match, the exact string that escaped the narrower pattern
    [InlineData("we can set it up for you")]
    public void THE_SETUP_VOICE_PATTERN_CAN_SEE_ITS_OWN_SUBJECT(string phrase) =>
        Assert.Matches(SetUpVoice, phrase);

    //these in-session sentences must stay unmatched, a pattern matching everything would pass every absence check
    [Theory]
    [InlineData("No, add it anyway")]
    [InlineData("Already added as gemma-4-26b-a4b, using that model")]
    [InlineData("The model is saved, but its server didn't start")]
    [InlineData("gatto audition runs the check whenever you want it.")]
    public void THE_SETUP_VOICE_PATTERN_LEAVES_THE_IN_SESSION_VOICE_ALONE(string phrase) =>
        Assert.DoesNotMatch(SetUpVoice, phrase);

    //only a run taken to the end proves the screens past the writes render in-session. it keeps shell phrasing and blank rows off them
    [Fact]
    public void F1_THE_FULL_IN_SESSION_WALK_NARRATES_AND_LANDS_WITHOUT_A_SHELL_VOICE()
    {
        var home = Directory.CreateTempSubdirectory("gatto-f1-").FullName;
        try
        {
            var sink = new StringWriter();
            //a leftover answer from a removed step shifts every later answer, the script must match the screens as they stand
            var prompter = new FakePrompter(FirstRowLabel, "Yes, check it", "Yes, check weekly");
            var surface = new PrompterWizardSurface(prompter, sink);

            SetupRunner.Run(new SetupFlow(InSessionProbes(home)), surface, out var added, home,
                modelSegmentOnly: true);

            var text = sink.ToString();

            //assert a model was added first, the guards below only mean something if the run reached the end
            Assert.True(added is not null, "the walk did not complete; transcript >>> " + text);

            //choice labels go to the prompter, so gather them from what it was asked (a check over the sink passes on an unfixed tree)
            var offered = string.Join(" | ", prompter.Asked
                .SelectMany(a => a.Options.Select(o => o.Label)).Concat([text]));
            foreach (var shellVoice in new[] { "Start gatto", "Starting gatto", "tell me the command" })
                Assert.DoesNotContain(shellVoice, offered, StringComparison.Ordinal);

            //a literal list missed the spaced spelling set up, so match the voice with the regex
            Assert.DoesNotMatch(SetUpVoice, offered);

            //no row may be whitespace-only, since a blank line shows as a bare gutter mark
            var blank = text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n')
                .Where(string.IsNullOrWhiteSpace).ToList();
            Assert.Empty(blank);

            //the closing rows narrate what the run did, so assert the fact rather than one fixed sentence
            Assert.Contains("ran commands and edited files", text, StringComparison.Ordinal);
        }
        finally { try { Directory.Delete(home, true); } catch (Exception) { } }
    }

    //every channel the run shows the user: each question, its body rows, its options and the narrated text. a helper missing one leaves every guard built on it blind
    private static string EverythingOffered(FakePrompter prompter, StringWriter sink) =>
        string.Join(" | ", prompter.Asked
            .SelectMany(a => a.Options.Select(o => o.Label)
                .Concat((a.BodyRows ?? []).Select(r => r.Text))
                .Append(a.Question ?? ""))
            .Concat([sink.ToString()]));

    //only a run that takes the reuse path reaches this sentence, so a test that exits early guards nothing
    [Fact]
    public void F1_FIFTH_FACE_THE_REUSE_NARRATION_DOES_NOT_SPEAK_SETUPS_VOICE()
    {
        var home = Directory.CreateTempSubdirectory("gatto-f1-reuse-").FullName;
        try
        {
            var sink = new StringWriter();
            var prompter = new FakePrompter(FirstRowLabel);
            var surface = new PrompterWizardSurface(prompter, sink);

            SetupRunner.Run(new SetupFlow(InSessionProbes(home, existingModel: "qwen-already")),
                surface, out _, home, modelSegmentOnly: true);

            var offered = EverythingOffered(prompter, sink);
            //prove the row renders first, or the absence check below passes about a screen nobody reached
            Assert.Contains("using that model", offered, StringComparison.Ordinal);
            Assert.DoesNotMatch(SetUpVoice, offered);
        }
        finally { try { Directory.Delete(home, true); } catch (Exception) { } }
    }

    //the reuse path skips the scaffold, so the added id must come from the existing model (the swap offer is built from it)
    [Fact]
    public void A_REUSED_MODEL_IS_STILL_ARMED_so_the_promised_swap_can_be_offered()
    {
        var home = Directory.CreateTempSubdirectory("gatto-reuse-").FullName;
        try
        {
            var prompter = new FakePrompter(FirstRowLabel);
            var surface = new PrompterWizardSurface(prompter, new StringWriter());

            SetupRunner.Run(new SetupFlow(InSessionProbes(home, existingModel: "gemma-already")),
                surface, out var added, home, modelSegmentOnly: true);

            Assert.Equal("gemma-already", added);
        }
        finally { try { Directory.Delete(home, true); } catch (Exception) { } }
    }

    //the save is stated exactly once, so count how often it is said rather than pinning the wording
    [Fact]
    public void THE_IN_SESSION_CLOSE_STATES_THE_SAVE_ONCE()
    {
        var home = Directory.CreateTempSubdirectory("gatto-close-").FullName;
        try
        {
            var sink = new StringWriter();
            var prompter = new FakePrompter(FirstRowLabel, "Yes, check it", "Use it anyway", "Yes, check weekly");
            var surface = new PrompterWizardSurface(prompter, sink);

            SetupRunner.Run(
                new SetupFlow(InSessionProbes(home, check: new AuditionCheck(
                    AuditionOutcome.Failed,
                    new AuditionFacts("BF16", "defaults", "template default", 62.0, 2, 4)))),
                surface, out _, home, modelSegmentOnly: true);

            var text = sink.ToString();

            //prove the failed-check close is on screen first, so the save count below is about that screen
            Assert.Contains("struggled", text, StringComparison.Ordinal);

            //the save can be a config row or a prose sentence, so the count pattern must accept both
            var saves = System.Text.RegularExpressions.Regex.Matches(
                text, @"is saved|has been saved|^\s*config\s",
                System.Text.RegularExpressions.RegexOptions.Multiline).Count;
            Assert.True(saves == 1, $"the save is stated {saves} times; transcript >>> {text}");

            //the close must not name the part that struggled or defend gatto from the failure
            Assert.DoesNotContain("the part that struggled", text, StringComparison.Ordinal);
            Assert.DoesNotContain("not gatto", text, StringComparison.Ordinal);
        }
        finally { try { Directory.Delete(home, true); } catch (Exception) { } }
    }

    //in session the held server becomes an ask that states its cost. the step is neither dropped nor offered as a check that can't run
    [Fact]
    public void X15_THE_HELD_SERVER_IS_AN_ASK_IN_SESSION_AND_THE_COST_IS_STATED()
    {
        var home = Directory.CreateTempSubdirectory("gatto-x15-").FullName;
        try
        {
            var sink = new StringWriter();
            var prompter = new FakePrompter(FirstRowLabel, "next");   //one answer spare, a script that runs out reads as leaving
            var surface = new PrompterWizardSurface(prompter, sink);

            SetupRunner.Run(new SetupFlow(InSessionProbes(home, heldBy: "lfm2.5-1.2b-thinking")),
                surface, out _, home, modelSegmentOnly: true);

            var offered = EverythingOffered(prompter, sink);

            //in session the blind check offer and the failure it caused must both stay off the screen
            Assert.DoesNotContain("Yes, check it", offered, StringComparison.Ordinal);
            Assert.DoesNotContain("Couldn't run the check", offered, StringComparison.Ordinal);
            //the ask names the model whose server stops, so the step never goes silent
            Assert.Contains("Check it now", offered, StringComparison.Ordinal);
            Assert.Contains("lfm2.5-1.2b-thinking's server stops", offered, StringComparison.Ordinal);
            //the stopped server is the whole cost, so say nothing about clients re-reading it on the next message
            Assert.DoesNotContain("re-reads", offered, StringComparison.Ordinal);
            Assert.DoesNotContain("your next message", offered, StringComparison.OrdinalIgnoreCase);
        }
        finally { try { Directory.Delete(home, true); } catch (Exception) { } }
    }

    //the text input row must come before the back row, so assert the index the widget is handed (it owns the row order)
    [Theory]
    [InlineData(false, 0)]   //without an offer the input takes row 0, since back can only follow it
    [InlineData(true, 1)]    //an offer takes row 0, so the input follows at row 1
    public void AN_IN_SESSION_ASK_NEVER_PUTS_BACK_ABOVE_THE_BOX(bool withOffer, int expected)
    {
        var prompter = new FakePrompter("typed");
        var surface = new PrompterWizardSurface(prompter, new StringWriter());

        surface.Ask(new WizardScreen.Ask("k", "Where is that llama-server.exe?", _ => null,
            Offer: withOffer ? new AskOffer("use the one I have", @"C:\llama") : null)
        { AllowBack = true });

        var ask = Assert.Single(prompter.Asked);
        Assert.Equal(expected, ask.FreeTextIndex);
    }

    //assert the saved model and why the running server was left alone, with no invented fault, no gatto doctor and no dead end
    [Fact]
    public void X5_A_SERVER_HELD_BY_ANOTHER_MODEL_IS_A_STATE_NOT_A_FAILURE()
    {
        var home = Directory.CreateTempSubdirectory("gatto-x5-").FullName;
        try
        {
            var sink = new StringWriter();
            //the script answers by option label, so a reworded option fails this test loudly instead of moving past it
            var prompter = new FakePrompter(FirstRowLabel, "Skip the tasks, just make sure it answers");
            var surface = new PrompterWizardSurface(prompter, sink);

            SetupRunner.Run(
                new SetupFlow(InSessionProbes(home,
                    prove: new Gatto.Core.Acquire.ProveOutcome(false, "held", TimeSpan.Zero,
                        ServedByAnother: "lfm2.5-1.2b-thinking"))),
                surface, out _, home, modelSegmentOnly: true);

            var offered = EverythingOffered(prompter, sink);

            //the screen says the model is saved and names the server that is still running
            Assert.Contains("is saved", offered, StringComparison.Ordinal);
            Assert.Contains("lfm2.5-1.2b-thinking is still the running server", offered, StringComparison.Ordinal);
            //the next step must name gatto serve start, with no promise of a switch question since that panel is gone
            Assert.Contains("gatto serve start", offered, StringComparison.Ordinal);
            Assert.DoesNotContain("whether to switch", offered, StringComparison.Ordinal);

            //none of the failure screen may appear: no invented fault, no doctor pointer, no dead end.
            Assert.DoesNotContain("did not start", offered, StringComparison.Ordinal);
            Assert.DoesNotContain("gatto doctor", offered, StringComparison.Ordinal);
            Assert.DoesNotContain("Finish anyway", offered, StringComparison.Ordinal);

            //a step marker must never announce a question the run then fails to ask.
            Assert.DoesNotContain("First question", offered, StringComparison.Ordinal);
        }
        finally { try { Directory.Delete(home, true); } catch (Exception) { } }
    }

    //the failure screen needs a prove step that fails, since a fixture that always passes never renders its wording
    [Fact]
    public void F1_SIXTH_FACE_THE_PROVE_FAILURE_HEADLINE_DOES_NOT_SPEAK_SETUPS_VOICE()
    {
        var home = Directory.CreateTempSubdirectory("gatto-f1-prove-").FullName;
        try
        {
            var sink = new StringWriter();
            var prompter = new FakePrompter(FirstRowLabel, "Skip the tasks, just make sure it answers");
            var surface = new PrompterWizardSurface(prompter, sink);

            SetupRunner.Run(
                new SetupFlow(InSessionProbes(home,
                    prove: new Gatto.Core.Acquire.ProveOutcome(false, "boom", TimeSpan.Zero, StartFailed: true))),
                surface, out _, home, modelSegmentOnly: true);

            var offered = EverythingOffered(prompter, sink);
            //prove the failure screen is on screen first, otherwise the voice guard below holds for the wrong reason
            Assert.Contains("didn't start", offered, StringComparison.Ordinal);
            Assert.DoesNotMatch(SetUpVoice, offered);
        }
        finally { try { Directory.Delete(home, true); } catch (Exception) { } }
    }

    [Fact]
    public void A_CHOICE_goes_through_the_SESSIONS_PROMPTER_and_maps_the_label_back_to_a_key()
    {
        //the flow speaks keys and the prompter speaks labels, so the mapping lives here rather than in the flow
        var prompter = new FakePrompter("The Beta");
        var surface = new PrompterWizardSurface(prompter, new StringWriter());

        Assert.Equal("beta", surface.Choose(Choice()));
        Assert.Single(prompter.Asked);
        Assert.Equal("pick one", prompter.Asked[0].Question);
    }

    [Fact]
    public void A_CHOICE_THAT_ALLOWS_BACK_OFFERS_THE_ROW_AND_MAPS_IT_TO_THE_BACK_KEY()
    {
        var prompter = new FakePrompter(SetupFace.BackOptionKey);
        var surface = new PrompterWizardSurface(prompter, new StringWriter());

        var answer = surface.Choose(Choice() with { AllowBack = true });

        Assert.Equal(SetupFlow.BackKey, answer);
        //the back row must be offered last, so no real option loses its number.
        Assert.Equal(SetupFace.BackLabelOf(Gatto.Terminal.GlyphSet.Unicode), prompter.Asked[0].Options[^1].Label);
    }

    [Fact]
    public void THE_BACK_ROW_IS_AN_ESCAPE_ROW_and_counts_toward_the_screens_own_arithmetic()
    {
        //back is one extra row handed over last, so it takes part in the nine the widget numbers
        var many = Enumerable.Range(1, 12).Select(i => new ChoiceOption($"k{i}", $"Option {i}")).ToList();
        var prompter = new FakePrompter("Option 1");
        var surface = new PrompterWizardSurface(prompter, new StringWriter());

        surface.Choose(new WizardScreen.Choice("k", "pick one", many) { AllowBack = true });

        var offered = prompter.Asked[0].Options;
        Assert.Equal(many.Count + 1, offered.Count);
        Assert.Equal(SetupFace.BackLabelOf(Gatto.Terminal.GlyphSet.Unicode), offered[^1].Label);
        Assert.Single(offered, o => o.Label == SetupFace.BackLabelOf(Gatto.Terminal.GlyphSet.Unicode));   //one back row in the whole list, even when the options fill several pages
    }

    [Fact]
    public void A_CHOICE_THAT_DOES_NOT_ALLOW_BACK_OFFERS_NO_SUCH_ROW()
    {
        var prompter = new FakePrompter("The Beta");
        var surface = new PrompterWizardSurface(prompter, new StringWriter());

        surface.Choose(Choice());

        Assert.DoesNotContain(prompter.Asked[0].Options, o => o.Label == SetupFace.BackLabelOf(Gatto.Terminal.GlyphSet.Unicode));
    }

    [Fact]
    public void AN_ASK_MAPS_BACK_WITHOUT_CONSULTING_ITS_VALIDATOR()
    {
        //a validator expects a path, a number or a name, so running it on back would re-ask the screen forever. assert the validator was never called
        var validated = new List<string>();
        var prompter = new FakePrompter(SetupFace.BackOptionKey);
        var surface = new PrompterWizardSurface(prompter, new StringWriter());
        var ask = new WizardScreen.Ask("k", "how many?",
            Validate: t => { validated.Add(t); return "always complains"; },
            Offer: new AskOffer("Use 32768", "32768")) with { AllowBack = true };

        var answer = surface.Ask(ask);

        Assert.Equal(SetupFlow.BackKey, answer);
        Assert.Empty(validated);
        //the offer stays row 0 and back follows it, so the back row sits in the same place on every screen
        Assert.Equal(["Use 32768", SetupFace.BackLabelOf(Gatto.Terminal.GlyphSet.Unicode)],
            prompter.Asked[0].Options.Select(o => o.Label));
    }

    [Fact]
    public void BACKING_OUT_reads_as_null_rather_than_as_a_choice()
    {
        //leaving returns null, since a fallback to the first option would read a departure as a choice
        var surface = new PrompterWizardSurface(new FakePrompter((string?)null), new StringWriter());
        Assert.Null(surface.Choose(Choice()));
    }

    [Fact]
    public void FREE_TEXT_that_matches_no_option_RE_ASKS_rather_than_abandoning_the_wizard()
    {
        //text matching no option re-asks, or one mistyped word discards the whole run
        var prompter = new FakePrompter("yes", "The Beta");
        var log = new StringWriter();

        Assert.Equal("beta", new PrompterWizardSurface(prompter, log).Choose(Choice()));
        Assert.Equal(2, prompter.Asked.Count);
        Assert.Contains("pick one of the options", log.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ENGINE_TEXT_IN_ROWS_IS_SANITIZED_on_this_binding_too()
    {
        //rows hold engine text such as a disk file name, so this binding must strip escape sequences as the console one does
        const string esc = "\u001b";
        var log = new StringWriter();

        new PrompterWizardSurface(new FakePrompter("x"), log)
            .Show(new WizardScreen.Info("k", [$"using evil{esc}[2Jname.gguf"]));

        Assert.DoesNotContain(esc, log.ToString(), StringComparison.Ordinal);
        Assert.Contains("evil[2Jname.gguf", log.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AN_ASK_RE_ASKS_until_the_screens_own_validation_passes()
    {
        //the validation rule lives on the screen, so the prompt and the check cannot drift on this surface
        var prompter = new FakePrompter("nope", "4096");
        var log = new StringWriter();
        var surface = new PrompterWizardSurface(prompter, log);

        var answer = surface.Ask(new WizardScreen.Ask("k", "how many?",
            Validate: t => int.TryParse(t, out _) ? null : "that needs to be a number"));

        Assert.Equal("4096", answer);
        Assert.Equal(2, prompter.Asked.Count);
        Assert.Contains("that needs to be a number", log.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AN_ASK_offers_NO_OPTIONS_because_the_free_text_row_IS_the_answer()
    {
        var prompter = new FakePrompter("x");
        new PrompterWizardSurface(prompter, new StringWriter())
            .Ask(new WizardScreen.Ask("k", "type it", Validate: _ => null));

        Assert.Empty(prompter.Asked[0].Options);
        Assert.NotNull(prompter.Asked[0].FreeTextLabel);
    }

    [Fact]
    public void This_surface_NEVER_CONSTRUCTS_A_KEY_SOURCE_which_is_the_whole_point()
    {
        //no constructor parameter may read keys, since the session's input pump already owns stdin
        var ctor = Assert.Single(typeof(PrompterWizardSurface).GetConstructors());
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        //the exact parameter list is pinned, so a new parameter must be an output or waiting seam. the cancellation seam must not throw and exit the process
        Assert.Equal(
            [typeof(IWizardPrompter), typeof(TextWriter), typeof(Theme), typeof(Action<TimeSpan>),
                typeof(Gatto.Terminal.GlyphSet)],
            paramTypes);

        //the loop states the rule, so a parameter that reads keys fails even before the exact list above is updated
        foreach (var t in paramTypes)
            Assert.False(typeof(IKeySource).IsAssignableFrom(t) || t == typeof(TextReader)
                || t.Name.Contains("Console", StringComparison.Ordinal),
                $"PrompterWizardSurface must not be constructed with anything that can read keys: {t.Name}");
    }
    //a run meets the held server twice, so only one of the two screens may explain it
    [Fact]
    public void THE_HELD_SERVER_IS_EXPLAINED_ONCE_NOT_TWICE()
    {
        var home = Directory.CreateTempSubdirectory("gatto-held-once-").FullName;
        try
        {
            var sink = new StringWriter();
            //the script must answer the ask, since a script that runs out reads as the user leaving
            var prompter = new FakePrompter(
                FirstRowLabel, SetupFlow.AddUncheckedLabel, "next");

            SetupRunner.Run(new SetupFlow(InSessionProbes(home, heldBy: "lfm2.5-1.2b-thinking")),
                new PrompterWizardSurface(prompter, sink), out _, home, modelSegmentOnly: true);

            var narrated = sink.ToString();
            //the completion keeps its own news, the running server and the next step.
            Assert.Contains("is still the running server", narrated, StringComparison.Ordinal);
            Assert.Contains("gatto serve start", narrated, StringComparison.Ordinal);
            //the completion says nothing about the check, since the ask already covers the held server
            Assert.DoesNotContain("gatto audition", narrated, StringComparison.Ordinal);

            //the ask states the cost of stopping the server, in its own body row rather than the narration
            Assert.Contains(prompter.Asked, a => (a.BodyRows ?? [])
                .Any(r => r.Text.Contains("server stops", StringComparison.Ordinal)));
        }
        finally { try { Directory.Delete(home, true); } catch (Exception) { } }
    }

    //a server taken after the offer renders no held screen, so the completion is the only text and must state the whole sentence
    [Fact]
    public void A_SERVER_TAKEN_AFTER_THE_OFFER_STILL_GETS_THE_WHOLE_SENTENCE()
    {
        var home = Directory.CreateTempSubdirectory("gatto-held-late-").FullName;
        try
        {
            var sink = new StringWriter();
            var prompter = new FakePrompter(FirstRowLabel, "Yes, check it", "next");
            var probes = InSessionProbes(home, prove: new Gatto.Core.Acquire.ProveOutcome(
                false, "lfm2.5-1.2b-thinking is the running server", TimeSpan.Zero,
                ServedByAnother: "lfm2.5-1.2b-thinking"));

            SetupRunner.Run(new SetupFlow(probes), new PrompterWizardSurface(prompter, sink),
                out _, home, modelSegmentOnly: true);

            var narrated = sink.ToString();
            Assert.Contains("Once lfm2.5-1.2b-thinking is no longer running", narrated,
                StringComparison.Ordinal);
            Assert.Contains("in a terminal to run the check", narrated, StringComparison.Ordinal);
            Assert.DoesNotContain("whenever you want", narrated, StringComparison.Ordinal);
        }
        finally { try { Directory.Delete(home, true); } catch (Exception) { } }
    }
    //the other guards here are all negative, so this phrase proves the collector can see a body row
    [Fact]
    public void THE_WALKS_ORACLE_READS_BODY_ROWS()
    {
        var home = Directory.CreateTempSubdirectory("gatto-oracle-").FullName;
        try
        {
            var sink = new StringWriter();
            var prompter = new FakePrompter(FirstRowLabel, "next");

            SetupRunner.Run(new SetupFlow(InSessionProbes(home, heldBy: "lfm2.5-1.2b-thinking")),
                new PrompterWizardSurface(prompter, sink), out _, home, modelSegmentOnly: true);

            var offered = EverythingOffered(prompter, sink);

            //the phrase lives in a body row alone, so nothing else on the screen may contain it
            Assert.Contains("starts in its place", offered, StringComparison.Ordinal);
            //the phrase appears on no label, question or narrated row, so only a collector that reads body rows finds it
            Assert.DoesNotContain("starts in its place", sink.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(prompter.Asked.SelectMany(a => a.Options.Select(o => o.Label))
                .Append(""), l => l.Contains("starts in its place", StringComparison.Ordinal));
        }
        finally { try { Directory.Delete(home, true); } catch (Exception) { } }
    }
    //compare the leave line against the row stored in the golden render, since a literal on both sides is a file agreeing with itself
    [Fact]
    public void NothingAddedTextIsWhatItsFrameDraws()
    {
        var golden = File.ReadAllLines(Path.Combine(
            Gatto.Tests.Census.SourceTree.RepoRoot(),
            "Gatto.Tests", "Setup", "Tui", "Goldens", "s10-after-leave-100.txt"));

        var drawn = golden.SingleOrDefault(l =>
            l.Contains("Nothing added", StringComparison.Ordinal));
        Assert.NotNull(drawn);

        Assert.Equal(SetupFlow.NothingAddedText, drawn!.TrimStart('\u266f', ' ').TrimEnd());
    }
}
