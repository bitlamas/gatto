namespace Gatto.Cli;

//the context budget and the line that says so, from one call so the sentence cannot drift from the value the session took
internal readonly record struct ContextResolution(int? Budget, string? Line);

//a connect endpoint's live n_ctx is the budget whenever the probe answers, the stored value is only a fallback
internal static class ConnectContext
{
    //a zero or negative report is not an answer, so a server saying 0 cannot become a zero-token budget
    public static ContextResolution Resolve(int? configured, int? liveNCtx)
    {
        var live = Positive(liveNCtx);
        var stored = Positive(configured);

        if (live is int n)
            return new(n, stored is int s
                ? s == n
                    ? null                                   //agreement says nothing
                    : $"server reports {n}. Using it (endpoint config says {s})"
                //no parenthetical here, it would name a config key the user never wrote
                : $"server reports {n}. Using it");

        return new(stored, stored is int fallback
            ? $"server didn't report a context. Using your configured {fallback}"
            //neither side answered. the context warning fires on this null budget and says it all, a second sentence would be the same fact twice
            : null);
    }

    private static int? Positive(int? value) => value is int n && n > 0 ? n : null;
}
