namespace Gatto.Core.Acquire;

//one moment of a running fetch, so no two readings on the download screen can disagree. it lives in core because the hub download reports it
internal readonly record struct FetchTick(
    string FileName, int Index, int Count,
    long Done, long Total, long ElapsedMs,   //the bytes count every sitting of this file, the clock only this one
    long Resumed = 0);   //the bytes already on disk when this sitting began. the rate subtracts them, a resumed fetch would otherwise show a rate no link can reach
