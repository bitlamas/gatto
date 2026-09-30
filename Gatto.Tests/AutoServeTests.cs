using Gatto.Cli;

namespace Gatto.Tests;

//loop over every combination of the decision inputs, since a scripted run never spawning a server is a negative. a few examples prove it only for those.
public class AutoServeTests
{
    [Fact]
    public void A_NON_INTERACTIVE_LAUNCH_NEVER_ASKS_AND_NEVER_SERVES_whatever_else_is_true()
    {
        //a non-interactive run, such as -p or run_agent, must never start a process. vary every other input and hold only this one fixed
        foreach (var serves in new[] { true, false })
            foreach (var answered in new[] { true, false })
                foreach (var consent in new bool?[] { null, true, false })
                    foreach (var canAsk in new[] { true, false })
                        Assert.Equal(AutoServeAction.Nothing,
                            AutoServe.Decide(interactive: false, serves, answered, consent, canAsk));
    }

    [Fact]
    public void AN_ENDPOINT_GATTO_DOES_NOT_SERVE_IS_NEVER_ITS_BUSINESS()
    {
        //a connect endpoint is a server that belongs to someone else, so gatto never starts it and never offers to
        foreach (var answered in new[] { true, false })
            foreach (var consent in new bool?[] { null, true, false })
                foreach (var canAsk in new[] { true, false })
                    Assert.Equal(AutoServeAction.Nothing,
                        AutoServe.Decide(interactive: true, gattoServesThisEndpoint: false, answered, consent, canAsk));
    }

    [Fact]
    public void A_SERVER_THAT_ANSWERED_needs_no_decision_at_all()
    {
        foreach (var consent in new bool?[] { null, true, false })
            foreach (var canAsk in new[] { true, false })
                Assert.Equal(AutoServeAction.Nothing,
                    AutoServe.Decide(true, true, serverAnswered: true, consent, canAsk));
    }

    //an absent consent means yes, starting a local process is not a network call
    [Fact]
    public void NEVER_ASKED_plus_somebody_at_the_terminal_NOW_STARTS_IT()
    {
        Assert.Equal(AutoServeAction.Serve,
            AutoServe.Decide(true, true, serverAnswered: false, consent: null, canAsk: true));
    }

    //with no human at the terminal, an absent consent only hints, so no process starts unattended
    [Fact]
    public void NEVER_ASKED_with_NOBODY_AT_THE_TERMINAL_still_only_hints()
    {
        Assert.Equal(AutoServeAction.Hint,
            AutoServe.Decide(true, true, serverAnswered: false, consent: null, canAsk: false));
    }

    [Fact]
    public void YES_ON_THIS_PACK_serves_without_asking_again()
    {
        foreach (var canAsk in new[] { true, false })
            Assert.Equal(AutoServeAction.Serve,
                AutoServe.Decide(true, true, serverAnswered: false, consent: true, canAsk: canAsk));
    }

    [Fact]
    public void DECLINED_MEANS_DECLINED_but_the_hint_still_shows()
    {
        //a decline must still show the hint, silence after a decline looks like a broken gatto. the hint is not what the user declined
        foreach (var canAsk in new[] { true, false })
            Assert.Equal(AutoServeAction.Hint,
                AutoServe.Decide(true, true, serverAnswered: false, consent: false, canAsk: canAsk));
    }

    //only a human at a local endpoint gatto serves, with the server down and no veto, may serve. absent consent starts one only when someone can be asked.
    [Fact]
    public void ONLY_A_HUMAN_AT_AN_IDLE_LOCAL_SESSION_EVER_SPAWNS_ANYTHING()
    {
        var serving = new List<(bool Interactive, bool Serves, bool Answered, bool? Consent, bool CanAsk)>();
        foreach (var interactive in new[] { true, false })
            foreach (var serves in new[] { true, false })
                foreach (var answered in new[] { true, false })
                    foreach (var consent in new bool?[] { null, true, false })
                        foreach (var canAsk in new[] { true, false })
                            if (AutoServe.Decide(interactive, serves, answered, consent, canAsk) == AutoServeAction.Serve)
                                serving.Add((interactive, serves, answered, consent, canAsk));

        Assert.All(serving, row =>
        {
            Assert.True(row.Interactive);
            Assert.True(row.Serves);
            Assert.False(row.Answered);
            //absent consent starts a server, so this checks only that a veto never does
            Assert.NotEqual(false, row.Consent);
        });

        //assert per consent value rather than a total, a total cannot show which row changed. a standing yes serves with both canAsk values, since it needs no human
        Assert.Equal(2, serving.Count(r => r.Consent == true));
        //absent consent serves only with someone at the terminal, so a redirected stdin cannot start a process
        Assert.Equal([true], serving.Where(r => r.Consent is null).Select(r => r.CanAsk));
        //a veto never starts a server.
        Assert.DoesNotContain(serving, r => r.Consent == false);
    }

    [Fact]
    public void THE_MID_SESSION_ARMING_SITE_MAPS_ONTO_THIS_TABLE_WITHOUT_NEW_ROWS()
    {
        Assert.Equal(AutoServeAction.Serve,
            AutoServe.Decide(true, true, false, consent: true, canAsk: true));
        //absent consent also means yes at this site, one shared table keeps the two sites from disagreeing
        Assert.Equal(AutoServeAction.Serve,
            AutoServe.Decide(true, true, false, consent: null, canAsk: true));
        //a decline gives a hint at this site, so the model the user added stays selected and the user learns how to serve it
        Assert.Equal(AutoServeAction.Hint,
            AutoServe.Decide(true, true, false, consent: false, canAsk: true));
    }
}
