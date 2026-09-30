namespace Gatto.Cli;

//fires when the resolved budget is null, the same condition that gates elision, the compact offer and auto-compaction
internal static class ContextWarning
{
    public static string? Compose(int? resolvedBudget) =>
        resolvedBudget is int and > 0
            ? null
            : "context window unknown: elision, the compact offer, and auto-compaction are off. " +
              "Fix: set \"context\" on the endpoint (endpoints.<name>.context in gatto.json) " +
              "or use a model with a context value.";
}
