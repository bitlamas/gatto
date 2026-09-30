using Gatto.Core.Client;

namespace Gatto.Repl.Term;

//the context figures for the status line, accessed in sequence with no locking
public sealed class CtxState
{
    private int? _lastTotal;
    private int? _estimateTokens;
    private int? _estimateBudget;
    private int? _usedTokens;

    public int? ContextWindow { get; set; }

    public void RecordUsage(Usage u) => _lastTotal = u.PromptTokens + u.CompletionTokens;

    //called after each turn with the chars/4 estimate and the resolved budget, which outranks the probe-based percent once it is above 0
    public void RecordEstimate(int estimateTokens, int? budgetTokens)
    {
        _estimateTokens = estimateTokens;
        _estimateBudget = budgetTokens;
    }

    //the growth-inclusive figure the auto-compact trigger acts on, so a fat tool result shows at once. pass null after a rotation to clear a stale percent
    public void RecordUsedTokens(int? usedTokens) => _usedTokens = usedTokens;

    //the count behind Percent, from the same ladder so the two can't disagree, and the footer shows it beside the percent when there's room
    public int? Tokens =>
        _usedTokens is int u && (_estimateBudget is > 0 || ContextWindow is > 0)
            ? u
            : _estimateBudget is > 0
                ? _estimateTokens ?? 0
                : ContextWindow is > 0 && _lastTotal is int t ? t : null;

    //the denominator is the budget when set, else the context window, and with neither the figure falls back rather than reporting 100%
    public int? Percent =>
        _usedTokens is int lp && (_estimateBudget is > 0 || ContextWindow is > 0)
            ? (int)Math.Round(100.0 * lp / (_estimateBudget is int eb and > 0 ? eb : (int)ContextWindow!))
            : _estimateBudget is int b and > 0
                ? (int)Math.Round(100.0 * (_estimateTokens ?? 0) / b)
                : ContextWindow is int w and > 0 && _lastTotal is int t
                    ? (int)Math.Round(100.0 * t / w) : null;
}
