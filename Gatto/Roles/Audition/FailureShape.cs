namespace Gatto.Roles.Audition;

//how a task went wrong in words a novice can act on, decided mechanically. a new member needs its own words in TaskWords
internal enum FailureShape
{
    //the turn ended in prose with no tool call, on a task that needs one
    NoToolCall,

    //the model emitted arguments the loop could not parse, the loop's own error rather than a tool's
    MalformedArguments,

    //a tool came back an error and the model went on as though it had not
    IgnoredError,

    //the same call with the same arguments three times running, the model is stuck
    RepeatLoop,

    //the model asserted content no tool result returned, the disqualifier at any score since a novice cannot spot it
    FabricatedResult,

    //generation hit the token ceiling with no natural stop
    Runaway,

    //the model called a tool that was never advertised to it
    UnknownTool,

    //the token cap was hit with no answer, gatto's cut rather than the server's finish_reason length, so a small n_predict is not a wedge
    StoppedAtCap,

    //nothing came back for the whole allowance, which points at the server, and it still counts as a failed task
    Stalled,
}
