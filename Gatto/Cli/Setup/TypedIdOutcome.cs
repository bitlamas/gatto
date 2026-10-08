using Gatto.Core.Acquire;

namespace Gatto.Cli.Setup;

//what came of evaluating a typed repo id, one record per sentence the screen may say
internal abstract record TypedIdOutcome
{
    private TypedIdOutcome() { }

    //the id resolved and the arithmetic priced it
    internal sealed record Ok(ModelRow Row) : TypedIdOutcome;

    //refused before the wire, since the text is not org/name, and the copy names the shape
    internal sealed record Malformed : TypedIdOutcome;

    //the repo answered and none of its quants fit this machine, said plainly instead of calling the Hub unreachable
    internal sealed record NoUsableQuant : TypedIdOutcome;

    //the repo answered and publishes no weights, which is not the user's fault and not the repo's
    internal sealed record NoWeights : TypedIdOutcome;

    //the lookup's answer as an outcome, kept here so a pure mapping can be tested. a row with a file wins, and the flag tells the two refusals apart
    internal static TypedIdOutcome For((ModelRow? Row, bool NoWeights) lookup) => lookup switch
    {
        { Row: { RowFile: not null } row } => new Ok(row),
        { NoWeights: true } => new NoWeights(),
        _ => new NoUsableQuant(),
    };

    //the Hub could not answer for it, and a private repo and a missing one are the same 401, so one sentence covers both
    internal sealed record Unreachable(bool Gated) : TypedIdOutcome;
}
