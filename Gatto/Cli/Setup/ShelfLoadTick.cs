namespace Gatto.Cli.Setup;

//one moment of a loading shelf, both lines composed by the flow. the local line is null once nothing is left to say about this machine
internal readonly record struct ShelfLoadTick(string Step, string? Local);
